// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Buffers.Binary;
using System.Globalization;

namespace SharpDnp3.App;

/// <summary>Resolves how large one object of a group and variation is.</summary>
/// <remarks>
/// The application layer cannot walk a fragment without this: the octets
/// following an object header are only delimited by the size implied by the
/// group and variation. Keeping it an interface is what lets this namespace
/// stay independent of the object codecs — in a full stack the generated object
/// registry supplies it.
/// </remarks>
public interface IObjectSizer
{
    /// <summary>
    /// Returns the encoded size of a single object in bits.
    /// </summary>
    /// <remarks>
    /// Bits rather than octets because several groups are bit-packed: a group 1
    /// variation 1 binary input occupies one bit, and a range of them shares
    /// octets. A size of zero means the object carries no data at all, as with
    /// the class objects of group 60.
    /// </remarks>
    /// <returns>
    /// <see langword="false"/> when the group and variation are unknown or when
    /// the object is variable-length, in which case the encoding must make its
    /// size self-describing through a size prefix.
    /// </returns>
    bool TrySizeBits(byte group, byte variation, out int bits);
}

/// <summary>
/// One decoded object header together with the raw octets of the objects it
/// introduces.
/// </summary>
public readonly record struct ObjectHeader
{
    /// <summary>The fixed part of an object header: group, variation and qualifier.</summary>
    public const int ObjectHeaderSize = 3;

    /// <summary>The object group.</summary>
    public byte Group { get; init; }

    /// <summary>The object variation.</summary>
    public byte Variation { get; init; }

    /// <summary>How the objects are addressed and delimited.</summary>
    public Qualifier Qualifier { get; init; }

    /// <summary>The decoded range field.</summary>
    public ObjectRange Range { get; init; }

    /// <summary>
    /// The object data this header introduces, aliasing the fragment it was
    /// parsed from. It excludes the header itself but includes any per-object
    /// index or size prefixes.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>Where this header begins in the fragment.</summary>
    /// <remarks>
    /// Decoders and hex viewers need this and <see cref="DataOffset"/> to point
    /// at the octets they are describing.
    /// </remarks>
    public int Offset { get; init; }

    /// <summary>Where this header's data begins in the fragment.</summary>
    public int DataOffset { get; init; }

    /// <summary>
    /// Returns the group and variation as a single value, which is how object
    /// registries are keyed.
    /// </summary>
    public ushort GroupVar => (ushort)((Group << 8) | Variation);

    /// <summary>The number of objects the header describes.</summary>
    public uint Count => Range.Count;

    /// <summary>The total octets the header and its data occupy.</summary>
    public int Size => ObjectHeaderSize + Qualifier.RangeSpec.Octets() + Data.Length;

    /// <inheritdoc/>
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "g{0}v{1} {2} {3} data={4}B",
        Group, Variation, Qualifier, Range, Data.Length);
}

/// <summary>Encodes and decodes object headers.</summary>
internal static class ObjectHeaderCodec
{
    /// <summary>Reads a little-endian unsigned integer of <paramref name="width"/> octets.</summary>
    private static uint ReadUIntLE(ReadOnlySpan<byte> buf, int width) => width switch
    {
        1 => buf[0],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(buf),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(buf),
        _ => 0,
    };

    /// <summary>
    /// Returns how many octets of object data follow a header.
    /// </summary>
    /// <remarks>
    /// <paramref name="buf"/> must begin at the first octet of the object data.
    /// The result is the count of octets those objects occupy, which the caller
    /// uses to advance to the next header.
    /// <para>
    /// <paramref name="carriesData"/> comes from the fragment's function code —
    /// see <see cref="FuncCodeExtensions.CarriesObjectData"/>. In a read
    /// request the header names points rather than carrying them, and no amount
    /// of inspecting the header itself reveals that.
    /// </para>
    /// </remarks>
    private static AppParseStatus ObjectDataLen(
        IObjectSizer sizer,
        byte group,
        byte variation,
        Qualifier qualifier,
        ObjectRange range,
        ReadOnlySpan<byte> buf,
        bool carriesData,
        out int length)
    {
        length = 0;
        var prefix = qualifier.IndexPrefix;

        if (!carriesData)
        {
            return AppParseStatus.Ok;
        }

        // Variation zero means "whatever variation you use by default". It
        // appears only in requests and never carries data.
        if (variation == 0)
        {
            return AppParseStatus.Ok;
        }

        // "All objects" has no count on the wire, so it cannot introduce data.
        // It is how a class poll asks for everything.
        if (range.Spec == RangeSpec.AllObjects)
        {
            return AppParseStatus.Ok;
        }

        if (range.Count == 0)
        {
            return AppParseStatus.Ok;
        }

        // A size prefix makes the data self-describing, so it can be walked
        // without knowing anything about the group. This is how variable-length
        // objects such as file transfer are carried.
        if (prefix.IsSize())
        {
            return WalkSizePrefixed(prefix.Octets(), range.Count, buf, out length);
        }

        if (!sizer.TrySizeBits(group, variation, out var bits))
        {
            return AppParseStatus.UnknownObject;
        }

        if (bits == 0)
        {
            return AppParseStatus.Ok;
        }

        var prefixOctets = prefix.Octets();

        if (bits < 8)
        {
            // Bit-packed objects share octets across the whole range, so a
            // per-object index prefix cannot be expressed alongside them.
            if (prefixOctets != 0)
            {
                return AppParseStatus.BadQualifier;
            }

            var packed = ((ulong)range.Count * (ulong)bits + 7) / 8;
            return CheckFits(packed, buf, out length);
        }

        if (bits % 8 != 0)
        {
            return AppParseStatus.UnknownObject;
        }

        var total = (ulong)range.Count * ((ulong)prefixOctets + ((ulong)bits / 8));
        return CheckFits(total, buf, out length);
    }

