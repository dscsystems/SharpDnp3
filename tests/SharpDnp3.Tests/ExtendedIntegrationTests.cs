// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Master and outstation together: command events, the LAN clock procedure,
// freezes, relative-time events and the master's handling of a peer that
// misbehaves. Each mirrors a test in go-dnp3.

using SharpDnp3.App;
using SharpDnp3.Channels;
using SharpDnp3.Master;
using SharpDnp3.Objects;
using SharpDnp3.Outstation;
using SharpDnp3.Stack;

namespace SharpDnp3.Tests;

internal sealed class CollectingHandler : RecordingHandler, ICommandEventHandler
{
    private readonly Lock _events = new();

    public List<(uint Index, CommandEvent Event)> CommandEvents { get; } = [];

    public void HandleCommandEvent(HeaderInfo info, IReadOnlyList<Indexed<CommandEvent>> values)
    {
        lock (_events)
        {
            foreach (var v in values)
            {
                CommandEvents.Add((v.Index, v.Value));
            }
        }
    }

    public int CommandEventCount
    {
        get
        {
            lock (_events)
            {
                return CommandEvents.Count;
            }
        }
    }
}

public class ExtendedIntegrationTests
{
    /// <summary>A control operated on the outstation reaches the master as a command event, whatever the handler answered.</summary>
    [Fact]
    public async Task CommandEventsReachTheMaster()
    {
        var (masterSide, outstationSide) = Pipe.Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var outstation = new OutstationSession(new OutstationConfig
        {
            LocalAddr = 10,
            RemoteAddr = 1,
            Database = new DatabaseConfig { Binary = 1, BinaryOutputStatus = 2, DefaultClass = Class.None },
        });
        outstation.Update(db => db.Configure(
            PointType.BinaryOutputStatus, 1, new PointConfig { Class = Class.None, CommandEventClass = Class.Class1 }));

        var handler = new CollectingHandler();
        var master = new MasterSession(new MasterConfig { LocalAddr = 1, RemoteAddr = 10 }, handler);
        var o = outstation.RunAsync(outstationSide, cts.Token);
        var m = master.RunAsync(masterSide, cts.Token);
        try
        {
            await TestPair.WaitForAsync(() => master.Connected, "connect");

            // The default handler refuses everything, and the refusal is recorded too.
            var result = await master.DirectOperateAsync([Command.LatchOn(1)], cts.Token);
            Assert.False(result.OK());
            await master.ScanClassesAsync(Class.Class123, cts.Token);

            Assert.Equal(1, handler.CommandEventCount);
            var (index, e) = handler.CommandEvents[0];
            Assert.Equal(1u, index);
            Assert.True(e.State);
            Assert.Equal(CommandStatus.NotSupported, e.Status);
        }
        finally
        {
            await cts.CancelAsync();
            masterSide.Close();
            outstationSide.Close();
            await Task.WhenAny(Task.WhenAll(o, m), Task.Delay(2000));
        }
    }

    /// <summary>The LAN clock procedure, FREEZE_CLEAR and FREEZE_AT_TIME work end to end.</summary>
    [Fact]
    public async Task MasterTimeSyncAndFreezeProcedures()
    {
        await using var pair = new TestPair(null, o =>
        {
            o.Database.Counter = 2;
            o.Database.FrozenCounter = 2;
            o.Database.DefaultClass = Class.None;
        });
        await pair.WaitConnectedAsync();

        await pair.Master.SyncTimeRecordedAsync();

        pair.Outstation.Update(db => db.UpdateCounter(0, new Counter(7, Flags.Online, default)));
        await TestPair.WaitForAsync(
            () => pair.Outstation.Database.TryGetCounter(0, out var c, out _) && c.Value == 7, "the counter");

        await pair.Master.FreezeCountersAsync(FreezeMode.FreezeAndClear);
        pair.Outstation.Database.TryGetFrozenCounter(0, out var f, out _);
        Assert.Equal(7u, f.Value);
        pair.Outstation.Database.TryGetCounter(0, out var c0, out _);
        Assert.Equal(0u, c0.Value);

        pair.Outstation.Update(db => db.UpdateCounter(1, new Counter(9, Flags.Online, default)));
        await pair.Master.FreezeAtTimeAsync(DateTimeOffset.UtcNow.AddMilliseconds(150));
        await TestPair.WaitForAsync(
            () => pair.Outstation.Database.TryGetFrozenCounter(1, out var f1, out _) && f1.Value == 9, "the scheduled freeze");

        await Assert.ThrowsAsync<RejectedException>(() => pair.Master.FreezeAtTimeAsync(DateTimeOffset.UtcNow.AddHours(-1)));
    }

    /// <summary>
    /// Relative-time events reach the master with the time they were stamped and
    /// the quality of the clock that stamped them: unsynchronized until the
    /// master sets the outstation's clock, synchronized after.
    /// </summary>
    [Fact]
    public async Task RelativeTimeEventsReachTheMasterWithTheirQuality()
    {
        await using var pair = new TestPair(null, o =>
        {
            o.Database = new DatabaseConfig { Binary = 2, DefaultClass = Class.Class1 };
        });
        pair.Outstation.Update(db =>
        {
            for (ushort i = 0; i < 2; i++)
            {
                db.Configure(PointType.Binary, i, new PointConfig { Class = Class.Class1, EventVariation = 3 });
            }
        });
        await pair.WaitConnectedAsync();
        await pair.Master.ScanClassesAsync(Class.Class123);

        var stamp = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());

