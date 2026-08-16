// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using SharpDnp3.Link;

namespace SharpDnp3.Tests.Link;

public class FrameTests
{
    private static byte[] PayloadBuffer() => new byte[LinkConstants.MaxPayload];

    [Fact]
    public void ControlRoundTrip()
    {
        // Every one of the 256 control octets must survive parse and re-encode.
        for (var i = 0; i < 256; i++)
        {
            var b = (byte)i;
            Assert.Equal(b, Control.Parse(b).ToByte());
        }
    }

    // Parameters stay primitive because the theory method must be public while
    // Control is internal: dir, prm, fcb, fcv and the function code.
    [Theory]
    [InlineData("master reset link states", (byte)0xC0, true, true, false, false, (byte)0)]
    [InlineData("master confirmed user data fcb set", (byte)0xF3, true, true, true, true, (byte)3)]
    [InlineData("outstation ack", (byte)0x00, false, false, false, false, (byte)0)]
    [InlineData("outstation link status with dfc", (byte)0x1B, false, false, false, true, (byte)11)]
    [InlineData("master unconfirmed user data", (byte)0xC4, true, true, false, false, (byte)4)]
    public void ControlFields(string name, byte b, bool dir, bool prm, bool fcb, bool fcv, byte func)
    {
        _ = name;
        var want = new Control(dir, prm, fcb, fcv, (LinkFunction)func);
        var got = Control.Parse(b);
        Assert.Equal(want, got);
        Assert.Equal(b, got.ToByte());
    }

    /// <summary>
    /// The same bit position is FCV on a primary frame and DFC on a secondary
    /// one. Confusing them stalls a link permanently, so pin the distinction.
    /// </summary>
    [Fact]
    public void DfcOnlyOnSecondary()
    {
        var primary = Control.Parse(0xF3);   // DIR|PRM|FCB|FCV, confirmed user data
        var secondary = Control.Parse(0x1B); // FCV position set, link status
        Assert.False(primary.Dfc);
        Assert.True(secondary.Dfc);
    }

    [Theory]
    [InlineData(0, 10)]    // header only
    [InlineData(1, 13)]    // header + 1 octet + CRC
    [InlineData(16, 28)]   // header + one full block
    [InlineData(17, 31)]   // header + one full block + 1 octet block
    [InlineData(250, 292)] // the maximum frame
    public void FrameSize(int payload, int want)
    {
        Assert.Equal(want, LinkConstants.FrameSize(payload));
    }

