// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Globalization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SharpDnp3.Generator;

/// <summary>One wire field of an object.</summary>
public sealed class Field
{
    public string Name { get; set; } = "";

    public string Type { get; set; } = "";
}

/// <summary>One group/variation entry from the spec.</summary>
public sealed class ObjectSpec
{
    public byte Group { get; set; }

    public byte Variation { get; set; }

    public string Name { get; set; } = "";

    public int Level { get; set; }

    public string Kind { get; set; } = "";

    public string Measurement { get; set; } = "";

    public int Packed { get; set; }

    public bool Variable { get; set; }

    public bool LengthIsVariation { get; set; }

    public List<Field> Fields { get; set; } = [];

    /// <summary>Returns the object's encoded width, and whether it is fixed.</summary>
    public (int Bits, bool Fixed) SizeBits()
    {
        if (Variable || LengthIsVariation)
        {
            return (0, false);
        }

        if (Packed > 0)
        {
            return (Packed, true);
        }

        var total = 0;
        foreach (var f in Fields)
        {
            total += SpecTables.FieldBits[f.Type];
        }

        return (total, true);
    }

    /// <summary>The identifier stem used for generated methods.</summary>
    public string CsName() =>
        string.Format(CultureInfo.InvariantCulture, "G{0}V{1}", Group, Variation);

    // Field roles, derived from the field types rather than declared separately.

    public Field? FlagsField() => FindType("flags");

    public Field? AbsTimeField() => FindType("time48");

    public Field? RelTimeField() => FindType("time16");

    private Field? FindType(string t) => Fields.Find(f => f.Type == t);

    /// <summary>Returns the numeric field carrying the measurement's value.</summary>
    public (Field Field, int Offset)? ValueField()
    {
        var off = 0;
        foreach (var f in Fields)
        {
            if (f.Type is "i16" or "u16" or "i32" or "u32" or "f32" or "f64" && f.Name == "Value")
            {
                return (f, off / 8);
            }

            off += SpecTables.FieldBits[f.Type];
        }

        return null;
    }

    /// <summary>Returns the octet offset of a named field.</summary>
    public int OffsetOf(string name)
    {
        var off = 0;
        foreach (var f in Fields)
        {
            if (f.Name == name)
            {
                return off / 8;
            }

            off += SpecTables.FieldBits[f.Type];
        }

        return -1;
    }

    /// <summary>
    /// Reports whether the object decodes into one of the measurement types,
    /// which is the set the generator emits codecs for.
    /// </summary>
    public bool IsMeasurement() =>
        Measurement is not ("" or "none") && Packed == 0 && !Variable && !LengthIsVariation;
}

/// <summary>The whole spec file.</summary>
public sealed class Spec
{
    public List<ObjectSpec> Objects { get; set; } = [];
}

/// <summary>Lookup tables shared by the loader and the emitters.</summary>
public static class SpecTables
{
    /// <summary>Maps a field type to its width on the wire.</summary>
    public static readonly Dictionary<string, int> FieldBits = new()
    {
        ["flags"] = 8,
        ["u8"] = 8,
        ["status"] = 8,
        ["i16"] = 16,
        ["u16"] = 16,
        ["i32"] = 32,
        ["u32"] = 32,
        ["f32"] = 32,
        ["f64"] = 64,
        ["time48"] = 48,
        ["time16"] = 16,
    };

    /// <summary>
    /// The measurement types a variation may decode into. "none" means the
    /// object is not a measurement — a command, a time, a class.
    /// </summary>
    public static readonly Dictionary<string, string> ValidMeasurements = new()
    {
        ["binary"] = "Binary",
        ["doublebit"] = "DoubleBitBinary",
        ["counter"] = "Counter",
        ["frozencounter"] = "FrozenCounter",
        ["analog"] = "Analog",
        ["binaryoutput"] = "BinaryOutputStatus",
        ["analogoutput"] = "AnalogOutputStatus",
        ["none"] = "",
    };

