// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

namespace SharpDnp3.Objects;

/// <summary>
/// One group 85 variation 1 or group 86 variation 1 descriptor element.
/// <see cref="Ancillary"/> holds its identifier, UUID, name, prototype
/// reference or other code-specific data.
/// </summary>
/// <param name="Code">The descriptor code.</param>
/// <param name="DataType">The data type code.</param>
/// <param name="MaxLength">The maximum length code.</param>
/// <param name="Ancillary">Code-specific data.</param>
public readonly record struct DatasetElement(byte Code, byte DataType, byte MaxLength, byte[] Ancillary);

/// <summary>Encodes and decodes dataset descriptor elements.</summary>
public static class DatasetCodec
{
    /// <summary>Decodes self-delimiting descriptor elements.</summary>
    /// <exception cref="MalformedException">An element length is invalid.</exception>
    public static List<DatasetElement> ParseDescriptor(ReadOnlySpan<byte> data)
    {
        var output = new List<DatasetElement>();
        while (data.Length > 0)
        {
            int n = data[0];
            if (n < 3 || n >= data.Length)
            {
                throw new MalformedException("dnp3: malformed dataset descriptor element length");
            }

            output.Add(new DatasetElement(data[1], data[2], data[3], data[4..(n + 1)].ToArray()));
            data = data[(n + 1)..];
        }

        return output;
    }

    /// <summary>Encodes elements, refusing an ancillary value too long for the one-octet element length.</summary>
    /// <exception cref="BadConfigException">An ancillary value cannot fit.</exception>
    public static void AppendDescriptor(List<byte> dst, IEnumerable<DatasetElement> elements)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(elements);
        foreach (var e in elements)
        {
            if (e.Ancillary.Length > 252)
            {
                throw new BadConfigException("dnp3: dataset ancillary value too long");
            }

            dst.Add((byte)(3 + e.Ancillary.Length));
            dst.Add(e.Code);
            dst.Add(e.DataType);
            dst.Add(e.MaxLength);
            dst.AddRange(e.Ancillary);
        }
    }
}
