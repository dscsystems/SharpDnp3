// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Group 0 is hand-written for the same reason group 70 is — the objects carry
// their own length — and for one more: there is nothing to generate. The
// variation is the attribute's identity rather than an encoding, so the table
// would need a row per attribute a device might invent.
//
// The encoding is the smallest in the protocol. One octet of data type, one of
// length, and that many octets of value.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SharpDnp3.Objects;

/// <summary>A group 0 object could not be decoded.</summary>
public sealed class AttributeException : MalformedException
{
    /// <summary>Creates the exception with a message.</summary>
    public AttributeException(string message) : base("objects: device attribute: " + message) { }
}

/// <summary>Encodes and decodes group 0 device attribute values.</summary>
public static class AttributeObjects
{
    /// <summary>
    /// The data type and length that introduce every attribute value.
    /// </summary>
    public const int AttributeHeaderSize = 2;

    /// <summary>The longest value a one-octet length can carry.</summary>
    public const int MaxAttributeValue = 255;

    /// <summary>
    /// Decodes one device attribute value and reports how many octets it
    /// consumed, so a header carrying several can be walked.
    /// </summary>
    /// <remarks>
    /// <paramref name="set"/> and <paramref name="variation"/> come from the
    /// object header — the header is where an attribute's identity lives — and
    /// are copied onto the result so a caller holding only the attribute still
    /// knows what it is.
    /// </remarks>
    public static bool TryParse(
        byte set,
        byte variation,
        ReadOnlySpan<byte> buf,
        out DeviceAttribute attribute,
        out int consumed,
        out string? error)
    {
        attribute = default;
        consumed = 0;
        error = null;

        if (buf.Length < AttributeHeaderSize)
        {
            error = string.Format(
                CultureInfo.InvariantCulture,
                "g0v{0} is {1} octets, needs {2}",
                variation, buf.Length, AttributeHeaderSize);
            return false;
        }

        var type = (AttributeType)buf[0];
        var size = buf[1];
        var end = AttributeHeaderSize + size;
        if (end > buf.Length)
        {
            error = string.Format(
                CultureInfo.InvariantCulture,
                "g0v{0} declares {1} octets with {2} left",
                variation, size, buf.Length - AttributeHeaderSize);
            return false;
        }

        var value = buf[AttributeHeaderSize..end];

        switch (type)
        {
            case AttributeType.VisibleString:
                attribute = new DeviceAttribute
                {
                    Set = set,
                    Variation = variation,
                    Type = type,
                    Text = Encoding.ASCII.GetString(value),
                };
                break;

            case AttributeType.UnsignedInt:
            {
                if (!TryReadUint(value, out var n))
                {
                    error = string.Format(
                        CultureInfo.InvariantCulture,
                        "g0v{0}: an unsigned value of {1} octets",
                        variation, value.Length);
                    return false;
                }

                attribute = new DeviceAttribute
                {
                    Set = set, Variation = variation, Type = type, Number = (long)n,
                };
                break;
            }

            case AttributeType.SignedInt:
            {
                if (!TryReadInt(value, out var n))
                {
                    error = string.Format(
                        CultureInfo.InvariantCulture,
                        "g0v{0}: a signed value of {1} octets",
                        variation, value.Length);
                    return false;
                }

                attribute = new DeviceAttribute
                {
                    Set = set, Variation = variation, Type = type, Number = n,
                };
                break;
            }

            case AttributeType.Float:
            {
                double real;
                switch (size)
                {
                    case 4:
                        real = BinaryPrimitives.ReadSingleLittleEndian(value);
                        break;
                    case 8:
                        real = BinaryPrimitives.ReadDoubleLittleEndian(value);
                        break;
                    default:
                        error = string.Format(
                            CultureInfo.InvariantCulture,
                            "g0v{0} is a {1} octet float", variation, size);
                        return false;
                }

                attribute = new DeviceAttribute
                {
                    Set = set, Variation = variation, Type = type, Real = real,
                };
                break;
            }

            case AttributeType.Time:
                if (size != CommandObjects.Time48Size)
                {
                    error = string.Format(
                        CultureInfo.InvariantCulture,
                        "g0v{0} is a {1} octet time", variation, size);
                    return false;
                }

                attribute = new DeviceAttribute
                {
                    Set = set,
                    Variation = variation,
                    Type = type,
                    Time = CommandObjects.ParseTime48(value).Time,
                };
                break;

            default:
                // An octet string, a bit string, or a type this implementation
                // has never heard of. The octets are kept either way: a value
                // nobody can interpret is still worth showing, and a device is
                // entitled to use a type code newer than this code.
                attribute = new DeviceAttribute
                {
                    Set = set,
                    Variation = variation,
                    Type = type,
                    Octets = value.ToArray(),
                };
                break;
        }

        consumed = end;
        return true;
    }

