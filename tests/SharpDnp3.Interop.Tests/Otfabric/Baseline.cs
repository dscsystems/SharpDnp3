// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Globalization;
using SharpDnp3.Channels;
using SharpDnp3.Master;
using SharpDnp3.Outstation;

namespace SharpDnp3.Interop.Tests.Otfabric;

/// <summary>
/// What a master received, in the form the reference repository's own cases
/// use: per group and variation, <c>g1v2x4</c>.
/// </summary>
internal sealed class Received : NopHandler
{
    private readonly Lock _gate = new();
    private readonly List<(int Group, int Variation, uint Index, object Value, Flags Flags)> _all = [];

    public override void HandleBinary(HeaderInfo info, IReadOnlyList<Indexed<Binary>> values) =>
        Add(info, values, v => v.Value, v => Quality(v.Flags, 0x80));

    public override void HandleDoubleBit(HeaderInfo info, IReadOnlyList<Indexed<DoubleBitBinary>> values) =>
        Add(info, values, v => (int)v.Value, v => Quality(v.Flags, 0xC0));

    public override void HandleCounter(HeaderInfo info, IReadOnlyList<Indexed<Counter>> values) =>
        Add(info, values, v => v.Value, v => v.Flags);

    public override void HandleFrozenCounter(HeaderInfo info, IReadOnlyList<Indexed<FrozenCounter>> values) =>
        Add(info, values, v => v.Value, v => v.Flags);

    public override void HandleAnalog(HeaderInfo info, IReadOnlyList<Indexed<Analog>> values) =>
        Add(info, values, v => v.Value, v => v.Flags);

    public override void HandleBinaryOutputStatus(
        HeaderInfo info, IReadOnlyList<Indexed<BinaryOutputStatus>> values) =>
        Add(info, values, v => v.Value, v => Quality(v.Flags, 0x80));

    public override void HandleAnalogOutputStatus(
        HeaderInfo info, IReadOnlyList<Indexed<AnalogOutputStatus>> values) =>
        Add(info, values, v => v.Value, v => v.Flags);

    public override void HandleOctetString(HeaderInfo info, IReadOnlyList<Indexed<byte[]>> values) =>
        Add(info, values, v => Convert.ToHexStringLower(v), _ => default);

    // The state of a binary point rides in the quality octet; the tests compare
    // the quality and the value apart.
    private static Flags Quality(Flags f, byte stateBits) => new((byte)(f.Value & ~stateBits));

    private void Add<T>(
        HeaderInfo info, IReadOnlyList<Indexed<T>> values, Func<T, object> value, Func<T, Flags> flags)
    {
        lock (_gate)
        {
            foreach (var v in values)
            {
                _all.Add((info.GV.Group, info.GV.Variation, v.Index, value(v.Value), flags(v.Value)));
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _all.Clear();
        }
    }

    /// <summary>Everything received, counted per group and variation, sorted.</summary>
    public string Summary
    {
        get
        {
            lock (_gate)
            {
                return string.Join(
                    ',',
                    _all.GroupBy(o => (o.Group, o.Variation))
                        .Select(g => string.Create(CultureInfo.InvariantCulture, $"g{g.Key.Group}v{g.Key.Variation}x{g.Count()}"))
                        .Order(StringComparer.Ordinal));
            }
        }
    }

    /// <summary>The value and flags last received for a group and index.</summary>
    public (object Value, Flags Flags) Point(int group, uint index)
    {
        lock (_gate)
        {
            var p = _all.Last(o => o.Group == group && o.Index == index);
            return (p.Value, p.Flags);
        }
    }
}

/// <summary>A master of ours connected to an outstation at an address.</summary>
internal sealed class OurMaster : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromMinutes(2));
    private readonly TcpClientChannel _channel;
    private readonly Task _run;

    private OurMaster(string address, ushort remote, TimeSpan responseTimeout)
    {
        Received = new Received();
        Session = new MasterSession(
            new MasterConfig
            {
                LocalAddr = 1,
                RemoteAddr = remote,
                ResponseTimeout = responseTimeout,
            },
            Received);

        _channel = new TcpClientChannel(address, Retry.Default);
        _run = Session.RunAsync(_channel, _cts.Token);
    }

    public MasterSession Session { get; }

    public Received Received { get; }

    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// Connects to a reference outstation. The baseline fixture's outstation is
    /// 1024 and answers master 1; the master does nothing on its own, as the
    /// reference masters do not.
    /// </summary>
    public static async Task<OurMaster> ConnectAsync(
        string address, ushort remote = 1024, TimeSpan? responseTimeout = null)
    {
        var m = new OurMaster(address, remote, responseTimeout ?? TimeSpan.FromSeconds(10));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!m.Session.Connected)
        {
            Assert.True(DateTime.UtcNow < deadline, "the master never connected");
            await Task.Delay(25);
        }

        return m;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await Task.WhenAny(_run, Task.Delay(5000));
        _channel.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// The controls an outstation of ours was asked to carry out, with the
