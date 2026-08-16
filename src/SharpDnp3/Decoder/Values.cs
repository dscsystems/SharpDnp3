// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Globalization;
using System.Text;
using SharpDnp3.App;
using SharpDnp3.Objects;

namespace SharpDnp3.Decoding;

/// <summary>
/// One decoded measurement together with the point index it was reported at.
/// </summary>
/// <remarks>
/// The value is held as a formatted string rather than as a typed union: a
/// decoder's job is to show what arrived, and every consumer of this namespace
/// — a log line, a terminal table, a text dump — wants text. Callers that need
/// typed measurements should use the object codecs directly.
/// </remarks>
public readonly record struct Value
{
    /// <summary>The point index the measurement was reported at.</summary>
    public ushort Index { get; init; }

    /// <summary>What kind of measurement it is.</summary>
    public PointType Type { get; init; }

    /// <summary>The value, already formatted for display.</summary>
    public string Text { get; init; }

    /// <summary>The quality octet.</summary>
    public Flags Flags { get; init; }

    /// <summary>When the measurement was taken.</summary>
    public Timestamp Time { get; init; }

    /// <inheritdoc/>
    public override string ToString()
    {
        var s = string.Format(CultureInfo.InvariantCulture, "[{0}] {1}", Index, Text);

        // The state bit is already spelled out as ON or OFF, so repeating it as
        // a quality flag is noise in the one place the output has to stay
        // scannable.
        var flags = Flags;
        if (Type is PointType.Binary or PointType.BinaryOutputStatus)
        {
            flags = flags.Clear(Flags.StateBit);
        }

        if (flags.Value != 0)
        {
            s += "  " + flags.StringFor(Type);
        }

        if (Time.IsValid)
        {
            s += "  " + Time;
        }

        return s;
    }
}

/// <summary>Decodes the measurements an object header introduces.</summary>
public static class ValueDecoder
{
    /// <summary>
    /// Decodes the measurements an object header introduces.
    /// </summary>
    /// <remarks>
    /// It returns an empty list for headers that carry no measurements — class
    /// objects, commands, times — rather than an error, because those are
    /// perfectly normal and a decoder that errored on them would be noise. The
    /// return value reports whether the header was one this method knows how to
    /// decode.
    /// <para>
    /// <paramref name="ctx"/> supplies the session state the objects themselves
    /// do not carry: whether the outstation's clock is synchronised, and the
    /// common time of occurrence that relative-time events are measured from.
    /// </para>
    /// </remarks>
    public static bool TryDecodeValues(ObjectHeader h, Context ctx, out List<Value> values)
    {
        values = [];

        var gv = GroupVar.GV(h.Group, h.Variation);
        if (h.Data.IsEmpty)
        {
            return false;
        }

        // Octet strings first: their length is the variation number, so there
        // is no descriptor row to look up and a registry-first check would drop
        // them.
        if (h.Group is 110 or 111)
        {
            values = DecodeOctetStrings(h);
            return true;
        }

        if (!ObjectRegistry.TryLookup(gv, out var d))
        {
            return false;
        }

        // Commands are not measurements, but they are the single most important
        // thing to be able to read in a capture: an operator debugging a failed
        // trip needs to see the control code and the status that came back.
        if (d.Kind == Kind.Command)
        {
            values = DecodeCommands(h, d);
            return values.Count > 0;
        }

        if (d.Measurement == PointType.Unknown)
        {
            return false;
        }

        var count = (int)h.Count;
        if (d.Packed)
        {
            values = DecodePacked(h, d, count);
            return true;
        }

        if (!d.TrySizeOctets(out var size) || size == 0)
        {
            return false;
        }

        var prefix = h.Qualifier.IndexPrefix;
        var prefixLen = prefix.IsIndex() ? prefix.Octets() : 0;

        var data = h.Data.Span;
        values = new List<Value>(count);

        var off = 0;
        for (var i = 0; i < count; i++)
        {
            if (off + prefixLen + size > data.Length)
            {
                // The framing layer validated this; stop rather than fault.
                break;
            }

            var index = (ushort)h.Range.IndexOf((uint)i);
            if (prefixLen > 0)
            {
                index = (ushort)ReadPrefix(data[off..], prefixLen);
                off += prefixLen;
            }

            values.Add(DecodeOne(gv, d, index, data.Slice(off, size), ctx));
            off += size;
        }

        return true;
    }

