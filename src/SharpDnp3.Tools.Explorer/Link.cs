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
using SharpDnp3.Channels;
using SharpDnp3.Master;

namespace SharpDnp3.Tools.Explorer;

/// <summary>The connection setup: which device, over what, and as whom.</summary>
/// <remarks>
/// These are the parameters an operator has to get right in front of an
/// unfamiliar device and usually cannot, because a link address read off a
/// drawing is a guess until something answers. Quitting and restarting the tool
/// to try 11 instead of 10 is how ten minutes of commissioning becomes an
/// afternoon, so they are editable while it runs and applied by reconnecting.
/// </remarks>
public readonly record struct LinkParams
{
    /// <summary>Whether to run a simulated outstation in-process.</summary>
    public bool Demo { get; init; }

    /// <summary>The serial port, when the device is on one.</summary>
    public string Serial { get; init; }

    /// <summary>The serial line rate.</summary>
    public int Baud { get; init; }

    /// <summary>The host and port, when the device is on a network.</summary>
    public string Host { get; init; }

    /// <summary>This master's link address.</summary>
    public ushort Local { get; init; }

    /// <summary>The outstation's link address.</summary>
    public ushort Remote { get; init; }

    /// <summary>How long to wait for a response.</summary>
    public TimeSpan Timeout { get; init; }

    /// <summary>How often to poll the event classes; zero disables it.</summary>
    public TimeSpan Poll { get; init; }

    /// <summary>Names the device for the header and the overview.</summary>
    public string Target
    {
        get
        {
            if (Demo)
            {
                return "demo (in-process outstation)";
            }

            return !string.IsNullOrEmpty(Serial) ? "serial " + Serial : Host ?? "";
        }
    }

    /// <summary>Names the link the device is reached over.</summary>
    public string Transport
    {
        get
        {
            if (Demo)
            {
                return "in-memory pipe";
            }

            return !string.IsNullOrEmpty(Serial)
                ? string.Format(CultureInfo.InvariantCulture, "serial {0} baud", Baud)
                : "tcp";
        }
    }

    /// <summary>
    /// Renders the transport the way the editor accepts it back, so what the
    /// operator sees in the field is what they could have typed.
    /// </summary>
    public string Address
    {
        get
        {
            if (Demo)
            {
                return "demo";
            }

            return !string.IsNullOrEmpty(Serial)
                ? string.Format(CultureInfo.InvariantCulture, "{0}@{1}", Serial, Baud)
                : Host ?? "";
        }
    }

    /// <summary>Reports whether two setups point at the same physical device.</summary>
    /// <remarks>
    /// Changing a timeout leaves the measurements on screen meaningful; changing
    /// the address or the link address does not, because they then describe a
    /// device this tool is no longer talking to.
    /// </remarks>
    public bool SameDevice(LinkParams o) =>
        Demo == o.Demo &&
        string.Equals(Serial ?? "", o.Serial ?? "", StringComparison.Ordinal) &&
        Baud == o.Baud &&
        string.Equals(Host ?? "", o.Host ?? "", StringComparison.Ordinal) &&
        Local == o.Local && Remote == o.Remote;

    /// <summary>Checks the parts that only make sense together.</summary>
    public void Validate()
    {
        if (Local == Remote)
        {
            // The link layer addresses the two ends apart. Equal addresses mean
            // every frame this master sends is addressed to itself, and the
            // symptom is a link that never comes up for no visible reason.
            throw new FormatException(string.Format(
                CultureInfo.InvariantCulture,
                "addresses: local and remote are both {0}; they must differ", Local));
        }

        if (Timeout <= TimeSpan.Zero)
        {
            throw new FormatException("timeout: must be longer than zero");
        }

        if (Poll < TimeSpan.Zero)
        {
            throw new FormatException("poll: cannot be negative; use 0 to disable it");
        }
    }

    /// <summary>
    /// Reads the one field that covers every transport: "demo", a serial device
    /// with an optional rate, or a host and port.
    /// </summary>
    /// <remarks>
    /// One field rather than three because the transports are alternatives, not
    /// a combination: a form with an empty serial box next to a filled host box
    /// invites the reader to wonder which one is in force.
    /// </remarks>
    public LinkParams WithAddress(string s)
    {
        ArgumentNullException.ThrowIfNull(s);

        s = s.Trim();

        if (s.Length == 0)
        {
            throw new FormatException("outstation: give a host:port, a serial device, or demo");
        }

        if (string.Equals(s, "demo", StringComparison.OrdinalIgnoreCase))
        {
            return this with { Demo = true, Serial = "", Host = "" };
        }

        if (s.StartsWith('/') || s.StartsWith('.') ||
            s.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
        {
            var device = s;
            var baud = Baud;

            var at = s.LastIndexOf('@');
            if (at >= 0)
            {
                device = s[..at].Trim();
                if (!int.TryParse(
                        s[(at + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var n) || n <= 0)
                {
                    throw new FormatException(
                        $"outstation: \"{s[(at + 1)..]}\" is not a line rate");
                }

                baud = n;
            }

            if (baud <= 0)
            {
                baud = 9600;
            }

            return this with { Demo = false, Serial = device, Host = "", Baud = baud };
        }

        // A bare host is almost always a slip rather than an intention, and
        // defaulting the port silently is how a tool ends up reporting that it
        // cannot reach a device the operator never asked for.
        var colon = s.LastIndexOf(':');
        if (colon < 0)
        {
            throw new FormatException($"outstation: \"{s}\" needs a port, as host:port");
        }

        var host = s[..colon];
        var port = s[(colon + 1)..];

        if (host.Length == 0)
        {
            throw new FormatException($"outstation: \"{s}\" has no host");
        }

        if (!int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ||
            p <= 0 || p > 65535)
        {
            throw new FormatException($"outstation: \"{port}\" is not a port");
        }

        return this with { Demo = false, Serial = "", Host = s };
    }

    /// <summary>Reads a link address, which is a 16-bit number.</summary>
    public static ushort ParseLinkAddr(string what, string s)
    {
        if (!ushort.TryParse(
            s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            throw new FormatException(
                $"{what}: \"{s}\" is not an address between 0 and 65535");
        }

        return n;
    }

    /// <summary>
    /// Reads a duration, accepting a bare number as seconds because that is what
    /// an operator in a hurry types.
    /// </summary>
    public static TimeSpan ParseInterval(string what, string s)
    {
        s = s?.Trim() ?? "";
        if (s.Length == 0)
        {
            throw new FormatException($"{what}: give a duration, such as 5s");
        }

        try
        {
            return Duration.Parse(s);
        }
        catch (FormatException)
        {
            throw new FormatException($"{what}: \"{s}\" is not a duration, such as 5s or 500ms");
        }
    }
}

/// <summary>The duration spelling a DNP3 operator already types: 500ms, 30s, 5m.</summary>
public static class Duration
{
    /// <summary>Parses a duration, or throws <see cref="FormatException"/>.</summary>
    public static TimeSpan Parse(string s)
    {
        ArgumentException.ThrowIfNullOrEmpty(s);

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var bare))
        {
            return TimeSpan.FromSeconds(bare);
        }

        var total = TimeSpan.Zero;
        var i = 0;

        while (i < s.Length)
        {
            var start = i;
            while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] is '.' or '-' or '+'))
            {
                i++;
            }

            if (start == i)
            {
                throw new FormatException($"\"{s}\" is not a duration");
            }

            var number = double.Parse(s[start..i], CultureInfo.InvariantCulture);

            var unitStart = i;
            while (i < s.Length && char.IsAsciiLetter(s[i]))
            {
                i++;
            }

            var unit = s[unitStart..i];
            total += unit switch
            {
                "ns" => TimeSpan.FromTicks((long)(number / 100)),
                "us" or "µs" => TimeSpan.FromTicks((long)(number * 10)),
                "ms" => TimeSpan.FromMilliseconds(number),
                "s" => TimeSpan.FromSeconds(number),
                "m" => TimeSpan.FromMinutes(number),
                "h" => TimeSpan.FromHours(number),
                _ => throw new FormatException($"\"{s}\" has an unknown time unit \"{unit}\""),
            };
        }

        return total;
    }

    /// <summary>Renders a duration the way it would be typed back in.</summary>
    public static string ToText(TimeSpan d)
    {
        if (d == TimeSpan.Zero)
        {
            return "0s";
        }

        if (d.TotalMilliseconds < 1000)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.###}ms", d.TotalMilliseconds);
        }

        if (d.TotalSeconds < 60)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.###}s", d.TotalSeconds);
        }

        if (d.TotalMinutes < 60)
        {
            var seconds = d.Seconds + (d.Milliseconds / 1000.0);
            return seconds == 0
                ? string.Format(CultureInfo.InvariantCulture, "{0}m", (int)d.TotalMinutes)
                : string.Format(
                    CultureInfo.InvariantCulture, "{0}m{1:0.###}s", (int)d.TotalMinutes, seconds);
        }

        return d.Minutes == 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}h", (int)d.TotalHours)
            : string.Format(CultureInfo.InvariantCulture, "{0}h{1}m", (int)d.TotalHours, d.Minutes);
    }
}

