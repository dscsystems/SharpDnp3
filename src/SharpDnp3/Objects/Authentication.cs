// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Buffers.Binary;

namespace SharpDnp3.Objects;

/// <summary>Group 120 variation 1. Algorithm 4 is SHA-256 HMAC truncated to 16 octets.</summary>
public readonly record struct AuthChallenge(uint Sequence, ushort User, byte Algorithm, byte Reason, byte[] Data);

/// <summary>Group 120 variation 2.</summary>
public readonly record struct AuthReply(uint Sequence, ushort User, byte[] Mac);

/// <summary>Group 120 variation 5. <see cref="Mac"/> authenticates the most recent key-change ASDU.</summary>
public readonly record struct AuthKeyStatus(
    uint Sequence, ushort User, byte WrapAlgorithm, byte Status, byte Algorithm, byte[] Challenge, byte[] Mac);

/// <summary>Group 120 variation 6. <see cref="Wrapped"/> is the RFC 3394 encrypted key data.</summary>
public readonly record struct AuthKeyChange(uint Sequence, ushort User, byte[] Wrapped);

/// <summary>Encodes and decodes the group 120 objects of the symmetric exchange.</summary>
public static class AuthenticationCodec
{
    private static MalformedException Bad() => new("dnp3: malformed authentication object");

    private static void U32(List<byte> dst, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        foreach (var x in b)
        {
            dst.Add(x);
        }
    }

    private static void U16(List<byte> dst, ushort v)
    {
        dst.Add((byte)v);
        dst.Add((byte)(v >> 8));
    }

    /// <summary>Appends a group 120 variation 1 object.</summary>
    public static void Append(List<byte> dst, AuthChallenge v)
    {
        ArgumentNullException.ThrowIfNull(dst);
        U32(dst, v.Sequence);
        U16(dst, v.User);
        dst.Add(v.Algorithm);
        dst.Add(v.Reason);
        dst.AddRange(v.Data);
    }

    /// <summary>Decodes a group 120 variation 1 object.</summary>
    public static AuthChallenge ParseChallenge(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
        {
            throw Bad();
        }

        return new AuthChallenge(
            BinaryPrimitives.ReadUInt32LittleEndian(data),
            BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
            data[6], data[7], data[8..].ToArray());
    }

    /// <summary>Appends a group 120 variation 2 object.</summary>
    public static void Append(List<byte> dst, AuthReply v)
    {
        ArgumentNullException.ThrowIfNull(dst);
        U32(dst, v.Sequence);
        U16(dst, v.User);
        dst.AddRange(v.Mac);
    }

    /// <summary>Decodes a group 120 variation 2 object.</summary>
    public static AuthReply ParseReply(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7)
        {
            throw Bad();
        }

        return new AuthReply(
            BinaryPrimitives.ReadUInt32LittleEndian(data),
            BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
            data[6..].ToArray());
    }

    /// <summary>Appends a group 120 variation 5 object.</summary>
    public static void Append(List<byte> dst, AuthKeyStatus v)
    {
        ArgumentNullException.ThrowIfNull(dst);
        U32(dst, v.Sequence);
        U16(dst, v.User);
        dst.Add(v.WrapAlgorithm);
        dst.Add(v.Status);
        dst.Add(v.Algorithm);
        U16(dst, (ushort)v.Challenge.Length);
        dst.AddRange(v.Challenge);
        dst.AddRange(v.Mac);
    }

    /// <summary>Decodes a group 120 variation 5 object.</summary>
    public static AuthKeyStatus ParseKeyStatus(ReadOnlySpan<byte> data)
    {
        if (data.Length < 11)
        {
            throw Bad();
        }

        int n = BinaryPrimitives.ReadUInt16LittleEndian(data[9..]);
        if (n < 4 || n > data.Length - 11)
        {
            throw Bad();
        }

        return new AuthKeyStatus(
            BinaryPrimitives.ReadUInt32LittleEndian(data),
            BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
            data[6], data[7], data[8],
            data[11..(11 + n)].ToArray(), data[(11 + n)..].ToArray());
    }

    /// <summary>Appends a group 120 variation 6 object.</summary>
    public static void Append(List<byte> dst, AuthKeyChange v)
    {
        ArgumentNullException.ThrowIfNull(dst);
        U32(dst, v.Sequence);
        U16(dst, v.User);
        dst.AddRange(v.Wrapped);
    }

    /// <summary>Decodes a group 120 variation 6 object.</summary>
    public static AuthKeyChange ParseKeyChange(ReadOnlySpan<byte> data)
    {
        if (data.Length < 30 || (data.Length - 6) % 8 != 0)
        {
            throw Bad();
        }

        return new AuthKeyChange(
            BinaryPrimitives.ReadUInt32LittleEndian(data),
            BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
            data[6..].ToArray());
    }
}
