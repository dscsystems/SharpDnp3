// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// One line, several stations.
//
// The end-to-end tests here put a bus at each end of a pipe: two masters on one
// side, two outstations on the other, all sharing one stream. That is the
// arrangement a serial multi-drop line actually has, and it is the only way to
// prove the two things the bus exists to do — route each frame to the station it
// belongs to, and keep two masters' exchanges from overlapping.

using SharpDnp3.Channels;
using SharpDnp3.Link;
using SharpDnp3.Master;
using SharpDnp3.Multidrop;
using SharpDnp3.Outstation;

namespace SharpDnp3.Tests;

public class MultidropTests
{
    private static LinkFrame Frame(ushort src, ushort dest, byte[]? payload = null) => new()
    {
        Header = new LinkHeader(
            Control: new Control(
                Dir: true, Prm: true, Fcb: false, Fcv: false,
                Func: LinkFunction.UnconfirmedUserData),
            Dest: dest,
            Src: src,
            Length: (byte)(LinkConstants.MinLength + (payload?.Length ?? 0))),
        Payload = payload ?? ReadOnlyMemory<byte>.Empty,
    };

    // ---- Routing ----

    /// <summary>
    /// Masters on one line normally share a link address, so the source is what
    /// separates one outstation's reply from another's.
    /// </summary>
    [Fact]
    public void MasterMatchesOnTheSourceAddress()
    {
        var s = new Station { LocalAddr = 1, RemoteAddr = 10, IsMaster = true };

        Assert.True(s.Matches(Frame(src: 10, dest: 1).Header));
        Assert.False(s.Matches(Frame(src: 11, dest: 1).Header));
        Assert.False(s.Matches(Frame(src: 10, dest: 2).Header));
    }

    /// <summary>
    /// An outstation answers whichever master addressed it, so the destination
    /// decides on its own — and a broadcast is addressed to it too.
    /// </summary>
    [Fact]
    public void OutstationMatchesOnTheDestinationAddress()
    {
        var s = new Station { LocalAddr = 10, RemoteAddr = 1, IsMaster = false };

        Assert.True(s.Matches(Frame(src: 1, dest: 10).Header));
        Assert.True(s.Matches(Frame(src: 2, dest: 10).Header));
        Assert.False(s.Matches(Frame(src: 1, dest: 11).Header));
        Assert.True(s.Matches(Frame(src: 1, dest: LinkConstants.BroadcastNoConfirm).Header));
    }

    /// <summary>A master is not the addressee of a broadcast.</summary>
    [Fact]
    public void MasterDoesNotMatchBroadcasts()
    {
        var s = new Station { LocalAddr = 1, RemoteAddr = 10, IsMaster = true };
        Assert.False(
            s.Matches(Frame(src: 10, dest: LinkConstants.BroadcastNoConfirm).Header));
    }

    // ---- Station admission ----

    /// <summary>
    /// Two stations a frame could match both of are unroutable, so the second
    /// is refused rather than silently shadowing the first.
    /// </summary>
    [Fact]
    public void ConflictingStationsAreRefused()
    {
        var (a, _) = Pipe.Create();
        using var bus = new Bus(a);

        bus.Add(new Station { LocalAddr = 1, RemoteAddr = 10, IsMaster = true });

        // The same master-to-outstation pair again.
        Assert.Throws<BadConfigException>(() =>
            bus.Add(new Station { LocalAddr = 1, RemoteAddr = 10, IsMaster = true }));

        // An outstation accepts every source, so it cannot share an address
        // with anything.
        Assert.Throws<BadConfigException>(() =>
            bus.Add(new Station { LocalAddr = 1, RemoteAddr = 99, IsMaster = false }));
    }

    /// <summary>Masters polling different outstations are distinguishable.</summary>
    [Fact]
    public void MastersOnOneAddressPollingDifferentStationsAreAllowed()
    {
        var (a, _) = Pipe.Create();
        using var bus = new Bus(a);

        bus.Add(new Station { LocalAddr = 1, RemoteAddr = 10, IsMaster = true });
        bus.Add(new Station { LocalAddr = 1, RemoteAddr = 11, IsMaster = true });
        bus.Add(new Station { LocalAddr = 1, RemoteAddr = 12, IsMaster = true });
    }