    [Fact]
    public void MaxFrameSizeIs292()
    {
        Assert.Equal(292, LinkConstants.MaxFrameSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(100)]
    [InlineData(249)]
    [InlineData(250)]
    public void EncodeDecodeRoundTrip(int n)
    {
        var r = new Random(7 + n);
        var payload = new byte[n];
        r.NextBytes(payload);

        var header = new LinkHeader(
            Control: new Control(Dir: true, Prm: true, Fcb: true, Fcv: true, LinkFunction.ConfirmedUserData),
            Dest: 1024,
            Src: 1,
            Length: (byte)(LinkConstants.MinLength + n));

        var wire = FrameCodec.Encode(header, payload);
        Assert.Equal(LinkConstants.FrameSize(n), wire.Length);

        var status = FrameCodec.Decode(
            wire, PayloadBuffer(), out var decoded, out var consumed, out var payloadLength);

        Assert.Equal(LinkDecodeStatus.Ok, status);
        Assert.Equal(wire.Length, consumed);
        Assert.Equal(header, decoded);
        Assert.Equal(n, payloadLength);
    }

    [Fact]
    public void EncodeDecodePayloadContentsMatch()
    {
        var r = new Random(11);
        var payload = new byte[100];
        r.NextBytes(payload);

        var header = new LinkHeader(
            Control: new Control(Dir: true, Prm: true, Fcb: false, Fcv: false, LinkFunction.UnconfirmedUserData),
            Dest: 10,
            Src: 1,
            Length: (byte)(LinkConstants.MinLength + payload.Length));

        var wire = FrameCodec.Encode(header, payload);
        var buffer = PayloadBuffer();
        var status = FrameCodec.Decode(wire, buffer, out _, out _, out var payloadLength);

        Assert.Equal(LinkDecodeStatus.Ok, status);
        Assert.Equal(payload, buffer[..payloadLength]);
    }

    /// <summary>
    /// A master's RESET_LINK_STATES to outstation 10 from master 1 is a fixed
    /// ten-octet frame. Pinning it catches endianness and field-order slips
    /// that a round-trip test cannot.
    /// </summary>
    [Fact]
    public void EncodeKnownFrame()
    {
        var header = new LinkHeader(
            Control: new Control(Dir: true, Prm: true, Fcb: false, Fcv: false, LinkFunction.ResetLinkStates),
            Dest: 10,
            Src: 1,
            Length: LinkConstants.MinLength);

        var got = FrameCodec.Encode(header, []);

        byte[] head = [0x05, 0x64, 0x05, 0xC0, 0x0A, 0x00, 0x01, 0x00];
        var crc = Crc.Compute(head);
        byte[] want = [.. head, (byte)crc, (byte)(crc >> 8)];

        Assert.Equal(want, got);
    }

    [Fact]
    public void EncodeRejectsOversizePayload()
    {
        var status = FrameCodec.TryEncode(
            new byte[LinkConstants.MaxFrameSize + 16],
            default,
            new byte[LinkConstants.MaxPayload + 1],
            out _);

        Assert.Equal(LinkDecodeStatus.PayloadTooLong, status);
        Assert.Throws<MalformedException>(() =>
            FrameCodec.Encode(default, new byte[LinkConstants.MaxPayload + 1]));
    }

    [Fact]
    public void EncodeWritesIntoCallerBufferWithoutAllocating()
    {
        var buf = new byte[LinkConstants.MaxFrameSize];
        var header = new LinkHeader(
            Control: new Control(Dir: false, Prm: true, Fcb: false, Fcv: false, LinkFunction.UnconfirmedUserData),
            Dest: 0,
            Src: 0,
            Length: LinkConstants.MinLength + 250);

        var status = FrameCodec.TryEncode(buf, header, new byte[250], out var written);

        Assert.Equal(LinkDecodeStatus.Ok, status);
        Assert.Equal(LinkConstants.MaxFrameSize, written);
    }

    private static byte[] ValidFrame()
    {
        var header = new LinkHeader(
            Control: new Control(Dir: false, Prm: true, Fcb: false, Fcv: false, LinkFunction.UnconfirmedUserData),
            Dest: 10,
            Src: 1,
            Length: LinkConstants.MinLength + 20);
        return FrameCodec.Encode(header, new byte[20]);
    }

    // The expected status travels as its underlying int so the theory method
    // can stay public while LinkDecodeStatus is internal.
    [Theory]
    [InlineData("empty", (int)LinkDecodeStatus.ShortFrame)]
    [InlineData("truncated header", (int)LinkDecodeStatus.ShortFrame)]
    [InlineData("truncated body", (int)LinkDecodeStatus.ShortFrame)]
    [InlineData("bad start", (int)LinkDecodeStatus.BadStart)]
    [InlineData("header crc", (int)LinkDecodeStatus.HeaderCrc)]
    [InlineData("body crc", (int)LinkDecodeStatus.BodyCrc)]
    [InlineData("corrupt payload", (int)LinkDecodeStatus.BodyCrc)]
    public void DecodeErrors(string name, int wantStatus)
    {
        var want = (LinkDecodeStatus)wantStatus;
        var valid = ValidFrame();
        var input = name switch
        {
            "empty" => [],
            "truncated header" => valid[..9],
            "truncated body" => valid[..^1],
            "bad start" => Mutate(valid, 0, 0x06),
            "header crc" => Xor(valid, 8, 0xFF),
            "body crc" => Xor(valid, valid.Length - 1, 0xFF),
            "corrupt payload" => Xor(valid, 12, 0xFF),
            _ => throw new InvalidOperationException(name),
        };

        var status = FrameCodec.Decode(input, PayloadBuffer(), out _, out _, out _);
        Assert.Equal(want, status);
    }

    private static byte[] Mutate(byte[] source, int index, byte value)
    {
        var copy = (byte[])source.Clone();
        copy[index] = value;
        return copy;
    }

    private static byte[] Xor(byte[] source, int index, byte mask)
    {
        var copy = (byte[])source.Clone();
        copy[index] ^= mask;
        return copy;
    }

    /// <summary>
    /// LEN below the five-octet minimum is malformed even with a valid CRC, so
    /// the header has to be re-CRC'd after tampering to reach the check.
    /// </summary>
    [Fact]
    public void DecodeBadLength()
    {
        byte[] head = [0x05, 0x64, 0x04, 0xC4, 0x0A, 0x00, 0x01, 0x00];
        var crc = Crc.Compute(head);
        byte[] hdr = [.. head, (byte)crc, (byte)(crc >> 8)];

        Assert.Equal(LinkDecodeStatus.BadLength, FrameCodec.DecodeHeader(hdr, out _));
    }

    [Theory]
    [InlineData((ushort)0xFFFD)]
    [InlineData((ushort)0xFFFE)]
    [InlineData((ushort)0xFFFF)]
    public void BroadcastAddresses(ushort address)
    {
        Assert.True(LinkConstants.IsBroadcast(address));
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData((ushort)65532)]
    [InlineData((ushort)0xFFF0)]
    [InlineData((ushort)0xFFFB)]
    public void NonBroadcastAddresses(ushort address)
    {
        Assert.False(LinkConstants.IsBroadcast(address));
    }

    [Fact]
    public void ReservedAddresses()
    {
        Assert.True(LinkConstants.IsReserved(0xFFF0));
        Assert.True(LinkConstants.IsReserved(0xFFFB));

        // The self-address is usable, not reserved.
        Assert.False(LinkConstants.IsReserved(LinkConstants.SelfAddress));

        // 0xFFEF is below the reserved range.
        Assert.False(LinkConstants.IsReserved(0xFFEF));
    }
}
