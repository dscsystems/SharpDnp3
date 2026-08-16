// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Globalization;

namespace SharpDnp3;

/// <summary>Describes how much a timestamp can be relied on.</summary>
public enum TimestampQuality : byte
{
    /// <summary>
    /// The measurement carried no time at all. The <see cref="Timestamp.Time"/>
    /// field is the zero value.
    /// </summary>
    Invalid = 0,

    /// <summary>
    /// The outstation reported a time while its own clock was not synchronised
    /// to the master.
    /// </summary>
    Unsynchronized,

    /// <summary>
    /// The outstation's clock was synchronised when the measurement was taken.
    /// </summary>
    Synchronized,
}

/// <summary>Extension helpers for <see cref="TimestampQuality"/>.</summary>
public static class TimestampQualityExtensions
{
    /// <summary>Renders the quality using the protocol's spelling.</summary>
    public static string ToDisplayString(this TimestampQuality quality) => quality switch
    {
        TimestampQuality.Invalid => "invalid",
        TimestampQuality.Unsynchronized => "unsynchronized",
        TimestampQuality.Synchronized => "synchronized",
        _ => "TimestampQuality(?)",
    };
}

/// <summary>Pairs a time with how much that time can be trusted.</summary>
/// <remarks>
/// The two are kept together deliberately. A DNP3 measurement can carry a
/// perfectly well-formed timestamp from an outstation whose clock has never
/// been set, and a consumer that ignores the quality will happily file that
/// event under 1970 or under whatever the drifted clock says.
/// </remarks>
public readonly record struct Timestamp
{
    /// <summary>The instant reported, in UTC.</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>How far the instant may be trusted.</summary>
    public TimestampQuality Quality { get; init; }

    /// <summary>Returns a synchronized timestamp for <paramref name="time"/>.</summary>
    public static Timestamp Now(DateTimeOffset time) =>
        new() { Time = time, Quality = TimestampQuality.Synchronized };

    /// <summary>
    /// Returns a timestamp for <paramref name="time"/> whose source clock was
    /// not synced.
    /// </summary>
    public static Timestamp Unsynchronized(DateTimeOffset time) =>
        new() { Time = time, Quality = TimestampQuality.Unsynchronized };

    /// <summary>
    /// Returns the timestamp used by measurements that carry no time.
    /// </summary>
    public static Timestamp NoTime() => default;

    /// <summary>Reports whether the timestamp carries a usable time.</summary>
    public bool IsValid => Quality != TimestampQuality.Invalid;

    /// <inheritdoc/>
    public override string ToString() => !IsValid
        ? "—"
        : string.Format(
            CultureInfo.InvariantCulture,
            "{0} ({1})",
            Time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            Quality.ToDisplayString());
}

/// <summary>Conversions between <see cref="DateTimeOffset"/> and DNP3 wire time.</summary>
public static class Dnp3Time
{
    /// <summary>
    /// The largest instant a 48-bit DNP3 timestamp can express: 2^48 - 1
    /// milliseconds after the UNIX epoch, which falls in the year 10889.
    /// </summary>
    public const long MaxDnp3Time = ((long)1 << 48) - 1;

    /// <summary>
    /// Converts <paramref name="time"/> to DNP3's wire representation:
    /// milliseconds since the UNIX epoch, clamped to the 48 bits the encoding
    /// provides. Times before the epoch clamp to zero.
    /// </summary>
    public static ulong ToDnp3(DateTimeOffset time)
    {
        var ms = time.ToUnixTimeMilliseconds();
        return ms switch
        {
            < 0 => 0,
            > MaxDnp3Time => (ulong)MaxDnp3Time,
            _ => (ulong)ms,
        };
    }

    /// <summary>
    /// Converts a 48-bit DNP3 timestamp to a <see cref="DateTimeOffset"/> in
    /// UTC. Bits above the low 48 are ignored, matching what a conforming
    /// encoder can emit.
    /// </summary>
    public static DateTimeOffset FromDnp3(ulong milliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)(milliseconds & (ulong)MaxDnp3Time)).ToUniversalTime();
}

