// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// One outstation, several masters, all of them real sessions talking through
// PipeListener — the same link, transport and application layers a socket would
// carry, with no socket.
//
// What is being proved here is mostly about isolation. Two masters share a
// device and share nothing else, and every way that could go wrong is a way an
// outstation silently loses data: events one master acknowledges disappearing
// from the other's queue, a select from one being operable by the other, an
// unsolicited response addressed to whichever master happens to be in the
// configuration.

using SharpDnp3.Channels;
using SharpDnp3.Master;
using SharpDnp3.Outstation;

namespace SharpDnp3.Tests;

/// <summary>Runs one outstation with as many masters attached as a test wants.</summary>
internal sealed class MultiMasterFixture : IAsyncDisposable
{
    private const ushort OutstationAddr = 10;

    private readonly CancellationTokenSource _cts = new();
    private readonly PipeListener _listener = new();
    private readonly Task _outstationTask;
    private readonly List<IChannel> _channels = [];
    private readonly List<Task> _masterTasks = [];

    public OutstationSession Outstation { get; }

    public RecordingCommandHandler Commands { get; }

    public MultiMasterFixture(Action<OutstationConfig>? configureOutstation = null)
    {
        var config = new OutstationConfig
        {
            LocalAddr = OutstationAddr,
            RemoteAddr = 1,
            MaxMasters = 4,
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
        configureOutstation?.Invoke(config);

        Commands = new RecordingCommandHandler();
        Outstation = new OutstationSession(config, null, Commands);
        _outstationTask = Outstation.RunAsync(_listener.Server, _cts.Token);
    }

    /// <summary>Attaches one more master, on its own connection and link address.</summary>
    public Peer AddMaster(ushort address, Action<MasterConfig>? configure = null)
    {
        var config = new MasterConfig
        {
            LocalAddr = address,
            RemoteAddr = OutstationAddr,
            ResponseTimeout = TimeSpan.FromSeconds(5),
        };
        configure?.Invoke(config);

        var handler = new RecordingHandler();
        var session = new MasterSession(config, handler);
        var channel = _listener.Connect();

        // Its own token, so a test can drop one master without disturbing the
        // rest — which is the case worth testing.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

        _channels.Add(channel);
        _masterTasks.Add(session.RunAsync(channel, cts.Token));

        return new Peer(session, handler, cts, channel);
    }

    /// <summary>Waits until every master given has reported a connection.</summary>
    public static async Task WaitConnectedAsync(params Peer[] peers)
    {
        foreach (var p in peers)
        {
            await TestPair.WaitForAsync(() => p.Session.Connected, "a master to connect");
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Cancel first and let the loops unwind before anything is disposed:
        // tearing the streams out from under a session in flight surfaces as a
        // disposal exception rather than a shutdown.
        await _cts.CancelAsync();

        try
        {
            await Task.WhenAll([_outstationTask, .. _masterTasks]).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or TimeoutException or Dnp3Exception
                or IOException or ObjectDisposedException)
        {
            // Shutting down.
        }

        foreach (var c in _channels)
        {
            c.Dispose();
        }

        _listener.Dispose();
        _cts.Dispose();
    }

    /// <summary>One attached master and what it has been told.</summary>
    internal sealed record Peer(
        MasterSession Session,
        RecordingHandler Handler,
        CancellationTokenSource Cancellation,
        IChannel Channel)
    {
        /// <summary>Drops this master's connection, leaving the others alone.</summary>
        public void Disconnect()
        {
            Channel.Close();
            Cancellation.Cancel();
        }
    }
}

public class MultiMasterTests
{
    private static Timestamp Now() => Timestamp.Now(DateTimeOffset.UtcNow);

    [Fact]
    public async Task SeveralMastersAreServedAtOnce()
    {
        await using var fx = new MultiMasterFixture();

        var a = fx.AddMaster(1);
        var b = fx.AddMaster(2);
        var c = fx.AddMaster(3);
        await MultiMasterFixture.WaitConnectedAsync(a, b, c);

        await TestPair.WaitForAsync(
            () => fx.Outstation.MastersAttached == 3, "three masters to attach");

        fx.Outstation.Update(db => db.UpdateAnalog(4, new Analog(77, Flags.Online, Now())));

        await a.Session.IntegrityPollAsync();
        await b.Session.IntegrityPollAsync();
        await c.Session.IntegrityPollAsync();

        foreach (var peer in new[] { a, b, c })
        {
            Assert.Equal(77, peer.Handler.Read(h => h.Analogs[4].Value), 3);
        }

        Assert.Equal(3, fx.Outstation.Stats.PeakMastersAttached);
    }

    [Fact]
    public async Task OneMasterConfirmingEventsDoesNotTakeThemFromAnother()
    {
        await using var fx = new MultiMasterFixture();

        var a = fx.AddMaster(1);
        var b = fx.AddMaster(2);
        await MultiMasterFixture.WaitConnectedAsync(a, b);
        await TestPair.WaitForAsync(
            () => fx.Outstation.MastersAttached == 2, "both masters to attach");

        fx.Outstation.Update(db => db.UpdateBinary(3, new Binary(true, Flags.Online, Now())));

        // The first master takes the event and acknowledges it, which is what
        // drops it from that master's queue. If the queue were shared, the
        // second master would poll an empty one and never learn the point
        // changed — the failure this whole design exists to prevent.
        await a.Session.ScanClassesAsync(Class.Class123);
        await b.Session.ScanClassesAsync(Class.Class123);

        Assert.Contains(a.Handler.Read(h => h.BinaryEvents), e => e.Index == 3 && e.Value.Value);
        Assert.Contains(b.Handler.Read(h => h.BinaryEvents), e => e.Index == 3 && e.Value.Value);
    }

    [Fact]
    public async Task EventsBufferedWhileNothingIsAttachedGoToTheMasterThatAttaches()
    {
        await using var fx = new MultiMasterFixture();

        // Nothing is connected yet, so this has nowhere to go but the queue an
        // attaching master inherits.
        fx.Outstation.Update(db => db.UpdateBinary(5, new Binary(true, Flags.Online, Now())));
        await TestPair.WaitForAsync(
            () => fx.Outstation.Events!.Total == 1, "the event to be queued while offline");

        var a = fx.AddMaster(1);
        await MultiMasterFixture.WaitConnectedAsync(a);

        await a.Session.ScanClassesAsync(Class.Class123);

        Assert.Contains(a.Handler.Read(h => h.BinaryEvents), e => e.Index == 5 && e.Value.Value);
    }

    [Fact]
    public async Task TheLastMasterToLeaveReturnsItsUnsentEvents()
    {
        await using var fx = new MultiMasterFixture();

        var a = fx.AddMaster(1);
        await MultiMasterFixture.WaitConnectedAsync(a);
        await TestPair.WaitForAsync(() => fx.Outstation.MastersAttached == 1, "the master to attach");

        fx.Outstation.Update(db => db.UpdateBinary(6, new Binary(true, Flags.Online, Now())));
        await TestPair.WaitForAsync(
            () => fx.Outstation.Events!.Total == 1, "the event to be queued");

        // Dropping the connection must not drop the event with it: the master
        // never saw it, and losing it here is exactly the silent hole the event
        // buffer exists to prevent.
        a.Disconnect();

        await TestPair.WaitForAsync(
            () => fx.Outstation.MastersAttached == 0, "the master to detach");
        await TestPair.WaitForAsync(
            () => fx.Outstation.Events!.Total == 1, "the event to survive the disconnection");
    }

    [Fact]
    public async Task SelectBeforeOperateIsPrivateToEachMaster()
    {
        await using var fx = new MultiMasterFixture();

        var a = fx.AddMaster(1);
        var b = fx.AddMaster(2);
        await MultiMasterFixture.WaitConnectedAsync(a, b);

        // Two masters running select-before-operate against the same outstation
        // at the same time. With one shared reservation the interleaved selects
        // overwrite each other and the operates come back NO_SELECT; with one
        // per master, every round succeeds.
        static async Task RunRoundsAsync(MultiMasterFixture.Peer peer, ushort index)
        {
            for (var i = 0; i < 20; i++)
            {
                var result = await peer.Session.SelectAndOperateAsync(Command.LatchOn(index));
                Assert.Null(result.Error());
            }
        }

        await Task.WhenAll(RunRoundsAsync(a, 0), RunRoundsAsync(b, 1));

        Assert.Equal(40, fx.Commands.Read(c => c.Operated.Count));
    }

    [Fact]
    public async Task EveryMasterEnablesAndReceivesItsOwnUnsolicitedResponses()
    {
        await using var fx = new MultiMasterFixture(o =>
        {
            o.Unsolicited.Enabled = true;
            o.Unsolicited.HoldTime = TimeSpan.FromMilliseconds(20);
            o.Unsolicited.ConfirmTimeout = TimeSpan.FromSeconds(2);
        });

        static void Configure(MasterConfig m)
        {
            m.IntegrityOnStartup = true;
            m.DisableUnsolOnStartup = false;
            m.UnsolClassMask = Class.Class123;
        }

        var a = fx.AddMaster(1, Configure);
        var b = fx.AddMaster(2, Configure);
        await MultiMasterFixture.WaitConnectedAsync(a, b);

        // Both masters must get through their startup sequence first: an
        // unsolicited response before the null one is confirmed goes nowhere.
        await TestPair.WaitForAsync(
            () => fx.Outstation.Stats.UnsolicitedSent >= 2,
            "a null unsolicited response for each master");

        fx.Outstation.Update(db => db.UpdateBinary(2, new Binary(true, Flags.Online, Now())));

        // Each master's response is addressed to its own link address. Sending
        // both to the configured one would leave the second master silent while
        // the first saw the event twice.
        await TestPair.WaitForAsync(
            () => a.Handler.Read(h => h.Binaries.TryGetValue(2, out var v) && v.Value) &&
                  b.Handler.Read(h => h.Binaries.TryGetValue(2, out var v) && v.Value),
            "an unsolicited binary event at both masters",
            TimeSpan.FromSeconds(10));

        Assert.True(a.Session.Stats.Unsolicited > 0);
        Assert.True(b.Session.Stats.Unsolicited > 0);
    }

    [Fact]
    public async Task ARestartIsReportedToEveryMasterSeparately()
    {
        await using var fx = new MultiMasterFixture();

        var a = fx.AddMaster(1, m => m.IntegrityOnStartup = true);
        var b = fx.AddMaster(2, m => m.IntegrityOnStartup = true);
        await MultiMasterFixture.WaitConnectedAsync(a, b);

        // Both masters answer the restart the outstation asserts at startup by
        // clearing it, so what is counted below is the new restart only.
        await TestPair.WaitForAsync(
            () => !a.Handler.Read(h => h.LastIin).Has(Iin.DeviceRestart) &&
                  !b.Handler.Read(h => h.LastIin).Has(Iin.DeviceRestart),
            "both masters to clear the startup restart indication");

        var seenByA = a.Session.Stats.RestartsSeen;
        var seenByB = b.Session.Stats.RestartsSeen;

        fx.Outstation.Restart();

        // The first master polls, is told, and clears the indication it was
        // given. The second has not polled yet, so it must still be told when
        // it does: the clear is a handshake with one master and says nothing
        // about whether another has re-baselined.
        await a.Session.IntegrityPollAsync();
        await TestPair.WaitForAsync(
            () => a.Session.Stats.RestartsSeen > seenByA, "the first master to see the restart");

        await b.Session.IntegrityPollAsync();
        await TestPair.WaitForAsync(
            () => b.Session.Stats.RestartsSeen > seenByB, "the second master to see the restart");
    }

    [Fact]
    public async Task ACommandHandlerCanUpdateTheDatabaseFromInsideAControl()
    {
        await using var fx = new MultiMasterFixture();

        var a = fx.AddMaster(1);
        var b = fx.AddMaster(2);
        await MultiMasterFixture.WaitConnectedAsync(a, b);

        // Closing the loop from the handler is the documented pattern: the
        // output moves and so does the status point the master reads back. It
        // re-enters the database lock the request is already being answered
        // under, which has to be allowed rather than deadlock.
        fx.Commands.OnOperate = (index, crob) =>
        {
            var on = crob.Code.OpType() == ControlCode.LatchOn;
            fx.Outstation.Update(db => db.UpdateBinaryOutputStatus(
                index, new BinaryOutputStatus(on, Flags.Online, Now())));
        };

        var result = await a.Session.DirectOperateAsync(Command.LatchOn(2));
        Assert.Null(result.Error());

        // The second master reads the movement back, because the point is the
        // device's and not the conversation's. No waiting loop: the update was
        // queued before the first master's response went out, so it is applied
        // before the second master's poll is answered.
        await b.Session.IntegrityPollAsync();

        Assert.True(b.Handler.Read(h => h.BinaryOutputs[2].Value));
    }

    [Fact]
    public async Task AMasterPastTheLimitIsTurnedAwayRatherThanLeftHanging()
    {
        await using var fx = new MultiMasterFixture(o => o.MaxMasters = 2);

        var a = fx.AddMaster(1);
        var b = fx.AddMaster(2);
        await MultiMasterFixture.WaitConnectedAsync(a, b);
        await TestPair.WaitForAsync(
            () => fx.Outstation.MastersAttached == 2, "both masters to attach");

        // The third finds the outstation full. Refusing it is the point: a
        // connection that is accepted and then never answered looks to the
        // master like an outstation that has gone mute.
        fx.AddMaster(3);

        await TestPair.WaitForAsync(
            () => fx.Outstation.Stats.MastersRefused > 0, "the third master to be refused");

        Assert.Equal(2, fx.Outstation.MastersAttached);
        Assert.True(a.Session.Connected);
        Assert.True(b.Session.Connected);
    }

    [Fact]
    public async Task ServingSeveralMastersNeedsAListeningChannel()
    {
        var config = new OutstationConfig
        {
            LocalAddr = 10,
            RemoteAddr = 1,
            MaxMasters = 4,
        };

        var session = new OutstationSession(config);
        var (a, _) = Pipe.Create();

        // A channel that dials produces the same peer every time. Serving one
        // master over it would be a quiet downgrade of what was configured.
        await Assert.ThrowsAsync<BadConfigException>(() => session.RunAsync(a));
    }
}