/// <summary>
/// Owns the session lifecycle so it can be torn down and rebuilt with new
/// parameters while the interface stays up.
/// </summary>
/// <remarks>
/// One session runs at a time. Restarting cancels the old token and waits for
/// its tasks to finish before starting any new ones, so a reconnect can never
/// leave two sessions pushing measurements into the same model — which would
/// show an operator a blend of two devices with nothing to say so.
/// </remarks>
public sealed class Supervisor
{
    private readonly Connection _conn;
    private readonly CancellationToken _root;

    // Serialises restarts. A second reconnect arriving while the first is still
    // tearing down waits for it rather than interleaving with it.
    private readonly SemaphoreSlim _op = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task _running = Task.CompletedTask;

    /// <summary>Creates a supervisor feeding <paramref name="conn"/>.</summary>
    public Supervisor(Connection conn, CancellationToken root)
    {
        _conn = conn;
        _root = root;
    }

    /// <summary>Stops whatever is running and brings up a session for <paramref name="p"/>.</summary>
    public async Task StartAsync(LinkParams p)
    {
        await _op.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopInnerAsync().ConfigureAwait(false);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_root);
            _cts = cts;
            var token = cts.Token;

            var (channel, device) = Demo.BuildChannel(p, token);

            var handler = new UiHandler(_conn);
            var session = new MasterSession(new MasterConfig
            {
                LocalAddr = p.Local,
                RemoteAddr = p.Remote,
                ResponseTimeout = p.Timeout,
                IntegrityOnStartup = true,
                DisableUnsolOnStartup = true,
                UnsolClassMask = Class.Class123,
                KeepAlive = TimeSpan.FromSeconds(30),
                // The explorer draws the terminal, so nothing else may write to
                // it. What the operator needs to see arrives as messages and
                // appears on the Log screen.
                Log = NullDnp3Logger.Instance,
            }, handler);

