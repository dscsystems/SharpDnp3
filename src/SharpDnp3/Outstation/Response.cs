// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using SharpDnp3.App;
using SharpDnp3.Objects;

namespace SharpDnp3.Outstation;

/// <summary>
/// Accumulates object headers into fragments, starting a new fragment when the
/// current one fills.
/// </summary>
/// <remarks>
/// Multi-fragment responses are the normal case for an integrity poll: a
/// thousand analog points do not fit in 2048 octets, so the response is a
/// series of fragments the master confirms one at a time.
/// </remarks>
internal sealed class ResponseBuilder
{
    private readonly int _max;
    private readonly List<byte[]> _fragments = [];
    private List<byte> _cur = [];

    /// <summary>Session state the object codecs need.</summary>
    public Context Ctx { get; }

    public ResponseBuilder(int maxFragment, Context ctx)
    {
        _max = maxFragment <= 0 ? AppConstants.DefaultMaxFragment : maxFragment;
        Ctx = ctx;
    }

    /// <summary>
    /// Returns how many octets remain in the current fragment, leaving space
    /// for the response header that will be prepended.
    /// </summary>
    public int Room => _max - AppConstants.ResponseHeaderSize - _cur.Count;

    /// <summary>The fragment size cap, response header included.</summary>
    public int Max => _max;

    /// <summary>Ends the current fragment.</summary>
    public void Flush()
    {
        if (_cur.Count > 0)
        {
            _fragments.Add([.. _cur]);
            _cur = [];
        }
    }

    /// <summary>
    /// Appends an object header, starting a new fragment if it does not fit.
    /// </summary>
    public void Add(ObjectHeader h)
    {
        if (h.Size > Room && _cur.Count > 0)
        {
            Flush();
        }

        ObjectHeaderCodec.AppendObjectHeader(_cur, h);
    }

    /// <summary>
    /// Returns every accumulated fragment body. A response with no objects
    /// still produces one empty body, because an empty response is a real
    /// answer.
    /// </summary>
    public List<byte[]> Done()
    {
        Flush();
        return _fragments.Count == 0 ? [[]] : _fragments;
    }
}

/// <summary>Encodes an outstation's data into response fragments.</summary>
internal sealed class ResponseWriter
{
    private readonly Database _db;

    public ResponseWriter(Database db) => _db = db;

    /// <summary>
    /// The order a class 0 response reports point types in. It matches the
    /// group numbering, which is what masters and analysers expect to see.
    /// </summary>
    public static readonly PointType[] StaticTypes =
    [
        PointType.Binary,
        PointType.DoubleBitBinary,
        PointType.BinaryOutputStatus,
        PointType.Counter,
        PointType.FrozenCounter,
        PointType.Analog,
        PointType.FrozenAnalog,
        PointType.AnalogOutputStatus,
        PointType.OctetString,
    ];

    // ---------- Static data ----------

    /// <summary>Appends the points of one type over an index range.</summary>
    /// <remarks>
    /// It emits a header per contiguous run that fits the current fragment, so
    /// a range spanning a fragment boundary is split into two headers rather
    /// than being truncated.
    /// </remarks>
    public void BuildStaticRange(
        ResponseBuilder b,
        PointType pt,
        byte variation,
        ushort start,
        ushort stop)
    {
        var counts = _db.Counts();
        var limit = TypeCount(counts, pt);
        if (limit == 0)
        {
            return;
        }

        if (pt == PointType.OctetString)
        {
            BuildOctetStrings(b, start, Math.Min(stop, (ushort)(limit - 1)));
            return;
        }

        if (stop >= limit)
        {
            stop = (ushort)(limit - 1);
        }

        if (start > stop)
        {
            return;
        }

        if (variation != 0)
        {
            // An explicit variation is the master's choice, and every point in
            // the range is reported as what it asked for.
            BuildStaticRun(b, pt, Database.StaticGroupVar(pt, variation), start, stop);
            return;
        }

        // Variation zero means "each point's own default" — the static
        // variation its configuration set, which can differ from one point to
        // the next. An object header carries exactly one variation, so points
        // that disagree cannot share one: the range is reported as runs of
        // consecutive points that agree, each under its own header.
        //
        // Resolving the variation once from the first point and applying it to
        // the rest reports every later point in a variation it did not ask
        // for. For a float analog following an integer one, that means through
        // the integer codec, and the master reads back a truncated value it has
        // no way to know is wrong.
        //
        // The loop counts in int so a range ending at 0xFFFF cannot wrap.
        for (var idx = (int)start; idx <= stop;)
        {
            if (!TryStaticVariation(pt, (ushort)idx, out var v))
            {
                return;
            }

            var end = idx;
            while (end < stop)
            {
                if (!TryStaticVariation(pt, (ushort)(end + 1), out var next) || next != v)
                {
                    break;
                }

                end++;
            }

            // A run that could not be written means the fragment has no room
            // for even one object, and every run after it would fail the same
            // way.
            if (!BuildStaticRun(b, pt, Database.StaticGroupVar(pt, v), (ushort)idx, (ushort)end))
            {
                return;
            }

            idx = end + 1;
        }
    }

