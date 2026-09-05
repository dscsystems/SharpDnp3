// Copyright (C) 2026 Ricardo Olsen / DSC Systems.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option)
// any later version. It is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details, in the LICENSE file at
// the root of this repository or at <https://www.gnu.org/licenses/>.
//
// What the simulated outstation says about itself over group 0, and what it
// lets a master read and write over group 70.

using System.Globalization;
using System.Text;

namespace SharpDnp3.Tools.Outstation;

/// <summary>
/// The group 0 variations this tool sets by name, rather than by number.
/// </summary>
/// <remarks>
/// They are spelled out here because a number used to answer a request is not a
/// label: getting one wrong reports the serial number as the location.
/// </remarks>
internal static class AttributeIds
{
    public const byte IdCode = 246;
    public const byte SubsetLevel = 249;
    public const byte ProductName = 250;
    public const byte VendorName = 252;
    public const byte SoftwareVersion = 242;
    public const byte HardwareVersion = 243;
    public const byte OwnerName = 244;
    public const byte Location = 245;
    public const byte DeviceName = 247;
    public const byte SerialNumber = 248;
}

/// <summary>One attribute given by number.</summary>
public sealed class AttributeConfig
{
    /// <summary>The attribute set. Zero is the standard's.</summary>
    public byte Set { get; set; }

    /// <summary>The attribute's number within its set.</summary>
    public byte Variation { get; set; }

    /// <summary>string, uint, int or float. Empty means string.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The value, parsed according to <see cref="Type"/>.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Converts the entry to what the library serves.</summary>
    public DeviceAttribute ToAttribute()
    {
        switch (Type.Trim().ToLowerInvariant())
        {
            case "":
            case "string":
                return DeviceAttribute.String(Variation, Value) with { Set = Set };

            case "uint":
                if (!ulong.TryParse(Value, CultureInfo.InvariantCulture, out var u))
                {
                    throw new InvalidOperationException(string.Format(
                        CultureInfo.InvariantCulture,
                        "attribute {0}: \"{1}\" is not an unsigned number", Variation, Value));
                }

                return DeviceAttribute.Uint(Variation, u) with { Set = Set };

            case "int":
                if (!long.TryParse(Value, CultureInfo.InvariantCulture, out var i))
                {
                    throw new InvalidOperationException(string.Format(
                        CultureInfo.InvariantCulture,
                        "attribute {0}: \"{1}\" is not a number", Variation, Value));
                }

                return DeviceAttribute.Int(Variation, i) with { Set = Set };

            case "float":
                if (!double.TryParse(Value, CultureInfo.InvariantCulture, out var f))
                {
                    throw new InvalidOperationException(string.Format(
                        CultureInfo.InvariantCulture,
                        "attribute {0}: \"{1}\" is not a number", Variation, Value));
                }

                return new DeviceAttribute
                {
                    Set = Set,
                    Variation = Variation,
                    Type = AttributeType.Float,
                    Real = f,
                };

            default:
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    "attribute {0}: unknown type \"{1}\"; use string, uint, int or float",
                    Variation, Type));
        }
    }
}

/// <summary>What the outstation says about itself over group 0.</summary>
public sealed class DeviceConfig
{
    /// <summary>Who made the device.</summary>
    public string Vendor { get; set; } = string.Empty;

    /// <summary>The product name and model.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>The firmware version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The hardware version.</summary>
    public string Hardware { get; set; } = string.Empty;

    /// <summary>The serial number.</summary>
    public string Serial { get; set; } = string.Empty;

    /// <summary>The device's own name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Where it is installed.</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>Who owns it.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>The user-assigned number a utility puts on the asset.</summary>
    public int IdCode { get; set; }

    /// <summary>The DNP3 subset level the device claims.</summary>
    /// <remarks>
    /// Zero omits it rather than claiming level 0, because a conformance claim
    /// is not something to report by accident.
    /// </remarks>
    public int Subset { get; set; }

