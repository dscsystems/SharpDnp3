// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SharpDnp3.Interop.Tests.Otfabric;

/// <summary>
/// The reference implementations of github.com/otfabric/dnp3-interop, which
/// ship as Docker images with one command line contract: <c>server</c> is an
/// outstation serving a fixture, <c>client &lt;operation&gt;</c> is a master
/// that runs one operation and prints one JSON document.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is built here. The tests need Docker and the images, which are
/// pulled on first use. <c>DNP3_INTEROP_TAG</c> picks the release (the default
/// is the one the expectations were written against),
/// <c>DNP3_INTEROP_ADAPTERS</c> a comma separated list of adapters to run
/// against, <c>DNP3_INTEROP_IMAGE_PREFIX</c> where the images come from, and
/// <c>DNP3_INTEROP_NO_PULL</c> keeps the tests from pulling. The stepfunc image
/// is not published, at Step Function I/O's request, so it takes part only
/// where it was built locally (<c>make image-stepfunc</c> in that repository,
/// with the same tag).
/// </para>
/// <para>
/// As with the other interop tests, a test whose peer is not available skips
/// rather than fails.
/// </para>
/// </remarks>
internal static class Reference
{
    private static readonly ConcurrentDictionary<string, Lazy<string?>> Images = new();
    private static readonly Lazy<string?> DockerProblem = new(CheckDocker);

    /// <summary>The release of the images the tests run against.</summary>
    public static string Tag => Environment.GetEnvironmentVariable("DNP3_INTEROP_TAG") is { Length: > 0 } t
        ? t
        : "v0.5.1";

    /// <summary>The adapters the tests run against.</summary>
    public static IReadOnlyList<string> Adapters =>
        (Environment.GetEnvironmentVariable("DNP3_INTEROP_ADAPTERS") is { Length: > 0 } a
            ? a
            : "opendnp3,dscsystems,stepfunc")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Theory data: one row per configured adapter.</summary>
    public static TheoryData<string> AdapterData()
    {
        var data = new TheoryData<string>();
        foreach (var a in Adapters)
        {
            data.Add(a);
        }

        return data;
    }

    /// <summary>The image reference of an adapter.</summary>
    public static string Image(string adapter) =>
        (Environment.GetEnvironmentVariable("DNP3_INTEROP_IMAGE_PREFIX") is { Length: > 0 } p
            ? p
            : "ghcr.io/otfabric/dnp3-interop-") + adapter + ":" + Tag;

    /// <summary>
    /// Skips the calling test unless Docker and the adapter's image are
    /// available.
    /// </summary>
    public static void RequireOrSkip(string adapter)
    {
        var problem = DockerProblem.Value
            ?? Images.GetOrAdd(adapter, a => new Lazy<string?>(() => CheckImage(a))).Value;

        Assert.SkipWhen(problem is not null, problem ?? "");
    }

    private static string? CheckDocker()
    {
        try
        {
            var r = Docker.Run(TimeSpan.FromSeconds(20), "info", "--format", "{{.ServerVersion}}");
            return r.ExitCode == 0 ? null : "docker is not available: " + r.Stderr.Trim();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "docker is not installed";
        }
    }

    private static string? CheckImage(string adapter)
    {
        var image = Image(adapter);
        if (Docker.Run(TimeSpan.FromSeconds(20), "image", "inspect", image).ExitCode == 0)
        {
            return null;
        }

        if (Environment.GetEnvironmentVariable("DNP3_INTEROP_NO_PULL") is { Length: > 0 })
        {
            return $"{image} is not present and DNP3_INTEROP_NO_PULL is set";
        }

        var pull = Docker.Run(TimeSpan.FromMinutes(5), "pull", image);
        return pull.ExitCode == 0
            ? null
            : $"{image} is not available (the stepfunc image is built locally, not published): " +
              pull.Stderr.Trim();
    }

