// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Text;

namespace SharpDnp3;

/// <summary>
/// The two internal indication octets an outstation returns on every response:
/// its running health and error report.
/// </summary>
/// <remarks>
/// The low octet is IIN1 and the high octet is IIN2, matching the order they
/// appear on the wire.
/// </remarks>
public readonly struct Iin : IEquatable<Iin>
{
    /// <summary>The packed indication word.</summary>
    public ushort Value { get; }

    /// <summary>Wraps a packed indication word.</summary>
    public Iin(ushort value) => Value = value;

    // ---- IIN1 bits — outstation state ----

    /// <summary>The last request was received via a broadcast address.</summary>
    public static Iin Broadcast => new(0x0001);

    /// <summary>Class 1 events are available. The master should poll.</summary>
    public static Iin Class1Events => new(0x0002);

    /// <summary>Class 2 events are available.</summary>
    public static Iin Class2Events => new(0x0004);

    /// <summary>Class 3 events are available.</summary>
    public static Iin Class3Events => new(0x0008);

    /// <summary>The outstation wants its clock set.</summary>
    public static Iin NeedTime => new(0x0010);

    /// <summary>
    /// One or more points are in local control mode and will not accept
    /// commands.
    /// </summary>
    public static Iin LocalControl => new(0x0020);

    /// <summary>
    /// A device-specific fault. Its meaning is defined by the outstation, not
    /// the standard.
    /// </summary>
    public static Iin DeviceTrouble => new(0x0040);

    /// <summary>
    /// The outstation has restarted. The master must re-run its startup
    /// sequence and clear this bit; leaving it set means the outstation keeps
    /// reporting a restart it has already recovered from.
    /// </summary>
    public static Iin DeviceRestart => new(0x0080);

    // ---- IIN2 bits — request errors ----

    /// <summary>The function code is not implemented.</summary>
    public static Iin NoFuncCodeSupport => new(0x0100);

    /// <summary>
    /// The request referenced a group or variation the outstation does not
    /// have.
    /// </summary>
    public static Iin ObjectUnknown => new(0x0200);

    /// <summary>Qualifier, range or data fields were not valid.</summary>
    public static Iin ParameterError => new(0x0400);

    /// <summary>
    /// Events were lost because the buffer filled. The master's picture of the
    /// sequence of events now has a hole in it.
    /// </summary>
    public static Iin EventBufferOverflow => new(0x0800);

    /// <summary>The requested operation is already running.</summary>
    public static Iin AlreadyExecuting => new(0x1000);

    /// <summary>The outstation's configuration is not valid.</summary>
    public static Iin ConfigCorrupt => new(0x2000);

    /// <summary>Reserved; must be transmitted as zero.</summary>
    public static Iin Reserved1 => new(0x4000);

    /// <summary>Reserved; must be transmitted as zero.</summary>
    public static Iin Reserved2 => new(0x8000);

    /// <summary>No indications set.</summary>
    public static Iin None => default;

    /// <summary>Every "events available" bit.</summary>
    public static Iin EventClassMask => Class1Events | Class2Events | Class3Events;

    /// <summary>Every IIN2 bit that reports a problem with the request.</summary>
    public static Iin ErrorMask =>
        NoFuncCodeSupport | ObjectUnknown | ParameterError | AlreadyExecuting | ConfigCorrupt;

    /// <summary>Decodes the two IIN octets in wire order.</summary>
    public static Iin Parse(byte iin1, byte iin2) => new((ushort)(iin1 | (iin2 << 8)));

    /// <summary>Returns the two IIN octets in wire order.</summary>
    public (byte Iin1, byte Iin2) Octets() => ((byte)Value, (byte)(Value >> 8));

    /// <summary>Reports whether every bit in <paramref name="mask"/> is set.</summary>
    public bool Has(Iin mask) => (Value & mask.Value) == mask.Value;

    /// <summary>Reports whether any bit in <paramref name="mask"/> is set.</summary>
    public bool HasAny(Iin mask) => (Value & mask.Value) != 0;

    /// <summary>Returns this with every bit in <paramref name="mask"/> set.</summary>
    public Iin Set(Iin mask) => new((ushort)(Value | mask.Value));

    /// <summary>Returns this with every bit in <paramref name="mask"/> cleared.</summary>
    public Iin Clear(Iin mask) => new((ushort)(Value & ~mask.Value));

    /// <summary>Reports whether any event class has data waiting.</summary>
    public bool HasEvents() => HasAny(EventClassMask);

    /// <summary>
    /// Reports whether the outstation rejected something about the request.
    /// </summary>
    public bool HasError() => HasAny(ErrorMask);

    /// <summary>Returns the event-class bits alone.</summary>
    public Iin EventClasses() => this & EventClassMask;

    /// <summary>Bitwise OR.</summary>
    public static Iin operator |(Iin a, Iin b) => new((ushort)(a.Value | b.Value));

    /// <summary>Bitwise AND.</summary>
    public static Iin operator &(Iin a, Iin b) => new((ushort)(a.Value & b.Value));

    /// <summary>Bitwise complement.</summary>
    public static Iin operator ~(Iin a) => new((ushort)~a.Value);

    /// <summary>Equality by packed value.</summary>
    public bool Equals(Iin other) => Value == other.Value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Iin other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value;

    /// <summary>Equality by packed value.</summary>
    public static bool operator ==(Iin a, Iin b) => a.Value == b.Value;

    /// <summary>Inequality by packed value.</summary>
    public static bool operator !=(Iin a, Iin b) => a.Value != b.Value;

    private static readonly (ushort Bit, string Name)[] BitNames =
    [
        (0x0001, "BROADCAST"),
        (0x0002, "CLASS_1_EVENTS"),
        (0x0004, "CLASS_2_EVENTS"),
        (0x0008, "CLASS_3_EVENTS"),
        (0x0010, "NEED_TIME"),
        (0x0020, "LOCAL_CONTROL"),
        (0x0040, "DEVICE_TROUBLE"),
        (0x0080, "DEVICE_RESTART"),
        (0x0100, "NO_FUNC_CODE_SUPPORT"),
        (0x0200, "OBJECT_UNKNOWN"),
        (0x0400, "PARAMETER_ERROR"),
        (0x0800, "EVENT_BUFFER_OVERFLOW"),
        (0x1000, "ALREADY_EXECUTING"),
        (0x2000, "CONFIG_CORRUPT"),
        (0x4000, "RESERVED_1"),
        (0x8000, "RESERVED_2"),
    ];

    /// <summary>
    /// Renders the set bits by name, which is what a protocol log needs. An
    /// unset IIN renders as an em dash rather than "0x0000", because "no
    /// indications" is the common case and should not look like a value.
    /// </summary>
    public override string ToString()
    {
        if (Value == 0)
        {
            return "—";
        }

        var b = new StringBuilder();
        foreach (var (bit, name) in BitNames)
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
}
