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
    /// <summary>
    /// The point index the measurement was reported at: the full 32-bit index
    /// the object header carried. A decoder exists to show what was on the
    /// wire, so it never narrows one to fit.
    /// </summary>
    public uint Index { get; init; }

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

        // File transfer likewise: its objects are self-describing and carry no
        // point index, so there is no descriptor row to look up and nothing the
        // measurement path below could do with them.
        if (h.Group == 70)
        {
            values = DecodeFileObjects(h);
            return values.Count > 0;
        }

        // And device attributes, where the variation is the attribute's
        // identity rather than an encoding: there is no row to look up because
        // there is no table that could hold one per attribute a device might
        // invent.
        if (h.Group == 0)
        {
            values = DecodeAttributes(h);
            return values.Count > 0;
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

            var index = h.Range.IndexOf((uint)i);
            if (prefixLen > 0)
            {
                index = ReadPrefix(data[off..], prefixLen);
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
        uint index,
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

    private static Value Make(uint index, Descriptor d, string text, Flags flags, Timestamp time) =>
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
                        Index = h.Range.IndexOf((uint)i),
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
                        Index = h.Range.IndexOf((uint)i),
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
                        Index = h.Range.IndexOf((uint)i),
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

            var index = h.Range.IndexOf(i);
            if (prefixLen > 0)
            {
                index = ReadPrefix(data[off..], prefixLen);
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
    /// <summary>Renders the group 70 objects a header carries.</summary>
    /// <remarks>
    /// Group 70 renders differently from everything else here because it is not
    /// measurements. A file exchange is a conversation, and what an engineer
    /// needs out of a capture is the thread of it: which file, which handle,
    /// which block, and what the outstation said about it.
    /// <para>
    /// The index a <see cref="Value"/> carries is the object's position in the
    /// header rather than a point index — group 70 has no point indexes — which
    /// is what keeps a header carrying several descriptors readable.
    /// </para>
    /// </remarks>
    private static List<Value> DecodeFileObjects(ObjectHeader h)
    {
        List<ReadOnlyMemory<byte>> objects;
        try
        {
            objects = FreeFormat.Objects(h);
        }
        catch (MalformedException)
        {
            return [];
        }

        var output = new List<Value>(objects.Count);
        for (var i = 0; i < objects.Count; i++)
        {
            if (TryFileObjectText(h.Variation, objects[i], out var text))
            {
                output.Add(new Value { Index = (uint)i, Text = text });
            }
        }

        return output;
    }

    /// <summary>Renders one group 70 object.</summary>
    /// <remarks>
    /// A malformed one is reported as such rather than dropped: a capture
    /// exists to show what arrived, and the fact that a device sent something
    /// undecodable is the most interesting thing on the line when it happens.
    /// </remarks>
    private static bool TryFileObjectText(
        byte variation, ReadOnlyMemory<byte> obj, out string text)
    {
        const string dateFormat = "yyyy-MM-dd HH:mm:ss";

        try
        {
            switch (variation)
            {
                case 2:
                {
                    var a = FileObjects.ParseAuth(obj.Span);

                    // The password is deliberately not rendered. A capture is a
                    // file that gets pasted into tickets.
                    text = string.Format(
                        CultureInfo.InvariantCulture,
                        "auth user=\"{0}\" key=0x{1:X8}", a.User, a.Key);
                    return true;
                }

                case 3:
                {
                    var c = FileObjects.ParseCommand(obj.Span);
                    var s = new StringBuilder();
                    s.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "{0} \"{1}\" req={2}",
                        c.Mode.ToString().ToLowerInvariant(), c.Name, c.RequestId);

                    if (c.Size > 0)
                    {
                        s.AppendFormat(CultureInfo.InvariantCulture, " size={0}", c.Size);
                    }

                    if (c.MaxBlockSize > 0)
                    {
                        s.AppendFormat(
                            CultureInfo.InvariantCulture, " block={0}", c.MaxBlockSize);
                    }

                    text = s.ToString();
                    return true;
                }

                case 4:
                {
                    var st = FileObjects.ParseCommandStatus(obj.Span);
                    var s = new StringBuilder();
                    s.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "handle=0x{0:X8} req={1} → {2}",
                        st.Handle, st.RequestId, st.Status.ToDisplayString());

                    if (st.Size > 0)
                    {
                        s.AppendFormat(CultureInfo.InvariantCulture, " size={0}", st.Size);
                    }

                    if (st.MaxBlockSize > 0)
                    {
                        s.AppendFormat(
                            CultureInfo.InvariantCulture, " block={0}", st.MaxBlockSize);
                    }

                    s.Append(OptionalText(st.Text));
                    text = s.ToString();
                    return true;
                }

                case 5:
                {
                    var t = FileObjects.ParseTransport(obj);

                    // The data itself is summarised, not dumped: a capture of a
                    // firmware image would otherwise be megabytes of hex nobody
                    // reads.
                    text = string.Format(
                        CultureInfo.InvariantCulture,
                        "handle=0x{0:X8} block={1}{2} data={3}B",
                        t.Handle, t.Block, t.Last ? " last" : string.Empty, t.Data.Length);
                    return true;
                }

                case 6:
                {
                    var st = FileObjects.ParseTransportStatus(obj.Span);
                    text = string.Format(
                        CultureInfo.InvariantCulture,
                        "handle=0x{0:X8} block={1}{2} → {3}{4}",
                        st.Handle, st.Block, st.Last ? " last" : string.Empty,
                        st.Status.ToDisplayString(), OptionalText(st.Text));
                    return true;
                }

                case 7:
                {
                    var d = FileObjects.ParseDescriptor(obj.Span);
                    var s = new StringBuilder();
                    s.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "{0} \"{1}\" {2} {3} octets",
                        d.Type.ToString().ToLowerInvariant(),
                        d.Name,
                        d.Permissions.ToDisplayString(),
                        d.Size);

                    if (d.Created != default)
                    {
                        s.Append(' ');
                        s.Append(d.Created.ToString(dateFormat, CultureInfo.InvariantCulture));
                    }

                    text = s.ToString();
                    return true;
                }

                case 8:
                    text = string.Format(
                        CultureInfo.InvariantCulture,
                        "file specification \"{0}\"",
                        Encoding.ASCII.GetString(obj.Span).TrimEnd('\0'));
                    return true;

                default:
                    text = string.Empty;
                    return false;
            }
        }
        catch (MalformedException ex)
        {
            text = "malformed: " + ex.Message;
            return true;
        }
    }

    private static string OptionalText(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty : " (" + s + ")";

    /// <summary>Renders the group 0 objects a header carries.</summary>
    /// <remarks>
    /// A device attribute renders as what it says rather than as a value at an
    /// index, because that is what it is: the variation names the attribute and
    /// the range names the set, so a capture reads "product name and model:
    /// RTU-9000" instead of a number nobody can look up mid-investigation.
    /// </remarks>
    private static List<Value> DecodeAttributes(ObjectHeader h)
    {
        var count = (int)h.Count;
        if (count == 0)
        {
            count = 1;
        }

        var output = new List<Value>(count);
        var off = 0;
        for (var i = 0; i < count; i++)
        {
            if (off >= h.Data.Length)
            {
                break;
            }

            var set = (byte)h.Range.IndexOf((uint)i);

            if (!AttributeObjects.TryParse(
                    set, h.Variation, h.Data.Span[off..], out var a, out var n, out var error))
            {
                // A capture exists to show what arrived. An attribute that will
                // not decode is the most interesting thing on the line when it
                // happens, so it is reported rather than dropped.
                output.Add(new Value
                {
                    Index = h.Variation,
                    Text = "malformed: " + error,
                });
                break;
            }

            off += n;

            // The index column carries the variation, which for group 0 is the
            // attribute's identity — the nearest thing it has to a point index.
            output.Add(new Value
            {
                Index = a.Variation,
                Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} = {1} [{2}]",
                    a.Name(), a.ValueText(), a.Type.ToDisplayString()),
            });
        }

        return output;
    }

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

            var index = h.Range.IndexOf(i);
            if (prefixLen > 0)
            {
                index = ReadPrefix(data[off..], prefixLen);
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
