// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Every integration test runs a full master against a full outstation through
// Pipe.Create() — the real link, transport, application and object layers, with
// no socket and no hardware. That is where the two halves are proven to agree.

using SharpDnp3.Channels;
using SharpDnp3.Master;
using SharpDnp3.Outstation;

namespace SharpDnp3.Tests;

/// <summary>Collects everything a master reports, for assertions.</summary>
internal sealed class RecordingHandler : NopHandler
{
    private readonly Lock _gate = new();

    public Dictionary<ushort, Binary> Binaries { get; } = [];

    public Dictionary<ushort, Analog> Analogs { get; } = [];

    public Dictionary<ushort, Counter> Counters { get; } = [];

    public Dictionary<ushort, BinaryOutputStatus> BinaryOutputs { get; } = [];

    public Dictionary<ushort, byte[]> OctetStrings { get; } = [];

    public List<(ushort Index, Binary Value)> BinaryEvents { get; } = [];

    public int Fragments { get; private set; }

    public Iin LastIin { get; private set; }

    public override void BeginFragment(ResponseInfo info)
    {
        lock (_gate)
        {
            Fragments++;
            LastIin = info.Iin;
        }
    }

    public override void HandleBinary(HeaderInfo info, IReadOnlyList<Indexed<Binary>> values)
    {
        lock (_gate)
        {
            foreach (var v in values)
            {
                Binaries[v.Index] = v.Value;
                if (info.IsEvent)
                {
                    BinaryEvents.Add((v.Index, v.Value));
                }
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

    public override void HandleOctetString(HeaderInfo info, IReadOnlyList<Indexed<byte[]>> values)
    {
        lock (_gate)
        {
            foreach (var v in values)
            {
                OctetStrings[v.Index] = v.Value;
            }
        }
    }

    public T Read<T>(Func<RecordingHandler, T> read)
    {
        lock (_gate)
        {
            return read(this);
        }
    }
}

/// <summary>A command handler that records what it was asked to do.</summary>
internal sealed class RecordingCommandHandler : ICommandHandler
{
    private readonly Lock _gate = new();

    public List<(ushort Index, ControlRelayOutputBlock Crob, OperateType Op)> Operated { get; } = [];

    public List<ushort> Selected { get; } = [];

    /// <summary>Points that refuse every control, standing in for an interlock.</summary>
    public HashSet<ushort> Interlocked { get; } = [];

    public Action<ushort, ControlRelayOutputBlock>? OnOperate { get; set; }

    public CommandStatus SelectCrob(ushort index, ControlRelayOutputBlock c)
    {
        lock (_gate)
        {
            if (Interlocked.Contains(index))
            {
                return CommandStatus.Blocked;
            }

            Selected.Add(index);
            return CommandStatus.Success;
        }
    }

    public CommandStatus OperateCrob(ushort index, ControlRelayOutputBlock c, OperateType op)
    {
        lock (_gate)
        {
            if (Interlocked.Contains(index))
            {
                return CommandStatus.Blocked;
            }

            Operated.Add((index, c, op));
        }

        OnOperate?.Invoke(index, c);
        return CommandStatus.Success;
    }

    public CommandStatus SelectAnalog(ushort index, AnalogOutputCommand v) => CommandStatus.Success;

    public CommandStatus OperateAnalog(ushort index, AnalogOutputCommand v, OperateType op) =>
        CommandStatus.Success;

    public T Read<T>(Func<RecordingCommandHandler, T> read)
    {
        lock (_gate)
        {
            return read(this);
        }
    }
}

/// <summary>Wires a master and an outstation together over an in-process pipe.</summary>
internal sealed class TestPair : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _masterTask;
    private readonly Task _outstationTask;
    private readonly IChannel _masterChannel;
    private readonly IChannel _outstationChannel;

    public MasterSession Master { get; }

    public OutstationSession Outstation { get; }

    public RecordingHandler Handler { get; }

    public RecordingCommandHandler Commands { get; }

    public TestPair(
        Action<MasterConfig>? configureMaster = null,
        Action<OutstationConfig>? configureOutstation = null)
    {
        var (a, b) = Pipe.Create();
        _masterChannel = a;
        _outstationChannel = b;

        var outstationConfig = new OutstationConfig
        {
            LocalAddr = 10,
            RemoteAddr = 1,
            Database = new DatabaseConfig
            {
                Binary = 8,
                DoubleBitBinary = 4,
                Counter = 4,
                FrozenCounter = 4,
                Analog = 8,
                BinaryOutputStatus = 8,
                AnalogOutputStatus = 4,
                OctetString = 2,
                DefaultClass = Class.Class1,
            },
        };
        configureOutstation?.Invoke(outstationConfig);

        Commands = new RecordingCommandHandler();
        Outstation = new OutstationSession(outstationConfig, null, Commands);

        var masterConfig = new MasterConfig
        {
            LocalAddr = 1,
            RemoteAddr = 10,
            ResponseTimeout = TimeSpan.FromSeconds(5),
        };
        configureMaster?.Invoke(masterConfig);

        Handler = new RecordingHandler();
        Master = new MasterSession(masterConfig, Handler);

        _outstationTask = Outstation.RunAsync(_outstationChannel, _cts.Token);
        _masterTask = Master.RunAsync(_masterChannel, _cts.Token);
    }

    /// <summary>Waits until the master reports a connection, or fails.</summary>
    public async Task WaitConnectedAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (Master.Connected)
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail("the master never connected");
    }

    /// <summary>Polls a predicate until it holds or the timeout expires.</summary>
    public static async Task WaitForAsync(
        Func<bool> condition,
        string what,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"timed out waiting for {what}");
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _masterChannel.Close();
        _outstationChannel.Close();

        await Task.WhenAny(Task.WhenAll(_masterTask, _outstationTask), Task.Delay(2000));
        _cts.Dispose();
    }
}