    /// <summary>
    /// Advances over <paramref name="count"/> objects, each introduced by its
    /// own size field of <paramref name="width"/> octets.
    /// </summary>
    private static AppParseStatus WalkSizePrefixed(
        int width,
        uint count,
        ReadOnlySpan<byte> buf,
        out int length)
    {
        length = 0;
        var off = 0;
        for (uint i = 0; i < count; i++)
        {
            if (off + width > buf.Length)
            {
                return AppParseStatus.Truncated;
            }

            var size = ReadUIntLE(buf[off..], width);
            off += width;
            if ((ulong)off + size > (ulong)buf.Length)
            {
                return AppParseStatus.Truncated;
            }

            off += (int)size;
        }

        length = off;
        return AppParseStatus.Ok;
    }

    /// <summary>
    /// Converts a computed length to an <see cref="int"/> after confirming the
    /// buffer actually holds it.
    /// </summary>
    /// <remarks>
    /// The comparison happens in 64 bits because a 32-bit count multiplied by
    /// an object size overflows an <see cref="int"/>, and an overflowed length
    /// would index past the fragment.
    /// </remarks>
    private static AppParseStatus CheckFits(ulong total, ReadOnlySpan<byte> buf, out int length)
    {
        length = 0;
        if (total > (ulong)buf.Length)
        {
            return AppParseStatus.Truncated;
        }

        length = (int)total;
        return AppParseStatus.Ok;
    }

    /// <summary>
    /// Decodes one object header and its data from the front of
    /// <paramref name="buf"/>, which must begin at the group octet.
    /// </summary>
    /// <remarks>
    /// <paramref name="offset"/> is the position of <paramref name="buf"/>
    /// within the enclosing fragment and is recorded on the result so decoders
    /// can point at the original octets. <paramref name="carriesData"/> says
    /// whether object data follows the header, which depends on the enclosing
    /// fragment's function code — see
    /// <see cref="FuncCodeExtensions.CarriesObjectData"/>.
    /// </remarks>
    public static AppParseStatus ParseObjectHeader(
        IObjectSizer? sizer,
        ReadOnlyMemory<byte> buf,
        int offset,
        bool carriesData,
        out ObjectHeader header,
        out int consumed)
    {
        header = default;
        consumed = 0;
        sizer ??= ObjectSizing.DefaultSizer;

        var span = buf.Span;
        if (span.Length < ObjectHeader.ObjectHeaderSize)
        {
            return AppParseStatus.Truncated;
        }

        var group = span[0];
        var variation = span[1];
        var qualifier = new Qualifier(span[2]);

        if (qualifier.Reserved)
        {
            return AppParseStatus.BadQualifier;
        }

        if (!qualifier.IndexPrefix.Valid())
        {
            return AppParseStatus.BadQualifier;
        }

        if (!qualifier.RangeSpec.Valid())
        {
            return AppParseStatus.BadQualifier;
        }

        var status = RangeCodec.ParseRange(
            qualifier.RangeSpec, span[ObjectHeader.ObjectHeaderSize..], out var range, out var rangeLen);
        if (status != AppParseStatus.Ok)
        {
            return status;
        }

        var dataStart = ObjectHeader.ObjectHeaderSize + rangeLen;

        status = ObjectDataLen(
            sizer, group, variation, qualifier, range, span[dataStart..], carriesData, out var dataLen);
        if (status != AppParseStatus.Ok)
        {
            return status;
        }

        header = new ObjectHeader
        {
            Group = group,
            Variation = variation,
            Qualifier = qualifier,
            Range = range,
            Offset = offset,
            DataOffset = offset + dataStart,
            Data = buf.Slice(dataStart, dataLen),
        };
        consumed = dataStart + dataLen;
        return AppParseStatus.Ok;
    }

    /// <summary>
    /// Appends an object header and its data to <paramref name="dst"/>.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for the data being consistent with the group,
    /// variation, qualifier and range — this method does not re-derive the
    /// size.
    /// </remarks>
    public static void AppendObjectHeader(List<byte> dst, ObjectHeader h)
    {
        dst.Add(h.Group);
        dst.Add(h.Variation);
        dst.Add(h.Qualifier.Value);
        RangeCodec.AppendRange(dst, h.Range);
        if (!h.Data.IsEmpty)
        {
            dst.AddRange(h.Data.Span);
        }
    }
}
