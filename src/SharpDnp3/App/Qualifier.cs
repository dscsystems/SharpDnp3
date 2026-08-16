// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Buffers.Binary;
using System.Globalization;

namespace SharpDnp3.App;

/// <summary>Says what precedes each object in the data field.</summary>
public enum IndexPrefix : byte
{
    /// <summary>
    /// Objects are packed with no per-object prefix. Their indexes come from
    /// the range field.
    /// </summary>
    None = 0,

    /// <summary>Each object is preceded by a one-octet point index.</summary>
    Index1 = 1,

    /// <summary>Each object is preceded by a two-octet point index.</summary>
    Index2 = 2,

    /// <summary>Each object is preceded by a four-octet point index.</summary>
    Index4 = 3,

    /// <summary>
    /// Each object is preceded by its own one-octet size, making the data
    /// self-describing. Used by variable-length objects such as file transfer.
    /// </summary>
    Size1 = 4,

    /// <summary>Each object is preceded by its own two-octet size.</summary>
    Size2 = 5,

    /// <summary>Each object is preceded by its own four-octet size.</summary>
    Size4 = 6,

    /// <summary>Not valid on the wire.</summary>
    Reserved = 7,
}

/// <summary>Helpers for <see cref="IndexPrefix"/>.</summary>
public static class IndexPrefixExtensions
{
    private static readonly string[] Names =
    [
        "none", "index8", "index16", "index32",
        "size8", "size16", "size32", "reserved",
    ];

    /// <summary>Renders the prefix using the protocol tools' spelling.</summary>
    public static string ToDisplayString(this IndexPrefix p) =>
        (int)p < Names.Length ? Names[(int)p] : "IndexPrefix(?)";

    /// <summary>The width of the prefix that precedes each object.</summary>
    public static int Octets(this IndexPrefix p) => p switch
    {
        IndexPrefix.None => 0,
        IndexPrefix.Index1 or IndexPrefix.Size1 => 1,
        IndexPrefix.Index2 or IndexPrefix.Size2 => 2,
        IndexPrefix.Index4 or IndexPrefix.Size4 => 4,
        _ => 0,
    };

    /// <summary>Reports whether the prefix carries a point index.</summary>
    public static bool IsIndex(this IndexPrefix p) =>
        p is IndexPrefix.Index1 or IndexPrefix.Index2 or IndexPrefix.Index4;

    /// <summary>
    /// Reports whether the prefix carries an object size, which makes the data
    /// self-describing and lets a parser walk objects whose length it could not
    /// otherwise know.
    /// </summary>
    public static bool IsSize(this IndexPrefix p) =>
        p is IndexPrefix.Size1 or IndexPrefix.Size2 or IndexPrefix.Size4;

    /// <summary>Reports whether the encoding is defined by the standard.</summary>
    public static bool Valid(this IndexPrefix p) => p <= IndexPrefix.Size4;
}

/// <summary>Says how the set of objects is delimited.</summary>
public enum RangeSpec : byte
{
    /// <summary>An inclusive one-octet start and stop point index.</summary>
    StartStop8 = 0,

    /// <summary>An inclusive two-octet start and stop point index.</summary>
    StartStop16 = 1,

    /// <summary>An inclusive four-octet start and stop point index.</summary>
    StartStop32 = 2,

    /// <summary>Start and stop one-octet virtual addresses.</summary>
    Virtual8 = 3,

    /// <summary>Start and stop two-octet virtual addresses.</summary>
    Virtual16 = 4,

    /// <summary>Start and stop four-octet virtual addresses.</summary>
    Virtual32 = 5,

    /// <summary>
    /// No range field. Every object of the type, which is how a class poll and
    /// an integrity poll are expressed.
    /// </summary>
    AllObjects = 6,

    /// <summary>A one-octet count of objects with no index information.</summary>
    Count8 = 7,

    /// <summary>A two-octet count of objects with no index information.</summary>
    Count16 = 8,

    /// <summary>A four-octet count of objects with no index information.</summary>
    Count32 = 9,

    /// <summary>Not valid on the wire.</summary>
    ReservedA = 10,

    /// <summary>A one-octet count of objects, each self-delimiting.</summary>
    Variable = 11,
}

/// <summary>Helpers for <see cref="RangeSpec"/>.</summary>
public static class RangeSpecExtensions
{
    private static readonly string[] Names =
    [
        "start-stop8", "start-stop16", "start-stop32",
        "virtual8", "virtual16", "virtual32",
        "all-objects",
        "count8", "count16", "count32",
        "reserved", "variable",
        "reserved", "reserved", "reserved", "reserved",
    ];

