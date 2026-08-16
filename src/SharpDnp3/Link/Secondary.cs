// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

namespace SharpDnp3.Link;

/// <summary>What a <see cref="Secondary"/> wants done with a received frame.</summary>
internal readonly struct SecResult
{
    /// <summary>
    /// A frame to transmit, or <see langword="null"/> when the frame needs no
    /// answer.
    /// </summary>
    public LinkFrame? Reply { get; init; }

    /// <summary>
    /// User data to pass to the transport function, or empty. It aliases the
    /// input frame's payload.
    /// </summary>
    public ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>
    /// Set when a frame carried user data that was dropped. The usual cause is
    /// a frame count bit mismatch, meaning the peer retransmitted something
    /// already accepted, which is correct behaviour rather than an error — but
    /// worth counting.
    /// </summary>
    public bool Discarded { get; init; }

    /// <summary>Reports whether the result carries user data.</summary>
    public bool HasPayload => !Payload.IsEmpty;
}

/// <summary>
/// The receiving half of the link layer: the side that answers
/// RESET_LINK_STATES, validates the frame count bit on confirmed user data,
/// and hands accepted payloads up to the transport function.
/// </summary>
/// <remarks>
/// It holds no timers and performs no I/O. Feed it frames, transmit whatever
/// reply it returns, and pass whatever payload it accepts upward.
/// </remarks>
internal sealed class Secondary
{
    /// <summary>
    /// This station's link address, used as the source of replies.
    /// </summary>
    public ushort LocalAddr { get; set; }

    /// <summary>Sets the DIR bit on replies.</summary>
    public bool IsMaster { get; set; }

    private bool _reset;
    private bool _expectFcb;

    /// <summary>
    /// Returns the secondary to its unreset state. A session calls this when
    /// the underlying connection is re-established, because link state does not
    /// survive a socket.
    /// </summary>
    public void Reset()
    {
        _reset = false;
        _expectFcb = false;
    }

    /// <summary>
    /// Reports whether the link has been reset by the peer, which is the
    /// precondition for accepting confirmed user data.
    /// </summary>
    public bool IsReset => _reset;

    /// <summary>Processes a frame addressed to this station.</summary>
    /// <remarks>
    /// Frames that are not primary messages are ignored — those are replies
    /// meant for the <see cref="Primary"/> half and are routed there by the
    /// session.
    /// </remarks>
    public SecResult OnFrame(LinkFrame f)
    {
        if (!f.Header.Control.Prm)
        {
            return default;
        }

        var src = f.Header.Src;
        switch (f.Header.Control.Func)
        {
            case LinkFunction.ResetLinkStates:
                // The peer is establishing the link. Its first confirmed frame
                // will carry FCB=1, so that is what we expect next.
                _reset = true;
                _expectFcb = true;
                return new SecResult { Reply = BuildReply(LinkFunction.Ack, src) };

            case LinkFunction.TestLinkStates:
                if (!_reset)
                {
                    return new SecResult { Reply = BuildReply(LinkFunction.Nack, src) };
                }

                if (f.Header.Control.Fcb != _expectFcb)
                {
                    return new SecResult { Reply = BuildReply(LinkFunction.Nack, src) };
                }

                _expectFcb = !_expectFcb;
                return new SecResult { Reply = BuildReply(LinkFunction.Ack, src) };

            case LinkFunction.ConfirmedUserData:
                if (!_reset)
                {
                    // Confirmed data before a reset is a protocol error on the
                    // peer's side. NACK tells it to reset the link and start
                    // over.
                    return new SecResult
                    {
                        Reply = BuildReply(LinkFunction.Nack, src),
                        Discarded = true,
                    };
                }

                if (f.Header.Control.Fcb != _expectFcb)
                {
                    // A retransmission of a frame we already accepted: our ACK
                    // was lost, not the data. Re-ACK and drop the duplicate
                    // payload, without touching the expected FCB.
                    return new SecResult
                    {
                        Reply = BuildReply(LinkFunction.Ack, src),
                        Discarded = true,
                    };
                }

                _expectFcb = !_expectFcb;
                return new SecResult
                {
                    Reply = BuildReply(LinkFunction.Ack, src),
                    Payload = f.Payload,
                };

            case LinkFunction.UnconfirmedUserData:
                // No link-layer handshake, no frame count bit, no reply.
                return new SecResult { Payload = f.Payload };

            case LinkFunction.RequestLinkStatus:
                return new SecResult { Reply = BuildReply(LinkFunction.LinkStatus, src) };

            default:
                return new SecResult { Reply = BuildReply(LinkFunction.NotSupported, src) };
        }
    }

    /// <summary>Builds a secondary-to-primary frame back to <paramref name="dest"/>.</summary>
    private LinkFrame BuildReply(LinkFunction fn, ushort dest) => new()
    {
        Header = new LinkHeader(
            Control: new Control(
                Dir: IsMaster,
                Prm: false,
                Fcb: false,
                // DFC is left clear: this implementation never asks a peer to
                // stop sending, because the transport function above it always
                // has somewhere to put a frame.
                Fcv: false,
                Func: fn),
            Dest: dest,
            Src: LocalAddr,
            Length: LinkConstants.MinLength),
        Payload = ReadOnlyMemory<byte>.Empty,
    };
}
