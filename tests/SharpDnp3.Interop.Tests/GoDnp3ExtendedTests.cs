// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The extended services, against a second implementation, in both directions:
// frozen analogs, command events, indexed time intervals, virtual terminals,
// datasets, the application and configuration function codes, the LAN time
// procedure, writable attributes, file authentication, self-address discovery
// and symmetric Secure Authentication.
//
// go-dnp3's stock binaries do not expose these, so the tests drive "gopeer"
// (see testdata/interop/gopeer), a small program built on go-dnp3's public packages that plays
// either role and reports what it sees, one line per fact. Build it with
// testdata/interop/gopeer/build.sh and point GO_DNP3_BIN at the output directory; the
// tests skip when it is absent.

using System.Globalization;
using SharpDnp3.App;
using SharpDnp3.Channels;
using SharpDnp3.Master;
using SharpDnp3.Objects;
using SharpDnp3.Outstation;

namespace SharpDnp3.Interop.Tests;

/// <summary>A master handler that keeps what the extended services deliver.</summary>
internal sealed class ExtendedHandler : NopHandler, IFrozenAnalogHandler, ICommandEventHandler, IDatasetHandler
{
    private readonly Lock _gate = new();

    public Dictionary<uint, Analog> FrozenAnalogs { get; } = [];

    public Dictionary<uint, FrozenCounter> FrozenCounters { get; } = [];

    public Dictionary<uint, Counter> Counters { get; } = [];

    public List<(HeaderInfo Info, byte[] Data)> Datasets { get; } = [];

    public List<(uint Index, CommandEvent Event)> CommandEvents { get; } = [];

    public List<(HeaderInfo Info, uint Index, byte[] Data)> Octets { get; } = [];

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

    public override void HandleFrozenCounter(HeaderInfo info, IReadOnlyList<Indexed<FrozenCounter>> values)
    {
        lock (_gate)
        {
            foreach (var v in values)
            {
                FrozenCounters[v.Index] = v.Value;
            }
        }
    }

    public void HandleFrozenAnalog(HeaderInfo info, IReadOnlyList<Indexed<Analog>> values)
    {
        lock (_gate)
        {
            foreach (var v in values)
            {
                FrozenAnalogs[v.Index] = v.Value;
            }
        }
    }

    public void HandleCommandEvent(HeaderInfo info, IReadOnlyList<Indexed<CommandEvent>> values)
    {
        lock (_gate)
        {
            foreach (var v in values)
            {
                CommandEvents.Add((v.Index, v.Value));
            }
        }
    }

    public void HandleDataset(HeaderInfo info, byte[] data)
    {
        lock (_gate)
        {
            Datasets.Add((info, data));
        }
    }

    public override void HandleOctetString(HeaderInfo info, IReadOnlyList<Indexed<byte[]>> values)
    {
        lock (_gate)
        {
            foreach (var v in values)
            {
                Octets.Add((info, v.Index, v.Value));
            }
        }
    }

    public T Read<T>(Func<ExtendedHandler, T> read)
    {
        lock (_gate)
        {
            return read(this);
        }
    }
}

public class GoDnp3ExtendedTests
{
    private const string Skip = "gopeer not available; build it with testdata/interop/gopeer/build.sh and set GO_DNP3_BIN";

    private static string Addr(int port) => string.Format(CultureInfo.InvariantCulture, "127.0.0.1:{0}", port);

    private static async Task WaitAsync(Func<bool> condition, string what, int seconds = 10)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
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

    // ---- our master against the go outstation ------------------------------

    private sealed class GoOutstation : IAsyncDisposable
    {
        public required PeerProcess Peer { get; init; }

        public required int Port { get; init; }

        public string? Dir { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Peer.DisposeAsync();
            if (Dir is not null)
            {
                Directory.Delete(Dir, recursive: true);
            }
        }
    }

