// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SharpDnp3.Interop.Tests;

/// <summary>
/// Locates and runs the reference implementations these tests interoperate
/// with.
/// </summary>
/// <remarks>
/// <para>
/// The peers are not built here. Point <c>GO_DNP3_BIN</c> at a directory
/// holding <c>dnp3-master</c> and <c>dnp3-outstation</c> built from
/// github.com/dscsystems/go-dnp3, and <c>OPENDNP3_BIN</c> at a directory
/// holding opendnp3's <c>outstation-demo</c> and <c>master-demo</c>.
/// </para>
/// <para>
/// Tests skip rather than fail when a peer is absent, so the suite stays green
/// on a machine that has not built them — an interop test that cannot reach its
/// peer has proved nothing, and reporting that as a failure trains people to
/// ignore it.
/// </para>
/// </remarks>
public static class Peers
{
    /// <summary>The directory holding the go-dnp3 binaries, if configured.</summary>
    public static string? GoDnp3Dir => Environment.GetEnvironmentVariable("GO_DNP3_BIN");

    /// <summary>The directory holding the opendnp3 demos, if configured.</summary>
    public static string? OpenDnp3Dir => Environment.GetEnvironmentVariable("OPENDNP3_BIN");

    /// <summary>Returns the path to a go-dnp3 binary, or null if unavailable.</summary>
    public static string? GoDnp3(string name) => Find(GoDnp3Dir, name);

    /// <summary>Returns the path to an opendnp3 binary, or null if unavailable.</summary>
    public static string? OpenDnp3(string name) => Find(OpenDnp3Dir, name);

    private static string? Find(string? dir, string name)
    {
        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }

        var path = Path.Combine(dir, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Returns a TCP port nothing is listening on.</summary>
    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>Runs an external peer process and captures its output.</summary>
public sealed class PeerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly Lock _gate = new();

    /// <summary>Starts <paramref name="path"/> with the given arguments.</summary>
    public PeerProcess(string path, params string[] arguments)
    {
        var info = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var a in arguments)
        {
            info.ArgumentList.Add(a);
        }

        _process = new Process { StartInfo = info };
        _process.OutputDataReceived += (_, e) => Append(_stdout, e.Data);
        _process.ErrorDataReceived += (_, e) => Append(_stderr, e.Data);

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private void Append(StringBuilder target, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_gate)
        {
            target.AppendLine(line);
        }
    }

    /// <summary>Everything the peer has written to standard output.</summary>
    public string StandardOutput
    {
        get
        {
            lock (_gate)
            {
                return _stdout.ToString();
            }
        }
    }

    /// <summary>Everything the peer has written to standard error.</summary>
    public string StandardError
    {
        get
        {
            lock (_gate)
            {
                return _stderr.ToString();
            }
        }
    }

    /// <summary>Reports whether the peer is still running.</summary>
    public bool Running => !_process.HasExited;

    /// <summary>The peer's exit code, once it has exited.</summary>
    public int ExitCode => _process.ExitCode;

    /// <summary>Waits for the peer to exit.</summary>
    public async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return _process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
    }

    /// <summary>Waits until a TCP port accepts a connection.</summary>
    public static async Task WaitForPortAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }

        throw new TimeoutException(string.Format(
            CultureInfo.InvariantCulture, "nothing accepted a connection on port {0}", port));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(new CancellationTokenSource(5000).Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            // The peer had already gone.
        }
        finally
        {
            _process.Dispose();
        }
    }
}
