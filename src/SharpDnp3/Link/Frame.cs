// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The DNP3 data link layer: frame encoding and decoding, the CRC, and the
// primary and secondary station state machines.
//
// Nothing here performs I/O. Frames are encoded into caller-supplied buffers
// and decoded from spans; the state machines are driven by explicit event
// calls and report what they want sent by returning it. Sessions supply the
// socket and the clock.

using System.Buffers.Binary;
using System.Globalization;

namespace SharpDnp3.Link;

/// <summary>Wire constants fixed by IEEE 1815 clause 9.</summary>
public static class LinkConstants
{
    /// <summary>First octet of the 0x0564 frame delimiter.</summary>
    public const byte StartByte0 = 0x05;

    /// <summary>Second octet of the 0x0564 frame delimiter.</summary>
    public const byte StartByte1 = 0x64;

    /// <summary>
    /// The fixed part of every frame: two start octets, length, control,
    /// destination, source and the header CRC.
    /// </summary>
    public const int HeaderSize = 10;

    /// <summary>The payload octet count per CRC-protected block.</summary>
    public const int BlockSize = 16;

    /// <summary>The octet count of one CRC.</summary>
    public const int CrcSize = 2;

    /// <summary>
    /// Lower bound of the LEN field, which counts the control, address and
    /// payload octets but excludes every CRC.
    /// </summary>
    public const int MinLength = 5;

    /// <summary>Upper bound of the LEN field.</summary>
    public const int MaxLength = 255;

    /// <summary>
    /// The largest user-data payload one frame can carry: <see cref="MaxLength"/>
    /// minus the five octets of control and addresses.
    /// </summary>
    public const int MaxPayload = MaxLength - MinLength; // 250

    /// <summary>
    /// The largest frame on the wire: a full header plus 250 payload octets
    /// spread over sixteen CRC-protected blocks.
    /// </summary>
    public const int MaxFrameSize = HeaderSize + MaxPayload + (16 * CrcSize); // 292

    // ---- Reserved and broadcast addresses ----

    /// <summary>
    /// Addresses every outstation and requests no application confirmation.
    /// </summary>
    public const ushort BroadcastNoConfirm = 0xFFFF;

    /// <summary>
    /// Addresses every outstation and requires an application confirmation.
    /// </summary>
    public const ushort BroadcastMandatoryConfirm = 0xFFFE;

    /// <summary>
    /// Addresses every outstation, leaving the confirmation to the
    /// outstation's discretion.
    /// </summary>
    public const ushort BroadcastOptionalConfirm = 0xFFFD;

    /// <summary>
    /// Lets a master address an outstation without knowing its configured
    /// address. Level 3.
    /// </summary>
    public const ushort SelfAddress = 0xFFFC;

    private const ushort ReservedLow = 0xFFF0;
    private const ushort ReservedHigh = 0xFFFB;

    /// <summary>
    /// Reports whether <paramref name="address"/> is one of the three broadcast
    /// addresses.
    /// </summary>
    public static bool IsBroadcast(ushort address) =>
        address is >= BroadcastOptionalConfirm and <= BroadcastNoConfirm;

    /// <summary>
    /// Reports whether <paramref name="address"/> falls in the reserved range,
    /// which a conforming device must not use as its own address.
    /// </summary>
    public static bool IsReserved(ushort address) =>
        address is >= ReservedLow and <= ReservedHigh;

    /// <summary>
    /// Returns the on-the-wire octet count of a payload of <paramref name="n"/>
    /// octets once it has been split into CRC-protected blocks.
    /// </summary>
    public static int BodySize(int n)
    {
        if (n == 0)
        {
            return 0;
        }

        var blocks = (n + BlockSize - 1) / BlockSize;
        return n + (blocks * CrcSize);
    }

    /// <summary>
    /// Returns the total wire size of a frame carrying <paramref name="n"/>
    /// payload octets.
    /// </summary>
    public static int FrameSize(int n) => HeaderSize + BodySize(n);
}

/// <summary>
/// The four-bit function code in the control octet.
/// </summary>
/// <remarks>
/// The same numeric value means different things depending on the PRM bit, so
/// the constants are split into two blocks and
/// <see cref="LinkFunctionExtensions.Name"/> needs to be told which direction
/// the frame travelled.
/// </remarks>
public enum LinkFunction : byte
{
    // ---- Primary-to-secondary function codes (PRM = 1) ----

    /// <summary>Reset the link state machines.</summary>
    ResetLinkStates = 0,