/// semantics of the baseline fixture: output 1 of each type needs a select,
/// and the analog outputs accept only a range.
/// </summary>
internal sealed class BaselineCommands : ICommandHandler
{
    private readonly Lock _gate = new();

    public List<(ushort Index, ControlRelayOutputBlock Crob, OperateType Type)> Crobs { get; } = [];

    public List<(ushort Index, AnalogOutputCommand Command, OperateType Type)> Analogs { get; } = [];

    public CommandStatus SelectCrob(ushort index, ControlRelayOutputBlock c) =>
        PlainPulse(c) ? CommandStatus.NotSupported : CommandStatus.Success;

    public CommandStatus OperateCrob(ushort index, ControlRelayOutputBlock c, OperateType op)
    {
        if (PlainPulse(c))
        {
            return CommandStatus.NotSupported;
        }

        if (index == 1 && op == OperateType.Direct)
        {
            return CommandStatus.NoSelect;
        }

        lock (_gate)
        {
            Crobs.Add((index, c, op));
        }

        return CommandStatus.Success;
    }

    public CommandStatus SelectAnalog(ushort index, AnalogOutputCommand v) =>
        InRange(index, v.Value) ? CommandStatus.Success : CommandStatus.OutOfRange;

    public CommandStatus OperateAnalog(ushort index, AnalogOutputCommand v, OperateType op)
    {
        if (!InRange(index, v.Value))
        {
            return CommandStatus.OutOfRange;
        }

        if (index == 1 && op == OperateType.Direct)
        {
            return CommandStatus.NoSelect;
        }

        lock (_gate)
        {
            Analogs.Add((index, v, op));
        }

        return CommandStatus.Success;
    }

    // The baseline outstation latches, closes and trips. A pulse with neither
    // trip nor close has no meaning for its breakers.
    private static bool PlainPulse(ControlRelayOutputBlock c) =>
        (c.Code.Value & 0x0F) is 1 or 2 && (c.Code.Value & 0xC0) == 0;

    private static bool InRange(ushort index, double v) =>
        index == 0 ? v is >= -1_000_000 and <= 1_000_000 : v is >= -100 and <= 100;
}

/// <summary>Records what the application was asked to do.</summary>
internal sealed class BaselineApplication : NopApplication
{
    private long _offsetTicks;

    public int ColdRestarts { get; private set; }

    public int WarmRestarts { get; private set; }

    public DateTimeOffset? TimeWritten { get; private set; }

    public override DateTimeOffset Now() =>
        DateTimeOffset.UtcNow + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public override bool WriteAbsoluteTime(DateTimeOffset t)
    {
        TimeWritten = t;
        Interlocked.Exchange(ref _offsetTicks, (t - DateTimeOffset.UtcNow).Ticks);
        return true;
    }

    public override TimeSpan ColdRestart()
    {
        ColdRestarts++;
        return TimeSpan.FromMilliseconds(5000);
    }

    public override TimeSpan WarmRestart()
    {
        WarmRestarts++;
        return TimeSpan.FromMilliseconds(1000);
    }
}