/// <summary>
/// The state of a double-bit binary input, which reports a two-contact device
/// — typically a breaker with separate open and closed auxiliary contacts —
/// and can therefore distinguish a device in transit or miswired from one that
/// is definitely open or definitely closed.
/// </summary>
/// <remarks>The numeric values are the on-the-wire encoding.</remarks>
public enum DoubleBit : byte
{
    /// <summary>Both contacts read open. The device is moving.</summary>
    Intermediate = 0,

    /// <summary>The device is open.</summary>
    DeterminedOff = 1,

    /// <summary>The device is closed.</summary>
    DeterminedOn = 2,

    /// <summary>Both contacts read closed, which is impossible.</summary>
    Indeterminate = 3,
}

/// <summary>Extension helpers for <see cref="DoubleBit"/>.</summary>
public static class DoubleBitExtensions
{
    /// <summary>Renders the state using the protocol's spelling.</summary>
    public static string ToDisplayString(this DoubleBit value) => value switch
    {
        DoubleBit.Intermediate => "Intermediate",
        DoubleBit.DeterminedOff => "Off",
        DoubleBit.DeterminedOn => "On",
        DoubleBit.Indeterminate => "Indeterminate",
        _ => "DoubleBit(?)",
    };
}

// ---- Measurement types ----
//
// Every one carries a value, a quality octet and a timestamp; static
// variations simply leave the timestamp invalid.

/// <summary>A single-bit status input.</summary>
/// <param name="Value">The reported state.</param>
/// <param name="Flags">The quality octet.</param>
/// <param name="Time">When the measurement was taken.</param>
public readonly record struct Binary(bool Value, Flags Flags, Timestamp Time);

/// <summary>A two-bit status input.</summary>
/// <param name="Value">The reported state.</param>
/// <param name="Flags">The quality octet.</param>
/// <param name="Time">When the measurement was taken.</param>
public readonly record struct DoubleBitBinary(DoubleBit Value, Flags Flags, Timestamp Time);

/// <summary>A running count. It wraps rather than saturating.</summary>
/// <param name="Value">The count.</param>
/// <param name="Flags">The quality octet.</param>
/// <param name="Time">When the measurement was taken.</param>
public readonly record struct Counter(uint Value, Flags Flags, Timestamp Time);

/// <summary>A counter value captured at a freeze command.</summary>
/// <param name="Value">The frozen count.</param>
/// <param name="Flags">The quality octet.</param>
/// <param name="Time">When the measurement was taken.</param>
public readonly record struct FrozenCounter(uint Value, Flags Flags, Timestamp Time);

/// <summary>An analog input.</summary>
/// <remarks>
/// The stack carries every analog variation — 16-bit, 32-bit, single and
/// double precision — as a <see cref="double"/> and narrows only at the
/// encoding boundary.
/// </remarks>
/// <param name="Value">The measured value.</param>
/// <param name="Flags">The quality octet.</param>
/// <param name="Time">When the measurement was taken.</param>
public readonly record struct Analog(double Value, Flags Flags, Timestamp Time);

/// <summary>Reports the present state of a control point.</summary>
/// <param name="Value">The reported state.</param>
/// <param name="Flags">The quality octet.</param>
/// <param name="Time">When the measurement was taken.</param>
public readonly record struct BinaryOutputStatus(bool Value, Flags Flags, Timestamp Time);

/// <summary>Reports the present value of an analog control point.</summary>
/// <param name="Value">The reported value.</param>
/// <param name="Flags">The quality octet.</param>
/// <param name="Time">When the measurement was taken.</param>
public readonly record struct AnalogOutputStatus(double Value, Flags Flags, Timestamp Time);

/// <summary>
/// A variable-length opaque value, group 110 and 111. The standard caps it at
/// 255 octets.
/// </summary>
public static class OctetString
{
    /// <summary>The largest octet string the encoding allows.</summary>
    public const int MaxOctetStringLen = 255;
}

/// <summary>Pairs a measurement with the point index it was reported at.</summary>
/// <typeparam name="T">The measurement type.</typeparam>
/// <param name="Index">The point index.</param>
/// <param name="Value">The measurement.</param>
public readonly record struct Indexed<T>(ushort Index, T Value);

/// <summary>
/// A bit mask over the DNP3 event classes plus class 0, the static data set.
/// </summary>
/// <remarks>
/// Masks are how polls are expressed: an integrity poll is
/// <c>Class0 | Class1 | Class2 | Class3</c>.
/// </remarks>
[Flags]
public enum Class : byte
{
    /// <summary>Assigns a point to no event class, suppressing its events.</summary>
    None = 0,

