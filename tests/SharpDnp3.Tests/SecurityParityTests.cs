// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SharpDnp3.App;
using SharpDnp3.Channels;
using SharpDnp3.Link;
using SharpDnp3.Master;
using SharpDnp3.Outstation;
using SharpDnp3.Security;

namespace SharpDnp3.Tests;

public sealed class SecurityParityTests
{
    [Fact]
    public async Task ReadOnlyPolicyStillAllowsPolling()
    {
        var events = new ConcurrentQueue<SecurityEvent>();
        await using (var pair = new TestPair(configureOutstation: c => c.Security = new()
        {
            Sink = events.Enqueue,
            Functions = new FunctionPolicy { ReadOnly = true },
        }))
        {
            await pair.WaitConnectedAsync();
            await pair.Master.IntegrityPollAsync();

            var result = await pair.Master.DirectOperateAsync(Command.Trip(3, 1000));
            Assert.Equal(CommandStatus.NotAuthorized, Assert.Single(result.Statuses));
            Assert.Empty(pair.Commands.Read(c => c.Operated));
        }

        var blocked = Assert.Single(events, e => e.Kind == SecurityEventKind.AccessFunctionBlocked);
        Assert.Equal(SecurityAction.Blocked, blocked.Action);
        Assert.Equal(3u, blocked.Index);
        Assert.Equal("outstation", blocked.Role);
        Assert.Equal("pipe", blocked.Transport);
    }

    [Fact]
    public async Task CommandsBeyondRateAreThrottledIndividually()
    {
        var events = new ConcurrentQueue<SecurityEvent>();
        await using (var pair = new TestPair(configureOutstation: c => c.Security = new()
        {
            Sink = events.Enqueue,
            FloodAction = SecurityAction.Throttled,
            Limits = new SecurityLimits { Commands = new(1, TimeSpan.FromMinutes(1)) },
        }))
        {
            await pair.WaitConnectedAsync();
            var result = await pair.Master.DirectOperateAsync(Command.Trip(0, 100), Command.Trip(1, 100));

            Assert.Equal(new[] { CommandStatus.Success, CommandStatus.TooManyOps }, result.Statuses);
            Assert.Equal(0, Assert.Single(pair.Commands.Read(c => c.Operated)).Index);
        }

        Assert.Contains(events, e => e.Kind == SecurityEventKind.FloodCommands && e.Action == SecurityAction.Throttled);
    }

    [Fact]
    public async Task UdpPinningRetainsItsFirstNonemptyPeer()
    {
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var channel = new UdpChannel(new UdpConfig { LocalAddr = "127.0.0.1:0", PinPeer = true });
        using var stream = await channel.ConnectAsync(ct.Token);
        using var peer = new UdpClient(AddressFamily.InterNetwork);
        using var extra = new UdpClient(AddressFamily.InterNetwork);
        var endpoint = channel.BoundAddress!;
        var read = new byte[1];

        await extra.SendAsync(ReadOnlyMemory<byte>.Empty, endpoint, ct.Token);
        await peer.SendAsync(new byte[] { 1 }, endpoint, ct.Token);
        Assert.Equal(1, await stream.ReadAsync(read, ct.Token));

        await extra.SendAsync(new byte[] { 2 }, endpoint, ct.Token);
        var reading = stream.ReadAsync(read, ct.Token).AsTask();
        await TestPair.WaitForAsync(() => channel.Stats.DroppedSources == 1, "foreign datagram");
        await peer.SendAsync(new byte[] { 3 }, endpoint, ct.Token);
        Assert.Equal(1, await reading);
        Assert.Equal(3, read[0]);
        Assert.Equal(new UdpStats(1, 1), channel.Stats);
    }

