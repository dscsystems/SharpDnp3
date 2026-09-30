// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The symmetric primitives Secure Authentication v5 uses: the SHA-256 HMAC
// (algorithm 4, truncated to 16 octets) and the RFC 3394 AES key wrap.

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SharpDnp3.Security;

/// <summary>An authentication or key-wrap integrity check failed.</summary>
internal sealed class AuthenticationException : Dnp3Exception
{
    public AuthenticationException(string message = "DNP3 authentication failed") : base(message) { }
}

/// <summary>The symmetric primitives of SAv5.</summary>
internal static class SaCrypto
{
    /// <summary>The length of a networked SAv5 MAC.</summary>
    public const int MacLength = 16;

    /// <summary>Computes the networked SAv5 SHA-256 HMAC (algorithm 4) over the concatenated parts.</summary>
    public static byte[] Mac(ReadOnlySpan<byte> key, params ReadOnlyMemory<byte>[] parts)
    {
        using var hmac = new HMACSHA256(key.ToArray());
        var total = 0;
        foreach (var p in parts)
        {
            total += p.Length;
        }

        var buf = new byte[total];
        var off = 0;
        foreach (var p in parts)
        {
            p.Span.CopyTo(buf.AsSpan(off));
            off += p.Length;
        }

        var full = hmac.ComputeHash(buf);
        return full.AsSpan(0, MacLength).ToArray();
    }

    /// <summary>Reports whether two values are equal, in constant time.</summary>
    public static bool Equal(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        CryptographicOperations.FixedTimeEquals(a, b);

    /// <summary>Implements RFC 3394 key wrap with the default initialisation vector.</summary>
    public static byte[] Wrap(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length < 16 || plaintext.Length % 8 != 0)
        {
            throw new AuthenticationException();
        }

        using var aes = NewAes(key);
        var output = new byte[plaintext.Length + 8];
        output.AsSpan(0, 8).Fill(0xA6);
        plaintext.CopyTo(output.AsSpan(8));
        var n = plaintext.Length / 8;
        var block = new byte[16];
        var encrypted = new byte[16];
        for (var j = 0; j < 6; j++)
        {
            for (var i = 1; i <= n; i++)
            {
                output.AsSpan(0, 8).CopyTo(block);
                output.AsSpan(8 * i, 8).CopyTo(block.AsSpan(8));
                aes.EncryptEcb(block, encrypted, PaddingMode.None);
                var t = BinaryPrimitives.ReadUInt64BigEndian(encrypted) ^ (ulong)((n * j) + i);
                BinaryPrimitives.WriteUInt64BigEndian(output, t);
                encrypted.AsSpan(8, 8).CopyTo(output.AsSpan(8 * i));
            }
        }

        return output;
    }

    /// <summary>Verifies the RFC 3394 integrity check before returning the plaintext.</summary>
    public static byte[] Unwrap(ReadOnlySpan<byte> key, ReadOnlySpan<byte> wrapped)
    {
        if (wrapped.Length < 24 || wrapped.Length % 8 != 0)
        {
            throw new AuthenticationException();
        }

        using var aes = NewAes(key);
        var output = wrapped.ToArray();
        var n = (output.Length / 8) - 1;
        var block = new byte[16];
        var decrypted = new byte[16];
        for (var j = 5; j >= 0; j--)
        {
            for (var i = n; i >= 1; i--)
            {
                var t = BinaryPrimitives.ReadUInt64BigEndian(output) ^ (ulong)((n * j) + i);
                BinaryPrimitives.WriteUInt64BigEndian(block, t);
                output.AsSpan(8 * i, 8).CopyTo(block.AsSpan(8));
                aes.DecryptEcb(block, decrypted, PaddingMode.None);
                decrypted.AsSpan(0, 8).CopyTo(output);
                decrypted.AsSpan(8, 8).CopyTo(output.AsSpan(8 * i));
            }
        }

        Span<byte> iv = stackalloc byte[8];
        iv.Fill(0xA6);
        if (!CryptographicOperations.FixedTimeEquals(output.AsSpan(0, 8), iv))
        {
            CryptographicOperations.ZeroMemory(output);
            throw new AuthenticationException();
        }

        return output[8..];
    }

    private static Aes NewAes(ReadOnlySpan<byte> key)
    {
        if (key.Length is not (16 or 24 or 32))
        {
            throw new AuthenticationException();
        }

        var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes;
    }
}