    /// <summary>Static data.</summary>
    Class0 = 1 << 0,

    /// <summary>Event class 1, conventionally the most urgent.</summary>
    Class1 = 1 << 1,

    /// <summary>Event class 2.</summary>
    Class2 = 1 << 2,

    /// <summary>Event class 3.</summary>
    Class3 = 1 << 3,

    /// <summary>Every event class, excluding static data.</summary>
    Class123 = Class1 | Class2 | Class3,

    /// <summary>An integrity poll: static data plus every event class.</summary>
    All = Class0 | Class123,
}

/// <summary>Extension helpers for <see cref="Class"/>.</summary>
public static class ClassExtensions
{
    /// <summary>Reports whether every class in <paramref name="mask"/> is present.</summary>
    public static bool Has(this Class value, Class mask) => (value & mask) == mask;

    /// <summary>Renders the mask as the protocol tools spell it, e.g. <c>0+1+2</c>.</summary>
    public static string ToDisplayString(this Class value)
    {
        if (value == 0)
        {
            return "none";
        }

        var parts = new List<string>(4);
        Class[] bits = [Class.Class0, Class.Class1, Class.Class2, Class.Class3];
        for (var i = 0; i < bits.Length; i++)
        {
            if ((value & bits[i]) != 0)
            {
                parts.Add(i.ToString(CultureInfo.InvariantCulture));
            }
        }

        return string.Join('+', parts);
    }
}

/// <summary>
/// The operation field of a Control Relay Output Block. It packs an operation
/// type in the low four bits with three modifier flags above.
/// </summary>
public readonly struct ControlCode : IEquatable<ControlCode>
{
    /// <summary>The raw operation octet.</summary>
    public byte Value { get; }

    /// <summary>Wraps a raw operation octet.</summary>
    public ControlCode(byte value) => Value = value;

    // ---- Operation types, occupying the low nibble ----

    /// <summary>No operation.</summary>
    public static ControlCode Nul => new(0x00);

    /// <summary>Pulse the point on for the on-time.</summary>
    public static ControlCode PulseOn => new(0x01);

    /// <summary>Pulse the point off for the off-time.</summary>
    public static ControlCode PulseOff => new(0x02);

    /// <summary>Latch the point on.</summary>
    public static ControlCode LatchOn => new(0x03);

    /// <summary>Latch the point off.</summary>
    public static ControlCode LatchOff => new(0x04);

    private const byte OpTypeMask = 0x0F;
    private const byte QueueBit = 0x10; // obsolete; kept for round-tripping
    private const byte ClearBit = 0x20;
    private const byte TripCloseMask = 0xC0;

    /// <summary>Sets the close coil field.</summary>
    public static ControlCode Close => new(0x80);

    /// <summary>Sets the trip coil field.</summary>
    public static ControlCode Trip => new(0x40);

    /// <summary>Returns the operation type nibble.</summary>
    public ControlCode OpType() => new((byte)(Value & OpTypeMask));

    /// <summary>Reports whether the trip coil is selected.</summary>
    public bool IsTrip() => (Value & TripCloseMask) == Trip.Value;

    /// <summary>Reports whether the close coil is selected.</summary>
    public bool IsClose() => (Value & TripCloseMask) == Close.Value;

    /// <summary>
    /// Reports whether the clear bit is set, which cancels a queued or running
    /// operation on the point.
    /// </summary>
    public bool IsClear() => (Value & ClearBit) != 0;

    /// <summary>Bitwise OR, for combining an operation type with modifiers.</summary>
    public static ControlCode operator |(ControlCode a, ControlCode b) =>
        new((byte)(a.Value | b.Value));

    /// <summary>Bitwise AND.</summary>
    public static ControlCode operator &(ControlCode a, ControlCode b) =>
        new((byte)(a.Value & b.Value));

    /// <summary>Wraps a raw octet.</summary>
    public static implicit operator ControlCode(byte value) => new(value);

    /// <summary>Unwraps to the raw octet.</summary>
    public static explicit operator byte(ControlCode code) => code.Value;

    /// <summary>Equality by raw octet.</summary>
    public bool Equals(ControlCode other) => Value == other.Value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ControlCode other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value;

    /// <summary>Equality by raw octet.</summary>
    public static bool operator ==(ControlCode a, ControlCode b) => a.Value == b.Value;

    /// <summary>Inequality by raw octet.</summary>
    public static bool operator !=(ControlCode a, ControlCode b) => a.Value != b.Value;

    /// <inheritdoc/>
    public override string ToString()
    {
        var op = (Value & OpTypeMask) switch
        {
            0x00 => "NUL",
            0x01 => "PULSE_ON",
            0x02 => "PULSE_OFF",
            0x03 => "LATCH_ON",
            0x04 => "LATCH_OFF",
            var other => string.Format(CultureInfo.InvariantCulture, "OP({0})", other),
        };

        if (IsTrip())
        {
            op += "|TRIP";
        }
        else if (IsClose())
        {
            op += "|CLOSE";
        }

        if (IsClear())
        {
            op += "|CLEAR";
        }

        if ((Value & QueueBit) != 0)
        {
            op += "|QUEUE";
        }

        return op;
    }
}