public class IntegrationTests
{
    private static Timestamp Now() => Timestamp.Now(DateTimeOffset.UtcNow);

    [Fact]
    public async Task IntegrityPollDeliversStaticData()
    {
        await using var pair = new TestPair();
        await pair.WaitConnectedAsync();

        pair.Outstation.Update(db =>
        {
            db.UpdateBinary(0, new Binary(true, Flags.Online, Now()));
            db.UpdateBinary(3, new Binary(false, Flags.Online | Flags.CommLost, Now()));
            db.UpdateAnalog(1, new Analog(1234, Flags.Online, Now()));
            db.UpdateCounter(2, new Counter(99, Flags.Online, Now()));
            db.UpdateBinaryOutputStatus(4, new BinaryOutputStatus(true, Flags.Online, Now()));
        });

        await pair.Master.IntegrityPollAsync();

        Assert.True(pair.Handler.Read(h => h.Binaries[0].Value));
        Assert.False(pair.Handler.Read(h => h.Binaries[3].Value));
        Assert.True(pair.Handler.Read(h => h.Binaries[3].Flags.HasAny(Flags.CommLost)));
        Assert.Equal(1234, pair.Handler.Read(h => h.Analogs[1].Value));
        Assert.Equal(99u, pair.Handler.Read(h => h.Counters[2].Value));
        Assert.True(pair.Handler.Read(h => h.BinaryOutputs[4].Value));
    }

