// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

namespace SharpDnp3.Objects;

/// <summary>
/// Bit-packed objects share octets across a whole range, so they have no
/// per-object codec: the unit of encoding is the range, not the object. Group 1
/// variation 1 puts ten binary inputs in two octets rather than ten.
/// </summary>
/// <remarks>
/// These are hand-written because the generator's model is one object at a
/// time, and forcing a range-shaped encoding through it would complicate every
/// other variation to serve five.
/// </remarks>
public static class PackedObjects
{
    /// <summary>
    /// Returns how many octets a range of <paramref name="count"/> objects
    /// occupies at <paramref name="bitsPerObject"/> bits each.
    /// </summary>
    public static int PackedOctets(int count, int bitsPerObject) =>
        count <= 0 || bitsPerObject <= 0 ? 0 : ((count * bitsPerObject) + 7) / 8;

    /// <summary>
    /// Decodes <paramref name="count"/> single-bit binary values from
    /// <paramref name="buf"/>.
    /// </summary>
    /// <remarks>
    /// It is used for group 1 variation 1 (binary inputs), group 10 variation 1
    /// (binary output status) and group 80 variation 1 (internal indications),
    /// which share an encoding.
    /// <para>
    /// Packed variations carry no quality information, so every value comes
    /// back online — the encoding has nowhere to say otherwise.
    /// </para>
    /// </remarks>
    public static void ParsePackedBinary(ReadOnlySpan<byte> buf, int count, List<Binary> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        for (var i = 0; i < count; i++)
        {
            output.Add(new Binary(BitAt(buf, i), Flags.Online, Timestamp.NoTime()));
        }
    }

    /// <summary>
    /// Decodes <paramref name="count"/> single-bit binary output statuses.
    /// </summary>
    public static void ParsePackedBinaryOutput(
        ReadOnlySpan<byte> buf,
        int count,
        List<BinaryOutputStatus> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        for (var i = 0; i < count; i++)
        {
            output.Add(new BinaryOutputStatus(BitAt(buf, i), Flags.Online, Timestamp.NoTime()));
        }
    }

    /// <summary>
    /// Decodes <paramref name="count"/> two-bit double-bit binary values, as
    /// group 3 variation 1 encodes them.
    /// </summary>
    public static void ParsePackedDoubleBit(
        ReadOnlySpan<byte> buf,
        int count,
        List<DoubleBitBinary> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        for (var i = 0; i < count; i++)
        {
            var pair = i * 2;
            var idx = pair / 8;
            var shift = pair % 8;

            var v = DoubleBit.Intermediate;
            if (idx < buf.Length)
            {
                v = (DoubleBit)((buf[idx] >> shift) & 0x03);
            }

            output.Add(new DoubleBitBinary(v, Flags.Online, Timestamp.NoTime()));
        }
    }

    /// <summary>
    /// Encodes values as single bits, least significant bit first, padding the
    /// final octet with zeros.
    /// </summary>
    public static void AppendPackedBinary(List<byte> dst, IReadOnlyList<bool> values)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            return;
        }

        var start = dst.Count;
        var octets = PackedOctets(values.Count, 1);
        for (var i = 0; i < octets; i++)
        {
            dst.Add(0);
        }

        for (var i = 0; i < values.Count; i++)
        {
            if (values[i])
            {
                dst[start + (i / 8)] |= (byte)(1 << (i % 8));
            }
        }
    }

    /// <summary>
    /// Encodes values as two-bit pairs, least significant pair first.
    /// </summary>
    public static void AppendPackedDoubleBit(List<byte> dst, IReadOnlyList<DoubleBit> values)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            return;
        }

        var start = dst.Count;
        var octets = PackedOctets(values.Count, 2);
        for (var i = 0; i < octets; i++)
        {
            dst.Add(0);
        }

        for (var i = 0; i < values.Count; i++)
        {
            var pair = i * 2;
            dst[start + (pair / 8)] |= (byte)(((int)values[i] & 0x03) << (pair % 8));
        }
    }

    /// <summary>
    /// Reads the <paramref name="i"/>'th bit, least significant bit of the
    /// first octet first.
    /// </summary>
    /// <remarks>
    /// Reading past the buffer yields <see langword="false"/> rather than
    /// throwing, because the count and the buffer come from different fields of
    /// an attacker-controlled header and are not guaranteed to agree.
    /// </remarks>
    private static bool BitAt(ReadOnlySpan<byte> buf, int i)
    {
        var idx = i / 8;
        var shift = i % 8;
        return idx < buf.Length && (buf[idx] & (1 << shift)) != 0;
    }
}
