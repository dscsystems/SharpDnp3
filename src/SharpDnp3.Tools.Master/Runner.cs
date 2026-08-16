// Copyright (C) 2026 Ricardo Olsen / DSC Systems.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option)
// any later version. It is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details, in the LICENSE file at
// the root of this repository or at <https://www.gnu.org/licenses/>.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using SharpDnp3.Channels;
using SharpDnp3.Master;

namespace SharpDnp3.Tools.Master;

/// <summary>One outstation being polled.</summary>
internal sealed class PolledSite : IDisposable
{
    public PolledSite(Site site, IDnp3Logger log)
    {
        ArgumentNullException.ThrowIfNull(site);

        Site = site;
        Channel = Runner.BuildChannel(site);

        try
        {
            // A generous buffer: an event storm from one site must not stall its
            // own session while the recorder catches up.
            Handler = new ChannelHandler(4096);

            Session = new MasterSession(new MasterConfig
            {
                LocalAddr = site.Local,
                RemoteAddr = site.Address,
                ResponseTimeout = site.Timeout,
                KeepAlive = site.KeepAlive,
                IntegrityOnStartup = true,
                DisableUnsolOnStartup = true,
                UnsolClassMask = site.UnsolMask,
                UseLinkConfirms = site.LinkConfirms == true,
                LinkRetries = 3,
                Log = new SiteLogger(log, site.Name),
            }, Handler);
        }
        catch
        {
            // A site that cannot be built must not leave its socket behind.
            Channel.Dispose();
            throw;
        }
    }

    public Site Site { get; }

    public MasterSession Session { get; }

    public IChannel Channel { get; }

    public ChannelHandler Handler { get; }

    public void Dispose() => Channel.Dispose();
}

/// <summary>Adds the site name to every record a session writes.</summary>
internal sealed class SiteLogger : IDnp3Logger
{
    private readonly IDnp3Logger _inner;
    private readonly string _site;

    public SiteLogger(IDnp3Logger inner, string site)
    {
        _inner = inner;
        _site = site;
    }

    public bool IsEnabled(Dnp3LogLevel level) => _inner.IsEnabled(level);

    public void Log(
        Dnp3LogLevel level,
        string message,
        params ReadOnlySpan<(string Key, object? Value)> fields)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        var all = new (string Key, object? Value)[fields.Length + 1];
        all[0] = ("site", _site);
        for (var i = 0; i < fields.Length; i++)
        {
            all[i + 1] = fields[i];
        }

        _inner.Log(level, message, all);
    }
}

/// <summary>Polls every configured site until the token is cancelled.</summary>
internal static class Runner
{
    /// <summary>Selects the transport for a site.</summary>
    public static IChannel BuildChannel(Site s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (!string.IsNullOrEmpty(s.Serial))
        {
            return new SerialChannel(new SerialConfig { Device = s.Serial, Baud = s.Baud });
        }

        if (s.Tls is not null)
        {
            return new TlsClientChannel(s.Host, new Dnp3TlsConfig
            {
                CertFile = s.Tls.Cert,
                KeyFile = s.Tls.Key,
                CaFile = s.Tls.Ca,
                ServerName = s.Tls.ServerName,
            }, Retry.Default);
        }

        return new TcpClientChannel(s.Host, Retry.Default);
    }