    /// <summary>
    /// Returns the static variation a point is configured to be reported in.
    /// </summary>
    private bool TryStaticVariation(PointType pt, ushort index, out byte variation)
    {
        if (TryPointConfig(pt, index, out var cfg))
        {
            variation = cfg.StaticVariation;
            return true;
        }

        variation = 0;
        return false;
    }

    /// <summary>
    /// Reports points <paramref name="start"/> through <paramref name="stop"/>,
    /// all in one encoding, splitting across fragments as the space in each
    /// allows.
    /// </summary>
    /// <returns><see langword="false"/> when nothing more can be written.</returns>
    private bool BuildStaticRun(ResponseBuilder b, PointType pt, GroupVar gv, ushort start, ushort stop)
    {
        if (!ObjectRegistry.TryLookup(gv, out var d))
        {
            return false;
        }

        if (!d.TrySizeOctets(out var size) || size == 0)
        {
            return false;
        }

        // Worst-case 16-bit range.
        const int HeaderOverhead = ObjectHeader.ObjectHeaderSize + 4;

        for (var idx = start; idx <= stop;)
        {
            // How many points fit in what is left of the fragment, after the
            // header and its range field.
            var avail = b.Room - HeaderOverhead;
            if (avail < size)
            {
                b.Flush();
                avail = b.Room - HeaderOverhead;
                if (avail < size)
                {
                    // A single object does not fit an empty fragment.
                    return false;
                }
            }

            var runLen = Math.Min(avail / size, stop - idx + 1);
            var last = (ushort)(idx + runLen - 1);

            var data = new List<byte>(runLen * size);
            for (var i = idx; i <= last; i++)
            {
                EncodeStatic(data, pt, gv, i, b.Ctx);
            }

            b.Add(RangeObjectHeader(gv, idx, last, [.. data]));

            if (last == ushort.MaxValue)
            {
                return true;
            }

            idx = (ushort)(last + 1);
        }

        return true;
    }

    /// <summary>Appends one point's static encoding.</summary>
    private void EncodeStatic(
        List<byte> dst,
        PointType pt,
        GroupVar gv,
        ushort index,
        Context ctx)
    {
        switch (pt)
        {
            case PointType.Binary:
                _db.TryGetBinary(index, out var bv, out _);
                if (ObjectRegistry.TryBinaryCodec(gv, out var bc))
                {
                    bc.Write(dst, bv, ctx);
                }

                break;

            case PointType.DoubleBitBinary:
                _db.TryGetDoubleBit(index, out var dv, out _);
                if (ObjectRegistry.TryDoubleBitCodec(gv, out var dc))
                {
                    dc.Write(dst, dv, ctx);
                }

                break;

            case PointType.Counter:
                _db.TryGetCounter(index, out var cv, out _);
                if (ObjectRegistry.TryCounterCodec(gv, out var cc))
                {
                    cc.Write(dst, cv, ctx);
                }

                break;

            case PointType.FrozenCounter:
                _db.TryGetFrozenCounter(index, out var fv, out _);
                if (ObjectRegistry.TryFrozenCounterCodec(gv, out var fc))
                {
                    fc.Write(dst, fv, ctx);
                }

                break;

            case PointType.Analog:
                _db.TryGetAnalog(index, out var av, out _);
                if (ObjectRegistry.TryAnalogCodec(gv, out var ac))
                {
                    ac.Write(dst, av, ctx);
                }

                break;

            case PointType.FrozenAnalog:
                _db.TryGetFrozenAnalog(index, out var fav, out _);
                if (ObjectRegistry.TryAnalogCodec(gv, out var fac))
                {
                    fac.Write(dst, fav, ctx);
                }

                break;

            case PointType.BinaryOutputStatus:
                _db.TryGetBinaryOutputStatus(index, out var bov, out _);
                if (ObjectRegistry.TryBinaryOutputCodec(gv, out var boc))
                {
                    boc.Write(dst, bov, ctx);
                }

                break;

            case PointType.AnalogOutputStatus:
                _db.TryGetAnalogOutputStatus(index, out var aov, out _);
                if (ObjectRegistry.TryAnalogOutputCodec(gv, out var aoc))
                {
                    aoc.Write(dst, aov, ctx);
                }

                break;

            default:
                break;
        }
    }

