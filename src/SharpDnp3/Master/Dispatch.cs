// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using SharpDnp3.App;
using SharpDnp3.Objects;

namespace SharpDnp3.Master;

/// <summary>
/// Decodes object headers into typed measurements and hands them to a handler.
/// </summary>
/// <remarks>
/// The headers a master must cope with come in two shapes: a contiguous index
/// range with no per-object prefix, which is how static data arrives, and a
/// count with a per-object index prefix, which is how events arrive because the
/// points that changed are not adjacent.
/// </remarks>
internal static class Dispatcher
{
    /// <summary>Group 110 carries static octet strings.</summary>
    private const byte GroupOctetString = 110;

    /// <summary>Group 111 carries octet string events.</summary>
    private const byte GroupOctetStringEvent = 111;

    /// <summary>
    /// Decodes one object header and hands the result to
    /// <paramref name="handler"/>.
    /// </summary>
    public static void Dispatch(IMasterHandler handler, ObjectHeader h, Context ctx)
    {
        var gv = GroupVar.GV(h.Group, h.Variation);
        if (h.Data.IsEmpty)
        {
            return;
        }

        // Octet strings are checked before the registry lookup, not after:
        // their length lives in the variation number, so there is no descriptor
        // row for g110v5 to find. Looking them up first would silently drop
        // every string a device reports.
        if (h.Group is GroupOctetString or GroupOctetStringEvent)
        {
            DispatchOctetStrings(
                handler,
                h,
                new HeaderInfo { GV = gv, Kind = Kind.String },
                h.Group == GroupOctetStringEvent);
            return;
        }

        if (!ObjectRegistry.TryLookup(gv, out var d) || d.Measurement == PointType.Unknown)
        {
            return;
        }

        var info = new HeaderInfo { GV = gv, Kind = d.Kind };

        if (d.Packed)
        {
            DispatchPacked(handler, h, d, info);
            return;
        }

        if (!d.TrySizeOctets(out var size) || size == 0)
        {
            return;
        }

        var prefixLen = 0;
        var p = h.Qualifier.IndexPrefix;
        if (p.IsIndex())
        {
            prefixLen = p.Octets();
        }

        switch (d.Measurement)
        {
            case PointType.Binary:
                ObjectRegistry.TryBinaryCodec(gv, out var bc);
                handler.HandleBinary(info, DecodeRun(h, size, prefixLen, ctx, bc.Parse));
                break;

            case PointType.DoubleBitBinary:
                ObjectRegistry.TryDoubleBitCodec(gv, out var dc);
                handler.HandleDoubleBit(info, DecodeRun(h, size, prefixLen, ctx, dc.Parse));
                break;

            case PointType.Counter:
                ObjectRegistry.TryCounterCodec(gv, out var cc);
                handler.HandleCounter(info, DecodeRun(h, size, prefixLen, ctx, cc.Parse));
                break;

            case PointType.FrozenCounter:
                ObjectRegistry.TryFrozenCounterCodec(gv, out var fc);
                handler.HandleFrozenCounter(info, DecodeRun(h, size, prefixLen, ctx, fc.Parse));
                break;

            case PointType.Analog:
                ObjectRegistry.TryAnalogCodec(gv, out var ac);
                handler.HandleAnalog(info, DecodeRun(h, size, prefixLen, ctx, ac.Parse));
                break;

            case PointType.BinaryOutputStatus:
                ObjectRegistry.TryBinaryOutputCodec(gv, out var boc);
                handler.HandleBinaryOutputStatus(info, DecodeRun(h, size, prefixLen, ctx, boc.Parse));
                break;

            case PointType.AnalogOutputStatus:
                ObjectRegistry.TryAnalogOutputCodec(gv, out var aoc);
                handler.HandleAnalogOutputStatus(info, DecodeRun(h, size, prefixLen, ctx, aoc.Parse));
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Walks the objects a header introduces, taking each index either from the
    /// range or from a per-object prefix.
    /// </summary>
    private static List<Indexed<T>> DecodeRun<T>(
        ObjectHeader h,
        int size,
        int prefixLen,
        Context ctx,
        ParseObject<T>? parse)
    {
        if (parse is null)
        {
            return [];
        }

        var count = (int)h.Count;
        var output = new List<Indexed<T>>(count);
        var data = h.Data.Span;

        var off = 0;
        for (var i = 0; i < count; i++)
        {
            if (off + prefixLen + size > data.Length)
            {
                // The framing layer already validated the header's arithmetic,
                // so this should not happen; stopping rather than indexing past
                // the buffer keeps a malformed peer from crashing the session.
                break;
            }

            var index = (ushort)h.Range.IndexOf((uint)i);
            if (prefixLen > 0)
            {
                index = (ushort)ReadPrefix(data[off..], prefixLen);
                off += prefixLen;
            }

            output.Add(new Indexed<T>(index, parse(data.Slice(off, size), ctx)));
            off += size;
        }

        return output;
    }

    /// <summary>
    /// Handles the bit-packed variations, whose unit of encoding is the range
    /// rather than the object.
    /// </summary>
    private static void DispatchPacked(
        IMasterHandler handler,
        ObjectHeader h,
        Descriptor d,
        HeaderInfo info)
    {
        var count = (int)h.Count;
        var start = h.Range.Start;
        var data = h.Data.Span;

        switch (d.Measurement)
        {
            case PointType.Binary:
            {
                var raw = new List<Binary>(count);
                PackedObjects.ParsePackedBinary(data, count, raw);
                var output = new List<Indexed<Binary>>(raw.Count);
                for (var i = 0; i < raw.Count; i++)
                {
                    output.Add(new Indexed<Binary>((ushort)(start + (uint)i), raw[i]));
                }

                handler.HandleBinary(info, output);
                break;
            }

            case PointType.DoubleBitBinary:
            {
                var raw = new List<DoubleBitBinary>(count);
                PackedObjects.ParsePackedDoubleBit(data, count, raw);
                var output = new List<Indexed<DoubleBitBinary>>(raw.Count);
                for (var i = 0; i < raw.Count; i++)
                {
                    output.Add(new Indexed<DoubleBitBinary>((ushort)(start + (uint)i), raw[i]));
                }

                handler.HandleDoubleBit(info, output);
                break;
            }

            case PointType.BinaryOutputStatus:
            {
                var raw = new List<BinaryOutputStatus>(count);
                PackedObjects.ParsePackedBinaryOutput(data, count, raw);
                var output = new List<Indexed<BinaryOutputStatus>>(raw.Count);
                for (var i = 0; i < raw.Count; i++)
                {
                    output.Add(new Indexed<BinaryOutputStatus>((ushort)(start + (uint)i), raw[i]));
                }

                handler.HandleBinaryOutputStatus(info, output);
                break;
            }

            default:
                break;
        }
    }

    /// <summary>Decodes group 110 and 111 objects.</summary>
    /// <remarks>
    /// The variation is the string's length, which is why a range of strings of
    /// differing lengths arrives as several headers rather than one.
    /// </remarks>
    private static void DispatchOctetStrings(
        IMasterHandler handler,
        ObjectHeader h,
        HeaderInfo info,
        bool isEvent)
    {
        if (isEvent)
        {
            info = info with { Kind = Kind.Event };
        }

        var size = (int)h.Variation;
        if (size == 0)
        {
            // Variation zero means "any length" and appears only in requests.
            return;
        }

        var prefixLen = 0;
        var p = h.Qualifier.IndexPrefix;
        if (p.IsIndex())
        {
            prefixLen = p.Octets();
        }

        var count = (int)h.Count;
        var output = new List<Indexed<byte[]>>(count);
        var data = h.Data.Span;

        var off = 0;
        for (var i = 0; i < count; i++)
        {
            if (off + prefixLen + size > data.Length)
            {
                break;
            }

            var index = (ushort)h.Range.IndexOf((uint)i);
            if (prefixLen > 0)
            {
                index = (ushort)ReadPrefix(data[off..], prefixLen);
                off += prefixLen;
            }

            // Copied: the header aliases the session's receive buffer, and a
            // handler that keeps the string would otherwise see it change.
            var v = data.Slice(off, size).ToArray();
            off += size;

            output.Add(new Indexed<byte[]>(index, v));
        }

        handler.HandleOctetString(info, output);
    }

    private static uint ReadPrefix(ReadOnlySpan<byte> buf, int width) => width switch
    {
        1 => buf[0],
        2 => (uint)(buf[0] | (buf[1] << 8)),
        4 => (uint)(buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24)),
        _ => 0,
    };
}
