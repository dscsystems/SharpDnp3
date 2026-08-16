// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The DNP3 outstation role: the device that holds measurements, answers a
// master's polls, and executes its commands.

using SharpDnp3.Objects;

namespace SharpDnp3.Outstation;

/// <summary>Describes how one point is reported.</summary>
public record struct PointConfig
{
    /// <summary>
    /// The event class the point's events are assigned to.
    /// <see cref="Class.None"/> suppresses events for the point entirely.
    /// </summary>
    public Class Class { get; set; }

    /// <summary>
    /// The variation used when the point is reported in response to a class 0
    /// or range read.
    /// </summary>
    public byte StaticVariation { get; set; }

    /// <summary>
    /// The variation used when the point's events are reported.
    /// </summary>
    public byte EventVariation { get; set; }

    /// <summary>
    /// How far an analog or counter must move before it generates an event. It
    /// is ignored for binary types, which event on any change.
    /// </summary>
    public double Deadband { get; set; }
}

/// <summary>
/// Sizes the database and sets the defaults every point starts with.
/// </summary>
public sealed class DatabaseConfig
{
    /// <summary>How many binary inputs.</summary>
    public int Binary { get; set; }

    /// <summary>How many double-bit binary inputs.</summary>
    public int DoubleBitBinary { get; set; }

    /// <summary>How many counters.</summary>
    public int Counter { get; set; }

    /// <summary>How many frozen counters.</summary>
    public int FrozenCounter { get; set; }

    /// <summary>How many analog inputs.</summary>
    public int Analog { get; set; }

    /// <summary>How many binary output status points.</summary>
    public int BinaryOutputStatus { get; set; }

    /// <summary>How many analog output status points.</summary>
    public int AnalogOutputStatus { get; set; }

    /// <summary>How many octet string points.</summary>
    public int OctetString { get; set; }

    /// <summary>
    /// Applied to every point at construction. A caller changes individual
    /// points afterwards with <see cref="Database.Configure"/>.
    /// </summary>
    public Class DefaultClass { get; set; }
}

/// <summary>
/// Pairs a value with its configuration and the last value reported, so
/// deadbands are measured against what the master was actually told.
/// </summary>
internal sealed class Point<T>
{
    public T Value = default!;

    public PointConfig Config;

    public double Reported;

    public bool HasEvent;

    /// <summary>
    /// Decides whether an update warrants an event, and records that the point
    /// has now been reported at least once.
    /// </summary>
    /// <remarks>
    /// The first-update latch is set unconditionally rather than inside the
    /// deadband branch. Folding it into a
    /// <c>flagsChanged || ExceedsDeadband(...)</c> expression looks equivalent
    /// and is not: C# short-circuits, so an update that changed the flags never
    /// reaches the deadband check, never sets the latch, and the <em>next</em>
    /// update then reports as if it were the first — firing an event no matter
    /// how small the move. That is a deadband that silently does nothing on
    /// every other update.
    /// </remarks>
    public bool ShouldReport(double v, bool flagsChanged)
    {
        var first = !HasEvent;
        HasEvent = true;

        if (first || flagsChanged)
        {
            Reported = v;
            return true;
        }

        var delta = Math.Abs(v - Reported);
        if (delta > Config.Deadband)
        {
            Reported = v;
            return true;
        }

        return false;
    }
}

/// <summary>Holds an outstation's measurements.</summary>
/// <remarks>
/// Access goes through <c>OutstationSession.Update</c> and the session's own
/// loop, which serialises it.
/// </remarks>
public sealed class Database
{
    private readonly Point<Binary>[] _binary;
    private readonly Point<DoubleBitBinary>[] _doubleBit;
    private readonly Point<Counter>[] _counter;
    private readonly Point<FrozenCounter>[] _frozen;
    private readonly Point<Analog>[] _analog;
    private readonly Point<BinaryOutputStatus>[] _binaryOut;
    private readonly Point<AnalogOutputStatus>[] _analogOut;
    private readonly Point<byte[]>[] _octet;

    private readonly EventBuffer? _events;

