// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The DNP3 object group and variation codecs.
//
// Most of this namespace is generated from Objects/Spec/dnp3_objects.yaml,
// which is the single source of truth for every group, variation, size and
// field layout. Regenerate with `dotnet run --project build/SharpDnp3.Generator`;
// the output is committed so consumers never run the generator.
//
// Hand-written code lives here for the encodings the table cannot express:
// bit-packed objects, whose objects share octets, and commands, whose fields
// map onto purpose-built structs rather than a measurement type.

using System.Globalization;

namespace SharpDnp3.Objects;

/// <summary>Identifies an object type: its group and its variation.</summary>
/// <param name="Group">The object group.</param>
/// <param name="Variation">The object variation.</param>
public readonly record struct GroupVar(byte Group, byte Variation)
{
    /// <summary>Shorthand for constructing a <see cref="GroupVar"/>.</summary>
    public static GroupVar GV(byte group, byte variation) => new(group, variation);

    /// <summary>Returns the packed form used as a key on the wire side.</summary>
    public ushort Key => (ushort)((Group << 8) | Variation);

    /// <inheritdoc/>
    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "g{0}v{1}", Group, Variation);
}

/// <summary>
/// Classifies what an object is for, which is what lets a master decide whether
/// a header names data, a command, or a class to poll.
/// </summary>
public enum Kind : byte
{
    /// <summary>Not a kind the spec defines.</summary>
    Unknown = 0,
    /// <summary>Current-value data.</summary>
    Static,
    /// <summary>A change report.</summary>
    Event,
    /// <summary>An output command.</summary>
    Command,
    /// <summary>A report of a command that was executed.</summary>
    CommandEvent,
    /// <summary>A time or interval object.</summary>
    Time,
    /// <summary>A class object, which names what to poll.</summary>
    Class,
    /// <summary>Internal indications addressable as bits.</summary>
    Indication,
    /// <summary>An analog deadband setting.</summary>
    Deadband,
    /// <summary>An octet string or virtual terminal object.</summary>
    String,
    /// <summary>A file transfer object.</summary>
    File,
    /// <summary>A device attribute.</summary>
    Attribute,
}

/// <summary>Naming helpers for <see cref="Kind"/>.</summary>
public static class KindExtensions
{
    private static readonly string[] Names =
    [
        "unknown", "static", "event", "command", "command-event",
        "time", "class", "indication", "deadband", "string", "file", "attribute",
    ];

    /// <summary>Renders the kind using the protocol tools' spelling.</summary>
    public static string ToDisplayString(this Kind k) =>
        (int)k < Names.Length ? Names[(int)k] : "Kind(?)";
}

/// <summary>Everything known about one group and variation.</summary>
public readonly record struct Descriptor
{
    /// <summary>The group and variation this describes.</summary>
    public GroupVar GV { get; init; }

    /// <summary>The standard's name for the variation.</summary>
    public string Name { get; init; }

    /// <summary>The IEEE 1815 conformance subset level.</summary>
    public int Level { get; init; }

    /// <summary>What the object is for.</summary>
    public Kind Kind { get; init; }

    /// <summary>The measurement type the object decodes into.</summary>
    public PointType Measurement { get; init; }

    /// <summary>
    /// The encoded size of one object. Values under eight mean the objects are
    /// bit-packed and share octets across a range.
    /// </summary>
    public int SizeBits { get; init; }

    /// <summary>Whether the objects share octets across a range.</summary>
    public bool Packed { get; init; }

    /// <summary>Whether the encoding carries a quality octet.</summary>
    public bool HasFlags { get; init; }

    /// <summary>Whether the encoding carries a timestamp.</summary>
    public bool HasTime { get; init; }

    /// <summary>
    /// Marks the variations whose timestamp is an offset from a preceding group
    /// 51 common-time-of-occurrence object rather than an absolute time.
    /// </summary>
    public bool RelativeTime { get; init; }

    /// <summary>The width of the object's value field.</summary>
    /// <remarks>
    /// This and <see cref="FloatValue"/> are recorded rather than inferred from
    /// the variation number because the mapping is not consistent across
    /// groups: variation 3 is a 32-bit integer in group 30 and a
    /// single-precision float in group 40. An outstation choosing which
    /// variation can carry a reading needs the real answer, not a rule that
    /// happens to hold for one group.
    /// </remarks>
    public int ValueBits { get; init; }

    /// <summary>Whether the value is IEEE 754 rather than an integer.</summary>
    public bool FloatValue { get; init; }

    /// <summary>
    /// Returns the object's size in whole octets. Packed objects have none:
    /// they are measured per range, not per object.
    /// </summary>
    public bool TrySizeOctets(out int octets)
    {
        if (Packed || SizeBits % 8 != 0)
        {
            octets = 0;
            return false;
        }

        octets = SizeBits / 8;
        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "{0} {1} ({2}, L{3})",
        GV, Name, Kind.ToDisplayString(), Level);
}

