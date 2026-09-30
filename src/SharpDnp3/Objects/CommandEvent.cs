// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Buffers.Binary;

namespace SharpDnp3.Objects;

/// <summary>
/// Command events record a control that was operated: group 13 for a binary
/// output and group 43 for an analog output. Both begin with a status octet.
/// </summary>
public static class CommandEventCodec
{
    /// <summary>Returns the size in octets of a group 13 or 43 variation, and whether it exists.</summary>
    public static bool TrySize(byte group, byte variation, out int size)
    {
        size = 0;
        switch (group, variation)
        {
            case (13, 1): size = 1; return true;
            case (13, 2): size = 1 + CommandObjects.Time48Size; return true;
            case (43, 1 or 5): size = 5; return true;
            case (43, 2): size = 3; return true;
            case (43, 3 or 7): size = 5 + CommandObjects.Time48Size; return true;
            case (43, 4): size = 3 + CommandObjects.Time48Size; return true;
            case (43, 6): size = 9; return true;
            case (43, 8): size = 9 + CommandObjects.Time48Size; return true;
            default: return false;
        }
    }

    private static bool HasTime(byte group, byte variation) =>
        group == 13 ? variation == 2 : variation is 3 or 4 or 7 or 8;

    /// <summary>
    /// Appends one group 13 or 43 object. It appends nothing for a variation
    /// that does not exist.
    /// </summary>
    /// <remarks>
    /// A binary event packs the status into the low seven bits of one octet and
    /// the commanded state into the top one. An analog event carries the status
    /// octet and then the value at the variation's width; an integer variation
    /// saturates rather than wrapping.
    /// </remarks>
    public static void Append(List<byte> dst, byte group, byte variation, CommandEvent e)
    {
        ArgumentNullException.ThrowIfNull(dst);
        if (!TrySize(group, variation, out _))
        {
            return;
        }

        if (group == 13)
        {
            dst.Add((byte)(((byte)e.Status & 0x7F) | (e.State ? 0x80 : 0)));
        }
        else
        {
            dst.Add((byte)e.Status);
            switch (variation)
            {
                case 1 or 3: ObjectConvert.AppendUInt32(dst, (uint)(int)Saturate(e.Value, int.MinValue, int.MaxValue)); break;
                case 2 or 4: ObjectConvert.AppendUInt16(dst, (ushort)(short)Saturate(e.Value, short.MinValue, short.MaxValue)); break;
                case 5 or 7: ObjectConvert.AppendSingle(dst, (float)e.Value); break;
                default: ObjectConvert.AppendDouble(dst, e.Value); break;
            }
        }

        if (HasTime(group, variation))
        {
            CommandObjects.AppendTime48(dst, e.Time);
        }
    }

    private static long Saturate(double v, long lo, long hi) =>
        double.IsNaN(v) ? 0 : v >= hi ? hi : v <= lo ? lo : (long)v;

    /// <summary>Decodes one group 13 or 43 object, reporting false if the variation does not exist or the buffer is short.</summary>
    public static bool TryParse(byte group, byte variation, ReadOnlySpan<byte> buf, out CommandEvent e)
    {
        e = default;
        if (!TrySize(group, variation, out var size) || buf.Length < size)
        {
            return false;
        }

        var status = CommandStatus.Success;
        var state = false;
        var value = 0.0;
        if (group == 13)
        {
            status = (CommandStatus)(buf[0] & 0x7F);
            state = (buf[0] & 0x80) != 0;
        }
        else
        {
            status = (CommandStatus)buf[0];
            value = variation switch
            {
                1 or 3 => BinaryPrimitives.ReadInt32LittleEndian(buf[1..]),
                2 or 4 => BinaryPrimitives.ReadInt16LittleEndian(buf[1..]),
                5 or 7 => BinaryPrimitives.ReadSingleLittleEndian(buf[1..]),
                _ => BinaryPrimitives.ReadDoubleLittleEndian(buf[1..]),
            };
        }

        var time = HasTime(group, variation)
            ? CommandObjects.ParseTime48(buf[(size - CommandObjects.Time48Size)..])
            : Timestamp.NoTime();
        e = new CommandEvent(status, group == 43, state, value, time);
        return true;
    }
}
