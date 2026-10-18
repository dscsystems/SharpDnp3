// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Diagnostics;
using SharpDnp3.Channels;
using SharpDnp3.Outstation;

namespace SharpDnp3.Conformance.Dut;

/// <summary>
/// What the procedures reconfigure. Like a real device's configuration they
/// take effect when the device restarts, which is how every procedure that
/// changes one uses it: change, then cycle power.
/// </summary>
internal sealed record Settings
{
    public bool LinkConfirms { get; set; }

    public bool SelfAddress { get; set; }

    public int FragmentSize { get; set; } = Device.FragmentSize;

    /// <summary>
    /// Off until a procedure turns it on: the procedures that test unsolicited
    /// responses enable them and cycle power first, and the rest take a startup
    /// unsolicited response for a fault.
    /// </summary>
    public bool Unsolicited { get; set; }

    public TimeSpan UnsolTimeout { get; set; } = Device.UnsolTimeout;

    /// <summary>The retry count, or null for unlimited.</summary>
    public int? UnsolRetries { get; set; }

    public ushort Master { get; set; } = Device.MasterAddr;

    public HashSet<Kind> Uninstalled { get; set; } = [];

    /// <summary>
    /// Turns cold restart support off. Unlike the rest it applies at once, as
    /// 3.1r1's 8.8.2 expects.
    /// </summary>
    public bool NoColdRestart { get; set; }

    public Settings Copy() => this with { Uninstalled = [.. Uninstalled] };

    /// <summary>How many points of a type the device has with these settings.</summary>
    public int Count(Kind k) => Uninstalled.Contains(k) ? 0 : Device.PointsPerType;
}

/// <summary>An output the master operated.</summary>
internal readonly record struct Operation(Kind Kind, ushort Index);

/// <summary>The outstation and everything the plugin needs to know about it.</summary>
internal sealed class Device : ICommandHandler, IOutputPoints
{
    // The device's shape. device.ini describes the same device to dnp3c, and
    // the two have to agree: a point count or class changed here and not there
    // makes the harness test a device that does not exist.
    public const ushort OutstationAddr = 1024;
    public const ushort MasterAddr = 1;

    /// <summary>How many points of each type the device has.</summary>
    public const int PointsPerType = 20;

    /// <summary>
    /// The event buffer's capacity, and what device.ini gives as the maximum
    /// for each type: SharpDnp3 has one buffer shared by every type, so a test
    /// that fills it with one type fills it at this count.
    /// </summary>
    public const int MaxEvents = 1000;

    public const int FragmentSize = 2048;

    /// <summary>How many controls one request may carry, device.ini's *.maxPointsInSingleRequest.</summary>
    public const int MaxControls = 4;

    public const int LinkRetries = 3;

    /// <summary>The first binary input outside class 1: 16 and 17 are in class 2, 18 and 19 in class 3.</summary>
    public const int BinaryClassOthers = 16;

    public static readonly TimeSpan SelectTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan UnsolTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan LinkTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long after the master sets the clock the device asks for the time
    /// again, device.ini's timesync.maxTimeBetweenSyncRequestsMs; and how much
    /// longer it trusts the clock: timesync.timeToUnsynchronizedMs is the two
    /// together.
    /// </summary>
    public static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan ClockHoldover = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Where each type's events go, as device.ini's *.eventsClass options say.
    /// Binary inputs from <see cref="BinaryClassOthers"/> up are the exception:
    /// 3.1r1's 8.26 reassigns the classes of binary inputs that start in class
    /// 2 and 3, so a few do, and the generators leave them alone.
    /// </summary>
    public static readonly IReadOnlyDictionary<Kind, Class> EventClass = new Dictionary<Kind, Class>
    {
        [Kind.Binary] = Class.Class1,
        [Kind.DoubleBit] = Class.Class1,
        [Kind.Counter] = Class.Class2,
        [Kind.FrozenCounter] = Class.Class2,
        [Kind.Analog] = Class.Class3,
    };

    /// <summary>
    /// Each type's event variation, where it is not the outstation's default.
    /// Subset level 3 has counter and analog events without time (g22v1,
    /// g23v1, g32v1); the defaults, with time, are level 4.
    /// </summary>
    private static readonly IReadOnlyDictionary<Kind, byte> EventVariation = new Dictionary<Kind, byte>
    {
        [Kind.Counter] = 1,
        [Kind.FrozenCounter] = 1,
        [Kind.Analog] = 1,
    };