    /// <summary>An address no station may hold is refused as a local address.</summary>
    [Theory]
    [InlineData(LinkConstants.BroadcastNoConfirm)]
    [InlineData((ushort)0xFFF5)]
    public void UnusableLocalAddressesAreRefused(ushort address)
    {
        var (a, _) = Pipe.Create();
        using var bus = new Bus(a);

        Assert.Throws<BadConfigException>(() =>
            bus.Add(new Station { LocalAddr = address, RemoteAddr = 10, IsMaster = true }));
    }

    // ---- The turn-taking rules ----

    /// <summary>
    /// A frame that leaves the line owed an answer is one the sender must keep
    /// the line for; one that does not must release it at once, or a confirmed
    /// link would idle a whole turnaround after every acknowledgement.
    /// </summary>
    [Fact]
    public void OnlyFramesThatExpectAnAnswerHoldTheLine()
    {
        // A read request: answered.
        Assert.True(StationConnection.ExpectsReply(
            FrameCodec.Encode(
                Frame(src: 1, dest: 10, payload: [0xC0, 0xC1, 0x01]).Header,
                [0xC0, 0xC1, 0x01])));

        // An application confirm: nothing comes back.
        Assert.False(StationConnection.ExpectsReply(
            FrameCodec.Encode(
                Frame(src: 1, dest: 10, payload: [0xC0, 0xC1, 0x00]).Header,
                [0xC0, 0xC1, 0x00])));

        // A no-reply control: nothing comes back.
        Assert.False(StationConnection.ExpectsReply(
            FrameCodec.Encode(
                Frame(src: 1, dest: 10, payload: [0xC0, 0xC1, 0x06]).Header,
                [0xC0, 0xC1, 0x06])));

        // A broadcast: every outstation staying silent is the whole point.
        Assert.False(StationConnection.ExpectsReply(
            FrameCodec.Encode(
                Frame(src: 1, dest: LinkConstants.BroadcastNoConfirm,
                    payload: [0xC0, 0xC1, 0x01]).Header,
                [0xC0, 0xC1, 0x01])));
    }

    /// <summary>
    /// A secondary frame is itself a reply. Holding the line for one would cost
    /// a turnaround on every link-layer acknowledgement.
    /// </summary>
    [Fact]
    public void SecondaryFramesDoNotHoldTheLine()
    {
        var ack = FrameCodec.Encode(
            new LinkHeader(
                Control: new Control(
                    Dir: false, Prm: false, Fcb: false, Fcv: false, LinkFunction.Ack),
                Dest: 10,
                Src: 1,
                Length: LinkConstants.MinLength),
            []);

        Assert.False(StationConnection.ExpectsReply(ack));
    }

    /// <summary>
    /// A link-layer request carrying no user data — a reset, a status request —
    /// is always answered.
    /// </summary>
    [Fact]
    public void LinkLayerRequestsHoldTheLine()
    {
        var reset = FrameCodec.Encode(
            new LinkHeader(
                Control: new Control(
                    Dir: true, Prm: true, Fcb: false, Fcv: false,
                    LinkFunction.ResetLinkStates),
                Dest: 10,
                Src: 1,
                Length: LinkConstants.MinLength),
            []);

        Assert.True(StationConnection.ExpectsReply(reset));
    }

    // ---- End to end ----

    /// <summary>
    /// Two masters and two outstations sharing one stream. Each master must see
    /// only its own outstation's data, which is the whole job of the bus.
    /// </summary>
    [Fact]
    public async Task TwoMastersShareOneLine()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (lineA, lineB) = Pipe.Create();
        using var masterBus = new Bus(lineA, new BusConfig
        {
            Turnaround = TimeSpan.FromMilliseconds(500),
        });
        using var outstationBus = new Bus(lineB, new BusConfig
        {
            // The outstation side never takes the line, so arbitration there is
            // only overhead.
            Turnaround = TimeSpan.FromMilliseconds(-1),
        });

        var tasks = new List<Task>();
        var handlers = new Dictionary<ushort, ChannelHandler>();
        var masters = new Dictionary<ushort, MasterSession>();