    /// <summary>Dispatches to the codec for the measurement type.</summary>
    private static Value DecodeOne(
        GroupVar gv,
        Descriptor d,
        ushort index,
        ReadOnlySpan<byte> buf,
        Context ctx)
    {
        switch (d.Measurement)
        {
            case PointType.Binary:
                if (ObjectRegistry.TryBinaryCodec(gv, out var bc))
                {
                    var m = bc.Parse(buf, ctx);
                    return Make(index, d, BoolText(m.Value), m.Flags, m.Time);
                }

                break;

            case PointType.DoubleBitBinary:
                if (ObjectRegistry.TryDoubleBitCodec(gv, out var dc))
                {
                    var m = dc.Parse(buf, ctx);
                    return Make(index, d, m.Value.ToDisplayString(), m.Flags, m.Time);
                }

                break;

            case PointType.Counter:
                if (ObjectRegistry.TryCounterCodec(gv, out var cc))
                {
                    var m = cc.Parse(buf, ctx);
                    return Make(
                        index, d, m.Value.ToString(CultureInfo.InvariantCulture), m.Flags, m.Time);
                }

                break;

            case PointType.FrozenCounter:
                if (ObjectRegistry.TryFrozenCounterCodec(gv, out var fc))
                {
                    var m = fc.Parse(buf, ctx);
                    return Make(
                        index, d, m.Value.ToString(CultureInfo.InvariantCulture), m.Flags, m.Time);
                }

                break;

            case PointType.Analog:
                if (ObjectRegistry.TryAnalogCodec(gv, out var ac))
                {
                    var m = ac.Parse(buf, ctx);
                    return Make(index, d, FormatFloat(m.Value), m.Flags, m.Time);
                }

                break;

            case PointType.BinaryOutputStatus:
                if (ObjectRegistry.TryBinaryOutputCodec(gv, out var boc))
                {
                    var m = boc.Parse(buf, ctx);
                    return Make(index, d, BoolText(m.Value), m.Flags, m.Time);
                }

                break;

            case PointType.AnalogOutputStatus:
                if (ObjectRegistry.TryAnalogOutputCodec(gv, out var aoc))
                {
                    var m = aoc.Parse(buf, ctx);
                    return Make(index, d, FormatFloat(m.Value), m.Flags, m.Time);
                }

                break;

            default:
                break;
        }

        return new Value { Index = index, Type = d.Measurement, Text = "" };
    }

    private static Value Make(ushort index, Descriptor d, string text, Flags flags, Timestamp time) =>
        new()
        {
            Index = index,
            Type = d.Measurement,
            Text = text,
            Flags = flags,
            Time = time,
        };

    /// <summary>
    /// Handles the bit-packed variations, whose unit of encoding is the range
    /// rather than the object.
    /// </summary>
    private static List<Value> DecodePacked(ObjectHeader h, Descriptor d, int count)
    {
        var output = new List<Value>(count);
        var data = h.Data.Span;

        switch (d.Measurement)
        {
            case PointType.DoubleBitBinary:
            {
                var raw = new List<DoubleBitBinary>(count);
                PackedObjects.ParsePackedDoubleBit(data, count, raw);
                for (var i = 0; i < raw.Count; i++)
                {
                    output.Add(new Value
                    {
                        Index = (ushort)h.Range.IndexOf((uint)i),
                        Type = d.Measurement,
                        Text = raw[i].Value.ToDisplayString(),
                        Flags = raw[i].Flags,
                    });
                }

                break;
            }

            case PointType.BinaryOutputStatus:
            {
                var raw = new List<BinaryOutputStatus>(count);
                PackedObjects.ParsePackedBinaryOutput(data, count, raw);
                for (var i = 0; i < raw.Count; i++)
                {
                    output.Add(new Value
                    {
                        Index = (ushort)h.Range.IndexOf((uint)i),
                        Type = d.Measurement,
                        Text = BoolText(raw[i].Value),
                        Flags = raw[i].Flags,
                    });
                }

                break;
            }

            default:
            {
                var raw = new List<Binary>(count);
                PackedObjects.ParsePackedBinary(data, count, raw);
                for (var i = 0; i < raw.Count; i++)
                {
                    output.Add(new Value
                    {
                        Index = (ushort)h.Range.IndexOf((uint)i),
                        Type = d.Measurement,
                        Text = BoolText(raw[i].Value),
                        Flags = raw[i].Flags,
                    });
                }

                break;
            }
        }

        return output;
    }

    /// <summary>
    /// Renders control relay output blocks and analog output commands, which
    /// carry their own structure rather than a measurement.
    /// </summary>
    private static List<Value> DecodeCommands(ObjectHeader h, Descriptor d)
    {
        var output = new List<Value>();

        if (!d.TrySizeOctets(out var size) || size == 0)
        {
            return output;
        }

        var prefix = h.Qualifier.IndexPrefix;
        var prefixLen = prefix.IsIndex() ? prefix.Octets() : 0;

        var data = h.Data.Span;
        var off = 0;

        for (uint i = 0; i < h.Count; i++)
        {
            if (off + prefixLen + size > data.Length)
            {
                break;
            }

            var index = (ushort)h.Range.IndexOf(i);
            if (prefixLen > 0)
            {
                index = (ushort)ReadPrefix(data[off..], prefixLen);
                off += prefixLen;
            }

            var buf = data.Slice(off, size);
            off += size;

            string text;
            switch (h.Group)
            {
                case 12:
                {
                    var c = CommandObjects.ParseCrob(buf);
                    text = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} count={1} on={2}ms off={3}ms → {4}",
                        c.Code, c.Count, c.OnTime, c.OffTime, c.Status.ToDisplayString());
                    break;
                }

                case 41:
                    text = AnalogOutputText(h.Variation, buf);
                    break;

                default:
                    continue;
            }

