// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

namespace SharpDnp3.Link;

/// <summary>
/// CRC-16/DNP: polynomial 0x3D65, reflected input and output, initial value
/// 0x0000, final XOR 0xFFFF.
/// </summary>
/// <remarks>
/// Reflecting the polynomial for the right-shifting form gives 0xA6BC, which
/// is what the table is built from. The check value for the ASCII string
/// "123456789" is 0xEA82; the test suite asserts that and cross-checks the
/// table against a bitwise implementation over random input, so a corrupted
/// table cannot pass silently.
/// </remarks>
internal static class Crc
{
    /// <summary>The polynomial as printed in IEEE 1815.</summary>
    internal const ushort Poly = 0x3D65;

    /// <summary><see cref="Poly"/> bit-reversed, for the right-shifting form.</summary>
    internal const ushort RevPoly = 0xA6BC;

    /// <summary>The value XORed into the register at the end.</summary>
    internal const ushort XorOut = 0xFFFF;

    private static readonly ushort[] Table = BuildTable();

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (var i = 0; i < table.Length; i++)
        {
            var crc = (ushort)i;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? (ushort)((crc >> 1) ^ RevPoly)
                    : (ushort)(crc >> 1);
            }

            table[i] = crc;
        }

        return table;
    }

    /// <summary>Computes the DNP3 CRC over <paramref name="data"/>.</summary>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc = (ushort)((crc >> 8) ^ Table[(byte)crc ^ b]);
        }

        return (ushort)(crc ^ XorOut);
    }

    /// <summary>
    /// The unoptimised reference. It exists so the table can be proven rather
    /// than trusted; production code calls <see cref="Compute"/>.
    /// </summary>
    internal static ushort ComputeBitwise(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? (ushort)((crc >> 1) ^ RevPoly)
                    : (ushort)(crc >> 1);
            }
        }

        return (ushort)(crc ^ XorOut);
    }

    /// <summary>
    /// Reports whether <paramref name="crc"/> holds <paramref name="data"/>'s
    /// CRC in the little-endian order DNP3 transmits it.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> rather than throwing when the span is
    /// too short, because it is called on partially received buffers.
    /// </remarks>
    public static bool IsValid(ReadOnlySpan<byte> data, ReadOnlySpan<byte> crc)
    {
        if (crc.Length < 2)
        {
            return false;
        }

        var want = Compute(data);
        return crc[0] == (byte)want && crc[1] == (byte)(want >> 8);
    }

    /// <summary>
    /// Writes the CRC of <paramref name="data"/> into <paramref name="dst"/> in
    /// transmission order. Returns the number of octets written, always 2.
    /// </summary>
    public static int Write(Span<byte> dst, ReadOnlySpan<byte> data)
    {
        var crc = Compute(data);
        dst[0] = (byte)crc;
        dst[1] = (byte)(crc >> 8);
        return 2;
    }
}