    /// <summary>Test the link with a frame-count-bit exchange.</summary>
    TestLinkStates = 2,

    /// <summary>User data requiring a link-layer confirmation.</summary>
    ConfirmedUserData = 3,

    /// <summary>User data requiring no link-layer confirmation.</summary>
    UnconfirmedUserData = 4,

    /// <summary>Ask the secondary for its link status.</summary>
    RequestLinkStatus = 9,

    // ---- Secondary-to-primary function codes (PRM = 0) ----

    /// <summary>Positive acknowledgement.</summary>
    Ack = 0,

    /// <summary>Negative acknowledgement.</summary>
    Nack = 1,

    /// <summary>Link status response.</summary>
    LinkStatus = 11,

    /// <summary>The requested function is not supported.</summary>
    NotSupported = 15,
}

/// <summary>Naming helpers for <see cref="LinkFunction"/>.</summary>
public static class LinkFunctionExtensions
{
    private static readonly Dictionary<byte, string> PrimaryNames = new()
    {
        [0] = "RESET_LINK_STATES",
        [2] = "TEST_LINK_STATES",
        [3] = "CONFIRMED_USER_DATA",
        [4] = "UNCONFIRMED_USER_DATA",
        [9] = "REQUEST_LINK_STATUS",
    };

    private static readonly Dictionary<byte, string> SecondaryNames = new()
    {
        [0] = "ACK",
        [1] = "NACK",
        [11] = "LINK_STATUS",
        [15] = "NOT_SUPPORTED",
    };

    /// <summary>
    /// Returns the function code's name for the given direction.
    /// </summary>
    public static string Name(this LinkFunction function, bool primary)
    {
        var table = primary ? PrimaryNames : SecondaryNames;
        return table.TryGetValue((byte)function, out var name)
            ? name
            : string.Format(CultureInfo.InvariantCulture, "FUNC_{0}", (byte)function);
    }
}

/// <summary>The link control octet.</summary>
/// <remarks>
/// <code>
/// bit 7  DIR   direction: set when sent from the master station
/// bit 6  PRM   primary message
/// bit 5  FCB   frame count bit, toggled per confirmed transmission
/// bit 4  FCV   frame count valid (primary) / DFC data flow control (secondary)
/// bits 3-0     function code
/// </code>
/// </remarks>
/// <param name="Dir">Set when sent from the master station.</param>
/// <param name="Prm">Set on a primary message.</param>
/// <param name="Fcb">The frame count bit.</param>
/// <param name="Fcv">Frame count valid, or DFC when <paramref name="Prm"/> is clear.</param>
/// <param name="Func">The four-bit function code.</param>
public readonly record struct Control(
    bool Dir,
    bool Prm,
    bool Fcb,
    bool Fcv,
    LinkFunction Func)
{
    private const byte DirBit = 0x80;
    private const byte PrmBit = 0x40;
    private const byte FcbBit = 0x20;
    private const byte FcvBit = 0x10;
    private const byte FuncMask = 0x0F;

    /// <summary>Encodes the control octet.</summary>
    public byte ToByte()
    {
        byte b = 0;
        if (Dir)
        {
            b |= DirBit;
        }

        if (Prm)
        {
            b |= PrmBit;
        }

        if (Fcb)
        {
            b |= FcbBit;
        }

        if (Fcv)
        {
            b |= FcvBit;
        }

        return (byte)(b | ((byte)Func & FuncMask));
    }

    /// <summary>Decodes a control octet.</summary>
    public static Control Parse(byte b) => new(
        Dir: (b & DirBit) != 0,
        Prm: (b & PrmBit) != 0,
        Fcb: (b & FcbBit) != 0,
        Fcv: (b & FcvBit) != 0,
        Func: (LinkFunction)(b & FuncMask));

    /// <summary>
    /// The data-flow-control bit, which occupies the FCV position on frames
    /// from a secondary station. A set DFC means the secondary's buffers are
    /// full and the primary must stop sending user data.
    /// </summary>
    public bool Dfc => !Prm && Fcv;

    /// <inheritdoc/>
    public override string ToString()
    {
        var dir = Dir ? "MSTR→OUTS" : "OUTS→MSTR";
        var s = string.Format(CultureInfo.InvariantCulture, "{0} {1}", dir, Func.Name(Prm));
        if (Prm)
        {
            if (Fcv)
            {
                s += string.Format(CultureInfo.InvariantCulture, " FCB={0} FCV", Fcb ? 1 : 0);
            }
        }
        else if (Fcv)
        {
            s += " DFC";
        }

        return s;
    }
}