    private static async Task<GoOutstation> StartGoOutstationAsync(string? key = null, bool files = false)
    {
        var bin = Peers.GoDnp3("gopeer");
        Assert.SkipUnless(bin is not null, Skip);

        var port = Peers.FreePort();
        var args = new List<string> { "outstation", "-listen", Addr(port) };
        string? dir = null;
        if (files)
        {
            dir = Path.Combine(Path.GetTempPath(), "sharpdnp3-interop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "hello.txt"), "hello");
            args.AddRange(["-dir", dir]);
        }

        if (key is not null)
        {
            args.AddRange(["-key", key]);
        }

        var peer = new PeerProcess(bin!, [.. args]);
        await peer.WaitForOutputAsync("READY", TimeSpan.FromSeconds(10));
        await PeerProcess.WaitForPortAsync(port, TimeSpan.FromSeconds(10));
        return new GoOutstation { Peer = peer, Port = port, Dir = dir };
    }

    private sealed class OurMaster : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly TcpClientChannel _channel;
        private readonly Task _run;

        public MasterSession Master { get; }

        public ExtendedHandler Handler { get; } = new();

        public CancellationToken Token => _cts.Token;

        public OurMaster(int port, Action<MasterConfig>? configure = null, ushort remote = 10)
        {
            var cfg = new MasterConfig
            {
                LocalAddr = 1,
                RemoteAddr = remote,
                ResponseTimeout = TimeSpan.FromSeconds(5),
            };
            configure?.Invoke(cfg);
            Master = new MasterSession(cfg, Handler);
            _channel = new TcpClientChannel(Addr(port), Retry.Default);
            _run = Master.RunAsync(_channel, _cts.Token);
            _cts.CancelAfter(TimeSpan.FromSeconds(60));
        }

        public async Task ConnectedAsync()
        {
            await WaitAsync(() => Master.Connected, "the master to connect", 15);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await Task.WhenAny(_run, Task.Delay(3000));
            _channel.Dispose();
            _cts.Dispose();
        }
    }

    private static readonly string HexKey16 = string.Concat(Enumerable.Repeat("71", 16));

