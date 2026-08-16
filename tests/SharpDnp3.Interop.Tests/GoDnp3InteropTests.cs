// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Interoperability against go-dnp3, verified in both directions: our master
// against its outstation, and its master against our outstation.

using System.Globalization;
using SharpDnp3.Channels;
using SharpDnp3.Master;
using SharpDnp3.Outstation;

namespace SharpDnp3.Interop.Tests;

/// <summary>Records what a master reports, for assertions.</summary>
internal sealed class InteropHandler : NopHandler
{
    private readonly Lock _gate = new();

    public Dictionary<ushort, Binary> Binaries { get; } = [];

    public Dictionary<ushort, Analog> Analogs { get; } = [];

    public Dictionary<ushort, Counter> Counters { get; } = [];

    public Dictionary<ushort, BinaryOutputStatus> BinaryOutputs { get; } = [];

    public int Fragments { get; private set; }

    public override void BeginFragment(ResponseInfo info)
    {
        lock (_gate)
        {
            Fragments++;
        }
    }

    public override void HandleBinary(HeaderInfo info, IReadOnlyList<Indexed<Binary>> values)
    {
        lock (_gate)
        {
            foreach (var v in values)
            {
                Binaries[v.Index] = v.Value;
            }
        }
    }

    public override void HandleAnalog(HeaderInfo info, IReadOnlyList<Indexed<Analog>> values)
    {
        lock (_gate)
        {
            foreach (var v in values)
            {
                Analogs[v.Index] = v.Value;
            }
        }
    }

    public override void HandleCounter(HeaderInfo info, IReadOnlyList<Indexed<Counter>> values)
    {
        lock (_gate)
        {
            foreach (var v in values)
            {
                Counters[v.Index] = v.Value;
            }
        }
    }

    public override void HandleBinaryOutputStatus(
        HeaderInfo info, IReadOnlyList<Indexed<BinaryOutputStatus>> values)
    {
        lock (_gate)
        {
            foreach (var v in values)
            {
                BinaryOutputs[v.Index] = v.Value;
            }
        }
    }

    public T Read<T>(Func<InteropHandler, T> read)
    {
        lock (_gate)
        {
            return read(this);
        }
    }
}

/// <summary>A command handler that accepts and records everything.</summary>
internal sealed class AcceptingCommandHandler : ICommandHandler
{
    private readonly Lock _gate = new();

    public List<(ushort Index, ControlRelayOutputBlock Crob)> Operated { get; } = [];

    public CommandStatus SelectCrob(ushort index, ControlRelayOutputBlock c) =>
        CommandStatus.Success;

    public CommandStatus OperateCrob(ushort index, ControlRelayOutputBlock c, OperateType op)
    {
        lock (_gate)
        {
            Operated.Add((index, c));
        }

        return CommandStatus.Success;
    }

    public CommandStatus SelectAnalog(ushort index, AnalogOutputCommand v) => CommandStatus.Success;

    public CommandStatus OperateAnalog(ushort index, AnalogOutputCommand v, OperateType op) =>
        CommandStatus.Success;

    public int OperatedCount
    {
        get
        {
            lock (_gate)
            {
                return Operated.Count;
            }
        }
    }
}