/// <summary>The fixed part of a link frame.</summary>
/// <param name="Control">The control octet.</param>
/// <param name="Dest">The destination link address.</param>
/// <param name="Src">The source link address.</param>
/// <param name="Length">The LEN octet: five plus the payload size.</param>
public readonly record struct LinkHeader(Control Control, ushort Dest, ushort Src, byte Length)
{
    /// <summary>The payload octet count the header declares.</summary>
    public int PayloadLen => Length - LinkConstants.MinLength;
}

/// <summary>A decoded link frame.</summary>
internal readonly struct LinkFrame
{
    /// <summary>The fixed header.</summary>
    public LinkHeader Header { get; init; }

    /// <summary>
    /// The reassembled user data with block CRCs stripped. It aliases the
    /// caller's buffer when decoded by <see cref="FrameCodec.Decode"/>; copy it
    /// if it must outlive that buffer.
    /// </summary>
    public ReadOnlyMemory<byte> Payload { get; init; }

    /// <inheritdoc/>
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "[{0}] {1}→{2} len={3} payload={4}B",
        Header.Control, Header.Src, Header.Dest, Header.Length, Payload.Length);
}

/// <summary>
/// Why a decode failed. Returned rather than thrown because the parser
/// classifies these per octet while resynchronising, where exceptions would
/// dominate the cost of the receive path.
/// </summary>
public enum LinkDecodeStatus
{
    /// <summary>The frame decoded cleanly.</summary>
    Ok = 0,

    /// <summary>
    /// The buffer holds fewer octets than the frame needs. Not a protocol
    /// violation: the caller should read more.
    /// </summary>
    ShortFrame,

    /// <summary>The buffer does not begin with 0x0564.</summary>
    BadStart,

    /// <summary>The LEN octet is outside 5..255.</summary>
    BadLength,

    /// <summary>The header CRC did not verify.</summary>
    HeaderCrc,

    /// <summary>A body block CRC did not verify.</summary>
    BodyCrc,

    /// <summary>An encode was asked for more than 250 octets.</summary>
    PayloadTooLong,
}

/// <summary>Naming and exception mapping for <see cref="LinkDecodeStatus"/>.</summary>
public static class LinkDecodeStatusExtensions
{
    /// <summary>Renders the status as the equivalent Go sentinel error text.</summary>
    public static string ToDisplayString(this LinkDecodeStatus status) => status switch
    {
        LinkDecodeStatus.Ok => "link: ok",
        LinkDecodeStatus.ShortFrame => "link: incomplete frame",
        LinkDecodeStatus.BadStart => "link: bad start octets",
        LinkDecodeStatus.BadLength => "link: length out of range",
        LinkDecodeStatus.HeaderCrc => "link: header CRC mismatch",
        LinkDecodeStatus.BodyCrc => "link: body CRC mismatch",
        LinkDecodeStatus.PayloadTooLong => "link: payload exceeds 250 octets",
        _ => "link: unknown",
    };

    /// <summary>Wraps the status as the exception the public API surfaces.</summary>
    public static MalformedException ToException(this LinkDecodeStatus status, string? detail = null) =>
        new(detail is null
            ? status.ToDisplayString()
            : string.Format(CultureInfo.InvariantCulture, "{0}: {1}", status.ToDisplayString(), detail));
}

/// <summary>Encodes and decodes link frames.</summary>
internal static class FrameCodec
{
    /// <summary>
    /// Encodes a complete frame into <paramref name="dst"/> and returns the
    /// octet count written.
    /// </summary>
    /// <remarks>
    /// The header CRC and every block CRC are computed here, so callers never
    /// handle CRCs directly.
    /// </remarks>
    public static LinkDecodeStatus TryEncode(
        Span<byte> dst,
        LinkHeader header,
        ReadOnlySpan<byte> payload,
        out int written)
    {
        written = 0;
        if (payload.Length > LinkConstants.MaxPayload)
        {
            return LinkDecodeStatus.PayloadTooLong;
        }

        var total = LinkConstants.FrameSize(payload.Length);
        if (dst.Length < total)
        {
            return LinkDecodeStatus.ShortFrame;
        }

        dst[0] = LinkConstants.StartByte0;
        dst[1] = LinkConstants.StartByte1;
        dst[2] = (byte)(LinkConstants.MinLength + payload.Length);
        dst[3] = header.Control.ToByte();
        BinaryPrimitives.WriteUInt16LittleEndian(dst[4..6], header.Dest);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[6..8], header.Src);
        Crc.Write(dst[8..10], dst[..8]);