    /// <summary>The event buffer this database raises events into.</summary>
    public EventBuffer? Events => _events;

    /// <summary>
    /// Guards reads taken outside the session loop, such as a diagnostic
    /// snapshot.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// The variations used when a point's configuration does not name one.
    /// </summary>
    /// <remarks>
    /// They are the widest lossless encoding for each type, which is the safe
    /// default: a narrower one silently truncates.
    /// </remarks>
    private static byte DefaultStaticVariation(PointType pt) => pt switch
    {
        PointType.Binary => 2,             // g1v2, with flags
        PointType.DoubleBitBinary => 2,    // g3v2
        PointType.Counter => 1,            // g20v1, 32-bit with flags
        PointType.FrozenCounter => 1,      // g21v1
        PointType.Analog => 1,             // g30v1, 32-bit with flags
        PointType.BinaryOutputStatus => 2, // g10v2
        PointType.AnalogOutputStatus => 1, // g40v1
        _ => 0,
    };

    private static byte DefaultEventVariation(PointType pt) => pt switch
    {
        PointType.Binary => 2,             // g2v2, with absolute time
        PointType.DoubleBitBinary => 2,    // g4v2
        PointType.Counter => 5,            // g22v5, with time
        PointType.FrozenCounter => 5,      // g23v5
        PointType.Analog => 3,             // g32v3, 32-bit with time
        PointType.BinaryOutputStatus => 2, // g11v2
        PointType.AnalogOutputStatus => 3, // g42v3
        _ => 0,
    };

    /// <summary>Builds a database from a configuration.</summary>
    public Database(DatabaseConfig config, EventBuffer? events = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        _events = events;
        _binary = MakePoints<Binary>(config.Binary, PointType.Binary, config.DefaultClass);
        _doubleBit = MakePoints<DoubleBitBinary>(
            config.DoubleBitBinary, PointType.DoubleBitBinary, config.DefaultClass);
        _counter = MakePoints<Counter>(config.Counter, PointType.Counter, config.DefaultClass);
        _frozen = MakePoints<FrozenCounter>(
            config.FrozenCounter, PointType.FrozenCounter, config.DefaultClass);
        _analog = MakePoints<Analog>(config.Analog, PointType.Analog, config.DefaultClass);
        _binaryOut = MakePoints<BinaryOutputStatus>(
            config.BinaryOutputStatus, PointType.BinaryOutputStatus, config.DefaultClass);
        _analogOut = MakePoints<AnalogOutputStatus>(
            config.AnalogOutputStatus, PointType.AnalogOutputStatus, config.DefaultClass);
        _octet = MakePoints<byte[]>(config.OctetString, PointType.OctetString, config.DefaultClass);
    }

    private static Point<T>[] MakePoints<T>(int n, PointType pt, Class cls)
    {
        var pts = new Point<T>[Math.Max(n, 0)];
        for (var i = 0; i < pts.Length; i++)
        {
            pts[i] = new Point<T>
            {
                Config = new PointConfig
                {
                    Class = cls,
                    StaticVariation = DefaultStaticVariation(pt),
                    EventVariation = DefaultEventVariation(pt),
                },
            };
        }

        return pts;
    }

    /// <summary>Returns how many points of each type the database holds.</summary>
    public DatabaseConfig Counts() => new()
    {
        Binary = _binary.Length,
        DoubleBitBinary = _doubleBit.Length,
        Counter = _counter.Length,
        FrozenCounter = _frozen.Length,
        Analog = _analog.Length,
        BinaryOutputStatus = _binaryOut.Length,
        AnalogOutputStatus = _analogOut.Length,
        OctetString = _octet.Length,
    };