    /// <summary>
    /// Where a container reaches a server running on the host, and the flags
    /// that make it work. Docker Desktop resolves <c>host.docker.internal</c>;
    /// on Linux the container shares the host's network instead.
    /// </summary>
    public static (string Host, string[] Flags) HostAccess() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? ("127.0.0.1", ["--network", "host"])
            : ("host.docker.internal", ["--add-host=host.docker.internal:host-gateway"]);
}

/// <summary>The outcome of one <c>docker</c> invocation.</summary>
internal readonly record struct DockerResult(int ExitCode, string Stdout, string Stderr);

internal static class Docker
{
    /// <summary>Runs <c>docker</c> to completion.</summary>
    public static DockerResult Run(TimeSpan timeout, params string[] arguments) =>
        RunAsync(timeout, arguments).GetAwaiter().GetResult();

    /// <summary>Runs <c>docker</c> to completion.</summary>
    public static async Task<DockerResult> RunAsync(TimeSpan timeout, params string[] arguments)
    {
        var info = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var a in arguments)
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"docker {string.Join(' ', arguments)} did not finish within {timeout}");
        }

        return new DockerResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}

/// <summary>A reference outstation running in a container.</summary>
internal sealed class ReferenceServer : IAsyncDisposable
{
    private readonly string _name;

    private ReferenceServer(string name, int port)
    {
        _name = name;
        Port = port;
    }

    /// <summary>The port on the host that reaches the outstation.</summary>
    public int Port { get; }

    /// <summary>The address to dial.</summary>
    public string Address => string.Format(CultureInfo.InvariantCulture, "127.0.0.1:{0}", Port);