public class GoDnp3InteropTests
{
    private static async Task WaitForAsync(Func<bool> condition, string what, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"timed out waiting for {what}");
    }

    /// <summary>
    /// Our master against go-dnp3's simulated outstation: an integrity poll has
    /// to return the plant it advertises, and a control has to be accepted.
    /// </summary>
    [Fact]
    public async Task OurMasterAgainstGoOutstation()
    {
        var outstationBin = Peers.GoDnp3("dnp3-outstation");
        Assert.SkipUnless(
            outstationBin is not null,
            "go-dnp3 not available; set GO_DNP3_BIN to a directory holding dnp3-outstation");

        var port = Peers.FreePort();
        await using var peer = new PeerProcess(
            outstationBin!,
            "-listen", string.Format(CultureInfo.InvariantCulture, "127.0.0.1:{0}", port));

        await PeerProcess.WaitForPortAsync(port, TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var handler = new InteropHandler();

        var master = new MasterSession(
            new MasterConfig
            {
                LocalAddr = 1,
                RemoteAddr = 10,
                ResponseTimeout = TimeSpan.FromSeconds(10),
                IntegrityOnStartup = true,
                DisableUnsolOnStartup = true,
            },
            handler);

        using var channel = new TcpClientChannel(
            string.Format(CultureInfo.InvariantCulture, "127.0.0.1:{0}", port), Retry.Default);

        var run = master.RunAsync(channel, cts.Token);

        await WaitForAsync(() => master.Connected, "the master to connect", TimeSpan.FromSeconds(15));

        // The peer's plant simulation populates its database on its first tick,
        // which is after it starts listening. Poll until the points come alive
        // rather than asserting against an outstation that is up but has not
        // yet decided what it is measuring.
        await WaitForAsync(
            async () =>
            {
                await master.IntegrityPollAsync(cts.Token);
                return handler.Read(h =>
                    h.Binaries.TryGetValue(0, out var b) && b.Flags.Has(Flags.Online));
            },
            "the peer's plant simulation to populate its database",
            TimeSpan.FromSeconds(20));

        // The simulated plant: four breakers, five analogs, two counters.
        Assert.True(
            handler.Read(h => h.Binaries.Count) >= 4,
            $"expected at least 4 binary inputs, got {handler.Read(h => h.Binaries.Count)}");
        Assert.True(
            handler.Read(h => h.Analogs.Count) >= 5,
            $"expected at least 5 analog inputs, got {handler.Read(h => h.Analogs.Count)}");
        Assert.True(
            handler.Read(h => h.Counters.Count) >= 2,
            $"expected at least 2 counters, got {handler.Read(h => h.Counters.Count)}");

        // Breaker 0 starts closed in the simulated plant.
        Assert.True(
            handler.Read(h => h.Binaries[0].Value),
            "breaker 0 should start closed; binaries were " +
            handler.Read(h => string.Join(
                ", ", h.Binaries.Select(kv => $"{kv.Key}={kv.Value.Value}/{kv.Value.Flags}"))));

        // The analogs are live plant values, so assert they are being reported
        // as good rather than pinning a number that moves.
        Assert.True(handler.Read(h => h.Analogs[0].Flags.Has(Flags.Online)));

        // Operate it. Feeder 1's breaker is not interlocked, so this must be
        // accepted and must open the breaker.
        var result = await master.DirectOperateAsync([Command.Trip(0, 100)], cts.Token);
        Assert.True(result.OK(), result.ToString());

        await WaitForAsync(
            async () =>
            {
                await master.ScanClassesAsync(Class.Class123, cts.Token);
                return !handler.Read(h => h.Binaries[0].Value);
            },
            "breaker 0 to open",
            TimeSpan.FromSeconds(20));

        await cts.CancelAsync();
        await Task.WhenAny(run, Task.Delay(5000));
    }

    /// <summary>Strips whitespace outside string literals, for JSON matching.</summary>
    private static string Compact(string json)
    {
        var b = new System.Text.StringBuilder(json.Length);
        var inString = false;
        var escaped = false;

        foreach (var c in json)
        {
            if (inString)
            {
                b.Append(c);
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                b.Append(c);
                continue;
            }

            if (!char.IsWhiteSpace(c))
            {
                b.Append(c);
            }
        }

        return b.ToString();
    }

    private static async Task WaitForAsync(
        Func<Task<bool>> condition, string what, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(200);
        }

        Assert.Fail($"timed out waiting for {what}");
    }

    /// <summary>
    /// go-dnp3's master against our outstation: it has to poll us, read what we
    /// hold, and drive a control through to our command handler.
    /// </summary>
    [Fact]
    public async Task GoMasterAgainstOurOutstation()
    {
        var masterBin = Peers.GoDnp3("dnp3-master");
        Assert.SkipUnless(
            masterBin is not null,
            "go-dnp3 not available; set GO_DNP3_BIN to a directory holding dnp3-master");

        var port = Peers.FreePort();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var commands = new AcceptingCommandHandler();

        var outstation = new OutstationSession(
            new OutstationConfig
            {
                LocalAddr = 10,
                RemoteAddr = 1,
                Database = new DatabaseConfig
                {
                    Binary = 4,
                    Analog = 4,
                    Counter = 2,
                    BinaryOutputStatus = 4,
                    AnalogOutputStatus = 2,
                    DefaultClass = Class.Class1,
                },
            },
            null,
            commands);

        using var channel = new TcpServerChannel(
            string.Format(CultureInfo.InvariantCulture, "127.0.0.1:{0}", port));

        var run = outstation.RunAsync(channel, cts.Token);

        outstation.Update(db =>
        {
            db.UpdateBinary(0, new Binary(true, Flags.Online, Timestamp.Now(DateTimeOffset.UtcNow)));
            db.UpdateBinary(1, new Binary(false, Flags.Online, Timestamp.Now(DateTimeOffset.UtcNow)));
            db.UpdateAnalog(0, new Analog(11000, Flags.Online, Timestamp.Now(DateTimeOffset.UtcNow)));
            db.UpdateCounter(0, new Counter(4242, Flags.Online, Timestamp.Now(DateTimeOffset.UtcNow)));
        });

        // dnp3-master's one-shot control mode connects, runs the command and
        // exits, which is exactly the shape of assertion this test wants.
        await using var peer = new PeerProcess(
            masterBin!,
            "-host", string.Format(CultureInfo.InvariantCulture, "127.0.0.1:{0}", port),
            "operate", "trip", "1");

        var exit = await peer.WaitForExitAsync(TimeSpan.FromSeconds(30));

        Assert.True(
            exit == 0,
            $"go-dnp3 master exited {exit}\nstdout:\n{peer.StandardOutput}\nstderr:\n{peer.StandardError}");

        Assert.Contains("SUCCESS", peer.StandardOutput, StringComparison.Ordinal);

        await WaitForAsync(
            () => commands.OperatedCount > 0,
            "the control to reach our command handler",
            TimeSpan.FromSeconds(10));

        var (index, crob) = commands.Operated[0];
        Assert.Equal(1, index);
        Assert.True(crob.Code.IsTrip());

        await cts.CancelAsync();
        await Task.WhenAny(run, Task.Delay(5000));
    }

    /// <summary>
    /// go-dnp3's master polling our outstation over a longer run, reading back
    /// the values we hold through its HTTP status endpoint.
    /// </summary>
    [Fact]
    public async Task GoMasterPollsOurOutstation()
    {
        var masterBin = Peers.GoDnp3("dnp3-master");
        Assert.SkipUnless(
            masterBin is not null,
            "go-dnp3 not available; set GO_DNP3_BIN to a directory holding dnp3-master");

        var port = Peers.FreePort();
        var statusPort = Peers.FreePort();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var outstation = new OutstationSession(
            new OutstationConfig
            {
                LocalAddr = 10,
                RemoteAddr = 1,
                Database = new DatabaseConfig
                {
                    Binary = 8,
                    Analog = 8,
                    Counter = 4,
                    BinaryOutputStatus = 8,
                    DefaultClass = Class.Class1,
                },
            });

        using var channel = new TcpServerChannel(
            string.Format(CultureInfo.InvariantCulture, "127.0.0.1:{0}", port));

        var run = outstation.RunAsync(channel, cts.Token);

        outstation.Update(db =>
        {
            for (ushort i = 0; i < 8; i++)
            {
                db.UpdateBinary(i, new Binary(i % 2 == 0, Flags.Online, Timestamp.Now(DateTimeOffset.UtcNow)));
                db.UpdateAnalog(i, new Analog(i * 111, Flags.Online, Timestamp.Now(DateTimeOffset.UtcNow)));
            }
        });

        await using var peer = new PeerProcess(
            masterBin!,
            "-host", string.Format(CultureInfo.InvariantCulture, "127.0.0.1:{0}", port),
            "-listen", string.Format(CultureInfo.InvariantCulture, "127.0.0.1:{0}", statusPort),
            "-poll", "1s");

        // Give it time to connect and complete its startup sequence.
        await WaitForAsync(
            () => outstation.Stats.RequestsReceived > 3,
            "the go-dnp3 master to poll us",
            TimeSpan.FromSeconds(30));

        // The peer publishes its own view of the session over HTTP. Poll it:
        // the endpoint is up before the session is registered, so a single
        // request races the very thing being asserted.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var statusUri = new Uri(string.Format(
            CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/status", statusPort));

        // The body is pretty-printed JSON, so compare against a whitespace-free
        // copy rather than guessing at the peer's formatting.
        var status = "";
        await WaitForAsync(
            async () =>
            {
                try
                {
                    status = Compact(await http.GetStringAsync(statusUri, cts.Token));
                }
                catch (HttpRequestException)
                {
                    return false;
                }

                return status.Contains("\"connected\":true", StringComparison.Ordinal);
            },
            "the peer to report a healthy session",
            TimeSpan.FromSeconds(30));

        // It polled us and every task it ran succeeded.
        Assert.Contains("\"connected\":true", status, StringComparison.Ordinal);
        Assert.Contains("\"tasks_failed\":0", status, StringComparison.Ordinal);
        Assert.Contains("\"response_timeouts\":0", status, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tasks_run\":0", status, StringComparison.Ordinal);

        Assert.True(outstation.Stats.ResponsesSent > 0);

        await cts.CancelAsync();
        await Task.WhenAny(run, Task.Delay(5000));
    }
}