            output.Add(new Value { Index = index, Text = text });
        }

        return output;
    }

    private static string AnalogOutputText(byte variation, ReadOnlySpan<byte> buf)
    {
        switch (variation)
        {
            case 1:
            {
                var c = CommandObjects.ParseAnalogOutputInt32(buf);
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} (int32) → {1}", c.Value, c.Status.ToDisplayString());
            }

            case 2:
            {
                var c = CommandObjects.ParseAnalogOutputInt16(buf);
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} (int16) → {1}", c.Value, c.Status.ToDisplayString());
            }

            case 3:
            {
                var c = CommandObjects.ParseAnalogOutputFloat32(buf);
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} (float32) → {1}", FormatFloat(c.Value), c.Status.ToDisplayString());
            }

            case 4:
            {
                var c = CommandObjects.ParseAnalogOutputFloat64(buf);
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} (float64) → {1}", FormatFloat(c.Value), c.Status.ToDisplayString());
            }

            default:
                return "";
        }
    }

    /// <summary>
    /// Renders group 110 and 111 objects as text, falling back to hex when the
    /// bytes are not printable — a serial number is usually ASCII, but nothing
    /// in the protocol says it must be.
    /// </summary>
    private static List<Value> DecodeOctetStrings(ObjectHeader h)
    {
        var output = new List<Value>();

        var size = (int)h.Variation;
        if (size == 0)
        {
            return output;
        }

        var prefix = h.Qualifier.IndexPrefix;
        var prefixLen = prefix.IsIndex() ? prefix.Octets() : 0;

        var data = h.Data.Span;
        var off = 0;

        for (uint i = 0; i < h.Count; i++)
        {
            if (off + prefixLen + size > data.Length)
            {
                break;
            }

            var index = (ushort)h.Range.IndexOf(i);
            if (prefixLen > 0)
            {
                index = (ushort)ReadPrefix(data[off..], prefixLen);
                off += prefixLen;
            }

            var raw = data.Slice(off, size);
            off += size;

            output.Add(new Value
            {
                Index = index,
                Type = PointType.OctetString,
                Text = OctetText(raw),
            });
        }

        return output;
    }

    /// <summary>Renders an octet string for display.</summary>
    internal static string OctetText(ReadOnlySpan<byte> raw)
    {
        var printable = true;
        foreach (var c in raw)
        {
            if (c != 0 && (c < 0x20 || c > 0x7E))
            {
                printable = false;
                break;
            }
        }

        if (printable)
        {
            var end = raw.Length;
            while (end > 0 && raw[end - 1] == 0)
            {
                end--;
            }

            return Quote(Encoding.ASCII.GetString(raw[..end]));
        }

        var b = new StringBuilder(raw.Length * 3);
        for (var i = 0; i < raw.Length; i++)
        {
            if (i > 0)
            {
                b.Append(' ');
            }

            b.Append(raw[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return b.ToString();
    }

    /// <summary>Wraps text in double quotes, escaping what needs it.</summary>
    private static string Quote(string s)
    {
        var b = new StringBuilder(s.Length + 2);
        b.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                default: b.Append(c); break;
            }
        }

        b.Append('"');
        return b.ToString();
    }

    internal static uint ReadPrefix(ReadOnlySpan<byte> buf, int width) => width switch
    {
        1 => buf[0],
        2 => (uint)(buf[0] | (buf[1] << 8)),
        4 => (uint)(buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24)),
        _ => 0,
    };

    /// <summary>
    /// Renders a binary state the way an operator reads a mimic panel, not the
    /// way a programmer reads a bool.
    /// </summary>
    internal static string BoolText(bool v) => v ? "ON" : "OFF";

    /// <summary>
    /// Prints an analog the way a value belongs in a telemetry table: whole
    /// numbers without a trailing ".0", fractions without trailing zeros, and no
    /// exponent notation for the ranges telemetry actually uses.
    /// </summary>
    internal static string FormatFloat(double v)
    {
        if (v == Math.Truncate(v) && Math.Abs(v) < 1e15)
        {
            return ((long)v).ToString(CultureInfo.InvariantCulture);
        }

        return v.ToString("F6", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
    }
}