    [Fact]
    public async Task BusyServerRejectsExtraPeerAndAcceptsTheNextAfterRelease()
    {
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var channel = new TcpServerChannel("127.0.0.1:0") { Busy = BusyPolicy.Reject };
        var rejection = new TaskCompletionSource<SecurityEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var security = new SecurityRuntime(new SecurityPolicy
        {
            Sink = observation =>
            {
                if (observation.Kind == SecurityEventKind.ConnRejectedBusy)
                {
                    rejection.TrySetResult(observation);
                }
            },
        }, "outstation");
        var accepting = SecurityRuntime.ConnectAsync(channel, security, ct.Token);
        using var first = new TcpClient();
        await first.ConnectAsync(channel.BoundAddress!, ct.Token);
        using (await accepting)
        {
            using var extra = new TcpClient();
            try
            {
                await extra.ConnectAsync(channel.BoundAddress!, ct.Token);
                Assert.Equal(0, await extra.GetStream().ReadAsync(new byte[1], ct.Token));
            }
            catch (Exception ex) when (ex is SocketException or IOException { InnerException: SocketException })
            {
                // The OS can surface the immediate close during connect or read.
            }

            Assert.Equal(SecurityAction.Rejected, (await rejection.Task.WaitAsync(ct.Token)).Action);
        }

        accepting = channel.ConnectAsync(ct.Token);
        using var next = new TcpClient();
        await next.ConnectAsync(channel.BoundAddress!, ct.Token);
        using var accepted = await accepting;
        Assert.True(accepted.CanRead);
    }

    [Fact]
    public async Task OutstationReportsIdleProbeTimeout()
    {
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var channel = new TcpServerChannel("127.0.0.1:0");
        var timeout = new TaskCompletionSource<SecurityEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var station = new OutstationSession(new OutstationConfig
        {
            LocalAddr = 10,
            RemoteAddr = 1,
            IdleTimeout = TimeSpan.FromMilliseconds(100),
            LinkTimeout = TimeSpan.FromMilliseconds(100),
            Security = new() { Sink = e =>
            {
                if (e.Kind == SecurityEventKind.ConnIdleTimeout)
                {
                    timeout.TrySetResult(e);
                }
            } },
        });

        var run = station.RunAsync(channel, ct.Token);
        try
        {
            using var peer = new TcpClient();
            await peer.ConnectAsync(channel.BoundAddress!, ct.Token);
            var observed = await timeout.Task.WaitAsync(ct.Token);
            Assert.Equal(SecurityAction.Disconnected, observed.Action);
            Assert.Equal("tcp", observed.Transport);
        }
        finally
        {
            ct.Cancel();
            await run;
        }
    }

    [Fact]
    public async Task MacBindingRefusalUsesTheConfiguredResolver()
    {
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var channel = new TcpServerChannel("127.0.0.1:0");
        var refusal = new TaskCompletionSource<SecurityEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var station = new OutstationSession(new OutstationConfig
        {
            LocalAddr = 10,
            RemoteAddr = 1,
            Security = new()
            {
                Resolver = (_, _) => ValueTask.FromResult<SecurityNeighbor?>(
                    new SecurityNeighbor(PhysicalAddress.Parse("020000000002"))),
                PeerBindings = new Dictionary<IPAddress, PhysicalAddress>
                {
                    [IPAddress.Loopback] = PhysicalAddress.Parse("020000000001"),
                },
                RejectBindingMismatch = true,
                MacCache = new SecurityPeerCache(),
                Sink = e =>
                {
                    if (e.Kind == SecurityEventKind.ConnUnexpectedMAC)
                    {
                        refusal.TrySetResult(e);
                    }
                },
            },
        });

        var run = station.RunAsync(channel, ct.Token);
        try
        {
            using var peer = new TcpClient();
            await peer.ConnectAsync(channel.BoundAddress!, ct.Token);
            var observed = await refusal.Task.WaitAsync(ct.Token);
            Assert.Equal("020000000002", observed.Peer?.Mac);
            Assert.Equal(SecurityAction.Rejected, observed.Action);
            Assert.Equal(0u, station.Stats.Connections);
        }
        finally
        {
            ct.Cancel();
            await run;
        }
    }

