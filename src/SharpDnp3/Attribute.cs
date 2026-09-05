// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Device attributes — group 0 — are what a device says about itself: who made
// it, what it is, what firmware it runs, and how much of DNP3 it implements.
//
// They are unlike every other object in the protocol in one way that shapes
// this whole file: the variation is not an encoding, it is the identity of the
// attribute. Group 0 variation 242 is not "software version encoded one way"
// against some other variation encoding it differently — 242 *is* the software
// version. So there is no codec table to generate, and an attribute a device
// invents for itself parses exactly as well as one the standard named.
//
// The value carries its own type and length, which is what makes that possible,
// and what lets a master display a device's private attributes without knowing
// anything about them.

using System.Globalization;
using System.Text;

namespace SharpDnp3;

/// <summary>
/// The encoding of an attribute's value, from the standard's attribute data
/// type codes.
/// </summary>
public enum AttributeType : byte
{
    /// <summary>Printable text.</summary>
    VisibleString = 1,

    /// <summary>An unsigned integer of one, two, four or eight octets.</summary>
    UnsignedInt = 2,

    /// <summary>A signed integer of one, two, four or eight octets.</summary>
    SignedInt = 3,

    /// <summary>A single- or double-precision float.</summary>
    Float = 4,

    /// <summary>Opaque octets.</summary>
    OctetString = 5,

    /// <summary>A bit string.</summary>
    BitString = 6,

    /// <summary>A 48-bit DNP3 timestamp.</summary>
    Time = 7,
}

/// <summary>Naming helpers for <see cref="AttributeType"/>.</summary>
public static class AttributeTypeExtensions
{
    /// <summary>Renders the type using the protocol tools' spelling.</summary>
    public static string ToDisplayString(this AttributeType t) => t switch
    {
        AttributeType.VisibleString => "string",
        AttributeType.UnsignedInt => "uint",
        AttributeType.SignedInt => "int",
        AttributeType.Float => "float",
        AttributeType.OctetString => "octets",
        AttributeType.BitString => "bits",
        AttributeType.Time => "time",
        _ => string.Format(CultureInfo.InvariantCulture, "AttributeType({0})", (byte)t),
    };
}

/// <summary>
/// Attribute set and variation numbers with a meaning fixed by the standard.
/// </summary>
public static class AttributeNumbers
{
    /// <summary>
    /// Attribute set 0, the one the standard defines. A device may keep private
    /// attributes in other sets.
    /// </summary>
    public const byte StandardSet = 0;

    /// <summary>
    /// The variation a master reads to ask for every attribute a device has,
    /// rather than naming them one at a time. It appears only in requests.
    /// </summary>
    public const byte All = 254;

    /// <summary>Asks which attributes the device implements.</summary>
    public const byte List = 255;

    /// <summary>
    /// The standard set's attribute names.
    /// </summary>
    /// <remarks>
    /// These names are for display and nothing else. The wire carries numbers,
    /// this library never routes on a name, and an entry that is wrong
    /// mislabels a row in a listing without affecting a single octet — which is
    /// the only reason it is safe to ship a table transcribed from the
    /// standard's set 0 rather than one verified against a device.
    /// <para>
    /// A device's own attributes, and any set other than 0, come back numbered.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<byte, string> Names = new()
    {
        [196] = "secure authentication statistics per association",
        [197] = "number of security statistics per association",
        [198] = "user-specific attributes supported",
        [199] = "master-defined data set prototypes",
        [200] = "outstation-defined data set prototypes",
        [201] = "master-defined data sets",
        [202] = "outstation-defined data sets",
        [203] = "max binary outputs per request",
        [204] = "local timing accuracy",
        [205] = "duration of time accuracy",
        [206] = "analog output events supported",
        [207] = "max analog output index",
        [208] = "number of analog outputs",
        [209] = "binary output events supported",
        [210] = "max binary output index",
        [211] = "number of binary outputs",
        [212] = "frozen counter events supported",
        [213] = "frozen counters supported",
        [214] = "counter events supported",
        [215] = "max counter index",
        [216] = "number of counters",
        [217] = "frozen analog inputs supported",
        [218] = "analog input events supported",
        [219] = "max analog input index",
        [220] = "number of analog inputs",
        [221] = "double-bit binary input events supported",
        [222] = "max double-bit binary input index",
        [223] = "number of double-bit binary inputs",
        [224] = "binary input events supported",
        [225] = "max binary input index",
        [226] = "number of binary inputs",
        [227] = "max transmit fragment size",
        [228] = "max receive fragment size",
        [242] = "software version",
        [243] = "hardware version",
        [244] = "owner name",
        [245] = "location",
        [246] = "ID code",
        [247] = "device name",
        [248] = "serial number",
        [249] = "subset level and conformance",
        [250] = "product name and model",
        [252] = "manufacturer name",
        [All] = "all attributes",
        [List] = "list of attributes",
    };