    /// <summary>
    /// Stops the outstation reporting attributes at all, which is how a
    /// master's handling of a device without them gets tested.
    /// </summary>
    public bool Disabled { get; set; }

    /// <summary>
    /// Attributes this section does not name, including a device's own in sets
    /// other than the standard one.
    /// </summary>
    public List<AttributeConfig> Attributes { get; set; } = [];

    /// <summary>Describes this simulator.</summary>
    public static DeviceConfig Default() => new()
    {
        Vendor = "DSC Systems",
        Model = "dnp3-outstation (simulator)",
        Version = typeof(DeviceConfig).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
        Hardware = "none — this is software",
        Serial = "SIM-0000-0001",
        Name = "SIM-RTU",
        Location = "simulated substation",
        Subset = 2,
    };

    /// <summary>Turns the section into what the outstation serves.</summary>
    /// <remarks>
    /// The point counts and fragment sizes are left out: the outstation derives
    /// those from its own database, and a number configured here that disagreed
    /// with the database would be worse than no number at all.
    /// </remarks>
    public List<DeviceAttribute> ToAttributes()
    {
        if (Disabled)
        {
            return [];
        }

        var output = new List<DeviceAttribute>();

        void Add(byte variation, string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                output.Add(DeviceAttribute.String(variation, text));
            }
        }

        Add(AttributeIds.VendorName, Vendor);
        Add(AttributeIds.ProductName, Model);
        Add(AttributeIds.SoftwareVersion, Version);
        Add(AttributeIds.HardwareVersion, Hardware);
        Add(AttributeIds.SerialNumber, Serial);
        Add(AttributeIds.DeviceName, Name);
        Add(AttributeIds.Location, Location);
        Add(AttributeIds.OwnerName, Owner);

        if (IdCode != 0)
        {
            output.Add(DeviceAttribute.Uint(AttributeIds.IdCode, (ulong)IdCode));
        }

        if (Subset != 0)
        {
            output.Add(DeviceAttribute.Uint(AttributeIds.SubsetLevel, (ulong)Subset));
        }

        foreach (var e in Attributes)
        {
            output.Add(e.ToAttribute());
        }

        return output;
    }

    /// <summary>
    /// Renders the attributes for the startup banner, so whoever points a
    /// master at this device can see what it will answer with.
    /// </summary>
    public static string Describe(IReadOnlyList<DeviceAttribute> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        if (attributes.Count == 0)
        {
            return "  Device attributes: none — the outstation reports only its point counts\n";
        }

        var sb = new StringBuilder();
        sb.Append("  Device attributes\n");
        foreach (var a in attributes)
        {
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "    {0,-28} {1}\n", a.Name(), a.ValueText());
        }

        return sb.ToString();
    }
}

/// <summary>
/// Configures what a master may read and write over file transfer.
/// </summary>
/// <remarks>
/// The default is a small filesystem synthesised on disk under a temporary
/// directory, so the feature can be exercised without preparing anything.
/// Naming a directory serves that directory instead — and only that directory:
/// the handler is rooted, so a master cannot climb out of it.
/// </remarks>
public sealed class FilesConfig
{
    /// <summary>
    /// Serves real files instead of the simulated ones. Empty uses the built-in
    /// sample tree.
    /// </summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>Refuses writes and deletes.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Turns file transfer off entirely, so the outstation answers the file
    /// function codes the way a device without them does.
    /// </summary>
    /// <remarks>
    /// It is how a master's handling of an unsupported feature gets tested.
    /// </remarks>
    public bool Disabled { get; set; }

    /// <summary>Caps the transfer block. Zero uses the library's default.</summary>
    public ushort BlockSize { get; set; }

    /// <summary>
    /// Closes a transfer that has gone quiet. Zero uses the default.
    /// </summary>
    public TimeSpan Timeout { get; set; }
}
