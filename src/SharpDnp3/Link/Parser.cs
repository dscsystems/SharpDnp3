// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

namespace SharpDnp3.Link;

/// <summary>
/// Counts what the parser saw.
/// </summary>
/// <remarks>
/// Every discard reason gets its own counter because "the link is flaky" is
/// not an actionable diagnosis — knowing whether the losses are CRC errors,
/// bad lengths, or garbage between frames is.
/// </remarks>
public struct LinkStats
{
    /// <summary>Frames that decoded cleanly.</summary>
    public ulong FramesDecoded;

    /// <summary>Octets thrown away while resynchronising.</summary>
    public ulong BytesDiscarded;

    /// <summary>Frames rejected because the header CRC failed.</summary>
    public ulong HeaderCrcErrors;

    /// <summary>Frames rejected because a body block CRC failed.</summary>
    public ulong BodyCrcErrors;

    /// <summary>Frames rejected because the LEN octet was out of range.</summary>
    public ulong BadLength;

    /// <summary>Times the parser hunted forward for a delimiter.</summary>
    public ulong Resyncs;
}

/// <summary>Turns a byte stream into frames.</summary>
/// <remarks>
/// <para>
/// It is resynchronizing: a corrupted frame costs the frames it overlaps, not
/// the connection. On any framing failure the parser discards one octet, scans
/// forward to the next 0x0564, and tries again — which is what lets a stack
/// survive line noise, a peer that half-closes mid-frame, or a device that
/// emits a malformed frame every few thousand messages.
/// </para>
/// <para>
/// The parser allocates once, at construction. Buffered octets live in a fixed
/// array addressed by read and write offsets; decoding slides them to the
/// front rather than reallocating, so a session running for months holds
/// steady.
/// </para>
/// <para>
/// A parser is not safe for concurrent use. One belongs to one connection.
/// </para>
/// </remarks>
internal sealed class FrameParser
{
    /// <summary>
    /// Holds two maximum frames, so a read that straddles a frame boundary
    /// never forces a slide mid-frame.
    /// </summary>
    private const int ParserBufSize = LinkConstants.MaxFrameSize * 2;

    private static ReadOnlySpan<byte> Delimiter =>
        [LinkConstants.StartByte0, LinkConstants.StartByte1];

    private readonly byte[] _buf = new byte[ParserBufSize];
    private int _r;
    private int _w;

    /// <summary>
    /// Backs the frame returned by <see cref="TryNext"/>, so decoding does not
    /// allocate. The returned frame aliases it and is valid only until the next
    /// call.
    /// </summary>
    private readonly byte[] _payload = new byte[LinkConstants.MaxPayload];

    private LinkStats _stats;

    /// <summary>Returns a snapshot of the parser's counters.</summary>
    public LinkStats Stats => _stats;

    /// <summary>The number of unconsumed octets held by the parser.</summary>
    public int Buffered => _w - _r;

    /// <summary>How many octets <see cref="Write"/> can accept without discarding.</summary>
    public int Free => _buf.Length - Buffered;

    /// <summary>Appends received octets.</summary>
    /// <returns>
    /// The octet count accepted. A short count means the buffer is full; drain
    /// the complete frames with <see cref="TryNext"/> and write the remainder.
    /// </returns>
    /// <remarks>
    /// The parser refuses octets rather than dropping them because a link layer
    /// that discards silently produces the worst class of field bug: frames
    /// that vanish with nothing in the logs to say why.
    /// </remarks>
    public int Write(ReadOnlySpan<byte> b)
    {
        // Only the tail of the array is writable, so reclaim the consumed
        // prefix before measuring room. Comparing against total free space
        // instead would let the copy below truncate silently.
        if (b.Length > _buf.Length - _w)
        {
            Slide();
        }

        var n = Math.Min(b.Length, _buf.Length - _w);
        b[..n].CopyTo(_buf.AsSpan(_w));
        _w += n;
        return n;
    }