    /// <summary>
    /// The default static variation for an analog is g30v1, a 32-bit
    /// <em>integer</em> with flags, so a fractional reading is truncated on the
    /// wire. Carrying it intact needs a point configured for a float variation.
    /// Pinning both halves keeps the default from being quietly widened.
    /// </summary>
    [Fact]
    public async Task AnalogVariationDecidesWhetherFractionsSurvive()
    {
        await using var pair = new TestPair();
        await pair.WaitConnectedAsync();

        pair.Outstation.Database.Configure(PointType.Analog, 2, new PointConfig
        {
            Class = Class.Class1,
            StaticVariation = 5, // g30v5, single precision with flags
            EventVariation = 5,
        });

        pair.Outstation.Update(db =>
        {
            db.UpdateAnalog(1, new Analog(1234.5, Flags.Online, Now()));
            db.UpdateAnalog(2, new Analog(1234.5, Flags.Online, Now()));
        });

        await pair.Master.IntegrityPollAsync();

        Assert.Equal(1234, pair.Handler.Read(h => h.Analogs[1].Value));
        Assert.Equal(1234.5, pair.Handler.Read(h => h.Analogs[2].Value));
    }

    [Fact]
    public async Task EventsArriveOnAClassPoll()
    {
        await using var pair = new TestPair();
        await pair.WaitConnectedAsync();

        // Baseline first, so the changes below are genuinely changes.
        await pair.Master.IntegrityPollAsync();

        pair.Outstation.Update(db =>
        {
            db.UpdateBinary(0, new Binary(false, Flags.Online, Now()));
            db.UpdateBinary(0, new Binary(true, Flags.Online, Now()));
        });

        await pair.Master.ScanClassesAsync(Class.Class123);

        var events = pair.Handler.Read(h => h.BinaryEvents.Count);
        Assert.True(events > 0, "expected at least one binary event");
        Assert.True(pair.Handler.Read(h => h.Binaries[0].Value));
    }

    [Fact]
    public async Task DirectOperateReachesTheCommandHandler()
    {
        await using var pair = new TestPair();
        await pair.WaitConnectedAsync();

        var result = await pair.Master.DirectOperateAsync(Command.Trip(3, 1000));

        Assert.True(result.OK(), result.ToString());
        Assert.Single(pair.Commands.Read(c => c.Operated));

        var (index, crob, op) = pair.Commands.Read(c => c.Operated[0]);
        Assert.Equal(3, index);
        Assert.True(crob.Code.IsTrip());
        Assert.Equal(1000u, crob.OnTime);
        Assert.Equal(OperateType.Direct, op);
    }

    [Fact]
    public async Task SelectAndOperateRunsBothPasses()
    {
        await using var pair = new TestPair();
        await pair.WaitConnectedAsync();

        var result = await pair.Master.SelectAndOperateAsync(Command.Close(2, 500));

        Assert.True(result.OK(), result.ToString());
        Assert.Equal([(ushort)2], pair.Commands.Read(c => c.Selected));

        var (index, crob, op) = pair.Commands.Read(c => c.Operated[0]);
        Assert.Equal(2, index);
        Assert.True(crob.Code.IsClose());
        Assert.Equal(OperateType.Selected, op);
    }

    [Fact]
    public async Task ARefusedSelectNeverOperates()
    {
        await using var pair = new TestPair();
        await pair.WaitConnectedAsync();

        pair.Commands.Interlocked.Add(5);

        var error = await Assert.ThrowsAsync<Dnp3Exception>(
            () => pair.Master.SelectAndOperateAsync(Command.Trip(5, 100)));

        Assert.Contains("select rejected", error.Message, StringComparison.Ordinal);

        // The whole point of select-before-operate: nothing moved.
        Assert.Empty(pair.Commands.Read(c => c.Operated));
    }

    [Fact]
    public async Task ControlClosesTheLoopBackToTheMaster()
    {
        await using var pair = new TestPair();
        await pair.WaitConnectedAsync();

        // Tripping breaker 0 opens breaker 0, which raises a binary input
        // event, which the master receives.
        pair.Commands.OnOperate = (index, crob) =>
            pair.Outstation.Update(db =>
            {
                var state = !crob.Code.IsTrip();
                db.UpdateBinary(index, new Binary(state, Flags.Online, Now()));
                db.UpdateBinaryOutputStatus(
                    index, new BinaryOutputStatus(state, Flags.Online, Now()));
            });

        pair.Outstation.Update(db =>
            db.UpdateBinary(0, new Binary(true, Flags.Online, Now())));

        await pair.Master.IntegrityPollAsync();
        Assert.True(pair.Handler.Read(h => h.Binaries[0].Value));

        var result = await pair.Master.DirectOperateAsync(Command.Trip(0, 100));
        Assert.True(result.OK(), result.ToString());

        await pair.Master.ScanClassesAsync(Class.Class123);
        Assert.False(pair.Handler.Read(h => h.Binaries[0].Value));
    }