/// <summary>The outcome an outstation reports for a control operation.</summary>
public enum CommandStatus : byte
{
    /// <summary>The command completed.</summary>
    Success = 0,
    /// <summary>The operation timed out before completing.</summary>
    Timeout = 1,
    /// <summary>An operate arrived with no matching select.</summary>
    NoSelect = 2,
    /// <summary>The request was malformed.</summary>
    FormatError = 3,
    /// <summary>The control operation is not supported for this point.</summary>
    NotSupported = 4,
    /// <summary>The point is already active.</summary>
    AlreadyActive = 5,
    /// <summary>The hardware reported a problem.</summary>
    HardwareError = 6,
    /// <summary>The point is under local control.</summary>
    Local = 7,
    /// <summary>Too many operations are already in progress.</summary>
    TooManyOps = 8,
    /// <summary>The requesting master is not authorized.</summary>
    NotAuthorized = 9,
    /// <summary>An automation process is inhibiting the point.</summary>
    AutomationInhibit = 10,
    /// <summary>Processing capacity is limited.</summary>
    ProcessingLimited = 11,
    /// <summary>The requested value is out of range.</summary>
    OutOfRange = 12,
    /// <summary>A downstream device is under local control.</summary>
    DownstreamLocal = 13,
    /// <summary>The operation was already complete.</summary>
    AlreadyComplete = 14,
    /// <summary>The point is blocked.</summary>
    Blocked = 15,
    /// <summary>The operation was canceled.</summary>
    Canceled = 16,
    /// <summary>Another master holds the point.</summary>
    BlockedOtherMaster = 17,
    /// <summary>The downstream operation failed.</summary>
    DownstreamFail = 18,
    /// <summary>The point is not participating.</summary>
    NonParticipating = 126,
    /// <summary>The status is undefined.</summary>
    Undefined = 127,
}

/// <summary>Extension helpers for <see cref="CommandStatus"/>.</summary>
public static class CommandStatusExtensions
{
    private static readonly Dictionary<CommandStatus, string> Names = new()
    {
        [CommandStatus.Success] = "SUCCESS",
        [CommandStatus.Timeout] = "TIMEOUT",
        [CommandStatus.NoSelect] = "NO_SELECT",
        [CommandStatus.FormatError] = "FORMAT_ERROR",
        [CommandStatus.NotSupported] = "NOT_SUPPORTED",
        [CommandStatus.AlreadyActive] = "ALREADY_ACTIVE",
        [CommandStatus.HardwareError] = "HARDWARE_ERROR",
        [CommandStatus.Local] = "LOCAL",
        [CommandStatus.TooManyOps] = "TOO_MANY_OPS",
        [CommandStatus.NotAuthorized] = "NOT_AUTHORIZED",
        [CommandStatus.AutomationInhibit] = "AUTOMATION_INHIBIT",
        [CommandStatus.ProcessingLimited] = "PROCESSING_LIMITED",
        [CommandStatus.OutOfRange] = "OUT_OF_RANGE",
        [CommandStatus.DownstreamLocal] = "DOWNSTREAM_LOCAL",
        [CommandStatus.AlreadyComplete] = "ALREADY_COMPLETE",
        [CommandStatus.Blocked] = "BLOCKED",
        [CommandStatus.Canceled] = "CANCELED",
        [CommandStatus.BlockedOtherMaster] = "BLOCKED_OTHER_MASTER",
        [CommandStatus.DownstreamFail] = "DOWNSTREAM_FAIL",
        [CommandStatus.NonParticipating] = "NON_PARTICIPATING",
        [CommandStatus.Undefined] = "UNDEFINED",
    };

