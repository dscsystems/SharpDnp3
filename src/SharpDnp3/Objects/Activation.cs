// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Buffers.Binary;
using System.Text;

namespace SharpDnp3.Objects;

/// <summary>One configuration file's activation result.</summary>
/// <param name="Code">The status code.</param>
/// <param name="Text">A human-readable explanation.</param>
public readonly record struct ActivationStatus(byte Code, string Text);

/// <summary>The group 91 variation 1 response to ACTIVATE_CONFIG.</summary>
/// <param name="Delay">How long the device needs before the configuration is live.</param>
/// <param name="Statuses">One entry per file.</param>
public sealed record ActivationResult(TimeSpan Delay, IReadOnlyList<ActivationStatus> Statuses)
{
    /// <summary>An empty result with no delay.</summary>
    public ActivationResult() : this(TimeSpan.Zero, []) { }
}

/// <summary>Encodes and decodes group 91.</summary>
public static class ActivationCodec
{
    /// <summary>Appends a group 91 variation 1 object.</summary>
    /// <exception cref="BadConfigException">A field does not fit its encoding.</exception>
    public static void Append(List<byte> dst, ActivationResult v)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(v);
        var ms = (long)v.Delay.TotalMilliseconds;
        if (ms is < 0 or > uint.MaxValue || v.Statuses.Count > 255)
        {
            throw new BadConfigException();
        }

        Span<byte> delay = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(delay, (uint)ms);
        foreach (var b in delay)
        {
            dst.Add(b);
        }

        dst.Add((byte)v.Statuses.Count);
        foreach (var s in v.Statuses)
        {
            var text = Encoding.UTF8.GetBytes(s.Text);
            if (text.Length > 254)
            {
                throw new BadConfigException();
            }

            dst.Add((byte)(text.Length + 1));
            dst.Add(s.Code);
            dst.AddRange(text);
        }
    }

    /// <summary>Decodes a group 91 variation 1 object.</summary>
    /// <exception cref="MalformedException">The octets are not a valid object.</exception>
    public static ActivationResult Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
        {
            throw new MalformedException();
        }

        var delay = TimeSpan.FromMilliseconds(BinaryPrimitives.ReadUInt32LittleEndian(data));
        int n = data[4];
        data = data[5..];
        var statuses = new List<ActivationStatus>(n);
        for (var i = 0; i < n; i++)
        {
            if (data.Length < 2 || data[0] == 0 || data[0] >= data.Length)
            {
                throw new MalformedException("dnp3: malformed activation status length");
            }

            int length = data[0];
            statuses.Add(new ActivationStatus(data[1], Encoding.UTF8.GetString(data[2..(1 + length)])));
            data = data[(1 + length)..];
        }

        if (data.Length != 0)
        {
            throw new MalformedException();
        }

        return new ActivationResult(delay, statuses);
    }
}