    /// <summary>Sets the reporting configuration for one point.</summary>
    /// <remarks>
    /// It is a no-op for an index the database does not have, so a
    /// configuration file listing a point that was removed does not fault the
    /// outstation at startup.
    /// </remarks>
    public bool Configure(PointType pt, ushort index, PointConfig config)
    {
        lock (_gate)
        {
            return pt switch
            {
                PointType.Binary => SetConfig(_binary, index, config),
                PointType.DoubleBitBinary => SetConfig(_doubleBit, index, config),
                PointType.Counter => SetConfig(_counter, index, config),
                PointType.FrozenCounter => SetConfig(_frozen, index, config),
                PointType.Analog => SetConfig(_analog, index, config),
                PointType.BinaryOutputStatus => SetConfig(_binaryOut, index, config),
                PointType.AnalogOutputStatus => SetConfig(_analogOut, index, config),
                PointType.OctetString => SetConfig(_octet, index, config),
                _ => false,
            };
        }
    }

    private static bool SetConfig<T>(Point<T>[] pts, ushort index, PointConfig config)
    {
        if (index >= pts.Length)
        {
            return false;
        }

        if (config.StaticVariation == 0)
        {
            config.StaticVariation = pts[index].Config.StaticVariation;
        }

        if (config.EventVariation == 0)
        {
            config.EventVariation = pts[index].Config.EventVariation;
        }

        pts[index].Config = config;
        return true;
    }

    /// <summary>
    /// Sets the event class of every point of a type, which is what the
    /// ASSIGN_CLASS function code does.
    /// </summary>
    public void AssignClass(PointType pt, Class cls)
    {
        lock (_gate)
        {
            switch (pt)
            {
                case PointType.Binary: AssignClass(_binary, cls); break;
                case PointType.DoubleBitBinary: AssignClass(_doubleBit, cls); break;
                case PointType.Counter: AssignClass(_counter, cls); break;
                case PointType.FrozenCounter: AssignClass(_frozen, cls); break;
                case PointType.Analog: AssignClass(_analog, cls); break;
                case PointType.BinaryOutputStatus: AssignClass(_binaryOut, cls); break;
                case PointType.AnalogOutputStatus: AssignClass(_analogOut, cls); break;
                case PointType.OctetString: AssignClass(_octet, cls); break;
                default: break;
            }
        }
    }

    private static void AssignClass<T>(Point<T>[] pts, Class cls)
    {
        foreach (var p in pts)
        {
            p.Config = p.Config with { Class = cls };
        }
    }

    // ---------- Updates ----------

    /// <summary>
    /// Sets a binary input, generating an event if the value or its quality
    /// changed and the point is assigned to an event class.
    /// </summary>
    public void UpdateBinary(ushort index, Binary v)
    {
        lock (_gate)
        {
            if (index >= _binary.Length)
            {
                return;
            }

            var p = _binary[index];
            var changed = p.Value.Value != v.Value || p.Value.Flags != v.Flags;
            p.Value = v;
            if (changed)
            {
                Raise(p.Config, new Event
                {
                    Type = PointType.Binary,
                    Index = index,
                    Variation = p.Config.EventVariation,
                    Binary = v,
                    Time = v.Time,
                });
            }
        }
    }

    /// <summary>Sets a double-bit binary input.</summary>
    public void UpdateDoubleBit(ushort index, DoubleBitBinary v)
    {
        lock (_gate)
        {
            if (index >= _doubleBit.Length)
            {
                return;
            }

            var p = _doubleBit[index];
            var changed = p.Value.Value != v.Value || p.Value.Flags != v.Flags;
            p.Value = v;
            if (changed)
            {
                Raise(p.Config, new Event
                {
                    Type = PointType.DoubleBitBinary,
                    Index = index,
                    Variation = p.Config.EventVariation,
                    DoubleBit = v,
                    Time = v.Time,
                });
            }
        }
    }

    /// <summary>Sets a binary output's reported state.</summary>
    public void UpdateBinaryOutputStatus(ushort index, BinaryOutputStatus v)
    {
        lock (_gate)
        {
            if (index >= _binaryOut.Length)
            {
                return;
            }

            var p = _binaryOut[index];
            var changed = p.Value.Value != v.Value || p.Value.Flags != v.Flags;
            p.Value = v;
            if (changed)
            {
                Raise(p.Config, new Event
                {
                    Type = PointType.BinaryOutputStatus,
                    Index = index,
                    Variation = p.Config.EventVariation,
                    BinaryOutput = v,
                    Time = v.Time,
                });
            }
        }
    }

