// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Text;

namespace SharpDnp3;

/// <summary>
/// Identifies a measurement type, which fixes the meaning of the upper three
/// quality bits.
/// </summary>
public enum PointType : byte
{
    /// <summary>Type is not known, so the upper bits are shown by position.</summary>
    Unknown = 0,
    /// <summary>Single-bit status input.</summary>
    Binary,
    /// <summary>Two-bit status input.</summary>
    DoubleBitBinary,
    /// <summary>Running count.</summary>
    Counter,
    /// <summary>Counter captured at a freeze.</summary>
    FrozenCounter,
    /// <summary>Analog input.</summary>
    Analog,
    /// <summary>Present state of a control point.</summary>
    BinaryOutputStatus,
    /// <summary>Present value of an analog control point.</summary>
    AnalogOutputStatus,
    /// <summary>Variable-length opaque value.</summary>
    OctetString,
}

/// <summary>
/// The quality octet that accompanies most measurements.
/// </summary>
/// <remarks>
/// <para>
/// The low five bits mean the same thing for every measurement type. The upper
/// three are type-specific: what is <c>CHATTER_FILTER</c> on a binary input is
/// <c>OVER_RANGE</c> on an analog input and <c>ROLLOVER</c> on a counter.
/// Because the interpretation depends on the point type, <see cref="Flags"/>
/// stores the raw octet and leaves naming to <see cref="StringFor"/>.
/// </para>
/// </remarks>
public readonly struct Flags : IEquatable<Flags>
{
    /// <summary>The raw quality octet.</summary>
    public byte Value { get; }

    /// <summary>Wraps a raw quality octet.</summary>
    public Flags(byte value) => Value = value;

    // ---- Bits common to every measurement type ----

    /// <summary>
    /// The point is being read from the field. A cleared <see cref="Online"/>
    /// bit is the single most important quality signal in DNP3: the value
    /// present alongside it is not trustworthy.
    /// </summary>
    public static Flags Online => new(0x01);

    /// <summary>The value has not been updated since the device restarted.</summary>
    public static Flags Restart => new(0x02);

    /// <summary>Communication with the source of the point has failed.</summary>
    public static Flags CommLost => new(0x04);

    /// <summary>The value was forced by a downstream device.</summary>
    public static Flags RemoteForced => new(0x08);

    /// <summary>The value was forced by the outstation itself.</summary>
    public static Flags LocalForced => new(0x10);

    // ---- Type-specific bits ----

    /// <summary>
    /// Binary and double-bit binary inputs: the point is toggling faster than
    /// the outstation's filter allows.
    /// </summary>
    public static Flags ChatterFilter => new(0x20);

    /// <summary>
    /// Counters. Deprecated by the standard in favour of letting the counter
    /// wrap, but still emitted by older devices.
    /// </summary>
    public static Flags Rollover => new(0x20);

    /// <summary>
    /// Counters: the value cannot be compared against the previous reading.
    /// </summary>
    public static Flags Discontinuity => new(0x40);

    /// <summary>
    /// Analog inputs and outputs: the value exceeds the range the point can
    /// represent.
    /// </summary>
    public static Flags OverRange => new(0x20);

    /// <summary>
    /// Analog inputs and outputs: the reference used to digitise the value is
    /// not accurate.
    /// </summary>
    public static Flags ReferenceErr => new(0x40);

    /// <summary>
    /// Carries the value itself for binary points inside a flags octet, as in
    /// group 1 variation 2.
    /// </summary>
    public static Flags StateBit => new(0x80);

    /// <summary>No quality bits set.</summary>
    public static Flags None => default;

    /// <summary>Reports whether every bit in <paramref name="mask"/> is set.</summary>
    public bool Has(Flags mask) => (Value & mask.Value) == mask.Value;

    /// <summary>Reports whether any bit in <paramref name="mask"/> is set.</summary>
    public bool HasAny(Flags mask) => (Value & mask.Value) != 0;

    /// <summary>Returns these flags with every bit in <paramref name="mask"/> set.</summary>
    public Flags Set(Flags mask) => new((byte)(Value | mask.Value));

    /// <summary>Returns these flags with every bit in <paramref name="mask"/> cleared.</summary>
    public Flags Clear(Flags mask) => new((byte)(Value & ~mask.Value));

    /// <summary>
    /// Reports whether the value carrying these flags may be trusted: online,
    /// not restarting, not comm-lost, and not forced from either end.
    /// </summary>
    public bool IsGood() =>
        Has(Online) && !HasAny(Restart | CommLost | RemoteForced | LocalForced);

    /// <summary>Bitwise OR.</summary>
    public static Flags operator |(Flags a, Flags b) => new((byte)(a.Value | b.Value));

    /// <summary>Bitwise AND.</summary>
    public static Flags operator &(Flags a, Flags b) => new((byte)(a.Value & b.Value));

    /// <summary>Bitwise XOR.</summary>
    public static Flags operator ^(Flags a, Flags b) => new((byte)(a.Value ^ b.Value));

    /// <summary>Bitwise complement.</summary>
    public static Flags operator ~(Flags a) => new((byte)~a.Value);

    /// <summary>Wraps a raw octet.</summary>
    public static implicit operator Flags(byte value) => new(value);

    /// <summary>Unwraps to the raw octet.</summary>
    public static explicit operator byte(Flags flags) => flags.Value;

    /// <summary>Equality by raw octet.</summary>
    public bool Equals(Flags other) => Value == other.Value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Flags other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value;

    /// <summary>Equality by raw octet.</summary>
    public static bool operator ==(Flags a, Flags b) => a.Value == b.Value;

    /// <summary>Inequality by raw octet.</summary>
    public static bool operator !=(Flags a, Flags b) => a.Value != b.Value;

    /// <summary>The five type-independent bit names, low bit first.</summary>
    private static readonly string[] CommonNames =
        ["ONLINE", "RESTART", "COMM_LOST", "REMOTE_FORCED", "LOCAL_FORCED"];

    /// <summary>
    /// Renders the common bits by name. Type-specific bits are shown by
    /// position because their meaning depends on the point type; use
    /// <see cref="StringFor"/> when the type is known.
    /// </summary>
    public override string ToString() => StringFor(PointType.Unknown);

    /// <summary>
    /// Renders the flags with the upper bits named according to
    /// <paramref name="type"/>.
    /// </summary>
    public string StringFor(PointType type)
    {
        if (Value == 0)
        {
            return "—";
        }

        var b = new StringBuilder();
        for (var i = 0; i < CommonNames.Length; i++)
        {
            if ((Value & (1 << i)) != 0)
            {
                if (b.Length > 0)
                {
                    b.Append('|');
                }

                b.Append(CommonNames[i]);
            }
        }

        foreach (var (bit, name) in UpperBitNames(type))
        {
            if ((Value & bit) != 0)
            {
                if (b.Length > 0)
                {
                    b.Append('|');
                }

                b.Append(name);
            }
        }

        return b.ToString();
    }

    private static (byte Bit, string Name)[] UpperBitNames(PointType type) => type switch
    {
        PointType.Binary or PointType.BinaryOutputStatus or PointType.DoubleBitBinary =>
            [(0x20, "CHATTER_FILTER"), (0x40, "BIT6"), (0x80, "STATE")],
        PointType.Counter or PointType.FrozenCounter =>
            [(0x20, "ROLLOVER"), (0x40, "DISCONTINUITY"), (0x80, "BIT7")],
        PointType.Analog or PointType.AnalogOutputStatus =>
            [(0x20, "OVER_RANGE"), (0x40, "REFERENCE_ERR"), (0x80, "BIT7")],
        _ => [(0x20, "BIT5"), (0x40, "BIT6"), (0x80, "BIT7")],
    };
}