/// <summary>
/// Carries what a decoder needs that is not in the object itself.
/// </summary>
/// <remarks>
/// Two things fall into that category, and both are properties of the session
/// rather than of the octets: whether the outstation's clock was synchronised
/// when it stamped an event, and the common time of occurrence that a
/// relative-time event is measured from.
/// </remarks>
public readonly record struct Context
{
    /// <summary>
    /// Reports whether the outstation's clock was synchronised. A master sets
    /// this <see langword="false"/> while the outstation is asserting
    /// NEED_TIME.
    /// </summary>
    public bool Synchronized { get; init; }

    /// <summary>
    /// The common time of occurrence from the most recent group 51 object in
    /// this fragment.
    /// </summary>
    public DateTimeOffset Cto { get; init; }

    /// <summary>Whether a common time of occurrence was seen.</summary>
    public bool HasCto { get; init; }

    /// <summary>Returns the quality to stamp on an absolute timestamp.</summary>
    public TimestampQuality TimeQuality() => Synchronized
        ? TimestampQuality.Synchronized
        : TimestampQuality.Unsynchronized;

    /// <summary>
    /// Resolves a relative-time offset against the context's common time of
    /// occurrence.
    /// </summary>
    /// <remarks>
    /// Without a CTO the offset means nothing, so the result is an invalid
    /// timestamp rather than one anchored to the epoch. Silently anchoring
    /// would file the event in 1970 and look like data rather than a missing
    /// base.
    /// </remarks>
    public Timestamp RelativeTime(ushort offsetMillis) => !HasCto
        ? Timestamp.NoTime()
        : new Timestamp
        {
            Time = Cto.AddMilliseconds(offsetMillis),
            Quality = TimeQuality(),
        };

    /// <summary>
    /// Returns the millisecond offset to encode for a relative-time event,
    /// measured from the context's common time of occurrence.
    /// </summary>
    /// <remarks>
    /// An event before the CTO, or more than 65535 ms after it, cannot be
    /// expressed in the sixteen bits the encoding provides. Such an event needs
    /// its own CTO rather than a clamped offset, so callers building a fragment
    /// should emit a fresh group 51 object instead of relying on the clamp
    /// here.
    /// </remarks>
    public ushort RelativeOffset(Timestamp t)
    {
        if (!HasCto || !t.IsValid)
        {
            return 0;
        }

        var delta = (long)(t.Time - Cto).TotalMilliseconds;
        return delta switch
        {
            < 0 => 0,
            > 0xFFFF => 0xFFFF,
            _ => (ushort)delta,
        };
    }

    /// <summary>
    /// Returns a copy of the context with its common time of occurrence set, as
    /// a parser does on encountering a group 51 object.
    /// </summary>
    public Context WithCto(DateTimeOffset t) => this with { Cto = t, HasCto = true };
}

/// <summary>Decodes one object of a measurement type.</summary>
/// <remarks>
/// <paramref name="buf"/> is assumed to hold at least the object's size;
/// callers get that guarantee from the framing layer, which has already
/// validated the header's length arithmetic against the fragment.
/// </remarks>
/// <typeparam name="T">The measurement type.</typeparam>
/// <param name="buf">The object's octets.</param>
/// <param name="ctx">Session state the octets do not carry.</param>
public delegate T ParseObject<out T>(ReadOnlySpan<byte> buf, Context ctx);

/// <summary>Encodes one object of a measurement type.</summary>
/// <typeparam name="T">The measurement type.</typeparam>
/// <param name="dst">The buffer to append to.</param>
/// <param name="value">The measurement to encode.</param>
/// <param name="ctx">Session state the octets do not carry.</param>
public delegate void WriteObject<in T>(List<byte> dst, T value, Context ctx);

/// <summary>Parses and writes one group and variation of a measurement type.</summary>
/// <typeparam name="T">The measurement type.</typeparam>
/// <param name="Parse">The decoder.</param>
/// <param name="Write">The encoder.</param>
public readonly record struct Codec<T>(ParseObject<T> Parse, WriteObject<T> Write);

/// <summary>The generated object registry and its codec lookups.</summary>
public static partial class ObjectRegistry
{
    /// <summary>Returns the descriptor for a group and variation.</summary>
    public static bool TryLookup(GroupVar gv, out Descriptor descriptor) =>
        Descriptors.TryGetValue(gv, out descriptor);

    /// <summary>
    /// Returns every descriptor the spec defines, keyed by group and variation.
    /// The returned dictionary must not be modified.
    /// </summary>
    public static IReadOnlyDictionary<GroupVar, Descriptor> All => Descriptors;

    // Codec lookups, one per measurement type. Generics do not let a single
    // dictionary hold codecs of differing types, and the alternative — one
    // record with seven mostly-null delegate fields — trades a compile-time
    // guarantee for a runtime one. These stay separate.

    /// <summary>Returns the codec for a binary input variation.</summary>
    public static bool TryBinaryCodec(GroupVar gv, out Codec<Binary> codec) =>
        BinaryCodecs.TryGetValue(gv, out codec);

    /// <summary>Returns the codec for a double-bit binary input variation.</summary>
    public static bool TryDoubleBitCodec(GroupVar gv, out Codec<DoubleBitBinary> codec) =>
        DoubleBitCodecs.TryGetValue(gv, out codec);

    /// <summary>Returns the codec for a counter variation.</summary>
    public static bool TryCounterCodec(GroupVar gv, out Codec<Counter> codec) =>
        CounterCodecs.TryGetValue(gv, out codec);

    /// <summary>Returns the codec for a frozen counter variation.</summary>
    public static bool TryFrozenCounterCodec(GroupVar gv, out Codec<FrozenCounter> codec) =>
        FrozenCounterCodecs.TryGetValue(gv, out codec);

    /// <summary>Returns the codec for an analog input variation.</summary>
    public static bool TryAnalogCodec(GroupVar gv, out Codec<Analog> codec) =>
        AnalogCodecs.TryGetValue(gv, out codec);

    /// <summary>Returns the codec for a binary output status variation.</summary>
    public static bool TryBinaryOutputCodec(GroupVar gv, out Codec<BinaryOutputStatus> codec) =>
        BinaryOutputCodecs.TryGetValue(gv, out codec);

    /// <summary>Returns the codec for an analog output status variation.</summary>
    public static bool TryAnalogOutputCodec(GroupVar gv, out Codec<AnalogOutputStatus> codec) =>
        AnalogOutputCodecs.TryGetValue(gv, out codec);
}