            // Set before the session starts, which is what makes it safe for the
            // session's own tasks to read without a lock.
            handler.Session = session;
            _conn.Adopt(session, p);

            _running = Task.WhenAll(
                device,
                RunSessionAsync(session, channel, token),
                SchedulePollAsync(session, p, token),
                WatchAsync(session, token));
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>Tears the current session down and waits for it.</summary>
    public async Task StopAsync()
    {
        await _op.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopInnerAsync().ConfigureAwait(false);
        }
        finally
        {
            _op.Release();
        }
    }

    private async Task StopInnerAsync()
    {
        var cts = _cts;
        _cts = null;
        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);

        // Waiting is the point: the next session must not start until this one
        // has stopped touching the model.
        try
        {
            await _running.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or Dnp3Exception or IOException)
        {
        }

        cts.Dispose();
        _running = Task.CompletedTask;
    }

    private async Task RunSessionAsync(
        MasterSession session, IChannel channel, CancellationToken token)
    {
        try
        {
            await session.RunAsync(channel, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is Dnp3Exception or IOException)
        {
            if (!token.IsCancellationRequested)
            {
                _conn.Push(new StatusMsg { Text = "failed", Error = ex.Message });
            }
        }
        finally
        {
            channel.Dispose();
        }
    }

    private async Task SchedulePollAsync(
        MasterSession session, LinkParams p, CancellationToken token)
    {
        if (p.Poll <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            // Give the startup sequence a moment before adding to the queue.
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            await session.AddPeriodicScanAsync(p.Poll, Class.Class123, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Dnp3Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                _conn.Push(new LogMsg("warn", "periodic poll: " + ex.Message));
            }
        }
    }

    /// <summary>
    /// A connection watcher, so the header reflects reality even when the
    /// outstation has nothing to say.
    /// </summary>
    private async Task WatchAsync(MasterSession session, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var connected = session.Connected;
                _conn.Push(new StatusMsg
                {
                    Text = connected ? "connected" : "disconnected",
                    Connected = connected,
                    Stats = session.Stats,
                    Iin = session.LastIin.ToString(),
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