    /// <summary>Renders the specifier using the protocol tools' spelling.</summary>
    public static string ToDisplayString(this RangeSpec r) =>
        (int)r < Names.Length ? Names[(int)r] : "RangeSpec(?)";

    /// <summary>The width of the range field on the wire.</summary>
    public static int Octets(this RangeSpec r) => r switch
    {
        // a start and a stop, one octet each
        RangeSpec.StartStop8 or RangeSpec.Virtual8 => 2,
        RangeSpec.StartStop16 or RangeSpec.Virtual16 => 4,
        RangeSpec.StartStop32 or RangeSpec.Virtual32 => 8,
        RangeSpec.AllObjects => 0,
        RangeSpec.Count8 or RangeSpec.Variable => 1,
        RangeSpec.Count16 => 2,
        RangeSpec.Count32 => 4,
        _ => 0,
    };

    /// <summary>Reports whether the range carries a start and stop index.</summary>
    public static bool IsStartStop(this RangeSpec r) => r <= RangeSpec.Virtual32;

    /// <summary>Reports whether the range carries a plain object count.</summary>
    public static bool IsCount(this RangeSpec r) => r is
        RangeSpec.Count8 or RangeSpec.Count16 or RangeSpec.Count32 or RangeSpec.Variable;

    /// <summary>Reports whether the encoding is defined by the standard.</summary>
    public static bool Valid(this RangeSpec r) => r <= RangeSpec.Count32 || r == RangeSpec.Variable;
}

/// <summary>
/// The octet following a group and variation that says how the objects are
/// addressed and delimited.
/// </summary>
/// <remarks>
/// <code>
/// bit 7     reserved, transmitted as zero
/// bits 6-4  index prefix
/// bits 3-0  range specifier
/// </code>
/// </remarks>
public readonly struct Qualifier : IEquatable<Qualifier>
{
    /// <summary>The raw qualifier octet.</summary>
    public byte Value { get; }

    /// <summary>Wraps a raw qualifier octet.</summary>
    public Qualifier(byte value) => Value = value;

    /// <summary>Composes a qualifier from its two fields.</summary>
    public static Qualifier Make(IndexPrefix prefix, RangeSpec range) =>
        new((byte)((((byte)prefix & 0x07) << 4) | ((byte)range & 0x0F)));

    /// <summary>Returns the prefix field.</summary>
    public IndexPrefix IndexPrefix => (IndexPrefix)((Value >> 4) & 0x07);

    /// <summary>Returns the range specifier field.</summary>
    public RangeSpec RangeSpec => (RangeSpec)(Value & 0x0F);

    /// <summary>
    /// Reports whether the reserved high bit is set, which a conforming device
    /// never transmits.
    /// </summary>
    public bool Reserved => (Value & 0x80) != 0;

    /// <summary>Wraps a raw octet.</summary>
    public static implicit operator Qualifier(byte value) => new(value);

    /// <summary>Unwraps to the raw octet.</summary>
    public static explicit operator byte(Qualifier q) => q.Value;

    /// <summary>Equality by raw octet.</summary>
    public bool Equals(Qualifier other) => Value == other.Value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Qualifier other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value;

    /// <summary>Equality by raw octet.</summary>
    public static bool operator ==(Qualifier a, Qualifier b) => a.Value == b.Value;

    /// <summary>Inequality by raw octet.</summary>
    public static bool operator !=(Qualifier a, Qualifier b) => a.Value != b.Value;

    /// <inheritdoc/>
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "0x{0:x2}({1},{2})",
        Value, IndexPrefix.ToDisplayString(), RangeSpec.ToDisplayString());
}

/// <summary>A decoded range field.</summary>
public readonly record struct ObjectRange
{
    /// <summary>How the set of objects is delimited.</summary>
    public RangeSpec Spec { get; init; }

    /// <summary>
    /// The inclusive lower index bound, valid when the specifier is a
    /// start-stop form.
    /// </summary>
    public uint Start { get; init; }

    /// <summary>
    /// The inclusive upper index bound, valid when the specifier is a
    /// start-stop form.
    /// </summary>
    public uint Stop { get; init; }

    /// <summary>
    /// The number of objects the header describes. For a start-stop range it is
    /// derived as <c>Stop-Start+1</c>; for <see cref="RangeSpec.AllObjects"/>
    /// it is zero, because the count is not on the wire.
    /// </summary>
    public uint Count { get; init; }

    /// <summary>
    /// Returns the point index of the <paramref name="i"/>'th object described
    /// by a start-stop range. It is meaningless for count ranges, where indexes
    /// come from per-object prefixes instead.
    /// </summary>
    public uint IndexOf(uint i) => Start + i;

    /// <inheritdoc/>
    public override string ToString()
    {
        if (Spec == RangeSpec.AllObjects)
        {
            return "all";
        }

        return Spec.IsStartStop()
            ? string.Format(CultureInfo.InvariantCulture, "[{0}..{1}]", Start, Stop)
            : string.Format(CultureInfo.InvariantCulture, "count={0}", Count);
    }
}

