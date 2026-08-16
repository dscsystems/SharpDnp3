// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Text;
using SharpDnp3.Link;

namespace SharpDnp3.Tests.Link;

public class CrcTests
{
    [Fact]
    public void CheckValue()
    {
        // The canonical check value for CRC-16/DNP: the CRC of "123456789".
        const ushort want = 0xEA82;
        var got = Crc.Compute(Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal(want, got);
    }

    public static TheoryData<string, byte[], ushort> KnownFrameData => new()
    {
        // Master → outstation, RESET_LINK_STATES, dest 10, src 1.
        {
            "reset link states",
            [0x05, 0x64, 0x05, 0xC0, 0x0A, 0x00, 0x01, 0x00],
            Crc.Compute([0x05, 0x64, 0x05, 0xC0, 0x0A, 0x00, 0x01, 0x00])
        },
        // init 0x0000 xor'd with 0xFFFF
        { "empty", [], 0xFFFF },
        { "single zero octet", [0x00], Crc.Compute([0x00]) },
    };

    [Theory]
    [MemberData(nameof(KnownFrameData))]
    public void KnownFrames(string name, byte[] header, ushort want)
    {
        _ = name;
        Assert.Equal(want, Crc.Compute(header));
    }

    /// <summary>
    /// Proves the lookup table rather than trusting it. A single wrong entry
    /// would otherwise pass every round-trip test, because both encode and
    /// decode would be wrong in the same way.
    /// </summary>
    [Fact]
    public void TableMatchesBitwise()
    {
        var r = new Random(12);
        var buf = new byte[300];
        for (var iteration = 0; iteration < 2000; iteration++)
        {
            var n = r.Next(buf.Length + 1);
            r.NextBytes(buf.AsSpan(0, n));

            var table = Crc.Compute(buf.AsSpan(0, n));
            var reference = Crc.ComputeBitwise(buf.AsSpan(0, n));
            Assert.Equal(reference, table);
        }
    }

    [Fact]
    public void IsValidRejectsCorruption()
    {
        byte[] data = [0x05, 0x64, 0x05, 0xC0, 0x0A, 0x00, 0x01, 0x00];
        var c = Crc.Compute(data);
        byte[] good = [(byte)c, (byte)(c >> 8)];

        Assert.True(Crc.IsValid(data, good));
        Assert.False(Crc.IsValid(data, [(byte)(good[0] ^ 0xFF), good[1]]));
        Assert.False(Crc.IsValid(data, [good[0], (byte)(good[1] ^ 0xFF)]));
        Assert.False(Crc.IsValid(data, good.AsSpan(0, 1)));
    }

    /// <summary>Checks the property the CRC exists for.</summary>
    [Fact]
    public void DetectsSingleBitErrors()
    {
        byte[] data =
        [
            0x05, 0x64, 0x14, 0x44, 0x0A, 0x00, 0x01, 0x00,
            0xC0, 0xC1, 0x01, 0x3C, 0x02, 0x06,
        ];
        var basis = Crc.Compute(data);
        var corrupt = new byte[data.Length];

        for (var i = 0; i < data.Length; i++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                data.CopyTo(corrupt, 0);
                corrupt[i] ^= (byte)(1 << bit);
                Assert.NotEqual(basis, Crc.Compute(corrupt));
            }
        }
    }
}