/// <summary>
/// An outstation of ours serving the reference repository's <c>baseline</c>
/// fixture (fixtures/baseline/fixture.json there): the expectations of the
/// tests are that fixture's, which is why it is written out point by point.
/// </summary>
internal sealed class BaselineOutstation : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromMinutes(2));
    private readonly TcpServerChannel _channel;
    private readonly Task _run;

    public BaselineOutstation()
    {
        Port = Peers.FreePort();
        Application = new BaselineApplication();
        Commands = new BaselineCommands();

        Session = new OutstationSession(
            new OutstationConfig
            {
                LocalAddr = 1024,
                RemoteAddr = 1,
                Database = new DatabaseConfig
                {
                    Binary = 5,
                    DoubleBitBinary = 2,
                    BinaryOutputStatus = 2,
                    Counter = 3,
                    FrozenCounter = 2,
                    Analog = 5,
                    AnalogOutputStatus = 2,
                    OctetString = 1,
                    DefaultClass = Class.None,
                },
                Unsolicited = new UnsolicitedConfig { Enabled = true },
            },
            Application,
            Commands);

        // The values go in while no point has a class, so the setup produces
        // no events; the reference outstation starts with an empty event
        // buffer too.
        Session.Update(db =>
        {
            var t = Timestamp.NoTime();
            var online = Flags.Online;

            db.UpdateBinary(0, new Binary(true, online, t));
            db.UpdateBinary(1, new Binary(false, online, t));
            db.UpdateBinary(2, new Binary(true, Flags.Online | Flags.LocalForced, t));
            db.UpdateBinary(3, new Binary(false, Flags.None, t));
            db.UpdateBinary(4, new Binary(true, online, t));

            db.UpdateDoubleBit(0, new DoubleBitBinary(DoubleBit.DeterminedOn, online, t));
            db.UpdateDoubleBit(1, new DoubleBitBinary(DoubleBit.Intermediate, Flags.Online | Flags.CommLost, t));

            db.UpdateBinaryOutputStatus(0, new BinaryOutputStatus(false, online, t));
            db.UpdateBinaryOutputStatus(1, new BinaryOutputStatus(false, online, t));

            db.UpdateCounter(0, new Counter(123456, online, t));
            db.UpdateCounter(1, new Counter(65535, online, t));
            db.UpdateCounter(2, new Counter(4294967295, online, t));

            db.UpdateFrozenCounter(0, new FrozenCounter(0, online, t));
            db.UpdateFrozenCounter(1, new FrozenCounter(0, online, t));

            db.UpdateAnalog(0, new Analog(230.5, online, t));
            db.UpdateAnalog(1, new Analog(-1234, online, t));
            db.UpdateAnalog(2, new Analog(32767, online, t));
            db.UpdateAnalog(3, new Analog(0, Flags.OverRange, t));
            db.UpdateAnalog(4, new Analog(0, online, t));

            db.UpdateAnalogOutputStatus(0, new AnalogOutputStatus(0, online, t));
            db.UpdateAnalogOutputStatus(1, new AnalogOutputStatus(0, online, t));

            db.UpdateOctetString(0, "Hello"u8);

            Configure(db, PointType.Binary, 0, Class.Class1, 2, 2);
            Configure(db, PointType.Binary, 1, Class.Class1, 2, 2);
            Configure(db, PointType.Binary, 2, Class.Class2, 2, 1);
            Configure(db, PointType.Binary, 3, Class.None, 2, 2);
            Configure(db, PointType.Binary, 4, Class.None, 1, 2);

            Configure(db, PointType.DoubleBitBinary, 0, Class.Class1, 2, 2);
            Configure(db, PointType.DoubleBitBinary, 1, Class.None, 2, 2);

            Configure(db, PointType.BinaryOutputStatus, 0, Class.Class1, 2, 2);
            Configure(db, PointType.BinaryOutputStatus, 1, Class.Class1, 2, 2);

            Configure(db, PointType.Counter, 0, Class.Class3, 1, 1);
            Configure(db, PointType.Counter, 1, Class.Class3, 2, 2);
            Configure(db, PointType.Counter, 2, Class.None, 5, 1);

            Configure(db, PointType.FrozenCounter, 0, Class.Class3, 1, 1);
            Configure(db, PointType.FrozenCounter, 1, Class.Class3, 2, 2);

            Configure(db, PointType.Analog, 0, Class.Class2, 5, 7);
            Configure(db, PointType.Analog, 1, Class.Class2, 1, 3);
            Configure(db, PointType.Analog, 2, Class.None, 2, 1);
            Configure(db, PointType.Analog, 3, Class.None, 1, 1);
            Configure(db, PointType.Analog, 4, Class.Class2, 1, 1);

            Configure(db, PointType.AnalogOutputStatus, 0, Class.Class1, 1, 1);
            Configure(db, PointType.AnalogOutputStatus, 1, Class.Class1, 3, 3);

            Configure(db, PointType.OctetString, 0, Class.None, 0, 0);
        });

        _channel = new TcpServerChannel(
            string.Format(CultureInfo.InvariantCulture, ":{0}", Port));
        _run = Session.RunAsync(_channel, _cts.Token);
    }

    public int Port { get; }

    public OutstationSession Session { get; }

    public BaselineApplication Application { get; }

    public BaselineCommands Commands { get; }

    private static void Configure(
        Database db, PointType type, ushort index, Class cls, byte staticVariation, byte eventVariation) =>
        db.Configure(
            type,
            index,
            new PointConfig { Class = cls, StaticVariation = staticVariation, EventVariation = eventVariation });

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await Task.WhenAny(_run, Task.Delay(5000));
        _channel.Dispose();
        _cts.Dispose();
    }
}