    [Fact]
    public async Task StalledFrameReleasesTheOutstationConnection()
    {
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var channel = new TcpServerChannel("127.0.0.1:0");
        var timeout = new TaskCompletionSource<SecurityEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var station = new OutstationSession(new OutstationConfig
        {
            LocalAddr = 10,
            RemoteAddr = 1,
            FrameAssemblyTimeout = TimeSpan.FromMilliseconds(150),
            Security = new() { Sink = e =>
            {
                if (e.Kind == SecurityEventKind.DoSSlowFrame)
                {
                    timeout.TrySetResult(e);
                }
            } },
        });
        var run = station.RunAsync(channel, ct.Token);
        try
        {
            using (var peer = new TcpClient())
            {
                await peer.ConnectAsync(channel.BoundAddress!, ct.Token);
                const ushort destination = 10;
                var frame = FrameCodec.Encode(new LinkHeader(
                    new Control(true, true, false, false, LinkFunction.UnconfirmedUserData), destination, 1, 0),
                    [0xC0, 0xC0, (byte)FuncCode.Read, 60, 1, 6]);
                await peer.GetStream().WriteAsync(frame.AsMemory(0, LinkConstants.HeaderSize), ct.Token);

                Assert.Equal(SecurityAction.Disconnected, (await timeout.Task.WaitAsync(ct.Token)).Action);
            }

            using var next = new TcpClient();
            await next.ConnectAsync(channel.BoundAddress!, ct.Token);
            await TestPair.WaitForAsync(() => station.Stats.Connections == 2, "the next connection");
        }
        finally
        {
            ct.Cancel();
            await run;
        }
    }

    [Fact]
    public async Task AuthMaterialIsRedactedBeforeTruncatingSamples()
    {
        List<SecurityEvent> events = [];
        using var channel = new TcpServerChannel("127.0.0.1:0");
        using var stream = new MemoryStream();
        await using (var runtime = new SecurityRuntime(new SecurityPolicy
        {
            Sink = events.Add,
            MaxRaw = 4,
            DispatcherOptions = new() { AggregateWindow = TimeSpan.FromTicks(-1) },
        }, "master"))
        {
            var observation = runtime.ForConnection(channel, stream);
            observation.Emit(new(SecurityEventKind.ProtoAppMalformed), [0xC0, (byte)FuncCode.Read, 60, 1, 6]);
            observation.Emit(new(SecurityEventKind.ProtoAppMalformed), [0xC1, (byte)FuncCode.AuthenticateFile, 70, 2]);
            observation.Emit(new(SecurityEventKind.ProtoAppMalformed),
                [0xC2, (byte)FuncCode.Write, 50, 1, 7, 0, 120, 2]);
        }

        Assert.Equal(3, events.Count);
        var first = Assert.Single(events, e => e.Raw is not null);
        Assert.Equal(new byte[] { 0xC0, (byte)FuncCode.Read, 60, 1 }, first.Raw);
        Assert.All(events.Where(e => e != first), e => Assert.Null(e.Raw));
    }

    [Fact]
    public async Task PeerHistoryPersistsAcrossCacheInstances()
    {
        using var store = new MemoryStream();
        var original = new SecurityPeerCache();
        var address = IPAddress.Parse("192.0.2.4");
        Assert.Empty(original.Observe(address, "020000000001", "first", 10));
        original.Save(store);
        store.Position = 0;

        var restored = new SecurityPeerCache();
        await restored.LoadAsync(store);

        Assert.Equal(new[] { SecurityEventKind.PeerMACChanged, SecurityEventKind.TLSCertChanged },
            restored.Observe(address, "020000000002", "second", 10));
    }

    [Fact]
    public async Task DispatcherDrainsAndAggregatesRepeats()
    {
        var events = new List<SecurityEvent>();
        var dispatcher = new SecurityDispatcher(events.Add);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(dispatcher.Submit(new(SecurityEventKind.LinkGarbage) { ConnId = 1 }));
        }

        await dispatcher.DisposeAsync();

        Assert.Collection(events,
            e => Assert.Equal(1, e.Count),
            e =>
            {
                Assert.Equal(2, e.Count);
                Assert.NotNull(e.FirstSeen);
                Assert.Equal("link", e.Category);
            });
    }
}