    /// <summary>Renders the status using the protocol's spelling.</summary>
    public static string ToDisplayString(this CommandStatus status) =>
        Names.TryGetValue(status, out var name)
            ? name
            : string.Format(CultureInfo.InvariantCulture, "CommandStatus({0})", (byte)status);

    /// <summary>Reports whether the command succeeded.</summary>
    public static bool OK(this CommandStatus status) => status == CommandStatus.Success;
}

/// <summary>
/// A group 12 variation 1 control: the command used to operate breakers,
/// reclosers and other discrete outputs.
/// </summary>
public readonly record struct ControlRelayOutputBlock
{
    /// <summary>The operation to perform.</summary>
    public ControlCode Code { get; init; }

    /// <summary>
    /// How many times to execute the operation. Zero is legal and means "do
    /// nothing", which some masters use to probe a point.
    /// </summary>
    public byte Count { get; init; }

    /// <summary>Milliseconds the pulse operations hold the point on.</summary>
    public uint OnTime { get; init; }

    /// <summary>Milliseconds the pulse operations hold the point off.</summary>
    public uint OffTime { get; init; }

    /// <summary>Meaningful only on a response echo.</summary>
    public CommandStatus Status { get; init; }

    /// <inheritdoc/>
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "CROB{{{0} count={1} on={2}ms off={3}ms status={4}}}",
        Code, Count, OnTime, OffTime, Status.ToDisplayString());
}

/// <summary>A group 41 variation 2 control.</summary>
/// <param name="Value">The commanded value.</param>
/// <param name="Status">Meaningful only on a response echo.</param>
public readonly record struct AnalogOutputInt16(short Value, CommandStatus Status = CommandStatus.Success);

/// <summary>A group 41 variation 1 control.</summary>
/// <param name="Value">The commanded value.</param>
/// <param name="Status">Meaningful only on a response echo.</param>
public readonly record struct AnalogOutputInt32(int Value, CommandStatus Status = CommandStatus.Success);

/// <summary>A group 41 variation 3 control.</summary>
/// <param name="Value">The commanded value.</param>
/// <param name="Status">Meaningful only on a response echo.</param>
public readonly record struct AnalogOutputFloat32(float Value, CommandStatus Status = CommandStatus.Success);

/// <summary>A group 41 variation 4 control.</summary>
/// <param name="Value">The commanded value.</param>
/// <param name="Status">Meaningful only on a response echo.</param>
public readonly record struct AnalogOutputFloat64(double Value, CommandStatus Status = CommandStatus.Success);

/// <summary>Selects between the two restart function codes.</summary>
public enum RestartMode : byte
{
    /// <summary>
    /// Reinitialises the outstation completely, as though power had been
    /// cycled.
    /// </summary>
    Cold = 0,

    /// <summary>Reinitialises only the communications process.</summary>
    Warm,
}

/// <summary>Extension helpers for <see cref="RestartMode"/>.</summary>
public static class RestartModeExtensions
{
    /// <summary>Renders the mode using the protocol's spelling.</summary>
    public static string ToDisplayString(this RestartMode mode) =>
        mode == RestartMode.Cold ? "cold" : "warm";
}

/// <summary>Range checks the outstation needs when narrowing analog values.</summary>
public static class AnalogRange
{
    /// <summary>
    /// Reports whether <paramref name="value"/> can be encoded as a 16-bit
    /// analog without loss, which the outstation needs when a master requests a
    /// narrow variation.
    /// </summary>
    public static bool FitsIn16(double value) =>
        value >= short.MinValue && value <= short.MaxValue && value == Math.Truncate(value);

    /// <summary>
    /// Reports whether <paramref name="value"/> can be encoded as a 32-bit
    /// analog without loss.
    /// </summary>
    public static bool FitsIn32(double value) =>
        value >= int.MinValue && value <= int.MaxValue && value == Math.Truncate(value);
}