        foreach (var addr in new ushort[] { 10, 11 })
        {
            // The outstation, carrying a value only it reports.
            var outstation = new OutstationSession(new OutstationConfig
            {
                LocalAddr = addr,
                RemoteAddr = 1,
                Database = new DatabaseConfig { Analog = 2, DefaultClass = Class.Class1 },
            });

            outstation.Update(db => db.UpdateAnalog(
                0, new Analog(addr * 100.0, Flags.Online, default)));

            var oc = outstationBus.Add(new Station
            {
                LocalAddr = addr, RemoteAddr = 1, IsMaster = false,
            });
            tasks.Add(outstation.RunAsync(oc, cts.Token));

            // The master that polls it.
            var handler = new ChannelHandler();
            handlers[addr] = handler;

            var master = new MasterSession(
                new MasterConfig { LocalAddr = 1, RemoteAddr = addr }, handler);
            masters[addr] = master;

            var mc = masterBus.Add(new Station
            {
                LocalAddr = 1, RemoteAddr = addr, IsMaster = true,
            });
            tasks.Add(master.RunAsync(mc, cts.Token));
        }

        try
        {
            foreach (var (_, m) in masters)
            {
                await WaitForAsync(() => m.Connected, "both masters to connect");
            }

            // Both poll at once, which is exactly the overlap the arbiter has
            // to prevent from becoming a collision.
            await Task.WhenAll(
                masters[10].IntegrityPollAsync(cts.Token),
                masters[11].IntegrityPollAsync(cts.Token));

            foreach (var addr in new ushort[] { 10, 11 })
            {
                var value = await ReadAnalogAsync(handlers[addr], cts.Token);
                Assert.Equal(addr * 100.0, value);
            }

            var stats = masterBus.Stats;
            Assert.True(stats.FramesRouted > 0);
            Assert.Equal(0u, stats.FramesDropped);
            Assert.Equal(0u, stats.HeaderCrcErrors);
            Assert.Equal(0u, stats.BodyCrcErrors);
        }
        finally
        {
            await cts.CancelAsync();
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(3000));
        }
    }

    private static async Task<double> ReadAnalogAsync(
        ChannelHandler handler, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        await foreach (var u in handler.Updates.ReadAllAsync(timeout.Token))
        {
            if (u.Type == PointType.Analog)
            {
                return u.Analog.Value;
            }
        }

        throw new InvalidOperationException("no analog update arrived");
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail($"{what} did not happen within 10s");
    }

    // ---- The registry ----

    /// <summary>
    /// Two callers asking for the same channel get the same bus, so the line is
    /// opened once however many parts of a program reach for it.
    /// </summary>
    [Fact]
    public void RegistryReusesABusForAnEquivalentChannel()
    {
        var registry = new BusRegistry();

        var (a, _) = Pipe.Create();
        var first = registry.Open(a);
        var second = registry.Open(a);

        Assert.Same(first, second);
        Assert.Equal(1, registry.Count);

        // Closing on the first release would drop the line out from under the
        // caller still using it.
        registry.Release(first);
        Assert.Equal(1, registry.Count);

        registry.Release(second);
        Assert.Equal(0, registry.Count);
    }

    /// <summary>Distinct channels get distinct buses.</summary>
    [Fact]
    public void RegistryKeepsDistinctChannelsApart()
    {
        var registry = new BusRegistry();

        var (a, _) = Pipe.Create();
        var (b, _) = Pipe.Create();

        var first = registry.Open(a);
        var second = registry.Open(b);

        Assert.NotSame(first, second);
        Assert.Equal(2, registry.Count);

        registry.Release(first);
        registry.Release(second);
        Assert.Equal(0, registry.Count);
    }

    /// <summary>A bus the registry never handed out cannot be released to it.</summary>
    [Fact]
    public void RegistryRefusesABusItDidNotOpen()
    {
        var registry = new BusRegistry();

        var (a, _) = Pipe.Create();
        using var stranger = new Bus(a);

        Assert.Throws<BadConfigException>(() => registry.Release(stranger));
    }
}