    /// <summary>Sets a counter.</summary>
    public void UpdateCounter(ushort index, Counter v)
    {
        lock (_gate)
        {
            if (index >= _counter.Length)
            {
                return;
            }

            var p = _counter[index];
            var flagsChanged = p.Value.Flags != v.Flags;
            var valueChanged = p.Value.Value != v.Value;
            p.Value = v;

            if ((flagsChanged || valueChanged) && p.ShouldReport(v.Value, flagsChanged))
            {
                Raise(p.Config, new Event
                {
                    Type = PointType.Counter,
                    Index = index,
                    Variation = p.Config.EventVariation,
                    Counter = v,
                    Time = v.Time,
                });
            }
        }
    }

    /// <summary>Sets a frozen counter.</summary>
    public void UpdateFrozenCounter(ushort index, FrozenCounter v)
    {
        lock (_gate)
        {
            if (index >= _frozen.Length)
            {
                return;
            }

            var p = _frozen[index];
            var changed = p.Value.Value != v.Value || p.Value.Flags != v.Flags;
            p.Value = v;
            if (changed)
            {
                Raise(p.Config, new Event
                {
                    Type = PointType.FrozenCounter,
                    Index = index,
                    Variation = p.Config.EventVariation,
                    FrozenCounter = v,
                    Time = v.Time,
                });
            }
        }
    }

    /// <summary>Sets an analog input.</summary>
    /// <remarks>
    /// An event is generated when the value moves further than the deadband
    /// from the value last <em>reported</em>, not from the value last stored.
    /// Comparing against the stored value lets a point drift indefinitely in
    /// deadband-sized steps without ever reporting — the classic implementation
    /// bug, and one that hides a slow ramp toward a limit.
    /// </remarks>
    public void UpdateAnalog(ushort index, Analog v)
    {
        lock (_gate)
        {
            if (index >= _analog.Length)
            {
                return;
            }

            var p = _analog[index];
            var flagsChanged = p.Value.Flags != v.Flags;
            p.Value = v;

            if (p.ShouldReport(v.Value, flagsChanged))
            {
                Raise(p.Config, new Event
                {
                    Type = PointType.Analog,
                    Index = index,
                    Variation = p.Config.EventVariation,
                    Analog = v,
                    Time = v.Time,
                });
            }
        }
    }

    /// <summary>Sets an analog output's reported value.</summary>
    public void UpdateAnalogOutputStatus(ushort index, AnalogOutputStatus v)
    {
        lock (_gate)
        {
            if (index >= _analogOut.Length)
            {
                return;
            }

            var p = _analogOut[index];
            var flagsChanged = p.Value.Flags != v.Flags;
            p.Value = v;

            if (p.ShouldReport(v.Value, flagsChanged))
            {
                Raise(p.Config, new Event
                {
                    Type = PointType.AnalogOutputStatus,
                    Index = index,
                    Variation = p.Config.EventVariation,
                    AnalogOutput = v,
                    Time = v.Time,
                });
            }
        }
    }

    /// <summary>Sets an octet string point.</summary>
    /// <remarks>
    /// Octet strings are how a device reports the things that are text rather
    /// than measurements: a point name, a firmware version, a serial number.
    /// Their encoding is unusual — the variation number <em>is</em> the length
    /// — so changing the string's length changes the variation the outstation
    /// reports it in, which is legal and which masters must cope with.
    /// </remarks>
    public void UpdateOctetString(ushort index, ReadOnlySpan<byte> v)
    {
        lock (_gate)
        {
            if (index >= _octet.Length)
            {
                return;
            }

            if (v.Length > OctetString.MaxOctetStringLen)
            {
                // The length has to fit a variation number. Truncating loses
                // data, but refusing silently would lose all of it.
                v = v[..OctetString.MaxOctetStringLen];
            }

            var p = _octet[index];
            var changed = p.Value is null || !v.SequenceEqual(p.Value);
            p.Value = v.ToArray();

            if (changed)
            {
                Raise(p.Config, new Event
                {
                    Type = PointType.OctetString,
                    Index = index,
                    Variation = (byte)v.Length,
                    OctetString = p.Value,
                });
            }
        }
    }