    /// <summary>Decodes the next frame from the buffered octets.</summary>
    /// <returns>
    /// <see langword="false"/> when more input is required, which is the normal
    /// way the parser reports "read more from the socket" and is not a protocol
    /// error.
    /// </returns>
    /// <remarks>
    /// The returned frame's payload aliases the parser's internal buffer and is
    /// invalidated by the next call; copy it if it must outlive that.
    /// </remarks>
    public bool TryNext(out LinkFrame frame)
    {
        while (true)
        {
            var buf = _buf.AsSpan(_r, _w - _r);

            if (buf.Length < LinkConstants.HeaderSize)
            {
                frame = default;
                return false;
            }

            if (buf[0] != LinkConstants.StartByte0 || buf[1] != LinkConstants.StartByte1)
            {
                Resync();
                continue;
            }

            var status = FrameCodec.Decode(
                buf, _payload, out var header, out var consumed, out var payloadLength);

            switch (status)
            {
                case LinkDecodeStatus.Ok:
                    _r += consumed;
                    _stats.FramesDecoded++;
                    frame = new LinkFrame
                    {
                        Header = header,
                        Payload = _payload.AsMemory(0, payloadLength),
                    };
                    return true;

                case LinkDecodeStatus.ShortFrame:
                    // The frame is well-formed so far but incomplete. Anything
                    // up to MaxFrameSize might still arrive, so wait rather
                    // than resync.
                    frame = default;
                    return false;

                case LinkDecodeStatus.HeaderCrc:
                    _stats.HeaderCrcErrors++;
                    break;

                case LinkDecodeStatus.BodyCrc:
                    _stats.BodyCrcErrors++;
                    break;

                case LinkDecodeStatus.BadLength:
                    _stats.BadLength++;
                    break;

                default:
                    break;
            }

            // Drop the leading octet so the resync scan cannot re-match the
            // delimiter it just rejected, then hunt for the next one.
            _r++;
            _stats.BytesDiscarded++;
            Resync();
        }
    }

    /// <summary>Discards octets up to the next 0x0564 delimiter.</summary>
    private void Resync()
    {
        _stats.Resyncs++;

        var buf = _buf.AsSpan(_r, _w - _r);
        var i = buf.IndexOf(Delimiter);
        if (i < 0)
        {
            // No delimiter in what we hold. Keep a trailing 0x05, since its
            // 0x64 may be in the next read, and discard everything before it.
            var keep = buf.Length > 0 && buf[^1] == LinkConstants.StartByte0 ? 1 : 0;
            _stats.BytesDiscarded += (ulong)(buf.Length - keep);
            _r = _w - keep;
        }
        else if (i > 0)
        {
            _stats.BytesDiscarded += (ulong)i;
            _r += i;
        }

        Slide();
    }

    /// <summary>
    /// Moves buffered octets to the front of the backing array, reclaiming the
    /// space consumed frames left behind. It copies at most one frame's worth.
    /// </summary>
    private void Slide()
    {
        if (_r == 0)
        {
            return;
        }

        var n = _w - _r;
        _buf.AsSpan(_r, n).CopyTo(_buf);
        _w = n;
        _r = 0;
    }

    /// <summary>
    /// Forces a discard of the leading octet followed by a resync. Used when
    /// the buffer is full of octets that will never form a frame.
    /// </summary>
    private void DropAndResync()
    {
        _r++;
        _stats.BytesDiscarded++;
        Resync();
    }

    /// <summary>
    /// Reads <paramref name="stream"/> to exhaustion, invoking
    /// <paramref name="onFrame"/> for each decoded frame. It returns when the
    /// stream reports end-of-file and propagates anything else.
    /// </summary>
    /// <remarks>
    /// The frame passed to <paramref name="onFrame"/> is only valid for the
    /// duration of the call.
    /// </remarks>
    public async Task DrainAsync(
        Stream stream,
        Func<LinkFrame, ValueTask> onFrame,
        CancellationToken cancellationToken = default)
    {
        var chunk = new byte[LinkConstants.MaxFrameSize];
        while (true)
        {
            var n = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return;
            }

            // Writing at most one frame's worth and draining after each write
            // keeps the buffer from filling, but loop anyway rather than assume
            // it: a short write that went unnoticed would corrupt the stream.
            var pending = chunk.AsMemory(0, n);
            while (!pending.IsEmpty)
            {
                var written = Write(pending.Span);
                pending = pending[written..];

                while (TryNext(out var frame))
                {
                    await onFrame(frame).ConfigureAwait(false);
                }

                if (written == 0 && Buffered == _buf.Length)
                {
                    // The buffer is full of octets that will never form a
                    // frame. Drop the leading octet and resync rather than
                    // spin.
                    DropAndResync();
                }
            }
        }
    }
}
