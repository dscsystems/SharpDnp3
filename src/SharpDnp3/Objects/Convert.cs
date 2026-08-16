// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

namespace SharpDnp3.Objects;

/// <summary>
/// Helpers the generated codecs call. They live here rather than in the
/// generated file so their behaviour can be reasoned about and tested directly.
/// </summary>
public static class ObjectConvert
{
    /// <summary>Appends a 48-bit little-endian DNP3 timestamp.</summary>
    /// <remarks>
    /// <see cref="System.Buffers.Binary.BinaryPrimitives"/> has no six-octet
    /// helper, and DNP3 uses that width everywhere it carries a time.
    /// </remarks>
    public static void AppendTime48(List<byte> dst, ulong ms)
    {
        dst.Add((byte)ms);
        dst.Add((byte)(ms >> 8));
        dst.Add((byte)(ms >> 16));
        dst.Add((byte)(ms >> 24));
        dst.Add((byte)(ms >> 32));
        dst.Add((byte)(ms >> 40));
    }

    /// <summary>Appends a little-endian <see cref="ushort"/>.</summary>
    public static void AppendUInt16(List<byte> dst, ushort value)
    {
        dst.Add((byte)value);
        dst.Add((byte)(value >> 8));
    }

    /// <summary>Appends a little-endian <see cref="short"/>.</summary>
    public static void AppendInt16(List<byte> dst, short value) =>
        AppendUInt16(dst, (ushort)value);

    /// <summary>Appends a little-endian <see cref="uint"/>.</summary>
    public static void AppendUInt32(List<byte> dst, uint value)
    {
        dst.Add((byte)value);
        dst.Add((byte)(value >> 8));
        dst.Add((byte)(value >> 16));
        dst.Add((byte)(value >> 24));
    }

    /// <summary>Appends a little-endian <see cref="int"/>.</summary>
    public static void AppendInt32(List<byte> dst, int value) =>
        AppendUInt32(dst, (uint)value);

    /// <summary>Appends a little-endian <see cref="ulong"/>.</summary>
    public static void AppendUInt64(List<byte> dst, ulong value)
    {
        AppendUInt32(dst, (uint)value);
        AppendUInt32(dst, (uint)(value >> 32));
    }

    /// <summary>Appends a little-endian IEEE 754 single.</summary>
    public static void AppendSingle(List<byte> dst, float value) =>
        AppendUInt32(dst, BitConverter.SingleToUInt32Bits(value));

    /// <summary>Appends a little-endian IEEE 754 double.</summary>
    public static void AppendDouble(List<byte> dst, double value) =>
        AppendUInt64(dst, BitConverter.DoubleToUInt64Bits(value));

    /// <summary>Decodes a 48-bit little-endian DNP3 timestamp.</summary>
    public static ulong ReadTime48(ReadOnlySpan<byte> buf) =>
        buf[0] |
        ((ulong)buf[1] << 8) |
        ((ulong)buf[2] << 16) |
        ((ulong)buf[3] << 24) |
        ((ulong)buf[4] << 32) |
        ((ulong)buf[5] << 40);

    // The clamp helpers exist because converting an out-of-range double to an
    // integer type is unchecked in C#: the result is unspecified, and in
    // practice it is the minimum value of the type.
    //
    // That matters here. An analog point configured as 16-bit whose reading
    // drifts past 32767 would encode as -32768: a value at the opposite end of
    // the scale, indistinguishable from a real reading. Saturating is not
    // perfect either, but a pegged reading is recognisable as a pegged reading,
    // and the OVER_RANGE quality bit is there to say so.

    /// <summary>Converts to <see cref="short"/>, saturating rather than wrapping.</summary>
    public static short ClampInt16(double v)
    {
        // NaN never satisfies a relational pattern, so it is tested first
        // rather than folded into the switch below.
        if (double.IsNaN(v))
        {
            return 0;
        }

        return v switch
        {
            >= short.MaxValue => short.MaxValue,
            <= short.MinValue => short.MinValue,
            _ => (short)v,
        };
    }

    /// <summary>Converts to <see cref="int"/>, saturating rather than wrapping.</summary>
    public static int ClampInt32(double v)
    {
        if (double.IsNaN(v))
        {
            return 0;
        }

        return v switch
        {
            >= int.MaxValue => int.MaxValue,
            <= int.MinValue => int.MinValue,
            _ => (int)v,
        };
    }

    /// <summary>Converts to <see cref="ushort"/>, saturating rather than wrapping.</summary>
    public static ushort ClampUInt16(double v)
    {
        if (double.IsNaN(v) || v <= 0)
        {
            return 0;
        }

        return v >= ushort.MaxValue ? ushort.MaxValue : (ushort)v;
    }

    /// <summary>Converts to <see cref="uint"/>, saturating rather than wrapping.</summary>
    public static uint ClampUInt32(double v)
    {
        if (double.IsNaN(v) || v <= 0)
        {
            return 0;
        }

        return v >= uint.MaxValue ? uint.MaxValue : (uint)v;
    }
}