    /// <summary>Returns a point's configuration.</summary>
    private bool TryPointConfig(PointType pt, ushort index, out PointConfig config) => pt switch
    {
        PointType.Binary => _db.TryGetBinary(index, out _, out config),
        PointType.DoubleBitBinary => _db.TryGetDoubleBit(index, out _, out config),
        PointType.Counter => _db.TryGetCounter(index, out _, out config),
        PointType.FrozenCounter => _db.TryGetFrozenCounter(index, out _, out config),
        PointType.Analog => _db.TryGetAnalog(index, out _, out config),
        PointType.FrozenAnalog => _db.TryGetFrozenAnalog(index, out _, out config),
        PointType.BinaryOutputStatus => _db.TryGetBinaryOutputStatus(index, out _, out config),
        PointType.AnalogOutputStatus => _db.TryGetAnalogOutputStatus(index, out _, out config),
        PointType.OctetString => _db.TryGetOctetString(index, out _, out config),
        _ => Fail(out config),
    };

    private static bool Fail(out PointConfig config)
    {
        config = default;
        return false;
    }

    internal static int TypeCount(DatabaseConfig c, PointType pt) => pt switch
    {
        PointType.Binary => c.Binary,
        PointType.DoubleBitBinary => c.DoubleBitBinary,
        PointType.Counter => c.Counter,
        PointType.FrozenCounter => c.FrozenCounter,
        PointType.Analog => c.Analog,
        PointType.FrozenAnalog => c.FrozenAnalog,
        PointType.VirtualTerminal => c.VirtualTerminal,
        PointType.BinaryOutputStatus => c.BinaryOutputStatus,
        PointType.AnalogOutputStatus => c.AnalogOutputStatus,
        PointType.OctetString => c.OctetString,
        _ => 0,
    };

    /// <summary>
    /// Builds a header addressing an inclusive index range, choosing the
    /// narrowest range encoding that fits.
    /// </summary>
    internal static ObjectHeader RangeObjectHeader(
        GroupVar gv, ushort start, ushort stop, byte[] data)
    {
        var spec = stop <= 0xFF ? RangeSpec.StartStop8 : RangeSpec.StartStop16;
        return new ObjectHeader
        {
            Group = gv.Group,
            Variation = gv.Variation,
            Qualifier = Qualifier.Make(IndexPrefix.None, spec),
            Range = new ObjectRange
            {
                Spec = spec,
                Start = start,
                Stop = stop,
                Count = (uint)(stop - start) + 1,
            },
            Data = data,
        };
    }

    /// <summary>Appends octet string points.</summary>
    /// <remarks>
    /// These need their own path because the variation number <em>is</em> the
    /// string's length: two points of different lengths cannot share an object
    /// header, so a range is emitted as one header per run of equal-length
    /// strings. Forcing them through the fixed-size path would report every
    /// string at one length and truncate or pad the rest.
    /// </remarks>
    private void BuildOctetStrings(ResponseBuilder b, ushort start, ushort stop)
    {
        if (start > stop)
        {
            return;
        }

        const int HeaderOverhead = ObjectHeader.ObjectHeaderSize + 4;

        for (var idx = start; idx <= stop;)
        {
            if (!_db.TryGetOctetString(idx, out var v, out _))
            {
                return;
            }

            // A zero-length string cannot be encoded: variation zero means "any
            // length" in a request and is not a valid response variation.
            var length = Math.Max(v.Length, 1);

            // Collect the run of following points with the same length.
            var last = idx;
            while (last < stop)
            {
                if (!_db.TryGetOctetString((ushort)(last + 1), out var next, out _) ||
                    Math.Max(next.Length, 1) != length)
                {
                    break;
                }

                last++;
            }

            while (idx <= last)
            {
                var avail = b.Room - HeaderOverhead;
                if (avail < length)
                {
                    b.Flush();
                    avail = b.Room - HeaderOverhead;
                    if (avail < length)
                    {
                        return;
                    }
                }

                var runEnd = (ushort)Math.Min(idx + (avail / length) - 1, last);

                var data = new List<byte>((runEnd - idx + 1) * length);
                for (var i = idx; i <= runEnd; i++)
                {
                    _db.TryGetOctetString(i, out var str, out _);
                    AppendOctetString(data, str, length);
                }

                b.Add(RangeObjectHeader(GroupVar.GV(110, (byte)length), idx, runEnd, [.. data]));

                if (runEnd == ushort.MaxValue)
                {
                    return;
                }

                idx = (ushort)(runEnd + 1);
            }
        }
    }