    /// <summary>Queues an event if the point is assigned to an event class.</summary>
    private void Raise(PointConfig config, Event e)
    {
        if (_events is null || config.Class == Class.None)
        {
            return;
        }

        e.Class = config.Class;
        _events.Add(e);
    }

    // ---------- Reads ----------

    /// <summary>Returns a binary input and whether the index exists.</summary>
    public bool TryGetBinary(ushort index, out Binary value, out PointConfig config)
    {
        lock (_gate)
        {
            return Get(_binary, index, out value, out config);
        }
    }

    /// <summary>Returns a double-bit binary input.</summary>
    public bool TryGetDoubleBit(ushort index, out DoubleBitBinary value, out PointConfig config)
    {
        lock (_gate)
        {
            return Get(_doubleBit, index, out value, out config);
        }
    }

    /// <summary>Returns a counter.</summary>
    public bool TryGetCounter(ushort index, out Counter value, out PointConfig config)
    {
        lock (_gate)
        {
            return Get(_counter, index, out value, out config);
        }
    }

    /// <summary>Returns a frozen counter.</summary>
    public bool TryGetFrozenCounter(ushort index, out FrozenCounter value, out PointConfig config)
    {
        lock (_gate)
        {
            return Get(_frozen, index, out value, out config);
        }
    }

    /// <summary>Returns an analog input.</summary>
    public bool TryGetAnalog(ushort index, out Analog value, out PointConfig config)
    {
        lock (_gate)
        {
            return Get(_analog, index, out value, out config);
        }
    }

    /// <summary>Returns a binary output's state.</summary>
    public bool TryGetBinaryOutputStatus(
        ushort index, out BinaryOutputStatus value, out PointConfig config)
    {
        lock (_gate)
        {
            return Get(_binaryOut, index, out value, out config);
        }
    }

    /// <summary>Returns an analog output's value.</summary>
    public bool TryGetAnalogOutputStatus(
        ushort index, out AnalogOutputStatus value, out PointConfig config)
    {
        lock (_gate)
        {
            return Get(_analogOut, index, out value, out config);
        }
    }

    /// <summary>Returns an octet string point.</summary>
    public bool TryGetOctetString(ushort index, out byte[] value, out PointConfig config)
    {
        lock (_gate)
        {
            var ok = Get(_octet, index, out var v, out config);
            value = v ?? [];
            return ok;
        }
    }

    private static bool Get<T>(Point<T>[] pts, ushort index, out T value, out PointConfig config)
    {
        if (index >= pts.Length)
        {
            value = default!;
            config = default;
            return false;
        }

        value = pts[index].Value;
        config = pts[index].Config;
        return true;
    }

    /// <summary>
    /// Copies every counter into its frozen counterpart, which is what the
    /// freeze function codes do.
    /// </summary>
    public void FreezeCounters()
    {
        lock (_gate)
        {
            var n = Math.Min(_counter.Length, _frozen.Length);
            for (var i = 0; i < n; i++)
            {
                var c = _counter[i].Value;
                _frozen[i].Value = new FrozenCounter(c.Value, c.Flags, c.Time);
            }
        }
    }

    /// <summary>
    /// Returns the group and variation a point is reported with for a static
    /// read.
    /// </summary>
    internal static GroupVar StaticGroupVar(PointType pt, byte variation)
    {
        byte group = pt switch
        {
            PointType.Binary => 1,
            PointType.DoubleBitBinary => 3,
            PointType.Counter => 20,
            PointType.FrozenCounter => 21,
            PointType.Analog => 30,
            PointType.BinaryOutputStatus => 10,
            PointType.AnalogOutputStatus => 40,
            PointType.OctetString => 110,
            _ => 0,
        };

        return GroupVar.GV(group, variation);
    }
}