    [Fact]
    public async Task OurMasterPollsGoFrozenAnalogsDatasetsAndEvents()
    {
        await using var go = await StartGoOutstationAsync();
        await using var m = new OurMaster(go.Port);
        await m.ConnectedAsync();

        await m.Master.IntegrityPollAsync(m.Token);

        // The frozen analog is its own storage, reported through its own handler.
        Assert.Equal(7, m.Handler.Read(h => h.FrozenAnalogs[0].Value));
        // A present-value dataset arrives as a whole encoded object.
        Assert.Contains(m.Handler.Read(h => h.Datasets), d => d.Info.GV.Group == 87 && d.Data.SequenceEqual(new byte[] { 0xAA, 0xBB, 0xCC }));
        Assert.Equal(300u, m.Handler.Read(h => h.Counters[2].Value));

        // A dataset update queues a class 2 snapshot event, and a terminal input a class 1 event.
        await go.Peer.WriteLineAsync("dataset-update");
        await go.Peer.WriteLineAsync("terminal-input");
        await Task.Delay(300);
        await m.Master.ScanClassesAsync(Class.Class123, m.Token);
        Assert.Contains(m.Handler.Read(h => h.Datasets), d => d.Info.GV.Group == 88 && d.Data.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
        Assert.Contains(
            m.Handler.Read(h => h.Octets),
            o => o.Info.GV.Group == 113 && o.Data.SequenceEqual("hello"u8.ToArray()));
    }

    [Fact]
    public async Task OurMasterFreezesSyncsTimeAndIsRefusedAPastFreeze()
    {
        await using var go = await StartGoOutstationAsync();
        await using var m = new OurMaster(go.Port);
        await m.ConnectedAsync();

        await m.Master.SyncTimeRecordedAsync(m.Token);

        await m.Master.FreezeCountersAsync(FreezeMode.Freeze, cancellationToken: m.Token);
        await m.Master.ScanRangeAsync(21, 0, 0, 2, m.Token);
        Assert.Equal(200u, m.Handler.Read(h => h.FrozenCounters[1].Value));

        await m.Master.FreezeCountersAsync(FreezeMode.FreezeAndClear, cancellationToken: m.Token);
        await m.Master.ScanRangeAsync(20, 0, 0, 2, m.Token);
        Assert.Equal(0u, m.Handler.Read(h => h.Counters[1].Value));

        await m.Master.FreezeAtTimeAsync(DateTimeOffset.UtcNow.AddMilliseconds(300), cancellationToken: m.Token);
        await Assert.ThrowsAsync<RejectedException>(() =>
            m.Master.FreezeAtTimeAsync(DateTimeOffset.UtcNow.AddMinutes(-1), cancellationToken: m.Token));
    }

    [Fact]
    public async Task OurMasterExtendedRequests()
    {
        await using var go = await StartGoOutstationAsync();
        await using var m = new OurMaster(go.Port);
        await m.ConnectedAsync();

        // The indexed time-and-interval objects, written and read back.
        var when = DateTimeOffset.FromUnixTimeMilliseconds(1_750_000_000_000);
        var value = new List<byte>();
        CommandObjects.AppendTime48(value, Timestamp.Now(when));
        ObjectConvert.AppendUInt32(value, 12_345);
        value.Add(4);
        var write = await m.Master.SendRequestAsync(
            FuncCode.Write,
            [
                new ObjectHeader
                {
                    Group = 50,
                    Variation = 4,
                    Qualifier = Qualifier.Make(IndexPrefix.Index1, RangeSpec.Count8),
                    Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
                    Data = new byte[] { 0 }.Concat(value).ToArray(),
                },
            ],
            cancellationToken: m.Token);
        Assert.False(write.Iin.HasAny(Iin.RequestErrorMask), $"IIN = {write.Iin}");

        var read = await m.Master.SendRequestAsync(
            FuncCode.Read, [FragmentFactory.ReadRange(50, 4, 0, 0)], cancellationToken: m.Token);
        var obj = Assert.Single(read.Fragments.SelectMany(f => f.Objects), o => o.Group == 50 && o.Variation == 4);
        Assert.Equal(when, CommandObjects.ParseTime48(obj.Data.Span).Time);
        Assert.Equal(12_345u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(obj.Data.Span[6..]));

        // Current time and the internal indications as data.
        var time = await m.Master.SendRequestAsync(
            FuncCode.Read, [FragmentFactory.ReadAllObjects(50, 1)], cancellationToken: m.Token);
        Assert.Contains(time.Fragments.SelectMany(f => f.Objects), o => o.Group == 50 && o.Variation == 1);
        var iin = await m.Master.SendRequestAsync(
            FuncCode.Read, [FragmentFactory.ReadRange(80, 1, 0, 15)], cancellationToken: m.Token);
        Assert.Contains(iin.Fragments.SelectMany(f => f.Objects), o => o.Group == 80 && o.Variation == 1);

        // A virtual terminal write reaches the application.
        var terminal = await m.Master.SendRequestAsync(
            FuncCode.Write,
            [
                new ObjectHeader
                {
                    Group = 112,
                    Variation = 5,
                    Qualifier = Qualifier.Make(IndexPrefix.Index1, RangeSpec.Count8),
                    Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
                    Data = new byte[] { 0, (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e' },
                },
            ],
            cancellationToken: m.Token);
        Assert.False(terminal.Iin.HasAny(Iin.RequestErrorMask), $"IIN = {terminal.Iin}");
        await go.Peer.WaitForOutputAsync("TERMINAL index=0 data=6162636465", TimeSpan.FromSeconds(5));

        // An application function code with no objects reaches the management handler.
        await m.Master.SendRequestAsync(FuncCode.InitializeAppl, [], cancellationToken: m.Token);
        await go.Peer.WaitForOutputAsync("MANAGE op=16", TimeSpan.FromSeconds(5));

        // ACTIVATE_CONFIG names files and is answered with a group 91 result.
        var activate = await m.Master.SendRequestAsync(
            FuncCode.ActivateConfig, [FreeFormat.Build(70, 8, "a.cfg"u8)], cancellationToken: m.Token);
        var result = Assert.Single(activate.Fragments.SelectMany(f => f.Objects), o => o.Group == 91 && o.Variation == 1);
        var parsed = ActivationCodec.Parse(result.Data.Span);
        Assert.Equal(TimeSpan.FromMilliseconds(250), parsed.Delay);
        Assert.Equal("a.cfg", Assert.Single(parsed.Statuses).Text);
        await go.Peer.WaitForOutputAsync("ACTIVATE [a.cfg]", TimeSpan.FromSeconds(5));

        // A dataset write is handed to the application with its encoding intact.
        var dataset = await m.Master.SendRequestAsync(
            FuncCode.Write, [FreeFormat.Build(87, 1, new byte[] { 9, 8, 7 })], cancellationToken: m.Token);
        Assert.False(dataset.Iin.HasAny(Iin.RequestErrorMask), $"IIN = {dataset.Iin}");
        await go.Peer.WaitForOutputAsync("DATASETWRITE group=87 variation=1 data=090807", TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OurMasterWritesAGoAttributeAndDrivesSecureControls()
    {
        await using var go = await StartGoOutstationAsync(HexKey16);
        var key = Convert.FromHexString(HexKey16);
        await using var m = new OurMaster(go.Port, cfg => cfg.SecureAuthentication =
            new SharpDnp3.Master.SecureAuthenticationConfig { User = 1, UpdateKey = key });
        await m.ConnectedAsync();

        await m.Master.WriteAttributeAsync(DeviceAttribute.String(247, "from-csharp"), m.Token);
        await go.Peer.WaitForOutputAsync("ATTRWRITE variation=247 text=from-csharp", TimeSpan.FromSeconds(5));
        Assert.Equal("from-csharp", (await m.Master.ReadAttributeAsync(0, 247, m.Token)).Text);

        var result = await m.Master.SelectAndOperateAsync(Command.Trip(1, 100));
        Assert.True(result.OK(), result.ToString());
        await go.Peer.WaitForOutputAsync("OPERATED crob index=1", TimeSpan.FromSeconds(5));

        // The operated control is a command event in class 2.
        await m.Master.ScanClassesAsync(Class.Class2, m.Token);
        var events = m.Handler.Read(h => h.CommandEvents.ToList());
        Assert.Contains(events, e => e.Index == 1 && !e.Event.Analog && e.Event.Status == CommandStatus.Success);
    }

    [Fact]
    public async Task OurMasterWithTheWrongUpdateKeyIsRefused()
    {
        await using var go = await StartGoOutstationAsync(HexKey16);
        await using var m = new OurMaster(go.Port, cfg => cfg.SecureAuthentication =
            new SharpDnp3.Master.SecureAuthenticationConfig
            {
                User = 1,
                UpdateKey = Enumerable.Repeat((byte)9, 16).ToArray(),
            });
        await m.ConnectedAsync();
        await Assert.ThrowsAnyAsync<Dnp3Exception>(() =>
            m.Master.WriteAttributeAsync(DeviceAttribute.String(247, "no"), m.Token));
        Assert.DoesNotContain("ATTRWRITE", go.Peer.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OurMasterAuthenticatesForFiles()
    {
        await using var go = await StartGoOutstationAsync(files: true);

        await using (var bare = new OurMaster(go.Port))
        {
            await bare.ConnectedAsync();
            await Assert.ThrowsAnyAsync<Dnp3Exception>(() => bare.Master.ReadFileBytesAsync("hello.txt", bare.Token));
        }

        await using var m = new OurMaster(go.Port, cfg => cfg.FileCredentials = new FileCredentials("admin", "pw"));
        await m.ConnectedAsync();
        Assert.Equal("hello"u8.ToArray(), await m.Master.ReadFileBytesAsync("hello.txt", m.Token));

        var payload = Enumerable.Range(0, 20_000).Select(i => (byte)(i * 7)).ToArray();
        await m.Master.WriteFileBytesAsync("upload.bin", payload, m.Token);
        Assert.Equal(payload, await m.Master.ReadFileBytesAsync("upload.bin", m.Token));
        await m.Master.DeleteFileAsync("upload.bin", m.Token);
        Assert.False(File.Exists(Path.Combine(go.Dir!, "upload.bin")));

        var wrong = await m.Master.AuthenticateFileAsync("admin", "pw", m.Token);
        Assert.NotEqual(0u, wrong);
        await Assert.ThrowsAnyAsync<Dnp3Exception>(() => m.Master.AuthenticateFileAsync("admin", "bad", m.Token));
    }

    [Fact]
    public async Task OurMasterDiscoversTheGoOutstationBySelfAddress()
    {
        await using var go = await StartGoOutstationAsync();
        await using var m = new OurMaster(go.Port, remote: 0xFFFC);
        await m.ConnectedAsync();
        await m.Master.ScanClassesAsync(Class.Class0, m.Token);
        Assert.Equal(300u, m.Handler.Read(h => h.Counters[2].Value));
    }

    // ---- the go master against our outstation ------------------------------

    private sealed class OurOutstation : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly TcpServerChannel _channel;
        private readonly Task _run;

        public OutstationSession Session { get; }

        public int Port { get; }

        public string Dir { get; }

        public AcceptingCommandHandler Commands { get; } = new();

        public ManagementRecorder Management { get; } = new();

        public List<string> AttributeWrites { get; } = [];

        public OurOutstation(Action<OutstationConfig>? configure = null, IOutstationApplication? app = null)
        {
            Port = Peers.FreePort();
            Dir = Path.Combine(Path.GetTempPath(), "sharpdnp3-interop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "hello.txt"), "hello");

            var cfg = new OutstationConfig
            {
                LocalAddr = 10,
                RemoteAddr = 1,
                Database = new DatabaseConfig
                {
                    Binary = 4,
                    Analog = 4,
                    Counter = 3,
                    FrozenCounter = 3,
                    FrozenAnalog = 4,
                    BinaryOutputStatus = 2,
                    AnalogOutputStatus = 2,
                    TimeAndInterval = 2,
                    VirtualTerminal = 1,
                    DefaultClass = Class.Class1,
                },
                SelfAddress = true,
                Management = Management,
            };
            cfg.Attributes.Add(DeviceAttribute.String(247, "before"));
            cfg.WritableAttributes.Add(new AttributeId(0, 247));
            cfg.AttributeWrite = a =>
            {
                lock (AttributeWrites)
                {
                    AttributeWrites.Add(a.Text ?? string.Empty);
                }

                return true;
            };
            cfg.Files.Handler = new DirectoryFileHandler(Dir);
            cfg.Files.Authenticate = (u, p) => u == "admin" && p == "pw";
            cfg.Datasets.Add(new DatasetObject { Group = 85, Variation = 1, Index = 0, Data = [4, 1, 2, 0] });
            cfg.Datasets.Add(new DatasetObject { Group = 87, Variation = 1, Index = 0, Data = [0xAA, 0xBB, 0xCC] });
            configure?.Invoke(cfg);

            Session = new OutstationSession(cfg, app, Commands);
            Session.Update(db =>
            {
                for (ushort i = 0; i < 2; i++)
                {
                    db.Configure(PointType.BinaryOutputStatus, i, new PointConfig { Class = Class.None, CommandEventClass = Class.Class2 });
                }

                for (ushort i = 0; i < 3; i++)
                {
                    db.UpdateCounter(i, new Counter((uint)(100 * (i + 1)), Flags.Online, default));
                }

                db.UpdateAnalog(1, new Analog(42.5, Flags.Online, default));
                db.UpdateFrozenAnalog(0, new Analog(7, Flags.Online, default));
            });

            _channel = new TcpServerChannel(Addr(Port));
            _run = Session.RunAsync(_channel, _cts.Token);
            _cts.CancelAfter(TimeSpan.FromSeconds(60));
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await Task.WhenAny(_run, Task.Delay(3000));
            _channel.Dispose();
            _cts.Dispose();
            Directory.Delete(Dir, recursive: true);
        }
    }

    private sealed class ManagementRecorder : IManagementHandler
    {
        public List<ManagementOperation> Operations { get; } = [];

        public bool Manage(ManagementOperation operation, byte[] objects)
        {
            lock (Operations)
            {
                Operations.Add(operation);
            }

            return true;
        }
    }

    private sealed class ClockApp : NopApplication
    {
        public List<DateTimeOffset> Written { get; } = [];

        public override bool WriteAbsoluteTime(DateTimeOffset t)
        {
            lock (Written)
            {
                Written.Add(t);
            }

            return true;
        }
    }

    private static async Task<string> RunGoMasterAsync(int port, string scenario, params string[] extra)
    {
        var bin = Peers.GoDnp3("gopeer");
        Assert.SkipUnless(bin is not null, Skip);
        string[] args = ["master", "-addr", Addr(port), "-scenario", scenario, .. extra];
        await using var peer = new PeerProcess(bin!, args);
        var exit = await peer.WaitForExitAsync(TimeSpan.FromSeconds(40));
        Assert.True(
            exit == 0,
            $"go master scenario '{scenario}' exited {exit}\nstdout:\n{peer.StandardOutput}\nstderr:\n{peer.StandardError}");
        return peer.StandardOutput;
    }

    [Fact]
    public async Task GoMasterPollsOurFrozenAnalogsDatasetsAndCommandEvents()
    {
        await using var ours = new OurOutstation();
        var output = await RunGoMasterAsync(ours.Port, "poll");
        Assert.Contains("FROZENANALOG g31v1 index=0 value=7", output, StringComparison.Ordinal);
        Assert.Contains("DATASET g87v1 data=aabbcc", output, StringComparison.Ordinal);
        Assert.Contains("COUNTER g20v1 index=2 value=300", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoMasterFreezesOurCountersAndAnalogs()
    {
        await using var ours = new OurOutstation();
        var output = await RunGoMasterAsync(ours.Port, "freeze");
        Assert.Contains("OK freeze-at-time", output, StringComparison.Ordinal);

        // FREEZE_CLEAR zeroed the running counters after freezing them, so the
        // scheduled freeze that followed froze zeros — which also proves it ran.
        ours.Session.Database.TryGetCounter(1, out var running, out _);
        Assert.Equal(0u, running.Value);
        await WaitAsync(() => ours.Session.Stats.ScheduledFreezes >= 1, "the scheduled freeze");
        ours.Session.Database.TryGetFrozenCounter(1, out var frozen, out _);
        Assert.Equal(0u, frozen.Value);
        Assert.Contains("FROZENCOUNTER g21v1 index=1 value=0", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoMasterSyncsOurClockWithTheRecordedTimeProcedure()
    {
        var app = new ClockApp();
        await using var ours = new OurOutstation(app: app);
        await RunGoMasterAsync(ours.Port, "recorded-time");
        await WaitAsync(() => app.Written.Count == 1, "the clock to be written");
        Assert.True(Math.Abs((app.Written[0] - DateTimeOffset.UtcNow).TotalSeconds) < 5, $"clock set to {app.Written[0]}");
    }

    [Fact]
    public async Task GoMasterReadsOurExtendedObjects()
    {
        await using var ours = new OurOutstation();
        ours.Session.Update(db =>
        {
            db.UpdateTimeAndInterval(1, new TimeAndInterval(Timestamp.Now(DateTimeOffset.UtcNow), 5000, 3));
            db.UpdateVirtualTerminal(0, "abc"u8);
        });
        await Task.Delay(100);
        var output = await RunGoMasterAsync(ours.Port, "ranges");
        Assert.Contains("OK time-intervals", output, StringComparison.Ordinal);
        Assert.Contains("DATASET g87v1 data=aabbcc", output, StringComparison.Ordinal);
        Assert.Contains("OK iin", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoMasterWritesOurAttributeUsingSecureAuthentication()
    {
        var key = Convert.FromHexString(HexKey16);
        await using var ours = new OurOutstation(cfg =>
        {
            var sa = new SharpDnp3.Outstation.SecureAuthenticationConfig();
            sa.Users[1] = key;
            cfg.SecureAuthentication = sa;
        });
        var output = await RunGoMasterAsync(ours.Port, "attribute-write", "-key", HexKey16);
        Assert.Contains("ATTRIBUTE variation=247 text=from-go", output, StringComparison.Ordinal);
        lock (ours.AttributeWrites)
        {
            Assert.Equal(["from-go"], ours.AttributeWrites);
        }
    }

    [Fact]
    public async Task GoMasterSecureControlRaisesACommandEvent()
    {
        var key = Convert.FromHexString(HexKey16);
        await using var ours = new OurOutstation(cfg =>
        {
            var sa = new SharpDnp3.Outstation.SecureAuthenticationConfig();
            sa.Users[1] = key;
            cfg.SecureAuthentication = sa;
        });
        var output = await RunGoMasterAsync(ours.Port, "control", "-key", HexKey16);
        Assert.Equal(1, ours.Commands.OperatedCount);
        Assert.Contains("COMMANDEVENT g13v2 index=1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoMasterWithTheWrongKeyCannotOperateOurOutstation()
    {
        var key = Convert.FromHexString(HexKey16);
        await using var ours = new OurOutstation(cfg =>
        {
            var sa = new SharpDnp3.Outstation.SecureAuthenticationConfig();
            sa.Users[1] = key;
            cfg.SecureAuthentication = sa;
        });
        var bin = Peers.GoDnp3("gopeer");
        Assert.SkipUnless(bin is not null, Skip);
        await using var peer = new PeerProcess(
            bin!, "master", "-addr", Addr(ours.Port), "-scenario", "control", "-key", string.Concat(Enumerable.Repeat("09", 16)));
        var exit = await peer.WaitForExitAsync(TimeSpan.FromSeconds(40));
        Assert.NotEqual(0, exit);
        Assert.Equal(0, ours.Commands.OperatedCount);
    }

    [Fact]
    public async Task GoMasterTransfersFilesWithAuthentication()
    {
        await using var ours = new OurOutstation();
        var output = await RunGoMasterAsync(ours.Port, "files");
        Assert.Contains("FILE size=30000 equal=true", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(ours.Dir, "upload.bin")));

        var refused = await RunGoMasterAsync(ours.Port, "files-open");
        Assert.Contains("OK refused", refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoMasterDiscoversOurOutstationBySelfAddress()
    {
        await using var ours = new OurOutstation();
        await RunGoMasterAsync(ours.Port, "self-address", "-remote", "65532");
    }
}