    /// <summary>
    /// Writes one string padded or truncated to <paramref name="length"/>,
    /// which the fixed-length variation requires.
    /// </summary>
    private static void AppendOctetString(List<byte> dst, byte[]? v, int length)
    {
        var span = (v ?? []).AsSpan();
        if (span.Length > length)
        {
            span = span[..length];
        }

        dst.AddRange(span);
        for (var i = span.Length; i < length; i++)
        {
            dst.Add(0);
        }
    }

    // ---------- Events ----------

    /// <summary>Returns the group an event of a point type is reported in.</summary>
    internal static byte EventGroup(PointType pt) => pt switch
    {
        PointType.Binary => 2,
        PointType.DoubleBitBinary => 4,
        PointType.BinaryOutputStatus => 11,
        PointType.Counter => 22,
        PointType.FrozenCounter => 23,
        PointType.FrozenAnalog => 33,
        PointType.Dataset => 88,
        PointType.SecurityStatistic => 122,
        PointType.VirtualTerminal => 113,
        PointType.Analog => 32,
        PointType.AnalogOutputStatus => 42,
        PointType.OctetString => 111,
        PointType.BinaryCommandEvent => 13,
        PointType.AnalogCommandEvent => 43,
        _ => 0,
    };

    /// <summary>Maps an event group to the kind of event it reports, and says whether it is one.</summary>
    internal static bool TryEventTypeForGroup(byte group, out PointType pt)
    {
        pt = group switch
        {
            2 => PointType.Binary,
            4 => PointType.DoubleBitBinary,
            11 => PointType.BinaryOutputStatus,
            13 => PointType.BinaryCommandEvent,
            22 => PointType.Counter,
            23 => PointType.FrozenCounter,
            33 => PointType.FrozenAnalog,
            88 => PointType.Dataset,
            122 => PointType.SecurityStatistic,
            113 => PointType.VirtualTerminal,
            32 => PointType.Analog,
            42 => PointType.AnalogOutputStatus,
            43 => PointType.AnalogCommandEvent,
            111 => PointType.OctetString,
            _ => PointType.Unknown,
        };
        return pt != PointType.Unknown;
    }

