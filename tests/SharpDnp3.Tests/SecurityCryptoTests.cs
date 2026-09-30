// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using SharpDnp3.Security;

namespace SharpDnp3.Tests;

public class SecurityCryptoTests
{
    private const string Data = "00112233445566778899aabbccddeeff000102030405060708090a0b0c0d0e0f";

    /// <summary>Vectors from RFC 3394 section 4, each cross-checked against OpenSSL.</summary>
    [Theory]
    [InlineData(16, 16, "1fa68b0a8112b447aef34bd8fb5a7b829d3e862371d2cfe5")]
    [InlineData(24, 16, "96778b25ae6ca435f92b5b97c050aed2468ab8a17ad84e5d")]
    [InlineData(32, 16, "64e8c3f9ce0f5ba263e9777905818a2a93c8191e7d6e8ae7")]
    [InlineData(24, 24, "031d33264e15d33268f24ec260743edce1c6c7ddee725a936ba814915c6762d2")]
    [InlineData(32, 32, "28c9f404c4b810f4cbccb35cfb87f8263f5786e2d80ed326cbc7f0e71a99f43bfb988b9b7a02dd21")]
    [InlineData(16, 32, "11826840774d993ff9c2fa02cca3cea0e93b1e1cf96361f93ea6dc2f345194e7b30f964c79f9e61d")]
    public void Rfc3394Vectors(int keyBytes, int length, string expected)
    {
        var key = Enumerable.Range(0, keyBytes).Select(i => (byte)i).ToArray();
        var plain = Convert.FromHexString(Data)[..length];
        var want = Convert.FromHexString(expected);

        Assert.Equal(want, SaCrypto.Wrap(key, plain));
        Assert.Equal(plain, SaCrypto.Unwrap(key, want));

        for (var i = 0; i < want.Length; i++)
        {
            var bad = (byte[])want.Clone();
            bad[i] ^= 1;
            Assert.ThrowsAny<Dnp3Exception>(() => SaCrypto.Unwrap(key, bad));
        }
    }

    /// <summary>RFC 4231 test case 2 (HMAC-SHA-256), truncated to the 16 octets SAv5 uses.</summary>
    [Fact]
    public void MacIsHmacSha256Truncated()
    {
        var key = "Jefe"u8.ToArray();
        var data = "what do ya want for nothing?"u8.ToArray();
        var want = Convert.FromHexString("5bdcc146bf60754e6a042426089575c7");
        Assert.Equal(want, SaCrypto.Mac(key, data));
        // Parts are concatenated.
        Assert.Equal(want, SaCrypto.Mac(key, data[..10], data[10..]));
    }
}