    /// <summary>Runs every site's session, recorder and schedule.</summary>
    public static async Task RunAsync(
        SitesConfig cfg,
        Recorder? recorder,
        Snapshot snapshot,
        IDnp3Logger log,
        string? listen,
        CancellationToken cancellationToken)
    {
        var sites = new List<PolledSite>();
        try
        {
            foreach (var raw in cfg.Sites)
            {
                sites.Add(new PolledSite(cfg.Resolved(raw), log));
            }
        }
        catch
        {
            foreach (var s in sites)
            {
                s.Dispose();
            }

            throw;
        }

        var tasks = new List<Task>();
        try
        {
            foreach (var s in sites)
            {
                tasks.Add(RunSessionAsync(s, log, cancellationToken));

                // One consumer per session, so a slow recorder delays only its
                // own site rather than every session sharing one drain loop.
                tasks.Add(ConsumeAsync(s, recorder, snapshot, log, cancellationToken));
                tasks.Add(ScheduleAsync(s, log, cancellationToken));
            }

            // A status poller, so the API and console reflect sessions that
            // have nothing to report as well as those that do.
            tasks.Add(PollStatusAsync(sites, snapshot, cancellationToken));

            if (!string.IsNullOrEmpty(listen))
            {
                tasks.Add(ServeApiAsync(listen, snapshot, log, cancellationToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl-C is how this program is meant to end.
        }
        finally
        {
            foreach (var s in sites)
            {
                s.Dispose();
            }
        }
    }

    private static async Task RunSessionAsync(
        PolledSite s, IDnp3Logger log, CancellationToken cancellationToken)
    {
        try
        {
            await s.Session.RunAsync(s.Channel, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is Dnp3Exception or IOException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                log.Log(Dnp3LogLevel.Error, "session ended",
                    ("site", s.Site.Name), ("err", ex.Message));
            }
        }
    }

    /// <summary>Drains one session's measurements into the recorder and snapshot.</summary>
    private static async Task ConsumeAsync(
        PolledSite s,
        Recorder? recorder,
        Snapshot snapshot,
        IDnp3Logger log,
        CancellationToken cancellationToken)
    {
        ulong dropped = 0;

        try
        {
            await foreach (var u in s.Handler.Updates.ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                snapshot.Apply(s.Site.Name, u);

                try
                {
                    recorder?.Record(s.Site.Name, u);
                }
                catch (IOException ex)
                {
                    log.Log(Dnp3LogLevel.Error, "recording failed",
                        ("site", s.Site.Name), ("err", ex.Message));
                }

                // The handler drops updates when its buffer fills rather than
                // stalling the protocol. That is the right trade, but it is not
                // free, and an operator reading a recording with holes in it
                // deserves to know they are there.
                var d = s.Handler.Dropped;
                if (d > dropped)
                {
                    log.Log(Dnp3LogLevel.Warn, "updates dropped; the recorder is not keeping up",
                        ("site", s.Site.Name), ("dropped", d - dropped), ("total", d));
                    dropped = d;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Sets up a site's recurring work.</summary>
    private static async Task ScheduleAsync(
        PolledSite s, IDnp3Logger log, CancellationToken cancellationToken)
    {
        try
        {
            // Wait for the startup sequence rather than racing it into the
            // queue.
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

            if (s.Site.SyncClock == true)
            {
                using var sync = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sync.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    await s.Session.SyncTimeAsync(sync.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is Dnp3Exception or OperationCanceledException)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        log.Log(Dnp3LogLevel.Warn, "clock sync failed",
                            ("site", s.Site.Name), ("err", ex.Message));
                    }
                }
            }

            if (s.Site.Poll > TimeSpan.Zero)
            {
                await s.Session.AddPeriodicScanAsync(s.Site.Poll, Class.Class123, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (s.Site.Integrity > TimeSpan.Zero)
            {
                await s.Session
                    .AddPeriodicScanAsync(s.Site.Integrity, Class.All, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Dnp3Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                log.Log(Dnp3LogLevel.Error, "scheduling the polls failed",
                    ("site", s.Site.Name), ("err", ex.Message));
            }
        }
    }

    private static async Task PollStatusAsync(
        List<PolledSite> sites, Snapshot snapshot, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var s in sites)
                {
                    var st = s.Session.Stats;
                    snapshot.SetStatus(new SiteStatus
                    {
                        Name = s.Site.Name,
                        Target = s.Site.Describe(),
                        Connected = s.Session.Connected,
                        Indications = s.Session.LastIin.ToString(),
                        TasksRun = st.TasksRun,
                        TasksSucceeded = st.TasksSucceeded,
                        TasksFailed = st.TasksFailed,
                        Timeouts = st.ResponseTimeouts,
                        Unsolicited = st.Unsolicited,
                        Restarts = st.RestartsSeen,
                        Connections = st.Connections,
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---------- HTTP API ----------

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Exposes the live snapshot over HTTP.</summary>
    private static async Task ServeApiAsync(
        string addr, Snapshot snapshot, IDnp3Logger log, CancellationToken cancellationToken)
    {
        using var listener = new HttpListener();
        foreach (var prefix in Prefixes(addr))
        {
            listener.Prefixes.Add(prefix);
        }

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            log.Log(Dnp3LogLevel.Error, "HTTP API failed to listen",
                ("addr", addr), ("err", ex.Message));
            return;
        }

        log.Log(Dnp3LogLevel.Info, "HTTP API listening", ("addr", addr));

        using var registration = cancellationToken.Register(listener.Close);

        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return; // The listener was closed, which is how this ends.
            }

            try
            {
                Serve(ctx, snapshot);
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException)
            {
                // If the client has gone, there is nobody left to tell.
            }
            finally
            {
                ctx.Response.Close();
            }
        }
    }

    private static void Serve(HttpListenerContext ctx, Snapshot snapshot)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";

        if (!string.Equals(ctx.Request.HttpMethod, "GET", StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            return;
        }

        switch (path)
        {
            case "/healthz":
                Write(ctx, "text/plain", "ok\n");
                break;

            case "/status":
                Write(ctx, "application/json",
                    JsonSerializer.Serialize(snapshot.Status(), JsonOptions) + "\n");
                break;

            case "/points":
                var site = ctx.Request.QueryString["site"];
                Write(ctx, "application/json",
                    JsonSerializer.Serialize(snapshot.Points(site), JsonOptions) + "\n");
                break;

            default:
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                Write(ctx, "text/plain", "not found\n");
                break;
        }
    }

    private static void Write(HttpListenerContext ctx, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Turns a Go-style listen address into the prefixes HttpListener wants.
    /// </summary>
    /// <remarks>
    /// ":8080" means every interface, which is what an operator expects from
    /// that spelling and what the Go tool does.
    /// </remarks>
    private static IEnumerable<string> Prefixes(string addr)
    {
        var colon = addr.LastIndexOf(':');
        var host = colon < 0 ? "" : addr[..colon];
        var port = colon < 0 ? addr : addr[(colon + 1)..];

        if (!int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            throw new FormatException($"listen address \"{addr}\" has no port");
        }

        if (host.Length == 0 || host == "*" || host == "0.0.0.0" || host == "[::]")
        {
            yield return $"http://+:{port}/";
            yield break;
        }

        yield return $"http://{host.Trim('[', ']')}:{port}/";
    }
}