    /// <summary>Appends event objects for the selected events.</summary>
    /// <remarks>
    /// Events carry per-object index prefixes because the points that changed
    /// are not contiguous, and they are grouped into runs sharing a group and
    /// variation so a burst of analog changes becomes one header rather than
    /// fifty.
    /// </remarks>
    public void BuildEvents(
        ResponseBuilder b,
        IReadOnlyList<Event> events,
        Func<DateTimeOffset>? now = null,
        Action<Event>? release = null)
    {
        for (var i = 0; i < events.Count;)
        {
            if (events[i].Type == PointType.Dataset)
            {
                var obj = events[i].Dataset ?? [];
                var h = FreeFormat.Build(88, 1, obj);
                if (h.Size + AppConstants.ResponseHeaderSize <= b.Max)
                {
                    b.Add(h);
                }
                else
                {
                    release?.Invoke(events[i]);
                }

                i++;
                continue;
            }

            var gv = GroupVar.GV(EventGroup(events[i].Type), events[i].Variation);

            // An octet string's size is its variation, not a table lookup:
            // group 111 has no descriptor row for a length to find. Consulting
            // the registry first would silently drop every string event.
            int size;
            var relative = false;
            if (events[i].Type is PointType.OctetString or PointType.VirtualTerminal)
            {
                size = gv.Variation;
            }
            else if (events[i].Type == PointType.SecurityStatistic)
            {
                size = gv.Variation == 2 ? 13 : 7;
            }
            else if (events[i].Type is PointType.BinaryCommandEvent or PointType.AnalogCommandEvent)
            {
                if (!CommandEventCodec.TrySize(gv.Group, gv.Variation, out size))
                {
                    i++;
                    continue;
                }
            }
            else
            {
                if (!ObjectRegistry.TryLookup(gv, out var d))
                {
                    i++;
                    continue;
                }

                if (!d.TrySizeOctets(out size))
                {
                    i++;
                    continue;
                }

                relative = d.RelativeTime;
            }

            if (size == 0)
            {
                i++;
                continue;
            }

            // Collect the run of consecutive events sharing this encoding.
            var j = i;
            while (j < events.Count &&
                   events[j].Type != PointType.Dataset &&
                   EventGroup(events[j].Type) == gv.Group &&
                   events[j].Variation == gv.Variation)
            {
                j++;
            }

            // Each event carries its point index as a prefix, and the prefix
            // has to be wide enough for every index in the run. A one-octet
            // prefix holds only 0-255: writing index 300 into it reports the
            // event against point 44, and the master has no way to know. One
            // octet is kept where it fits, which is the common case and the
            // smaller encoding; a run reaching past 255 moves to two octets,
            // and so to a two-octet count.
            var prefix = IndexPrefix.Index1;
            var spec = RangeSpec.Count8;
            var maxCount = 0xFF;
            for (var k = i; k < j; k++)
            {
                if (events[k].Index > 0xFF)
                {
                    prefix = IndexPrefix.Index2;
                    spec = RangeSpec.Count16;
                    maxCount = 0xFFFF;
                    break;
                }
            }

            var prefixLen = prefix.Octets();
            var perObject = prefixLen + size;
            var headerOverhead = ObjectHeader.ObjectHeaderSize + spec.Octets();

            // A relative-time event is an offset from a common time of
            // occurrence, and the offset means nothing without the group 51
            // object it is measured from — in the same fragment, since a master
            // resolves each fragment on its own. So the base travels with every
            // header of relative events, and the room for it is reserved with
            // the header's so the two can never be split across a fragment
            // boundary.
            var ctoSize = relative ? CtoHeader(b.Ctx, default).Size : 0;

            while (i < j)
            {
                var avail = b.Room - ctoSize - headerOverhead;
                if (avail < perObject)
                {
                    b.Flush();
                    avail = b.Room - ctoSize - headerOverhead;
                    if (avail < perObject)
                    {
                        return;
                    }
                }

                var runLen = Math.Min(Math.Min(avail / perObject, j - i), maxCount);
                var ctx = b.Ctx;
                if (relative)
                {
                    // The base is the first event's time, so its own offset is
                    // zero, and the run stops at the first event too far past it
                    // for sixteen bits of milliseconds (or before it, which an
                    // unsigned offset cannot say). That event starts a new base.
                    var baseTime = EventBase(events[i], now);
                    runLen = Math.Min(runLen, EventsWithinWindow(events, i, runLen, baseTime));
                    ctx = ctx.WithCto(baseTime);
                    b.Add(CtoHeader(b.Ctx, baseTime));
                }

                var data = new List<byte>(runLen * perObject);
                for (var k = 0; k < runLen; k++)
                {
                    var e = events[i + k];
                    if (prefixLen == 1)
                    {
                        data.Add((byte)e.Index);
                    }
                    else
                    {
                        data.Add((byte)e.Index);
                        data.Add((byte)(e.Index >> 8));
                    }

                    EncodeEvent(data, gv, e, ctx);
                }

                b.Add(new ObjectHeader
                {
                    Group = gv.Group,
                    Variation = gv.Variation,
                    Qualifier = Qualifier.Make(prefix, spec),
                    Range = new ObjectRange { Spec = spec, Count = (uint)runLen },
                    Data = data.ToArray(),
                });

                i += runLen;
            }
        }
    }

    /// <summary>
    /// Builds the group 51 object that carries a common time of occurrence:
    /// variation 1 when the outstation's clock is synchronised and 2 when it is
    /// not, which is how a master learns how far to trust the relative times
    /// that follow.
    /// </summary>
    private static ObjectHeader CtoHeader(Context ctx, DateTimeOffset baseTime)
    {
        var data = new List<byte>(CommandObjects.Time48Size);
        CommandObjects.AppendTime48(data, Timestamp.Now(baseTime));
        return new ObjectHeader
        {
            Group = 51,
            Variation = (byte)(ctx.Synchronized ? 1 : 2),
            Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
            Data = data.ToArray(),
        };
    }