    // The codes every binary output accepts. dnp3c's single-function and
    // complementary roles (device.ini's binaryOutput.*Point options) test only
    // that an output carries out the codes its role names, and the procedures
    // that send several commands at once send the primary code to every
    // installed output, so each output takes them all.
    private static readonly ControlCode CodeLatchOn = ControlCode.LatchOn;
    private static readonly ControlCode CodeLatchOff = ControlCode.LatchOff;
    private static readonly ControlCode CodePulseClose = ControlCode.Close | ControlCode.PulseOn;
    private static readonly ControlCode CodePulseTrip = ControlCode.Trip | ControlCode.PulseOn;

    private static readonly ControlCode[] CrobCodes =
        [ControlCode.PulseOn, CodeLatchOn, CodeLatchOff, CodePulseClose, CodePulseTrip];

    private readonly string _addr;
    private readonly IDnp3Logger _log;

    /// <summary>The subset level the device is configured for, which limits the variations it may report in.</summary>
    private readonly int _level;

    private readonly Lock _gate = new();
    private Settings _settings = new();

    /// <summary>
    /// The settings the session was started with, which say what points it
    /// has; <see cref="_settings"/> may have changed since, for the next start.
    /// </summary>
    private Settings _running = new();

    private OutstationSession? _sess;
    private DutClock? _clock;
    private TcpServerChannel? _ch;
    private CancellationTokenSource? _cts;
    private Task? _run;

    /// <summary>
    /// The inputs carried across a restart. A power cycle does not change what
    /// the plant is doing, so the device comes back reporting the values it
    /// had.
    /// </summary>
    private Snapshot? _values;

    // The remote supervisory state: the global switch, and the outputs
    // switched to local one by one.
    private bool _globalLocal;
    private HashSet<ushort> _localPoints = [];

    /// <summary>The outputs the master operated since the last check of them.</summary>
    private List<Operation> _operations = [];

    /// <summary>
    /// The values the device reports in group 0, kept to check a read against,
    /// and updated by the master's writes.
    /// </summary>
    private Dictionary<byte, DeviceAttribute> _attributes = [];

    private Tracker _track = new();

    public Device(string addr, int level, IDnp3Logger log)
    {
        _addr = addr;
        _level = level;
        _log = log;
    }

    /// <summary>The class a point starts in.</summary>
    public static Class PointClass(Kind k, int i)
    {
        if (k == Kind.Binary && i >= BinaryClassOthers)
        {
            return i < BinaryClassOthers + 2 ? Class.Class2 : Class.Class3;
        }

        return EventClass[k];
    }

    /// <summary>How many points of a kind the generators change.</summary>
    public static int Generated(Kind k) => k == Kind.Binary ? BinaryClassOthers : PointsPerType;

    /// <summary>The tracker, for a check to read. Callers take <see cref="Gate"/>.</summary>
    public Tracker Track => _track;

    public Lock Gate => _gate;

    /// <summary>The settings for the next start. Callers take <see cref="Gate"/>.</summary>
    public Settings NextSettings
    {
        get => _settings;
        set => _settings = value;
    }

    /// <summary>Changes cold restart support at once, and for the next start.</summary>
    public void SetColdRestart(bool supported)
    {
        lock (_gate)
        {
            _settings.NoColdRestart = !supported;
            _running.NoColdRestart = !supported;
            _clock?.SetColdRestart(supported);
        }
    }

    /// <summary>Puts the device back to how it ships, for the start of a procedure.</summary>
    public void Reset()
    {
        Stop();
        lock (_gate)
        {
            _settings = new Settings();
            _values = null;
            _globalLocal = false;
            _localPoints = [];
            _operations = [];
            _track = new Tracker();
        }

        Start();
    }

    /// <summary>Cycles the outstation's power with the current settings.</summary>
    public void Restart()
    {
        Stop();
        Start();
    }