    [Fact]
    public async Task AnalogSetpointRoundTrips()
    {
        await using var pair = new TestPair();
        await pair.WaitConnectedAsync();

        var result = await pair.Master.DirectOperateAsync(
            Command.AnalogOutputFloat32(1, 13.75f));

        Assert.True(result.OK(), result.ToString());
    }

    [Fact]
    public async Task MultipleCommandsReportPerPointStatus()
    {
        await using var pair = new TestPair();
        await pair.WaitConnectedAsync();

        pair.Commands.Interlocked.Add(6);

        var result = await pair.Master.DirectOperateAsync(
            [Command.LatchOn(1), Command.LatchOn(6)], CancellationToken.None);

        // A multi-command request can partially succeed, and reporting that as
        // success would tell an operator a breaker operated when it did not.
        Assert.False(result.OK());
        Assert.Equal(2, result.Statuses.Count);
        Assert.Equal(CommandStatus.Success, result.Statuses[0]);
        Assert.Equal(CommandStatus.Blocked, result.Statuses[1]);
    }

    [Fact]
    public async Task RangeScanReadsOneGroup()
    {
        await using var pair = new TestPair();
        await pair.WaitConnectedAsync();

        pair.Outstation.Update(db =>
        {
            for (ushort i = 0; i < 8; i++)
            {
                db.UpdateAnalog(i, new Analog(i * 10.0, Flags.Online, Now()));
            }
        });

        await pair.Master.ScanRangeAsync(30, 0, 2, 5);

        for (ushort i = 2; i <= 5; i++)
        {
            var idx = i;
            Assert.Equal(idx * 10.0, pair.Handler.Read(h => h.Analogs[idx].Value));
        }
    }

    [Fact]
    public async Task ClockWriteClearsNeedTime()
    {
        await using var pair = new TestPair();
        await pair.WaitConnectedAsync();

        // A fresh outstation has never had its clock set.
        await pair.Master.IntegrityPollAsync();
        Assert.True(pair.Master.LastIin.Has(Iin.NeedTime));

        await pair.Master.SyncTimeAsync();
        await pair.Master.IntegrityPollAsync();

        Assert.False(pair.Master.LastIin.Has(Iin.NeedTime));
    }

    [Fact]
    public async Task RestartIndicationIsClearedByTheStartupSequence()
    {
        await using var pair = new TestPair(m =>
        {
            m.IntegrityOnStartup = true;
            m.DisableUnsolOnStartup = true;
        });

        await pair.WaitConnectedAsync();

        // The startup sequence writes to g80v1 index 7, which is how a master
        // tells an outstation it has seen the restart.
        await TestPair.WaitForAsync(
            () => !pair.Master.LastIin.Has(Iin.DeviceRestart) && pair.Master.LastIin != default,
            "the restart indication to clear");
    }

    [Fact]
    public async Task OctetStringsRoundTrip()
    {
        await using var pair = new TestPair();
        await pair.WaitConnectedAsync();

        pair.Outstation.Update(db =>
        {
            db.UpdateOctetString(0, "GO-DNP3 DEMO RTU"u8);
            db.UpdateOctetString(1, "v1.0"u8);
        });

        await pair.Master.IntegrityPollAsync();

        Assert.Equal("GO-DNP3 DEMO RTU"u8.ToArray(), pair.Handler.Read(h => h.OctetStrings[0]));
        Assert.Equal("v1.0"u8.ToArray(), pair.Handler.Read(h => h.OctetStrings[1]));
    }