    /// <summary>
    /// Picks the common time of occurrence for a run of relative-time events
    /// starting at <paramref name="e"/>: the event's own time, to the
    /// millisecond the encoding can carry. An event that carries no time has
    /// nothing to anchor to, so the current time is used rather than the epoch.
    /// </summary>
    private static DateTimeOffset EventBase(Event e, Func<DateTimeOffset>? now)
    {
        var t = e.Time.IsValid ? e.Time.Time : (now?.Invoke() ?? DateTimeOffset.UtcNow);
        return new DateTimeOffset(t.UtcTicks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
    }

    /// <summary>
    /// Returns how many of the events from <paramref name="from"/>, at most
    /// <paramref name="max"/>, can be expressed as a sixteen-bit millisecond
    /// offset from <paramref name="baseTime"/>. An event with no time is
    /// expressed as the base itself. It is always at least one: the run's first
    /// event is its own base.
    /// </summary>
    private static int EventsWithinWindow(IReadOnlyList<Event> events, int from, int max, DateTimeOffset baseTime)
    {
        for (var n = 0; n < max; n++)
        {
            var e = events[from + n];
            if (!e.Time.IsValid)
            {
                continue;
            }

            var d = (long)(e.Time.Time - baseTime).TotalMilliseconds;
            if (d is < 0 or > 0xFFFF)
            {
                return Math.Max(n, 1);
            }
        }

        return max;
    }

    /// <summary>Appends one event's object encoding.</summary>
    private static void EncodeEvent(List<byte> dst, GroupVar gv, Event e, Context ctx)
    {
        switch (e.Type)
        {
            case PointType.Binary:
                if (ObjectRegistry.TryBinaryCodec(gv, out var bc))
                {
                    bc.Write(dst, e.Binary, ctx);
                }

                break;

            case PointType.DoubleBitBinary:
                if (ObjectRegistry.TryDoubleBitCodec(gv, out var dc))
                {
                    dc.Write(dst, e.DoubleBit, ctx);
                }

                break;

            case PointType.Counter:
                if (ObjectRegistry.TryCounterCodec(gv, out var cc))
                {
                    cc.Write(dst, e.Counter, ctx);
                }

                break;

            case PointType.FrozenCounter:
                if (ObjectRegistry.TryFrozenCounterCodec(gv, out var fc))
                {
                    fc.Write(dst, e.FrozenCounter, ctx);
                }

                break;

            case PointType.Analog:
                if (ObjectRegistry.TryAnalogCodec(gv, out var ac))
                {
                    ac.Write(dst, e.Analog, ctx);
                }

                break;

            case PointType.FrozenAnalog:
                if (ObjectRegistry.TryAnalogCodec(gv, out var fac))
                {
                    fac.Write(dst, e.FrozenAnalog, ctx);
                }

                break;

            case PointType.BinaryOutputStatus:
                if (ObjectRegistry.TryBinaryOutputCodec(gv, out var boc))
                {
                    boc.Write(dst, e.BinaryOutput, ctx);
                }

                break;

            case PointType.AnalogOutputStatus:
                if (ObjectRegistry.TryAnalogOutputCodec(gv, out var aoc))
                {
                    aoc.Write(dst, e.AnalogOutput, ctx);
                }

                break;

            case PointType.SecurityStatistic:
                dst.Add(Flags.Online.Value);
                dst.Add(0);
                dst.Add(0);
                ObjectConvert.AppendUInt32(dst, e.SecurityStatistic);
                if (gv.Variation == 2)
                {
                    CommandObjects.AppendTime48(dst, e.Time);
                }

                break;

            case PointType.OctetString or PointType.VirtualTerminal:
                AppendOctetString(dst, e.OctetString, gv.Variation);
                break;

            case PointType.BinaryCommandEvent or PointType.AnalogCommandEvent:
                CommandEventCodec.Append(dst, gv.Group, gv.Variation, new CommandEvent(
                    e.CommandStatus, e.Type == PointType.AnalogCommandEvent, e.CommandState, e.CommandValue, e.Time));
                break;

            default:
                break;
        }
    }
}
