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

    /// <summary>
    /// The class of the command events (groups 13 and 43) recorded when a
    /// control is operated on this output point. It applies to binary and
    /// analog output status points only, and <see cref="Class.None"/>, the
    /// default, records none.
    /// </summary>
    public Class CommandEventClass { get; set; }

    /// <summary>
    /// The group 13 or 43 variation those events are reported in. Zero mirrors
    /// the command: the variation with a time whose value has the width of the
    /// command's own.
    /// </summary>
    public byte CommandEventVariation { get; set; }
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

    /// <summary>How many frozen analog inputs, which have storage of their own.</summary>
    public int FrozenAnalog { get; set; }

    /// <summary>How many indexed time-and-interval values (group 50 variation 4).</summary>
    public int TimeAndInterval { get; set; }

    /// <summary>How many virtual terminal ports.</summary>
    public int VirtualTerminal { get; set; }

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

        // Every comparison against NaN is false, so the deadband check below
        // can neither see a reading become NaN nor recover from one: a point
        // whose last reported value was NaN would never report again. A move
        // into or out of NaN is a change by definition; NaN to NaN is not.
        var nowNaN = double.IsNaN(v);
        var wasNaN = double.IsNaN(Reported);
        if (nowNaN || wasNaN)
        {
            if (nowNaN == wasNaN)
            {
                return false;
            }

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
public sealed partial class Database
{
    private readonly Point<Binary>[] _binary;
    private readonly Point<DoubleBitBinary>[] _doubleBit;
    private readonly Point<Counter>[] _counter;
    private readonly Point<FrozenCounter>[] _frozen;
    private readonly Point<Analog>[] _analog;
    private readonly Point<Analog>[] _frozenAnalog;
    private readonly TimeAndInterval[] _timeAndInterval;
    private readonly Point<byte[]>[] _terminal;
    private readonly Point<BinaryOutputStatus>[] _binaryOut;
    private readonly Point<AnalogOutputStatus>[] _analogOut;
    private readonly Point<byte[]>[] _octet;

    private readonly EventBuffer? _events;

    /// <summary>
    /// The buffers of the masters currently attached, replaced wholesale so a
    /// reader never sees a half-built list.
    /// </summary>
    private EventBuffer[] _subscribers = [];

    /// <summary>
    /// The event queue used while no master is attached.
    /// </summary>
    /// <remarks>
    /// Events do not stop happening because nobody is connected, and a master
    /// that reconnects wants the ones it missed. They accumulate here and the
    /// next master to attach takes them; while masters are attached this stays
    /// empty and each of them holds its own queue instead.
    /// </remarks>
    public EventBuffer? Events => _events;

    /// <summary>
    /// Guards reads taken outside the session loop, such as a diagnostic
    /// snapshot.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// Takes the database's lock for a batch of related work.
    /// </summary>
    /// <remarks>
    /// Every individual accessor locks already, so this is not about safety —
    /// it is about a set of updates being visible together. A session serving
    /// several masters builds each response inside this scope, so a master
    /// cannot read a breaker as open while the alarm that went with it is still
    /// half applied.
    /// </remarks>
    internal Lock.Scope EnterScope() => _gate.EnterScope();

    /// <summary>Attaches a master's event buffer, handing it whatever is queued.</summary>
    internal void Subscribe(EventBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        lock (_gate)
        {
            var next = new EventBuffer[_subscribers.Length + 1];
            _subscribers.CopyTo(next, 0);
            next[^1] = buffer;
            _subscribers = next;

            // Whatever piled up while nothing was attached belongs to whoever
            // attaches next. A master joining an outstation that already has
            // one finds nothing here, which is right: it has just connected,
            // and its integrity poll is what gives it the present state.
            if (_events is null)
            {
                return;
            }

            var backlog = _events.Drain(out var overflowed);
            if (backlog.Count > 0 || overflowed)
            {
                buffer.Seed(backlog, overflowed);
            }
        }
    }

    /// <summary>Detaches a master's event buffer.</summary>
    /// <remarks>
    /// The last one out puts its queue back, so events a master never got are
    /// still there when it reconnects rather than being lost with its socket.
    /// </remarks>
    internal void Unsubscribe(EventBuffer buffer)
    {
        lock (_gate)
        {
            var next = new List<EventBuffer>(_subscribers.Length);
            foreach (var b in _subscribers)
            {
                if (!ReferenceEquals(b, buffer))
                {
                    next.Add(b);
                }
            }

            _subscribers = [.. next];

            var queued = buffer.Drain(out var overflowed);
            if (_subscribers.Length == 0 && _events is not null)
            {
                _events.Seed(queued, overflowed);
            }
        }
    }

    /// <summary>Empties every event queue, attached or not.</summary>
    internal void ResetEvents()
    {
        lock (_gate)
        {
            _events?.Reset();
            foreach (var b in _subscribers)
            {
                b.Reset();
            }
        }
    }

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
        PointType.FrozenAnalog => 1,       // g31v1
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
        PointType.FrozenAnalog => 3,       // g33v3
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
        _frozenAnalog = MakePoints<Analog>(
            config.FrozenAnalog, PointType.FrozenAnalog, config.DefaultClass);
        _timeAndInterval = new TimeAndInterval[Math.Max(config.TimeAndInterval, 0)];
        _terminal = MakePoints<byte[]>(
            config.VirtualTerminal, PointType.VirtualTerminal, config.DefaultClass);
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
        FrozenAnalog = _frozenAnalog.Length,
        TimeAndInterval = _timeAndInterval.Length,
        VirtualTerminal = _terminal.Length,
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
                PointType.FrozenAnalog => SetConfig(_frozenAnalog, index, config),
                PointType.VirtualTerminal => SetConfig(_terminal, index, config),
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
    public void AssignClass(PointType pt, Class cls) =>
        AssignClass(pt, cls, 0, 0xFFFF);

    /// <summary>
    /// Sets the event class of the points of a type from
    /// <paramref name="start"/> through <paramref name="stop"/>, which is what
    /// an ASSIGN_CLASS request naming those points asks for. Indexes past the
    /// last point are ignored.
    /// </summary>
    public void AssignClass(PointType pt, Class cls, ushort start, ushort stop)
    {
        lock (_gate)
        {
            switch (pt)
            {
                case PointType.Binary: AssignClass(_binary, cls, start, stop); break;
                case PointType.DoubleBitBinary:
                    AssignClass(_doubleBit, cls, start, stop); break;
                case PointType.Counter: AssignClass(_counter, cls, start, stop); break;
                case PointType.FrozenCounter: AssignClass(_frozen, cls, start, stop); break;
                case PointType.Analog: AssignClass(_analog, cls, start, stop); break;
                case PointType.FrozenAnalog: AssignClass(_frozenAnalog, cls, start, stop); break;
                case PointType.VirtualTerminal: AssignClass(_terminal, cls, start, stop); break;
                case PointType.BinaryOutputStatus:
                    AssignClass(_binaryOut, cls, start, stop); break;
                case PointType.AnalogOutputStatus:
                    AssignClass(_analogOut, cls, start, stop); break;
                case PointType.OctetString: AssignClass(_octet, cls, start, stop); break;
                case PointType.Dataset: AssignDatasetClass(cls, start, stop); break;
                default: break;
            }
        }
    }

    private static void AssignClass<T>(Point<T>[] pts, Class cls, ushort start, ushort stop)
    {
        // Counted in int so a stop of 0xFFFF cannot wrap the loop.
        for (var i = (int)start; i <= stop && i < pts.Length; i++)
        {
            pts[i].Config = pts[i].Config with { Class = cls };
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

            SetCounter(index, v);
        }
    }

    /// <summary>Stores a counter and raises an event if it moved enough. The caller holds the lock.</summary>
    private void SetCounter(int index, Counter v)
    {
        var p = _counter[index];
        var flagsChanged = p.Value.Flags != v.Flags;
        var valueChanged = p.Value.Value != v.Value;
        p.Value = v;

        if ((flagsChanged || valueChanged) && p.ShouldReport(v.Value, flagsChanged))
        {
            Raise(p.Config, new Event
            {
                Type = PointType.Counter,
                Index = (ushort)index,
                Variation = p.Config.EventVariation,
                Counter = v,
                Time = v.Time,
            });
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

            SetFrozen(index, v);
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

    /// <summary>Stores a frozen analog snapshot and applies its own event deadband.</summary>
    public void UpdateFrozenAnalog(ushort index, Analog v)
    {
        lock (_gate)
        {
            if (index < _frozenAnalog.Length)
            {
                SetFrozenAnalog(index, v);
            }
        }
    }

    private void SetFrozenAnalog(int index, Analog v)
    {
        var p = _frozenAnalog[index];
        var flagsChanged = p.Value.Flags != v.Flags;
        p.Value = v;
        if (p.ShouldReport(v.Value, flagsChanged))
        {
            Raise(p.Config, new Event
            {
                Type = PointType.FrozenAnalog,
                Index = (ushort)index,
                Variation = p.Config.EventVariation,
                FrozenAnalog = v,
                Time = v.Time,
            });
        }
    }

    /// <summary>Snapshots every analog that has a frozen counterpart.</summary>
    public void FreezeAnalogs() =>
        FreezeAnalogs(0, 0xFFFF, Timestamp.Unsynchronized(DateTimeOffset.UtcNow), false);

    /// <summary>
    /// Freezes the analogs from <paramref name="start"/> through
    /// <paramref name="stop"/>; with <paramref name="clear"/> each running
    /// analog is zeroed afterwards, as FREEZE_CLEAR does.
    /// </summary>
    public void FreezeAnalogs(ushort start, ushort stop, Timestamp at, bool clear)
    {
        lock (_gate)
        {
            var n = Math.Min(_analog.Length, _frozenAnalog.Length);
            for (var i = (int)start; i <= stop && i < n; i++)
            {
                var v = _analog[i].Value with { Time = at };
                SetFrozenAnalog(i, v);
                if (!clear)
                {
                    continue;
                }

                var p = _analog[i];
                v = v with { Value = 0 };
                p.Value = v;
                if (p.ShouldReport(0, false))
                {
                    Raise(p.Config, new Event
                    {
                        Type = PointType.Analog,
                        Index = (ushort)i,
                        Variation = p.Config.EventVariation,
                        Analog = v,
                        Time = at,
                    });
                }
            }
        }
    }

    /// <summary>Stores an indexed time-and-interval value; these generate no events.</summary>
    /// <returns><see langword="false"/> when the index does not exist.</returns>
    public bool UpdateTimeAndInterval(ushort index, TimeAndInterval v)
    {
        lock (_gate)
        {
            if (index >= _timeAndInterval.Length)
            {
                return false;
            }

            _timeAndInterval[index] = v;
            return true;
        }
    }

    /// <summary>Queues new terminal input as a group 113 event.</summary>
    public void UpdateVirtualTerminal(ushort index, ReadOnlySpan<byte> v)
    {
        lock (_gate)
        {
            if (index >= _terminal.Length || v.Length is 0 or > 255)
            {
                return;
            }

            var p = _terminal[index];
            p.Value = v.ToArray();
            Raise(p.Config, new Event
            {
                Type = PointType.VirtualTerminal,
                Index = index,
                Variation = (byte)v.Length,
                OctetString = p.Value,
            });
        }
    }

    /// <summary>
    /// Records that a control was operated on a binary output, as a group 13
    /// event, if the point has a command event class.
    /// </summary>
    /// <param name="index">The output point.</param>
    /// <param name="state">The state the output was commanded to.</param>
    /// <param name="status">The outcome the outstation reported for the command.</param>
    /// <param name="at">When it was operated.</param>
    public void RaiseBinaryCommandEvent(ushort index, bool state, CommandStatus status, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (index >= _binaryOut.Length)
            {
                return;
            }

            var cfg = _binaryOut[index].Config;
            if (cfg.CommandEventClass == Class.None)
            {
                return;
            }

            var variation = cfg.CommandEventVariation is 1 or 2 ? cfg.CommandEventVariation : (byte)2;
            RaiseWithClass(cfg.CommandEventClass, new Event
            {
                Type = PointType.BinaryCommandEvent,
                Index = index,
                Variation = variation,
                Time = Timestamp.Now(at),
                CommandStatus = status,
                CommandState = state,
            });
        }
    }

    /// <summary>
    /// Records that a control was operated on an analog output, as a group 43
    /// event, if the point has a command event class.
    /// </summary>
    /// <param name="index">The output point.</param>
    /// <param name="value">The value the output was commanded to.</param>
    /// <param name="commandVariation">
    /// The group 41 variation the command arrived in, which decides the event's
    /// width when the point does not name one.
    /// </param>
    /// <param name="status">The outcome the outstation reported for the command.</param>
    /// <param name="at">When it was operated.</param>
    public void RaiseAnalogCommandEvent(
        ushort index, double value, byte commandVariation, CommandStatus status, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (index >= _analogOut.Length)
            {
                return;
            }

            var cfg = _analogOut[index].Config;
            if (cfg.CommandEventClass == Class.None)
            {
                return;
            }

            var variation = cfg.CommandEventVariation;
            if (variation is < 1 or > 8)
            {
                // Mirror the command, with a time: g41v1 (int32) -> g43v3, g41v2
                // (int16) -> g43v4, g41v3 (float) -> g43v7, g41v4 (double) ->
                // g43v8.
                variation = commandVariation switch
                {
                    2 => 4,
                    3 => 7,
                    4 => 8,
                    _ => 3,
                };
            }

            RaiseWithClass(cfg.CommandEventClass, new Event
            {
                Type = PointType.AnalogCommandEvent,
                Index = index,
                Variation = variation,
                Time = Timestamp.Now(at),
                CommandStatus = status,
                CommandValue = value,
            });
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
    /// <remarks>
    /// Every attached master gets its own copy. Sharing one queue between them
    /// would be worse than wasteful: selection and confirmation are per-master,
    /// so the first master to poll would take the events and the second would
    /// never learn they happened.
    /// </remarks>
    private void Raise(PointConfig config, Event e) => RaiseWithClass(config.Class, e);

    private void RaiseWithClass(Class cls, Event e)
    {
        if (cls == Class.None)
        {
            return;
        }

        e.Class = cls;

        var subscribers = _subscribers;
        if (subscribers.Length == 0)
        {
            _events?.Add(e);
            return;
        }

        foreach (var b in subscribers)
        {
            b.Add(e);
        }
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

    /// <summary>Returns a frozen analog input, which is independent of the running input.</summary>
    public bool TryGetFrozenAnalog(ushort index, out Analog value, out PointConfig config)
    {
        lock (_gate)
        {
            return Get(_frozenAnalog, index, out value, out config);
        }
    }

    /// <summary>Returns an indexed time-and-interval value.</summary>
    public bool TryGetTimeAndInterval(ushort index, out TimeAndInterval value)
    {
        lock (_gate)
        {
            if (index >= _timeAndInterval.Length)
            {
                value = default;
                return false;
            }

            value = _timeAndInterval[index];
            return true;
        }
    }

    internal bool TryGetVirtualTerminal(ushort index, out byte[] value)
    {
        lock (_gate)
        {
            if (index >= _terminal.Length)
            {
                value = [];
                return false;
            }

            value = _terminal[index].Value ?? [];
            return true;
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
    /// Stores a frozen counter value and raises an event if it changed.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="UpdateFrozenCounter"/> and the freeze functions so
    /// the two cannot disagree about when a frozen counter reports. The caller
    /// holds the lock.
    /// </remarks>
    private void SetFrozen(int i, FrozenCounter v)
    {
        var p = _frozen[i];
        var changed = p.Value.Value != v.Value || p.Value.Flags != v.Flags;
        p.Value = v;
        if (!changed)
        {
            return;
        }

        Raise(p.Config, new Event
        {
            Type = PointType.FrozenCounter,
            Index = (ushort)i,
            Variation = p.Config.EventVariation,
            FrozenCounter = v,
            Time = v.Time,
        });
    }

    /// <summary>
    /// Copies every counter into its frozen counterpart, which is what the
    /// freeze function codes do. The frozen values are stamped with the current
    /// time; see <see cref="FreezeCounters(ushort, ushort, Timestamp)"/>.
    /// </summary>
    public void FreezeCounters() =>
        FreezeCounters(0, 0xFFFF, Timestamp.Unsynchronized(DateTimeOffset.UtcNow));

    /// <summary>
    /// Freezes the counters from <paramref name="start"/> through
    /// <paramref name="stop"/>, which is what a freeze request naming those
    /// counters asks for. Indexes past the last counter are ignored.
    /// </summary>
    /// <remarks>
    /// A frozen value is a snapshot, and <paramref name="at"/> is when it was
    /// taken: the time the frozen counter variations that carry one report,
    /// rather than whenever the running counter last happened to change. Each
    /// frozen counter that changes raises an event just as an application
    /// update would, which is how a master polling events learns a freeze
    /// produced something new — writing the value in place, as this once did,
    /// left event-driven masters to discover it only by reading the frozen
    /// counters outright.
    /// </remarks>
    public void FreezeCounters(ushort start, ushort stop, Timestamp at) =>
        FreezeCounters(start, stop, at, false);

    /// <summary>
    /// Freezes the counters from <paramref name="start"/> through
    /// <paramref name="stop"/>; with <paramref name="clear"/> each counter is
    /// reset to zero once its value has been frozen, as FREEZE_CLEAR does, so
    /// the frozen value holds what accumulated up to the freeze and the running
    /// counter starts over.
    /// </summary>
    public void FreezeCounters(ushort start, ushort stop, Timestamp at, bool clear)
    {
        lock (_gate)
        {
            var n = Math.Min(_counter.Length, _frozen.Length);

            // Counted in int so a stop of 0xFFFF cannot wrap the loop.
            for (var i = (int)start; i <= stop && i < n; i++)
            {
                var c = _counter[i].Value;
                SetFrozen(i, new FrozenCounter(c.Value, c.Flags, at));
                if (clear)
                {
                    SetCounter(i, new Counter(0, c.Flags, at));
                }
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
            PointType.FrozenAnalog => 31,
            PointType.VirtualTerminal => 112,
            PointType.Analog => 30,
            PointType.BinaryOutputStatus => 10,
            PointType.AnalogOutputStatus => 40,
            PointType.OctetString => 110,
            _ => 0,
        };

        return GroupVar.GV(group, variation);
    }
}
