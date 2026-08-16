// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Buffers.Binary;

namespace SharpDnp3.Objects;

/// <summary>
/// Command and time objects, hand-written because they do not decode into a
/// measurement type.
/// </summary>
/// <remarks>
/// A CROB is five heterogeneous fields that mean something only together, and
/// an analog output command puts its value before its status where every status
/// object puts them the other way round. Generating these would mean teaching
/// the generator about field roles it needs nowhere else.
/// </remarks>
public static class CommandObjects
{
    /// <summary>
    /// The encoded size of a group 12 variation 1 control relay output block:
    /// control code, count, on time, off time and status.
    /// </summary>
    public const int CrobSize = 11;

    /// <summary>
    /// Decodes a control relay output block from <paramref name="buf"/>, which
    /// must hold at least <see cref="CrobSize"/> octets.
    /// </summary>
    public static ControlRelayOutputBlock ParseCrob(ReadOnlySpan<byte> buf) => new()
    {
        Code = new ControlCode(buf[0]),
        Count = buf[1],
        OnTime = BinaryPrimitives.ReadUInt32LittleEndian(buf[2..6]),
        OffTime = BinaryPrimitives.ReadUInt32LittleEndian(buf[6..10]),
        Status = (CommandStatus)buf[10],
    };

    /// <summary>Encodes a control relay output block.</summary>
    public static void AppendCrob(List<byte> dst, ControlRelayOutputBlock c)
    {
        ArgumentNullException.ThrowIfNull(dst);
        dst.Add(c.Code.Value);
        dst.Add(c.Count);
        ObjectConvert.AppendUInt32(dst, c.OnTime);
        ObjectConvert.AppendUInt32(dst, c.OffTime);
        dst.Add((byte)c.Status);
    }

    /// <summary>The encoded size of a group 41 variation 1 command.</summary>
    public const int AnalogOutput32Size = 5;

    /// <summary>The encoded size of a group 41 variation 2 command.</summary>
    public const int AnalogOutput16Size = 3;

    /// <summary>The encoded size of a group 41 variation 3 command.</summary>
    public const int AnalogOutputFloatSize = 5;

    /// <summary>The encoded size of a group 41 variation 4 command.</summary>
    public const int AnalogOutputDoubleSize = 9;

    /// <summary>Decodes a group 41 variation 1 command.</summary>
    public static AnalogOutputInt32 ParseAnalogOutputInt32(ReadOnlySpan<byte> buf) => new(
        BinaryPrimitives.ReadInt32LittleEndian(buf[0..4]),
        (CommandStatus)buf[4]);

    /// <summary>Encodes a group 41 variation 1 command.</summary>
    public static void AppendAnalogOutputInt32(List<byte> dst, AnalogOutputInt32 v)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ObjectConvert.AppendInt32(dst, v.Value);
        dst.Add((byte)v.Status);
    }

    /// <summary>Decodes a group 41 variation 2 command.</summary>
    public static AnalogOutputInt16 ParseAnalogOutputInt16(ReadOnlySpan<byte> buf) => new(
        BinaryPrimitives.ReadInt16LittleEndian(buf[0..2]),
        (CommandStatus)buf[2]);

    /// <summary>Encodes a group 41 variation 2 command.</summary>
    public static void AppendAnalogOutputInt16(List<byte> dst, AnalogOutputInt16 v)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ObjectConvert.AppendInt16(dst, v.Value);
        dst.Add((byte)v.Status);
    }

    /// <summary>Decodes a group 41 variation 3 command.</summary>
    public static AnalogOutputFloat32 ParseAnalogOutputFloat32(ReadOnlySpan<byte> buf) => new(
        BinaryPrimitives.ReadSingleLittleEndian(buf[0..4]),
        (CommandStatus)buf[4]);

    /// <summary>Encodes a group 41 variation 3 command.</summary>
    public static void AppendAnalogOutputFloat32(List<byte> dst, AnalogOutputFloat32 v)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ObjectConvert.AppendSingle(dst, v.Value);
        dst.Add((byte)v.Status);
    }

    /// <summary>Decodes a group 41 variation 4 command.</summary>
    public static AnalogOutputFloat64 ParseAnalogOutputFloat64(ReadOnlySpan<byte> buf) => new(
        BinaryPrimitives.ReadDoubleLittleEndian(buf[0..8]),
        (CommandStatus)buf[8]);

    /// <summary>Encodes a group 41 variation 4 command.</summary>
    public static void AppendAnalogOutputFloat64(List<byte> dst, AnalogOutputFloat64 v)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ObjectConvert.AppendDouble(dst, v.Value);
        dst.Add((byte)v.Status);
    }

    // ---------- Time objects ----------

    /// <summary>The encoded size of an absolute DNP3 timestamp.</summary>
    public const int Time48Size = 6;

    /// <summary>
    /// Decodes a 48-bit absolute timestamp, as carried by group 50 variation 1
    /// and the group 51 common-time-of-occurrence objects.
    /// </summary>
    public static Timestamp ParseTime48(ReadOnlySpan<byte> buf) => new()
    {
        Time = Dnp3Time.FromDnp3(ObjectConvert.ReadTime48(buf)),
        Quality = TimestampQuality.Synchronized,
    };

    /// <summary>Encodes a 48-bit absolute timestamp.</summary>
    public static void AppendTime48(List<byte> dst, Timestamp t) =>
        ObjectConvert.AppendTime48(dst, Dnp3Time.ToDnp3(t.Time));

    /// <summary>
    /// Decodes a group 52 time delay, returning milliseconds.
    /// </summary>
    /// <remarks>
    /// Variation 1 is coarse and counts seconds; variation 2 is fine and counts
    /// milliseconds. Both are returned in milliseconds so callers need not
    /// care, which is the whole reason the two variations exist separately on
    /// the wire.
    /// </remarks>
    public static uint ParseTimeDelay(byte variation, ReadOnlySpan<byte> buf)
    {
        var v = (uint)BinaryPrimitives.ReadUInt16LittleEndian(buf[0..2]);
        return variation == 1 ? v * 1000 : v;
    }
}
