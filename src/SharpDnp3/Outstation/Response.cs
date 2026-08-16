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

        if (variation == 0)
        {
            // Variation zero means "use your default", which is the per-point
            // static variation the configuration set.
            if (!TryPointConfig(pt, start, out var cfg))
            {
                return;
            }

            variation = cfg.StaticVariation;
        }

        var gv = Database.StaticGroupVar(pt, variation);
        if (!ObjectRegistry.TryLookup(gv, out var d))
        {
            return;
        }

        if (!d.TrySizeOctets(out var size) || size == 0)
        {
            return;
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
                    return;
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
                return;
            }

            idx = (ushort)(last + 1);
        }
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
        PointType.BinaryOutputStatus => c.BinaryOutputStatus,
        PointType.AnalogOutputStatus => c.AnalogOutputStatus,
        PointType.OctetString => c.OctetString,
        _ => 0,
    };

    /// <summary>
    /// Builds a header addressing an inclusive index range, choosing the
    /// narrowest range encoding that fits.
    /// </summary>
    private static ObjectHeader RangeObjectHeader(
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
        PointType.Analog => 32,
        PointType.AnalogOutputStatus => 42,
        PointType.OctetString => 111,
        _ => 0,
    };

    /// <summary>Appends event objects for the selected events.</summary>
    /// <remarks>
    /// Events carry per-object index prefixes because the points that changed
    /// are not contiguous, and they are grouped into runs sharing a group and
    /// variation so a burst of analog changes becomes one header rather than
    /// fifty.
    /// </remarks>
    public void BuildEvents(ResponseBuilder b, IReadOnlyList<Event> events)
    {
        const int HeaderOverhead = ObjectHeader.ObjectHeaderSize + 1;

        for (var i = 0; i < events.Count;)
        {
            var gv = GroupVar.GV(EventGroup(events[i].Type), events[i].Variation);

            // An octet string's size is its variation, not a table lookup:
            // group 111 has no descriptor row for a length to find. Consulting
            // the registry first would silently drop every string event.
            int size;
            if (events[i].Type == PointType.OctetString)
            {
                size = gv.Variation;
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
            }

            if (size == 0)
            {
                i++;
                continue;
            }

            // Collect the run of consecutive events sharing this encoding.
            var j = i;
            while (j < events.Count &&
                   EventGroup(events[j].Type) == gv.Group &&
                   events[j].Variation == gv.Variation)
            {
                j++;
            }

            // A one-octet index prefix plus the object.
            var perObject = 1 + size;

            while (i < j)
            {
                var avail = b.Room - HeaderOverhead;
                if (avail < perObject)
                {
                    b.Flush();
                    avail = b.Room - HeaderOverhead;
                    if (avail < perObject)
                    {
                        return;
                    }
                }

                var runLen = Math.Min(Math.Min(avail / perObject, j - i), 255);
                var data = new List<byte>(runLen * perObject);
                for (var k = 0; k < runLen; k++)
                {
                    var e = events[i + k];
                    data.Add((byte)e.Index);
                    EncodeEvent(data, gv, e, b.Ctx);
                }

                b.Add(new ObjectHeader
                {
                    Group = gv.Group,
                    Variation = gv.Variation,
                    Qualifier = Qualifier.Make(IndexPrefix.Index1, RangeSpec.Count8),
                    Range = new ObjectRange { Spec = RangeSpec.Count8, Count = (uint)runLen },
                    Data = data.ToArray(),
                });

                i += runLen;
            }
        }
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

            case PointType.OctetString:
                AppendOctetString(dst, e.OctetString, gv.Variation);
                break;

            default:
                break;
        }
    }
}