    [Fact]
    public async Task AMultiFragmentResponseIsReassembled()
    {
        // A small fragment cap forces the outstation to split its answer, which
        // is the normal case for an integrity poll on a real device.
        await using var pair = new TestPair(
            configureOutstation: o =>
            {
                o.MaxTxFragment = 64;
                o.Database.Analog = 40;
            });

        await pair.WaitConnectedAsync();

        pair.Outstation.Update(db =>
        {
            for (ushort i = 0; i < 40; i++)
            {
                db.UpdateAnalog(i, new Analog(i * 100, Flags.Online, Now()));
            }
        });

        await pair.Master.IntegrityPollAsync();

        Assert.Equal(40, pair.Handler.Read(h => h.Analogs.Count));
        for (ushort i = 0; i < 40; i++)
        {
            var idx = i;
            Assert.Equal(idx * 100, pair.Handler.Read(h => h.Analogs[idx].Value));
        }
    }

    [Fact]
    public async Task UnsolicitedResponsesReachTheMaster()
    {
        await using var pair = new TestPair(
            m =>
            {
                m.IntegrityOnStartup = true;
                m.DisableUnsolOnStartup = false;
                m.UnsolClassMask = Class.Class123;
            },
            o =>
            {
                o.Unsolicited.Enabled = true;
                o.Unsolicited.HoldTime = TimeSpan.FromMilliseconds(20);
                o.Unsolicited.ConfirmTimeout = TimeSpan.FromSeconds(2);
            });

        await pair.WaitConnectedAsync();

        // Let the startup sequence enable unsolicited reporting first.
        await TestPair.WaitForAsync(
            () => pair.Outstation.Stats.UnsolicitedSent > 0,
            "the null unsolicited response");

        pair.Outstation.Update(db =>
            db.UpdateBinary(2, new Binary(true, Flags.Online, Now())));

        await TestPair.WaitForAsync(
            () => pair.Handler.Read(h => h.Binaries.ContainsKey(2) && h.Binaries[2].Value),
            "an unsolicited binary event",
            TimeSpan.FromSeconds(10));

        Assert.True(pair.Master.Stats.Unsolicited > 0);
    }

    [Fact]
    public async Task FreezeCopiesCountersIntoFrozenCounters()
    {
        await using var pair = new TestPair();
        await pair.WaitConnectedAsync();

        pair.Outstation.Update(db =>
            db.UpdateCounter(1, new Counter(4242, Flags.Online, Now())));

        // The freeze is issued by the outstation-side API here; the master's
        // freeze function codes are exercised by the conformance suite.
        pair.Outstation.Update(db => db.FreezeCounters());

        await pair.Master.IntegrityPollAsync();

        Assert.True(pair.Outstation.Database.TryGetFrozenCounter(1, out var frozen, out _));
        Assert.Equal(4242u, frozen.Value);
    }

    [Fact]
    public async Task DeadbandSuppressesSmallAnalogMoves()
    {
        await using var pair = new TestPair();
        await pair.WaitConnectedAsync();

        await pair.Master.WriteDeadbandAsync(new Dictionary<ushort, float> { [0] = 5.0f });

        // Written straight to the database rather than through Update: that
        // queues onto the session loop, and this test asserts on the event
        // buffer immediately afterwards. The database takes its own lock, so a
        // direct write is applied by the time it returns.
        var db = pair.Outstation.Database;

        db.UpdateAnalog(0, new Analog(100, Flags.Online, Now()));

        // Drain whatever the first update produced.
        await pair.Master.ScanClassesAsync(Class.Class123);

        var before = pair.Outstation.Events!.Total;

        // Inside the deadband: no event.
        db.UpdateAnalog(0, new Analog(102, Flags.Online, Now()));
        Assert.Equal(before, pair.Outstation.Events.Total);

        // Outside it: an event.
        db.UpdateAnalog(0, new Analog(120, Flags.Online, Now()));
        Assert.True(pair.Outstation.Events.Total > before);
    }
}