    /// <summary>Brings the outstation up and returns once it is listening.</summary>
    public void Start()
    {
        lock (_gate)
        {
            var st = _settings.Copy();
            var clk = new DutClock(st.NoColdRestart);
            var cfg = new OutstationConfig
            {
                LocalAddr = OutstationAddr,
                RemoteAddr = st.Master,
                Database = new DatabaseConfig
                {
                    Binary = st.Count(Kind.Binary),
                    DoubleBitBinary = st.Count(Kind.DoubleBit),
                    Counter = st.Count(Kind.Counter),
                    FrozenCounter = st.Count(Kind.FrozenCounter),
                    Analog = st.Count(Kind.Analog),
                    BinaryOutputStatus = st.Count(Kind.BinaryOutput),
                    AnalogOutputStatus = st.Count(Kind.AnalogOutput),
                },
                Events = new EventBufferConfig { MaxEvents = MaxEvents },
                MaxTxFragment = st.FragmentSize,
                MaxRxFragment = FragmentSize,
                ConfirmTimeout = ConfirmTimeout,
                SelectTimeout = SelectTimeout,
                Clock = new ClockConfig { SyncInterval = SyncInterval, Holdover = ClockHoldover },
                MaxControlsPerRequest = MaxControls,
                Unsolicited = new UnsolicitedConfig
                {
                    Enabled = st.Unsolicited,
                    ConfirmTimeout = st.UnsolTimeout,
                    MaxRetries = st.UnsolRetries ?? 0,
                    UnlimitedRetries = st.UnsolRetries is null,

                    // Once the retries run out, the next series starts one
                    // confirm timeout later, as in the reference
                    // implementation the procedures were first run against.
                    RetryDelay = st.UnsolTimeout,
                },
                AttributeWrite = AttributeWritten,
                SelfAddress = st.SelfAddress,
                UseLinkConfirms = st.LinkConfirms,
                LinkRetries = LinkRetries,
                LinkTimeout = LinkTimeout,
                Log = _log,
            };
            foreach (var a in ConfiguredAttributes())
            {
                cfg.Attributes.Add(a);
            }

            cfg.WritableAttributes.Add(new AttributeId(0, 245));
            cfg.WritableAttributes.Add(new AttributeId(0, 247));
            _attributes = ExpectedAttributes(cfg);

            var sess = new OutstationSession(cfg, clk, this);
            Initialise(sess.Database, st, clk);
            if (_globalLocal || _localPoints.Count > 0)
            {
                sess.SetIndication(Indication.LocalControl, true);
            }

            var ch = new TcpServerChannel(_addr);
            var cts = new CancellationTokenSource();
            var run = Task.Run(async () =>
            {
                try
                {
                    await sess.RunAsync(ch, cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Log(Dnp3LogLevel.Error, "outstation stopped", ("err", ex.Message));
                }
            });

            // The harness connects as soon as this returns, without retrying,
            // so "listening" has to mean the socket is bound, not that RunAsync
            // was called.
            var deadline = Stopwatch.StartNew();
            while (ch.BoundAddress is null)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(5) || run.IsCompleted)
                {
                    cts.Cancel();
                    ch.Close();
                    run.Wait(TimeSpan.FromSeconds(5));
                    cts.Dispose();
                    throw new DutException($"outstation did not start listening on {_addr}");
                }

                Thread.Sleep(5);
            }

            (_sess, _clock, _ch, _cts, _run) = (sess, clk, ch, cts, run);
            _running = st;
        }
    }

    /// <summary>Powers the outstation off, keeping its inputs for the next start.</summary>
    public void Stop()
    {
        OutstationSession? sess;
        TcpServerChannel? ch;
        CancellationTokenSource? cts;
        Task? run;
        lock (_gate)
        {
            (sess, ch, cts, run) = (_sess, _ch, _cts, _run);
            (_sess, _ch, _cts, _run) = (null, null, null, null);
        }

        if (sess is null)
        {
            return;
        }

        var values = Read(sess);
        cts!.Cancel();
        ch!.Close();
        if (!run!.Wait(TimeSpan.FromSeconds(10)))
        {
            _log.Log(Dnp3LogLevel.Error, "the outstation did not stop within 10 s");
        }

        cts.Dispose();
        lock (_gate)
        {
            _values = values;
        }
    }

    /// <summary>Loads the points' values and configuration into a new database.</summary>
    /// <remarks>
    /// The values go in before the points are assigned to classes. Loaded the
    /// other way round, every point would raise an event for changing from the
    /// database's zero value to the value it already had before the restart.
    /// Caller holds <see cref="_gate"/>.
    /// </remarks>
    private void Initialise(Database db, Settings st, DutClock clk)
    {
        var online = Flags.Online;
        var at = Timestamp.Unsynchronized(clk.Now());
        var v = _values;

        T Kept<T>(List<T>? from, int i, T fallback) => from is not null && i < from.Count ? from[i] : fallback;

        for (ushort i = 0; i < st.Count(Kind.Binary); i++)
        {
            db.UpdateBinary(i, Kept(v?.Binary, i, new Binary(false, online, at)));
        }

        for (ushort i = 0; i < st.Count(Kind.DoubleBit); i++)
        {
            db.UpdateDoubleBit(i, Kept(v?.DoubleBit, i, new DoubleBitBinary(DoubleBit.DeterminedOff, online, at)));
        }

        for (ushort i = 0; i < st.Count(Kind.Counter); i++)
        {
            db.UpdateCounter(i, Kept(v?.Counter, i, new Counter(0, online, at)));
        }

        for (ushort i = 0; i < st.Count(Kind.FrozenCounter); i++)
        {
            db.UpdateFrozenCounter(i, Kept(v?.FrozenCounter, i, new FrozenCounter(0, online, at)));
        }

        for (ushort i = 0; i < st.Count(Kind.Analog); i++)
        {
            db.UpdateAnalog(i, Kept(v?.Analog, i, new Analog(0, online, at)));
        }

        for (ushort i = 0; i < st.Count(Kind.BinaryOutput); i++)
        {
            var val = Kept(v?.BinaryOutput, i, new BinaryOutputStatus(false, online, at));
            val = val with { Flags = IsLocal(i) ? val.Flags.Clear(Flags.Online) : val.Flags.Set(Flags.Online) };
            db.UpdateBinaryOutputStatus(i, val);
        }

        for (ushort i = 0; i < st.Count(Kind.AnalogOutput); i++)
        {
            db.UpdateAnalogOutputStatus(i, Kept(v?.AnalogOutput, i, new AnalogOutputStatus(0, online, at)));
        }

        // Subset level 2 has 16-bit analog output status (g40v2) only.
        if (_level == 2)
        {
            for (ushort i = 0; i < st.Count(Kind.AnalogOutput); i++)
            {
                db.Configure(PointType.AnalogOutputStatus, i, new PointConfig { StaticVariation = 2 });
            }
        }

        foreach (var k in EventClass.Keys)
        {
            for (ushort i = 0; i < st.Count(k); i++)
            {
                db.Configure(k.PointType(), i, new PointConfig
                {
                    Class = PointClass(k, i),
                    EventVariation = EventVariation.GetValueOrDefault(k),
                });
            }
        }
    }

    /// <summary>Applies <paramref name="fn"/> to the database and waits until it has been applied.</summary>
    /// <remarks>
    /// <see cref="OutstationSession.Update"/> returns before applying a batch
    /// while a connection is being served. The plugin's next step may be a
    /// check of what the batch did, so returning early would race it.
    /// </remarks>
    public void Update(Action<Database, Timestamp> fn)
    {
        OutstationSession? sess;
        DutClock? clk;
        lock (_gate)
        {
            (sess, clk) = (_sess, _clock);
        }

        if (sess is null || clk is null)
        {
            throw new DutException("the outstation is not running");
        }

        using var done = new ManualResetEventSlim();
        Exception? failed = null;
        sess.Update(db =>
        {
            try
            {
                fn(db, clk.Stamp());
            }
            catch (Exception ex)
            {
                failed = ex;
            }
            finally
            {
                done.Set();
            }
        });

        if (!done.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new DutException("the outstation did not apply an update within 10 s");
        }

        if (failed is not null)
        {
            throw new DutException(failed.Message);
        }
    }

    /// <summary>Reports whether a binary output is under local control. Caller holds <see cref="_gate"/>.</summary>
    private bool IsLocal(ushort index) => _globalLocal || _localPoints.Contains(index);

    /// <summary>
    /// Applies the remote supervisory state to the outputs' flags and the
    /// LOCAL_CONTROL indication.
    /// </summary>
    /// <remarks>
    /// In local mode an output clears ONLINE and keeps its state and every
    /// other flag (IEEE 1815-2012 Table 11-5): it is still there, it is just not
    /// the master's to command.
    /// </remarks>
    public void SetLocal(bool global, HashSet<ushort> points)
    {
        OutstationSession? sess;
        int n;
        lock (_gate)
        {
            (_globalLocal, _localPoints) = (global, points);
            sess = _sess;
            n = _running.Count(Kind.BinaryOutput);
        }

        if (sess is null)
        {
            throw new DutException("the outstation is not running");
        }

        sess.SetIndication(Indication.LocalControl, global || points.Count > 0);
        Update((db, stamp) =>
        {
            for (ushort i = 0; i < n; i++)
            {
                db.TryGetBinaryOutputStatus(i, out var v, out _);
                var local = global || points.Contains(i);
                db.UpdateBinaryOutputStatus(i, v with
                {
                    Flags = local ? v.Flags.Clear(Flags.Online) : v.Flags.Set(Flags.Online),
                });
            }
        });
    }

    // ---------- Commands ----------

    /// <summary>
    /// Says which outputs the device has: the ones installed. With every one
    /// uninstalled, the database has no output status points to go by.
    /// </summary>
    public bool HasOutput(PointType pt, ushort index)
    {
        var k = pt == PointType.AnalogOutputStatus ? Kind.AnalogOutput : Kind.BinaryOutput;
        lock (_gate)
        {
            return index < _running.Count(k);
        }
    }

    private CommandStatus CrobStatus(ushort index, ControlRelayOutputBlock c)
    {
        lock (_gate)
        {
            if (index >= _running.Count(Kind.BinaryOutput) || !CrobCodes.Contains(c.Code))
            {
                return CommandStatus.NotSupported;
            }

            return IsLocal(index) ? CommandStatus.Local : CommandStatus.Success;
        }
    }

    /// <summary>Reports whether an operate would be accepted.</summary>
    public CommandStatus SelectCrob(ushort index, ControlRelayOutputBlock c) => CrobStatus(index, c);

    /// <summary>
    /// Operates a binary output, moving its status the way the operation would
    /// move a latching relay: on for latch on and close, off for latch off and
    /// trip, and unchanged for a plain pulse, which ends where it started.
    /// </summary>
    public CommandStatus OperateCrob(ushort index, ControlRelayOutputBlock c, OperateType op)
    {
        var status = CrobStatus(index, c);
        if (status != CommandStatus.Success)
        {
            return status;
        }

        OutstationSession? sess;
        DutClock? clk;
        lock (_gate)
        {
            _operations.Add(new Operation(Kind.BinaryOutput, index));
            (sess, clk) = (_sess, _clock);
        }

        if (sess is null || clk is null)
        {
            return CommandStatus.Success;
        }

        // This runs on the session loop, between requests, so the database is
        // the handler's to change directly; Update from here would queue
        // behind the request being answered.
        var db = sess.Database;
        db.TryGetBinaryOutputStatus(index, out var v, out _);
        var value = v.Value;
        if (c.Code == CodeLatchOn || c.Code == CodePulseClose)
        {
            value = true;
        }
        else if (c.Code == CodeLatchOff || c.Code == CodePulseTrip)
        {
            value = false;
        }

        db.UpdateBinaryOutputStatus(index, v with { Value = value, Time = clk.Stamp() });
        return CommandStatus.Success;
    }

    private CommandStatus AnalogStatus(ushort index)
    {
        lock (_gate)
        {
            return index < _running.Count(Kind.AnalogOutput) ? CommandStatus.Success : CommandStatus.NotSupported;
        }
    }

    /// <summary>Reports whether an operate would be accepted.</summary>
    public CommandStatus SelectAnalog(ushort index, AnalogOutputCommand v) => AnalogStatus(index);

    /// <summary>Sets an analog output, whose status then reports the value.</summary>
    public CommandStatus OperateAnalog(ushort index, AnalogOutputCommand v, OperateType op)
    {
        var status = AnalogStatus(index);
        if (status != CommandStatus.Success)
        {
            return status;
        }

        OutstationSession? sess;
        DutClock? clk;
        lock (_gate)
        {
            _operations.Add(new Operation(Kind.AnalogOutput, index));
            (sess, clk) = (_sess, _clock);
        }

        if (sess is null || clk is null)
        {
            return CommandStatus.Success;
        }

        var db = sess.Database;
        db.TryGetAnalogOutputStatus(index, out var output, out _);
        db.UpdateAnalogOutputStatus(index, output with { Value = v.Value, Time = clk.Stamp() });
        return CommandStatus.Success;
    }

    /// <summary>
    /// Reports whether an output operated since its last check, and forgets
    /// that it did. The other operations stay for their own checks: a request
    /// that operates several outputs is checked one output at a time.
    /// </summary>
    public bool TakeOperation(Kind k, ushort index, out List<Operation> seen)
    {
        lock (_gate)
        {
            seen = [.. _operations];
            var i = _operations.FindIndex(op => op.Kind == k && op.Index == index);
            if (i < 0)
            {
                return false;
            }

            _operations.RemoveAt(i);
            return true;
        }
    }

    /// <summary>Returns the operations not yet checked, and forgets them.</summary>
    public List<Operation> TakeOperations()
    {
        lock (_gate)
        {
            var ops = _operations;
            _operations = [];
            return ops;
        }
    }

    // ---------- Attributes ----------

    /// <summary>
    /// The device attributes the device states about itself. The point counts
    /// and fragment sizes are the outstation's to derive.
    /// </summary>
    private static DeviceAttribute[] ConfiguredAttributes() =>
    [
        DeviceAttribute.String(242, "SharpDnp3"),
        DeviceAttribute.String(243, "software"),
        DeviceAttribute.String(245, "conformance bench"),
        DeviceAttribute.String(247, "SharpDnp3 dut"),
        DeviceAttribute.String(248, "0001"),
        DeviceAttribute.String(250, "SharpDnp3 outstation"),
        DeviceAttribute.String(252, "DSC Systems"),
    ];

    /// <summary>Everything a read of the standard set should report.</summary>
    private static Dictionary<byte, DeviceAttribute> ExpectedAttributes(OutstationConfig cfg)
    {
        static DeviceAttribute Uint(byte v, int n) => DeviceAttribute.Uint(v, (ulong)n);

        var db = cfg.Database;
        var output = new Dictionary<byte, DeviceAttribute>
        {
            [240] = Uint(240, cfg.MaxTxFragment),
            [241] = Uint(241, cfg.MaxRxFragment),
        };
        foreach (var (v, n) in new (byte, int)[]
        {
            (239, db.Binary),
            (236, db.DoubleBitBinary),
            (229, db.Counter),
            (233, db.Analog),
            (224, db.BinaryOutputStatus),
            (221, db.AnalogOutputStatus),
        })
        {
            if (n > 0)
            {
                output[v] = Uint(v, n);
            }
        }

        foreach (var a in cfg.Attributes)
        {
            output[a.Variation] = a;
        }

        return output;
    }

    /// <summary>The attribute values to check a read against.</summary>
    public Dictionary<byte, DeviceAttribute> Attributes()
    {
        lock (_gate)
        {
            return new Dictionary<byte, DeviceAttribute>(_attributes);
        }
    }

    /// <summary>
    /// Keeps the expected values in step with the master's writes, which the
    /// outstation stores.
    /// </summary>
    private bool AttributeWritten(DeviceAttribute a)
    {
        lock (_gate)
        {
            if (a.Set == 0)
            {
                _attributes[a.Variation] = a;
            }

            return true;
        }
    }

    // ---------- Snapshots and changes ----------

    /// <summary>Reads every point of a running session.</summary>
    private Snapshot Read(OutstationSession sess)
    {
        Settings st;
        Snapshot? kept;
        lock (_gate)
        {
            (st, kept) = (_running, _values);
        }

        // The getters take the database's own lock, so a read from here cannot
        // tear a value. What it can do is land between two halves of a batch,
        // which is what Update is for, but a stopping session may no longer be
        // applying batches, and the plugin never reads while one is in flight.
        var db = sess.Database;
        var s = new Snapshot();
        for (ushort i = 0; i < st.Count(Kind.Binary); i++)
        {
            db.TryGetBinary(i, out var v, out _);
            s.Binary.Add(v);
        }

        for (ushort i = 0; i < st.Count(Kind.DoubleBit); i++)
        {
            db.TryGetDoubleBit(i, out var v, out _);
            s.DoubleBit.Add(v);
        }

        for (ushort i = 0; i < st.Count(Kind.Counter); i++)
        {
            db.TryGetCounter(i, out var v, out _);
            s.Counter.Add(v);
        }

        for (ushort i = 0; i < st.Count(Kind.FrozenCounter); i++)
        {
            db.TryGetFrozenCounter(i, out var v, out _);
            s.FrozenCounter.Add(v);
        }

        for (ushort i = 0; i < st.Count(Kind.Analog); i++)
        {
            db.TryGetAnalog(i, out var v, out _);
            s.Analog.Add(v);
        }

        for (ushort i = 0; i < st.Count(Kind.BinaryOutput); i++)
        {
            db.TryGetBinaryOutputStatus(i, out var v, out _);
            s.BinaryOutput.Add(v);
        }

        for (ushort i = 0; i < st.Count(Kind.AnalogOutput); i++)
        {
            db.TryGetAnalogOutputStatus(i, out var v, out _);
            s.AnalogOutput.Add(v);
        }

        // Uninstalled points keep their values for when they come back.
        if (kept is not null)
        {
            if (st.Count(Kind.Binary) == 0)
            {
                s.Binary = kept.Binary;
            }

            if (st.Count(Kind.DoubleBit) == 0)
            {
                s.DoubleBit = kept.DoubleBit;
            }

            if (st.Count(Kind.Counter) == 0)
            {
                s.Counter = kept.Counter;
            }

            if (st.Count(Kind.FrozenCounter) == 0)
            {
                s.FrozenCounter = kept.FrozenCounter;
            }

            if (st.Count(Kind.Analog) == 0)
            {
                s.Analog = kept.Analog;
            }

            if (st.Count(Kind.BinaryOutput) == 0)
            {
                s.BinaryOutput = kept.BinaryOutput;
            }

            if (st.Count(Kind.AnalogOutput) == 0)
            {
                s.AnalogOutput = kept.AnalogOutput;
            }
        }

        return s;
    }

    /// <summary>A snapshot of the running outstation.</summary>
    public Snapshot Current()
    {
        OutstationSession? sess;
        lock (_gate)
        {
            sess = _sess;
        }

        return sess is null ? throw new DutException("the outstation is not running") : Read(sess);
    }

    /// <summary>
    /// Makes the changes as one batch, recording the events they raise. Only
    /// points in an event class raise one: a change of an output's status, or
    /// of a point that is uninstalled, is a change and nothing more.
    /// </summary>
    public List<DutEvent> Apply(IReadOnlyList<Change> changes)
    {
        DutClock? clk;
        lock (_gate)
        {
            clk = _clock;
        }

        var batch = new List<DutEvent>();
        Update((db, _) =>
        {
            foreach (var c in changes)
            {
                if (ApplyOne(db, clk!, c) is { } ev)
                {
                    batch.Add(ev);
                }
            }
        });

        lock (_gate)
        {
            _track.Add(batch);
        }

        return batch;
    }

    /// <summary>Makes one change and reports the event it raised, if any.</summary>
    private static DutEvent? ApplyOne(Database db, DutClock clk, Change c)
    {
        var at = clk.Stamp();
        var i = (ushort)c.Index;
        Flags FlagsOr(Flags f) => c.Flags is { } replace ? new Flags(replace) : f;

        double value;
        Flags flags;
        bool changed;
        switch (c.Kind)
        {
            case Kind.Binary:
            {
                if (!db.TryGetBinary(i, out var v, out _))
                {
                    return null;
                }

                var n = new Binary(c.Next(Snapshot.Num(v.Value)) != 0, FlagsOr(v.Flags), at);
                changed = n.Value != v.Value || n.Flags != v.Flags;
                db.UpdateBinary(i, n);
                (value, flags) = (Snapshot.Num(n.Value), n.Flags);
                break;
            }

            case Kind.DoubleBit:
            {
                if (!db.TryGetDoubleBit(i, out var v, out _))
                {
                    return null;
                }

                var n = new DoubleBitBinary((DoubleBit)(byte)c.Next((double)v.Value), FlagsOr(v.Flags), at);
                changed = n.Value != v.Value || n.Flags != v.Flags;
                db.UpdateDoubleBit(i, n);
                (value, flags) = ((double)n.Value, n.Flags);
                break;
            }

            case Kind.Counter:
            {
                if (!db.TryGetCounter(i, out var v, out _))
                {
                    return null;
                }

                var n = new Counter((uint)c.Next(v.Value), FlagsOr(v.Flags), at);
                changed = n.Value != v.Value || n.Flags != v.Flags;
                db.UpdateCounter(i, n);
                (value, flags) = (n.Value, n.Flags);
                break;
            }

            case Kind.FrozenCounter:
            {
                if (!db.TryGetFrozenCounter(i, out var v, out _))
                {
                    return null;
                }

                var n = new FrozenCounter((uint)c.Next(v.Value), FlagsOr(v.Flags), at);
                changed = n.Value != v.Value || n.Flags != v.Flags;
                db.UpdateFrozenCounter(i, n);
                (value, flags) = (n.Value, n.Flags);
                break;
            }

            case Kind.Analog:
            {
                if (!db.TryGetAnalog(i, out var v, out _))
                {
                    return null;
                }

                var n = new Analog(c.Next(v.Value), FlagsOr(v.Flags), at);
                changed = n.Value != v.Value || n.Flags != v.Flags;
                db.UpdateAnalog(i, n);
                (value, flags) = (n.Value, n.Flags);
                break;
            }

            case Kind.BinaryOutput:
            {
                if (db.TryGetBinaryOutputStatus(i, out var v, out _))
                {
                    db.UpdateBinaryOutputStatus(
                        i, new BinaryOutputStatus(c.Next(Snapshot.Num(v.Value)) != 0, FlagsOr(v.Flags), at));
                }

                return null;
            }

            case Kind.AnalogOutput:
            {
                if (db.TryGetAnalogOutputStatus(i, out var v, out _))
                {
                    db.UpdateAnalogOutputStatus(i, new AnalogOutputStatus(c.Next(v.Value), FlagsOr(v.Flags), at));
                }

                return null;
            }

            default:
                return null;
        }

        return changed && EventClass.ContainsKey(c.Kind)
            ? new DutEvent(c.Kind, c.Index, value, flags.Value, at.Time)
            : null;
    }
}