    /// <summary>Decodes one attribute, throwing on malformed input.</summary>
    public static DeviceAttribute Parse(
        byte set, byte variation, ReadOnlySpan<byte> buf, out int consumed) =>
        TryParse(set, variation, buf, out var attribute, out consumed, out var error)
            ? attribute
            : throw new AttributeException(error!);

    /// <summary>
    /// Encodes an attribute's value: its data type, its length, and the octets
    /// themselves.
    /// </summary>
    /// <remarks>
    /// The identity goes in the object header, not here.
    /// </remarks>
    public static void Append(List<byte> dst, DeviceAttribute a)
    {
        ArgumentNullException.ThrowIfNull(dst);

        var value = new List<byte>(16);

        switch (a.Type)
        {
            case AttributeType.VisibleString:
                value.AddRange(Encoding.ASCII.GetBytes(a.Text ?? string.Empty));
                break;

            case AttributeType.UnsignedInt:
            case AttributeType.SignedInt:
                // The narrowest width that holds the number, which is what
                // devices send: a point count of 6 arrives as one octet, not
                // eight.
                AppendNarrowInt(value, a.Number, a.Type == AttributeType.SignedInt);
                break;

            case AttributeType.Float:
            {
                Span<byte> buf = stackalloc byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(buf, (float)a.Real);
                value.AddRange(buf);
                break;
            }

            case AttributeType.Time:
                CommandObjects.AppendTime48(value, new Timestamp { Time = a.Time });
                break;

            default:
                value.AddRange(a.Octets.Span);
                break;
        }

        if (value.Count > MaxAttributeValue)
        {
            throw new AttributeException(string.Format(
                CultureInfo.InvariantCulture,
                "g0v{0} is {1} octets, and the length field holds {2}",
                a.Variation, value.Count, MaxAttributeValue));
        }

        dst.Add((byte)a.Type);
        dst.Add((byte)value.Count);
        dst.AddRange(value);
    }

    /// <summary>The encoded size of an attribute, header included.</summary>
    public static int SizeOf(DeviceAttribute a)
    {
        var scratch = new List<byte>(16);
        Append(scratch, a);
        return scratch.Count;
    }

    /// <summary>
    /// Decodes an unsigned attribute of one, two, four or eight octets.
    /// </summary>
    /// <remarks>
    /// The width is whatever the device sent. Anything else is refused rather
    /// than padded, because a length nobody expects usually means the value is
    /// not what this thinks it is.
    /// </remarks>
    private static bool TryReadUint(ReadOnlySpan<byte> b, out ulong value)
    {
        switch (b.Length)
        {
            case 1: value = b[0]; return true;
            case 2: value = BinaryPrimitives.ReadUInt16LittleEndian(b); return true;
            case 4: value = BinaryPrimitives.ReadUInt32LittleEndian(b); return true;
            case 8: value = BinaryPrimitives.ReadUInt64LittleEndian(b); return true;
            default: value = 0; return false;
        }
    }

    /// <summary>Decodes a signed attribute, sign-extending from its width.</summary>
    private static bool TryReadInt(ReadOnlySpan<byte> b, out long value)
    {
        switch (b.Length)
        {
            case 1: value = (sbyte)b[0]; return true;
            case 2: value = BinaryPrimitives.ReadInt16LittleEndian(b); return true;
            case 4: value = BinaryPrimitives.ReadInt32LittleEndian(b); return true;
            case 8: value = BinaryPrimitives.ReadInt64LittleEndian(b); return true;
            default: value = 0; return false;
        }
    }

    /// <summary>Encodes an integer in the fewest octets that hold it.</summary>
    private static void AppendNarrowInt(List<byte> dst, long v, bool signed)
    {
        if (signed ? v is >= sbyte.MinValue and <= sbyte.MaxValue : v is >= 0 and <= byte.MaxValue)
        {
            dst.Add((byte)v);
            return;
        }

        if (signed
            ? v is >= short.MinValue and <= short.MaxValue
            : v is >= 0 and <= ushort.MaxValue)
        {
            Span<byte> buf = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)v);
            dst.AddRange(buf);
            return;
        }

        if (signed ? v is >= int.MinValue and <= int.MaxValue : v is >= 0 and <= uint.MaxValue)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)v);
            dst.AddRange(buf);
            return;
        }

        {
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(buf, (ulong)v);
            dst.AddRange(buf);
        }
    }
}