/// <summary>Encodes and decodes range fields.</summary>
internal static class RangeCodec
{
    /// <summary>
    /// Decodes the range field for <paramref name="spec"/> from the front of
    /// <paramref name="buf"/>, reporting the octets consumed.
    /// </summary>
    public static AppParseStatus ParseRange(
        RangeSpec spec,
        ReadOnlySpan<byte> buf,
        out ObjectRange range,
        out int consumed)
    {
        range = default;
        consumed = 0;

        var n = spec.Octets();
        if (buf.Length < n)
        {
            return AppParseStatus.Truncated;
        }

        uint start = 0;
        uint stop = 0;
        uint count = 0;

        switch (spec)
        {
            case RangeSpec.AllObjects:
                range = new ObjectRange { Spec = spec };
                consumed = 0;
                return AppParseStatus.Ok;

            case RangeSpec.StartStop8:
            case RangeSpec.Virtual8:
                start = buf[0];
                stop = buf[1];
                break;

            case RangeSpec.StartStop16:
            case RangeSpec.Virtual16:
                start = BinaryPrimitives.ReadUInt16LittleEndian(buf[0..2]);
                stop = BinaryPrimitives.ReadUInt16LittleEndian(buf[2..4]);
                break;

            case RangeSpec.StartStop32:
            case RangeSpec.Virtual32:
                start = BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]);
                stop = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..8]);
                break;

            case RangeSpec.Count8:
            case RangeSpec.Variable:
                count = buf[0];
                break;

            case RangeSpec.Count16:
                count = BinaryPrimitives.ReadUInt16LittleEndian(buf[0..2]);
                break;

            case RangeSpec.Count32:
                count = BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]);
                break;

            default:
                return AppParseStatus.BadQualifier;
        }

        if (spec.IsStartStop())
        {
            if (stop < start)
            {
                return AppParseStatus.BadRange;
            }

            // Stop-Start+1 is computed in 64 bits: a range of 0..0xFFFFFFFF is
            // legal on the wire and overflows a uint count.
            var wide = (ulong)stop - start + 1;
            if (wide > uint.MaxValue)
            {
                return AppParseStatus.BadRange;
            }

            count = (uint)wide;
        }

        range = new ObjectRange { Spec = spec, Start = start, Stop = stop, Count = count };
        consumed = n;
        return AppParseStatus.Ok;
    }

    /// <summary>Encodes a range field onto <paramref name="dst"/>.</summary>
    public static void AppendRange(List<byte> dst, ObjectRange r)
    {
        switch (r.Spec)
        {
            case RangeSpec.AllObjects:
                return;

            case RangeSpec.StartStop8:
            case RangeSpec.Virtual8:
                dst.Add((byte)r.Start);
                dst.Add((byte)r.Stop);
                return;

            case RangeSpec.StartStop16:
            case RangeSpec.Virtual16:
                AppendUInt16(dst, (ushort)r.Start);
                AppendUInt16(dst, (ushort)r.Stop);
                return;

            case RangeSpec.StartStop32:
            case RangeSpec.Virtual32:
                AppendUInt32(dst, r.Start);
                AppendUInt32(dst, r.Stop);
                return;

            case RangeSpec.Count8:
            case RangeSpec.Variable:
                dst.Add((byte)r.Count);
                return;

            case RangeSpec.Count16:
                AppendUInt16(dst, (ushort)r.Count);
                return;

            case RangeSpec.Count32:
                AppendUInt32(dst, r.Count);
                return;

            default:
                return;
        }
    }

    internal static void AppendUInt16(List<byte> dst, ushort value)
    {
        dst.Add((byte)value);
        dst.Add((byte)(value >> 8));
    }

    internal static void AppendUInt32(List<byte> dst, uint value)
    {
        dst.Add((byte)value);
        dst.Add((byte)(value >> 8));
        dst.Add((byte)(value >> 16));
        dst.Add((byte)(value >> 24));
    }
}