/// <summary>
/// The outstation's time: the host's, corrected by the master's writes, and
/// unsynchronized until the first one.
/// </summary>
internal sealed class DutClock(bool noColdRestart) : IOutstationApplication, IRestartSupport
{
    private readonly Lock _gate = new();
    private TimeSpan _offset;
    private long? _syncedAt;
    private DateTimeOffset _last;
    private bool _noCold = noColdRestart;

    public DateTimeOffset Now()
    {
        lock (_gate)
        {
            return DateTimeOffset.UtcNow + _offset;
        }
    }

    public bool WriteAbsoluteTime(DateTimeOffset t)
    {
        lock (_gate)
        {
            _offset = t - DateTimeOffset.UtcNow;
            _syncedAt = Stopwatch.GetTimestamp();
            return true;
        }
    }

    public TimeSpan ColdRestart() => TimeSpan.FromSeconds(1);

    public TimeSpan WarmRestart() => TimeSpan.FromSeconds(1);

    public bool SupportsWriteTime() => true;

    public bool SupportsColdRestart()
    {
        lock (_gate)
        {
            return !_noCold;
        }
    }

    public bool SupportsWarmRestart() => true;

    public void SetColdRestart(bool supported)
    {
        lock (_gate)
        {
            _noCold = !supported;
        }
    }

    /// <summary>
    /// The time to give a change made now. Successive stamps are at least a
    /// millisecond apart, the resolution DNP3 carries, so events generated in a
    /// burst still have an order the master can see.
    /// </summary>
    public Timestamp Stamp()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.FromUnixTimeMilliseconds((DateTimeOffset.UtcNow + _offset).ToUnixTimeMilliseconds());
            if (now <= _last)
            {
                now = _last.AddMilliseconds(1);
            }

            _last = now;

            // Synchronized for as long as the outstation says so, which is
            // until the holdover after the sync interval.
            return _syncedAt is { } at && Stopwatch.GetElapsedTime(at) < Device.SyncInterval + Device.ClockHoldover
                ? Timestamp.Now(now)
                : Timestamp.Unsynchronized(now);
        }
    }
}