        var pos = LinkConstants.HeaderSize;
        for (var off = 0; off < payload.Length; off += LinkConstants.BlockSize)
        {
            var end = Math.Min(off + LinkConstants.BlockSize, payload.Length);
            var block = payload[off..end];
            block.CopyTo(dst[pos..]);
            pos += block.Length;
            pos += Crc.Write(dst[pos..], block);
        }

        written = pos;
        return LinkDecodeStatus.Ok;
    }

    /// <summary>
    /// Encodes a complete frame into a newly allocated array.
    /// </summary>
    public static byte[] Encode(LinkHeader header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > LinkConstants.MaxPayload)
        {
            throw LinkDecodeStatus.PayloadTooLong.ToException(
                string.Format(CultureInfo.InvariantCulture, "{0} octets", payload.Length));
        }

        var buffer = new byte[LinkConstants.FrameSize(payload.Length)];
        var status = TryEncode(buffer, header, payload, out var written);
        if (status != LinkDecodeStatus.Ok)
        {
            throw status.ToException();
        }

        return written == buffer.Length ? buffer : buffer[..written];
    }

    /// <summary>
    /// Parses the fixed header from <paramref name="buf"/> and verifies its
    /// CRC.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="LinkDecodeStatus.ShortFrame"/> when
    /// <paramref name="buf"/> holds fewer than
    /// <see cref="LinkConstants.HeaderSize"/> octets, which callers treat as
    /// "read more" rather than as an error.
    /// </remarks>
    public static LinkDecodeStatus DecodeHeader(ReadOnlySpan<byte> buf, out LinkHeader header)
    {
        header = default;
        if (buf.Length < LinkConstants.HeaderSize)
        {
            return LinkDecodeStatus.ShortFrame;
        }

        if (buf[0] != LinkConstants.StartByte0 || buf[1] != LinkConstants.StartByte1)
        {
            return LinkDecodeStatus.BadStart;
        }

        if (!Crc.IsValid(buf[..8], buf[8..10]))
        {
            return LinkDecodeStatus.HeaderCrc;
        }

        var length = buf[2];
        if (length < LinkConstants.MinLength)
        {
            return LinkDecodeStatus.BadLength;
        }

        header = new LinkHeader(
            Control: Control.Parse(buf[3]),
            Dest: BinaryPrimitives.ReadUInt16LittleEndian(buf[4..6]),
            Src: BinaryPrimitives.ReadUInt16LittleEndian(buf[6..8]),
            Length: length);
        return LinkDecodeStatus.Ok;
    }

    /// <summary>
    /// Parses one complete frame from the front of <paramref name="buf"/>,
    /// writing the reassembled payload into <paramref name="payloadBuf"/>.
    /// </summary>
    /// <returns>
    /// The decode status. On <see cref="LinkDecodeStatus.Ok"/>,
    /// <paramref name="consumed"/> holds the octet count the frame occupied and
    /// <paramref name="payloadLength"/> the octets written to
    /// <paramref name="payloadBuf"/>.
    /// </returns>
    public static LinkDecodeStatus Decode(
        ReadOnlySpan<byte> buf,
        Span<byte> payloadBuf,
        out LinkHeader header,
        out int consumed,
        out int payloadLength)
    {
        consumed = 0;
        payloadLength = 0;

        var status = DecodeHeader(buf, out header);
        if (status != LinkDecodeStatus.Ok)
        {
            return status;
        }

        var payloadLen = header.PayloadLen;
        var total = LinkConstants.FrameSize(payloadLen);
        if (buf.Length < total)
        {
            return LinkDecodeStatus.ShortFrame;
        }

        if (payloadBuf.Length < payloadLen)
        {
            return LinkDecodeStatus.ShortFrame;
        }

        var body = buf[LinkConstants.HeaderSize..total];
        var written = 0;
        for (var off = 0; off < payloadLen; off += LinkConstants.BlockSize)
        {
            var n = Math.Min(LinkConstants.BlockSize, payloadLen - off);
            var block = body[..n];
            if (!Crc.IsValid(block, body[n..(n + LinkConstants.CrcSize)]))
            {
                return LinkDecodeStatus.BodyCrc;
            }

            block.CopyTo(payloadBuf[written..]);
            written += n;
            body = body[(n + LinkConstants.CrcSize)..];
        }

        consumed = total;
        payloadLength = written;
        return LinkDecodeStatus.Ok;
    }
}