        async Task<Binary> ReportAsync(ushort index, DateTimeOffset at)
        {
            pair.Outstation.Update(db => db.UpdateBinary(index, new Binary(true, Flags.Online, Timestamp.Now(at))));
            await TestPair.WaitForAsync(() => pair.Outstation.Events!.Total > 0, "the event");
            await pair.Master.ScanClassesAsync(Class.Class123);
            return pair.Handler.Read(h => h.Binaries[index]);
        }

        var got = await ReportAsync(0, stamp);
        Assert.True(Math.Abs((got.Time.Time - stamp).TotalMilliseconds) <= 1, $"event time = {got.Time.Time}, want {stamp}");
        Assert.Equal(TimestampQuality.Unsynchronized, got.Time.Quality);

        await pair.Master.SyncTimeAsync();
        got = await ReportAsync(1, stamp.AddSeconds(1));
        Assert.Equal(TimestampQuality.Synchronized, got.Time.Quality);
    }

    /// <summary>A keep-alive probe nobody answers ends the connection so the session reconnects.</summary>
    [Fact]
    public async Task UnansweredKeepAliveDropsTheConnection()
    {
        var (masterSide, outstationSide) = Pipe.Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var master = new MasterSession(new MasterConfig
        {
            LocalAddr = 1,
            RemoteAddr = 10,
            KeepAlive = TimeSpan.FromMilliseconds(40),
            LinkTimeout = TimeSpan.FromMilliseconds(40),
            LinkRetries = 1,
            ResponseTimeout = TimeSpan.FromSeconds(1),
        });
        var run = master.RunAsync(masterSide, cts.Token);
        try
        {
            using var conn = await outstationSide.ConnectAsync(cts.Token);

            // A peer that reads everything and answers nothing.
            var buf = new byte[512];
            var closed = false;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline && !closed)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                readCts.CancelAfter(TimeSpan.FromMilliseconds(500));
                try
                {
                    closed = await conn.ReadAsync(buf, readCts.Token) == 0;
                }
                catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                {
                    // Nothing this time.
                }
                catch (IOException)
                {
                    closed = true;
                }
            }

            Assert.True(closed, "the master kept a connection whose peer never answered a keep-alive");
        }
        finally
        {
            await cts.CancelAsync();
            masterSide.Close();
            outstationSide.Close();
            await Task.WhenAny(run, Task.Delay(2000));
        }
    }

    /// <summary>A response the master cannot parse is the outstation's fault, not silence.</summary>
    [Fact]
    public async Task MalformedResponseIsNotATimeout()
    {
        var (masterSide, outstationSide) = Pipe.Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var master = new MasterSession(new MasterConfig
        {
            LocalAddr = 1,
            RemoteAddr = 10,
            ResponseTimeout = TimeSpan.FromSeconds(3),
        });
        var run = master.RunAsync(masterSide, cts.Token);
        var armed = 0;
        Task? peer = null;
        try
        {
            var conn = await outstationSide.ConnectAsync(cts.Token);
            peer = Task.Run(async () =>
            {
                var st = new ProtocolStack(new StackConfig { LocalAddr = 10, RemoteAddr = 1, IsMaster = false });
                var sink = new BufferSink();
                var buf = new byte[ProtocolStack.ReadChunk];
                try
                {
                    while (true)
                    {
                        var n = await conn.ReadAsync(buf, cts.Token);
                        if (n == 0)
                        {
                            return;
                        }

                        st.Receive(sink, buf.AsSpan(0, n), r =>
                        {
                            FragmentParser.ParseFragment(null, r.Fragment, out var frag, out _);
                            if (frag.Header.Func == FuncCode.Confirm)
                            {
                                return;
                            }

                            var fire = Interlocked.Exchange(ref armed, 0) == 1;
                            var body = new List<byte>();
                            if (fire)
                            {
                                // An object header promising more data than follows.
                                body.AddRange([1, 2, 0x00, 0x00, 0x09]);
                            }

                            var bytes = new List<byte>();
                            HeaderCodec.AppendHeader(bytes, new AppHeader(
                                new AppControl(true, true, false, false, frag.Header.Control.Seq),
                                FuncCode.Response,
                                Iin.None));
                            bytes.AddRange(body);
                            st.SendTo(sink, r.Source, [.. bytes]);
                        });

                        if (!sink.IsEmpty)
                        {
                            await conn.WriteAsync(sink.Pending.ToArray(), cts.Token);
                            sink.Clear();
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or Dnp3Exception)
                {
                    // Going away.
                }
            });

            await TestPair.WaitForAsync(() => master.Connected, "connect");
            await Task.Delay(300); // let the startup sequence finish
            Interlocked.Exchange(ref armed, 1);
            var timeoutsBefore = master.Stats.ResponseTimeouts;

            using var pollCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => master.IntegrityPollAsync(pollCts.Token));
            Assert.IsType<MalformedException>(ex);
            Assert.Equal(timeoutsBefore, master.Stats.ResponseTimeouts);
        }
        finally
        {
            await cts.CancelAsync();
            masterSide.Close();
            outstationSide.Close();
            await Task.WhenAny(Task.WhenAll(run, peer ?? Task.CompletedTask), Task.Delay(2000));
        }
    }
}