    /// <summary>
    /// Returns the standard set's name for a variation, and whether there is
    /// one.
    /// </summary>
    /// <remarks>
    /// Exposed so a tool can label an attribute it has only the number of.
    /// </remarks>
    public static bool TryName(byte variation, out string name) =>
        Names.TryGetValue(variation, out name!);
}

/// <summary>One thing a device says about itself.</summary>
/// <remarks>
/// Exactly one of the value members carries the value, chosen by
/// <see cref="Type"/>. They are separate members rather than an interface
/// because the overwhelmingly common thing to do with an attribute is print it,
/// and the second most common is to read one number out of it.
/// </remarks>
public readonly record struct DeviceAttribute
{
    /// <summary>The attribute set.</summary>
    /// <remarks>
    /// With <see cref="Variation"/> it is the attribute's name on the wire.
    /// </remarks>
    public byte Set { get; init; }

    /// <summary>Identifies the attribute within its set.</summary>
    public byte Variation { get; init; }

    /// <summary>How the value is encoded.</summary>
    public AttributeType Type { get; init; }

    /// <summary>The value when <see cref="Type"/> is a visible string.</summary>
    public string? Text { get; init; }

    /// <summary>The value when <see cref="Type"/> is a signed or unsigned integer.</summary>
    public long Number { get; init; }

    /// <summary>The value when <see cref="Type"/> is a float.</summary>
    public double Real { get; init; }

    /// <summary>The raw octets, for a type this library does not interpret.</summary>
    public ReadOnlyMemory<byte> Octets { get; init; }

    /// <summary>The value when <see cref="Type"/> is a time.</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>Builds a visible-string attribute.</summary>
    /// <remarks>
    /// Which is what most of the ones a person reads are.
    /// </remarks>
    public static DeviceAttribute String(byte variation, string text) => new()
    {
        Variation = variation,
        Type = AttributeType.VisibleString,
        Text = text,
    };

    /// <summary>Builds an unsigned attribute.</summary>
    /// <remarks>
    /// Which is what the counts and the capability flags are.
    /// </remarks>
    public static DeviceAttribute Uint(byte variation, ulong value) => new()
    {
        Variation = variation,
        Type = AttributeType.UnsignedInt,
        Number = (long)value,
    };

    /// <summary>Builds a signed attribute.</summary>
    public static DeviceAttribute Int(byte variation, long value) => new()
    {
        Variation = variation,
        Type = AttributeType.SignedInt,
        Number = value,
    };

    /// <summary>Renders the value as text, whatever its type.</summary>
    public string ValueText() => Type switch
    {
        AttributeType.VisibleString => Text ?? string.Empty,
        AttributeType.UnsignedInt or AttributeType.SignedInt =>
            Number.ToString(CultureInfo.InvariantCulture),
        AttributeType.Float => Real.ToString("G", CultureInfo.InvariantCulture),
        AttributeType.Time => Time == default
            ? "—"
            : Time.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
        _ => OctetText(Octets.Span),
    };

    /// <summary>
    /// Returns what the attribute is called, or a placeholder naming its number
    /// for one this library does not know.
    /// </summary>
    public string Name()
    {
        if (Set == AttributeNumbers.StandardSet)
        {
            return AttributeNumbers.TryName(Variation, out var known)
                ? known
                : string.Format(CultureInfo.InvariantCulture, "attribute {0}", Variation);
        }

        return string.Format(
            CultureInfo.InvariantCulture, "set {0} attribute {1}", Set, Variation);
    }

    /// <inheritdoc/>
    public override string ToString() => Name() + ": " + ValueText();

    /// <summary>
    /// Renders octets as text when they are printable and as hex when they are
    /// not.
    /// </summary>
    /// <remarks>
    /// Which is what a device that packs a version number into an octet string
    /// needs.
    /// </remarks>
    private static string OctetText(ReadOnlySpan<byte> b)
    {
        if (b.IsEmpty)
        {
            return string.Empty;
        }

        var printable = true;
        foreach (var c in b)
        {
            if (c is < 0x20 or > 0x7E)
            {
                printable = false;
                break;
            }
        }

        if (printable)
        {
            return Encoding.ASCII.GetString(b);
        }

        var sb = new StringBuilder(b.Length * 3);
        for (var i = 0; i < b.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(b[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