    /// <summary>Maps a spec kind onto the generated enum member.</summary>
    public static readonly Dictionary<string, string> ValidKinds = new()
    {
        ["static"] = "Kind.Static",
        ["event"] = "Kind.Event",
        ["command"] = "Kind.Command",
        ["command_event"] = "Kind.CommandEvent",
        ["time"] = "Kind.Time",
        ["class"] = "Kind.Class",
        ["indication"] = "Kind.Indication",
        ["deadband"] = "Kind.Deadband",
        ["string"] = "Kind.String",
        ["file"] = "Kind.File",
        ["attribute"] = "Kind.Attribute",
    };

    /// <summary>
    /// The measurement names in the order the codec maps are emitted.
    /// </summary>
    public static readonly string[] MeasurementOrder =
    [
        "binary", "doublebit", "counter", "frozencounter",
        "analog", "binaryoutput", "analogoutput",
    ];

    /// <summary>Maps a measurement to the codec dictionary name.</summary>
    public static string CodecMapName(string measure) => measure switch
    {
        "binary" => "BinaryCodecs",
        "doublebit" => "DoubleBitCodecs",
        "counter" => "CounterCodecs",
        "frozencounter" => "FrozenCounterCodecs",
        "analog" => "AnalogCodecs",
        "binaryoutput" => "BinaryOutputCodecs",
        "analogoutput" => "AnalogOutputCodecs",
        _ => throw new InvalidOperationException($"no codec map for measurement {measure}"),
    };
}

/// <summary>Reads and validates the spec.</summary>
public static class SpecLoader
{
    public static Spec Load(string path)
    {
        var text = File.ReadAllText(path);

        // Underscored rather than lower-case: every scalar key in the spec is a
        // single lower-case word, and this is what maps LengthIsVariation onto
        // `length_is_variation`.
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            // A typo in a field name must fail, not be ignored.
            .WithEnforceRequiredMembers()
            .Build();

        var spec = deserializer.Deserialize<Spec>(text)
            ?? throw new InvalidOperationException($"{path}: empty spec");

        var seen = new Dictionary<ushort, string>();
        for (var i = 0; i < spec.Objects.Count; i++)
        {
            var o = spec.Objects[i];
            var where = string.Format(
                CultureInfo.InvariantCulture,
                "{0}: entry {1} (g{2}v{3})", path, i, o.Group, o.Variation);

            if (string.IsNullOrEmpty(o.Name))
            {
                throw new InvalidOperationException($"{where}: missing name");
            }

            if (!SpecTables.ValidKinds.ContainsKey(o.Kind))
            {
                throw new InvalidOperationException($"{where}: unknown kind \"{o.Kind}\"");
            }

            if (!SpecTables.ValidMeasurements.ContainsKey(o.Measurement))
            {
                throw new InvalidOperationException(
                    $"{where}: unknown measurement \"{o.Measurement}\"");
            }

            foreach (var f in o.Fields)
            {
                if (!SpecTables.FieldBits.ContainsKey(f.Type))
                {
                    throw new InvalidOperationException(
                        $"{where}: field {f.Name} has unknown type \"{f.Type}\"");
                }
            }

            if (o.Packed > 0 && o.Fields.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{where}: a packed object cannot also declare fields");
            }

            // A measurement that is neither packed nor variable must have a
            // value the decoder can find. Binary types are the exception: their
            // state rides in the flags octet.
            if (o.IsMeasurement())
            {
                var hasValue = o.ValueField() is not null;
                var isBinaryLike = o.Measurement is "binary" or "doublebit" or "binaryoutput";

                if (!hasValue && !isBinaryLike)
                {
                    throw new InvalidOperationException(
                        $"{where}: measurement \"{o.Measurement}\" has no Value field");
                }

                if (isBinaryLike && o.FlagsField() is null)
                {
                    throw new InvalidOperationException(
                        $"{where}: binary measurement has no Flags field to carry its state");
                }
            }

            var key = (ushort)((o.Group << 8) | o.Variation);
            if (seen.TryGetValue(key, out var prev))
            {
                throw new InvalidOperationException($"{where}: duplicate of {prev}");
            }

            seen[key] = o.Name;
        }

        spec.Objects.Sort((a, b) =>
            a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Variation.CompareTo(b.Variation));

        return spec;
    }
}
