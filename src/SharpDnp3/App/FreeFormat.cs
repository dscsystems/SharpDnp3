// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Free format is the qualifier a variable-length object travels under: a size
// prefix in front of every object and a count of them in the range field, which
// is what lets a parser walk objects whose length it cannot look up.
//
// File transfer is the reason it exists. A file command carries a name and a
// transport object carries a block of file data, so neither has a size the
// object table could state.

using System.Buffers.Binary;
using System.Globalization;

namespace SharpDnp3.App;

/// <summary>Builds and reads free-format object headers.</summary>
public static class FreeFormat
{
    /// <summary>
    /// The free-format qualifier, 0x5B: a two-octet size before each object,
    /// with a one-octet count of objects.
    /// </summary>
    /// <remarks>
    /// It is the encoding IEEE 1815 specifies for group 70, and the only
    /// free-format combination devices use in practice.
    /// </remarks>
    public static Qualifier Qualifier { get; } =
        App.Qualifier.Make(IndexPrefix.Size2, RangeSpec.Variable);

    /// <summary>
    /// The largest single free-format object, bounded by the two-octet size
    /// prefix that introduces it.
    /// </summary>
    public const int MaxFreeFormatObject = 0xFFFF;

    /// <summary>
    /// Builds an object header carrying one variable-length object.
    /// </summary>
    /// <remarks>
    /// The size prefix goes in the data rather than being derived at encode
    /// time: <see cref="ObjectHeader.Data"/> is the octets exactly as they go on
    /// the wire, and having one place that decides what the header means is what
    /// keeps the encoder and the size walk in agreement.
    /// </remarks>
    public static ObjectHeader Build(byte group, byte variation, ReadOnlySpan<byte> obj)
    {
        if (obj.Length > MaxFreeFormatObject)
        {
            throw new Dnp3Exception(string.Format(
                CultureInfo.InvariantCulture,
                "app: a free-format object of {0} octets exceeds the {1} a size prefix can carry",
                obj.Length, MaxFreeFormatObject));
        }

        var data = new byte[2 + obj.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)obj.Length);
        obj.CopyTo(data.AsSpan(2));

        return new ObjectHeader
        {
            Group = group,
            Variation = variation,
            Qualifier = Qualifier,
            Range = new ObjectRange { Spec = RangeSpec.Variable, Count = 1 },
            Data = data,
        };
    }

    /// <summary>
    /// Returns the objects a free-format header carries, with their size
    /// prefixes stripped.
    /// </summary>
    /// <remarks>
    /// The returned memory aliases the header's data. It refuses a header that
    /// is not free format, so a caller cannot mistake a fixed-size encoding of
    /// the same group for one — the octets would decode into something that
    /// looked plausible and was not.
    /// </remarks>
    public static List<ReadOnlyMemory<byte>> Objects(ObjectHeader h)
    {
        var prefix = h.Qualifier.IndexPrefix;
        if (!prefix.IsSize())
        {
            throw new MalformedException(string.Format(
                CultureInfo.InvariantCulture,
                "app: g{0}v{1} arrived with qualifier {2}, which is not free format",
                h.Group, h.Variation, h.Qualifier));
        }

        var width = prefix.Octets();
        var output = new List<ReadOnlyMemory<byte>>((int)Math.Min(h.Range.Count, 64));

        var data = h.Data;
        var off = 0;
        while (off < data.Length)
        {
            if (off + width > data.Length)
            {
                throw new MalformedException(string.Format(
                    CultureInfo.InvariantCulture,
                    "app: g{0}v{1}: a size prefix runs past the end of the header",
                    h.Group, h.Variation));
            }

            var size = (int)ReadUIntLE(data.Span[off..], width);
            off += width;

            if (off + size > data.Length)
            {
                throw new MalformedException(string.Format(
                    CultureInfo.InvariantCulture,
                    "app: g{0}v{1}: an object of {2} octets runs past the end of the header",
                    h.Group, h.Variation, size));
            }

            output.Add(data.Slice(off, size));
            off += size;
        }

        return output;
    }

    /// <summary>
    /// Returns the single object a header carries, which is what every group 70
    /// exchange in practice contains.
    /// </summary>
    public static ReadOnlyMemory<byte> FirstObject(ObjectHeader h)
    {
        var objects = Objects(h);
        return objects.Count > 0
            ? objects[0]
            : throw new MalformedException(string.Format(
                CultureInfo.InvariantCulture,
                "app: g{0}v{1} carries no object", h.Group, h.Variation));
    }

    private static uint ReadUIntLE(ReadOnlySpan<byte> buf, int width) => width switch
    {
        1 => buf[0],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(buf),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(buf),
        _ => 0,
    };
}