    /// <summary>
    /// Starts <c>server</c> and waits for its readiness file, which is the
    /// contract's definition of ready: not the open port and not a delay.
    /// </summary>
    public static async Task<ReferenceServer> StartAsync(string adapter, params string[] serverArguments)
    {
        var name = "sharpdnp3-interop-" + Guid.NewGuid().ToString("N")[..8];

        string[] run =
        [
            "run", "-d", "--rm", "--name", name, "-p", "127.0.0.1::20000",
            Reference.Image(adapter), "server", .. serverArguments,
        ];

        var started = await Docker.RunAsync(TimeSpan.FromSeconds(60), run).ConfigureAwait(false);
        Assert.True(started.ExitCode == 0, "docker run failed: " + started.Stderr);

        var server = new ServerStart(name);
        try
        {
            await server.WaitReadyAsync().ConfigureAwait(false);
            var port = await server.PortAsync().ConfigureAwait(false);
            return new ReferenceServer(name, port);
        }
        catch
        {
            await Docker.RunAsync(TimeSpan.FromSeconds(30), "rm", "-f", name).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The events the server has printed so far (one JSON object per line of
    /// standard output; its logs go to standard error).
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> EventsAsync(string? kind = null)
    {
        var logs = await Docker.RunAsync(TimeSpan.FromSeconds(30), "logs", _name).ConfigureAwait(false);

        var events = new List<JsonElement>();
        foreach (var line in logs.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.Trim();
            if (!text.StartsWith('{'))
            {
                continue;
            }

            var e = JsonDocument.Parse(text).RootElement.Clone();
            if (kind is null || e.GetProperty("event").GetString() == kind)
            {
                events.Add(e);
            }
        }

        return events;
    }

    /// <summary>Waits until the server has printed at least <paramref name="count"/> events of a kind.</summary>
    public async Task<IReadOnlyList<JsonElement>> WaitForEventsAsync(string kind, int count = 1)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var events = await EventsAsync(kind).ConfigureAwait(false);
            if (events.Count >= count || DateTime.UtcNow > deadline)
            {
                Assert.True(events.Count >= count, $"the server printed {events.Count} '{kind}' events, expected {count}");
                return events;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() =>
        await Docker.RunAsync(TimeSpan.FromSeconds(30), "rm", "-f", _name).ConfigureAwait(false);

    private sealed class ServerStart(string name)
    {
        public async Task WaitReadyAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                var r = await Docker.RunAsync(
                    TimeSpan.FromSeconds(10), "exec", name, "test", "-f", "/run/dnp3-interop/ready")
                    .ConfigureAwait(false);
                if (r.ExitCode == 0)
                {
                    return;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            var logs = await Docker.RunAsync(TimeSpan.FromSeconds(10), "logs", name).ConfigureAwait(false);
            throw new TimeoutException($"{name} never became ready:\n{logs.Stdout}\n{logs.Stderr}");
        }

        public async Task<int> PortAsync()
        {
            var r = await Docker.RunAsync(TimeSpan.FromSeconds(10), "port", name, "20000/tcp").ConfigureAwait(false);
            var first = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            return int.Parse(first[(first.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>The result document of a reference master's operation.</summary>
internal sealed class ClientResult
{
    private ClientResult(int exitCode, string raw, JsonElement document)
    {
        ExitCode = exitCode;
        Raw = raw;
        Document = document;
    }

    public int ExitCode { get; }

    public string Raw { get; }

    public JsonElement Document { get; }

    public bool Ok => Document.GetProperty("ok").GetBoolean();

    public string? ErrorCode => Document.GetProperty("error") is { ValueKind: JsonValueKind.Object } e
        ? e.GetProperty("code").GetString()
        : null;

    public string? CommandStatus => Text("commandStatus");

    /// <summary>The IIN bits of the last solicited response, sorted.</summary>
    public IReadOnlyList<string> Iin => Document.TryGetProperty("iin", out var iin) && iin.ValueKind == JsonValueKind.Array
        ? [.. iin.EnumerateArray().Select(i => i.GetString()!).Order(StringComparer.Ordinal)]
        : [];

    public IEnumerable<JsonElement> Objects => Document.GetProperty("objects").EnumerateArray();

    /// <summary>
    /// What was received, counted per group and variation, as
    /// <c>g1v2x4,g30v5x1</c> and sorted: the form the reference repository's
    /// own cases use.
    /// </summary>
    public string ObjectSummary => string.Join(
        ',',
        Objects
            .GroupBy(o => (G: o.GetProperty("group").GetInt32(), V: o.GetProperty("variation").GetInt32()))
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"g{g.Key.G}v{g.Key.V}x{g.Count()}"))
            .Order(StringComparer.Ordinal));

    /// <summary>The object of a group and index.</summary>
    public JsonElement Object(int group, int index) =>
        Objects.Single(o => o.GetProperty("group").GetInt32() == group && o.GetProperty("index").GetInt32() == index);

    private string? Text(string property) =>
        Document.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>
    /// Runs <c>client &lt;operation&gt;</c> against an outstation running on
    /// the host.
    /// </summary>
    public static async Task<ClientResult> RunAsync(
        string adapter, int port, string operation, params string[] operationArguments)
    {
        var (host, network) = Reference.HostAccess();

        string[] run =
        [
            "run", "--rm", .. network, Reference.Image(adapter),
            "client", operation, "--host", host, "--port", port.ToString(CultureInfo.InvariantCulture),
            .. operationArguments,
        ];

        var r = await Docker.RunAsync(TimeSpan.FromSeconds(90), run).ConfigureAwait(false);
        var line = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";

        Assert.True(
            line.StartsWith('{'),
            $"client {operation} printed no result document (exit {r.ExitCode})\nstdout: {r.Stdout}\nstderr: {r.Stderr}");

        return new ClientResult(r.ExitCode, line, JsonDocument.Parse(line).RootElement.Clone());
    }

    /// <summary>The master features an adapter declares, from <c>print-capabilities</c>.</summary>
    public static async Task<JsonElement> CapabilitiesAsync(string adapter)
    {
        var r = await Docker.RunAsync(
            TimeSpan.FromSeconds(60), "run", "--rm", Reference.Image(adapter), "print-capabilities")
            .ConfigureAwait(false);
        return JsonDocument.Parse(r.Stdout.Trim()).RootElement.GetProperty("features").Clone();
    }

    public override string ToString() => Raw;
}
