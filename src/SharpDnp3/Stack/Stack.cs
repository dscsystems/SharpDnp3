// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Couples the link and transport layers to a byte stream.
//
// A master and an outstation differ entirely at the application layer and
// barely at all below it: both frame fragments, both reassemble them, both
// answer link-layer requests. That shared plumbing lives here so neither
// session has to reimplement it, and so the two cannot drift apart.
//
// A Stack is not safe for concurrent use, and is deliberately not made so.
// Sessions call it only from their own loop; their read task hands raw octets
// across a channel rather than calling in directly. Locking the stack instead
// would make it possible to interleave a send with the processing of an
// inbound frame, and the link state machines are not interleavable — a frame
// count bit advanced from two places is a lost or duplicated fragment.

using SharpDnp3.Link;
using SharpDnp3.Transport;

namespace SharpDnp3.Stack;

/// <summary>Where the stack puts the octets it wants transmitted.</summary>
/// <remarks>
/// The stack performs no I/O of its own. A session hands it a sink, drains
/// whatever accumulated, and writes that to the socket — which keeps the
/// blocking, the tasks and the timers in the session, where they can be
/// cancelled.
/// </remarks>
internal interface IByteSink
{
    /// <summary>Accepts octets to transmit.</summary>
    void Write(ReadOnlySpan<byte> data);
}

/// <summary>A <see cref="IByteSink"/> that accumulates into a growable buffer.</summary>
internal sealed class BufferSink : IByteSink
{
    private byte[] _buf = new byte[LinkConstants.MaxFrameSize];

    /// <summary>The octets written since the last <see cref="Clear"/>.</summary>
    public int Length { get; private set; }

    /// <summary>The pending octets.</summary>
    public ReadOnlyMemory<byte> Pending => _buf.AsMemory(0, Length);

    /// <summary>Reports whether anything is waiting to be transmitted.</summary>
    public bool IsEmpty => Length == 0;

    /// <inheritdoc/>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (Length + data.Length > _buf.Length)
        {
            Array.Resize(ref _buf, Math.Max(Length + data.Length, _buf.Length * 2));
        }

        data.CopyTo(_buf.AsSpan(Length));
        Length += data.Length;
    }

    /// <summary>Discards the pending octets, keeping the buffer.</summary>
    public void Clear() => Length = 0;
}

/// <summary>Parameterises a stack.</summary>
internal sealed class StackConfig
{
    /// <summary>This station's link address.</summary>
    public ushort LocalAddr { get; set; }

    /// <summary>The peer's link address.</summary>
    public ushort RemoteAddr { get; set; }

    /// <summary>
    /// Sets the direction bit and selects which side answers what.
    /// </summary>
    public bool IsMaster { get; set; }

    /// <summary>
    /// Enables link-layer confirmation. Over TCP this is normally off, since
    /// the transport already guarantees ordered delivery; over serial it is
    /// normally on.
    /// </summary>
    public bool UseConfirms { get; set; }

    /// <summary>How many times a confirmed frame is retransmitted.</summary>
    public int MaxRetries { get; set; }

    /// <summary>Caps a reassembled application fragment.</summary>
    public int MaxRxFragment { get; set; }
}

/// <summary>What one completed fragment looks like.</summary>
internal readonly struct Received
{
    /// <summary>
    /// A completed application fragment. It aliases the stack's reassembly
    /// buffer and is valid only for the duration of the callback.
    /// </summary>
    public ReadOnlyMemory<byte> Fragment { get; init; }

    /// <summary>The link address the fragment came from.</summary>
    public ushort Source { get; init; }

    /// <summary>
    /// The link address it was sent to, which may be a broadcast address. An
    /// outstation must answer a broadcast without echoing it.
    /// </summary>
    public ushort Dest { get; init; }

    /// <summary>Reports whether <see cref="Dest"/> was a broadcast address.</summary>
    public bool Broadcast { get; init; }
}

/// <summary>Owns the link and transport state for one connection.</summary>
internal sealed class ProtocolStack
{
    /// <summary>The size a session's read loop should use.</summary>
    public const int ReadChunk = LinkConstants.MaxFrameSize;

    private readonly StackConfig _cfg;

    private FrameParser _parser;
    private readonly Primary _pri;
    private readonly Secondary _sec;
    private readonly Segmenter _seg = new();
    private readonly Reassembler _reasm;

    /// <summary>
    /// Set while a confirmed frame is unacknowledged.
    /// </summary>
    private bool _awaiting;

    /// <summary>The address the fragment in flight is going to.</summary>
    private ushort _dest;

    // txFrame and txSeg are reused across sends so a steady poll loop does not
    // allocate. They are single-loop state, like everything else here.
    private readonly byte[] _txFrame = new byte[LinkConstants.MaxFrameSize];
    private readonly byte[] _txSeg = new byte[TransportConstants.MaxSegmentSize];

    /// <summary>
    /// A separate buffer for link-layer replies, so answering an inbound frame
    /// cannot overwrite a fragment mid-transmission.
    /// </summary>
    private readonly byte[] _rxFrame = new byte[LinkConstants.MaxFrameSize];

    /// <summary>Creates a stack for one connection.</summary>
    public ProtocolStack(StackConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);

        if (cfg.MaxRxFragment <= 0)
        {
            cfg.MaxRxFragment = TransportConstants.DefaultMaxFragment;
        }

        _cfg = cfg;
        _parser = new FrameParser();
        _reasm = new Reassembler(cfg.MaxRxFragment);
        _pri = new Primary
        {
            LocalAddr = cfg.LocalAddr,
            RemoteAddr = cfg.RemoteAddr,
            IsMaster = cfg.IsMaster,
            UseConfirms = cfg.UseConfirms,
            MaxRetries = cfg.MaxRetries,
        };
        _sec = new Secondary
        {
            LocalAddr = cfg.LocalAddr,
            IsMaster = cfg.IsMaster,
        };
        _dest = cfg.RemoteAddr;
    }

    /// <summary>
    /// Clears all link and transport state, as when a connection is
    /// re-established. A fragment cannot span a connection and link state does
    /// not survive one.
    /// </summary>
    public void Reset()
    {
        _parser = new FrameParser();
        _reasm.Reset();
        _pri.Reset();
        _sec.Reset();
        _awaiting = false;
        _seg.Clear();
    }

    /// <summary>Returns the link and transport counters.</summary>
    public (LinkStats Link, TransportStats Transport) Stats => (_parser.Stats, _reasm.Stats);

    /// <summary>
    /// Reports whether a confirmed frame is awaiting acknowledgement.
    /// </summary>
    /// <remarks>
    /// A session arms its link timer while this is <see langword="true"/> and
    /// calls <see cref="OnTimeout"/> when it fires. With confirmations disabled
    /// it is never <see langword="true"/>.
    /// </remarks>
    public bool Pending => _awaiting;

    /// <summary>
    /// Reports whether a fragment is still being transmitted, either awaiting a
    /// confirmation or with segments still to send.
    /// </summary>
    public bool Busy => _awaiting || _seg.Pending;

    /// <summary>Begins transmitting an application fragment.</summary>
    /// <remarks>
    /// With confirmations disabled every segment goes out immediately and the
    /// call completes the fragment. With them enabled only the first frame is
    /// sent; <see cref="Pending"/> then reports <see langword="true"/> until the
    /// peer acknowledges it, and the remaining segments follow as each
    /// acknowledgement arrives.
    /// </remarks>
    public void Send(IByteSink sink, byte[] fragment) =>
        SendTo(sink, _cfg.RemoteAddr, fragment);

    /// <summary>
    /// Frames a fragment addressed to a specific station, overriding the
    /// configured remote address.
    /// </summary>
    /// <remarks>
    /// An outstation needs this: it must answer whichever master addressed it,
    /// which is not necessarily the one in its configuration, and a broadcast
    /// request must be answered to the real source rather than to the broadcast
    /// address it arrived on.
    /// </remarks>
    public void SendTo(IByteSink sink, ushort dest, byte[] fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        if (fragment.Length == 0)
        {
            throw new Dnp3Exception("stack: refusing to send an empty fragment");
        }

        if (Busy)
        {
            throw new Dnp3Exception("stack: a fragment is already in flight");
        }

        _dest = dest;
        _seg.Reset(fragment);
        Pump(sink);
    }

    /// <summary>
    /// Sends segments until one needs acknowledging or the fragment is done.
    /// </summary>
    private void Pump(IByteSink sink)
    {
        while (_seg.Pending)
        {
            if (_pri.DataFlowControl)
            {
                // The peer's last reply said its buffers are full: what is left
                // of this fragment stays queued rather than being sent through
                // that, or lost by consuming it here anyway. Sending resumes on
                // its own once some later reply clears DFC — Drain calls Pump
                // again after every LinkAction.Complete, including the ACK to a
                // SendLinkStatusRequest probe, whose own gate deliberately lets
                // it go out while paused like this so there is something to
                // elicit that later reply in the first place.
                return;
            }

            if (!_seg.TryNext(_txSeg, out var segLen))
            {
                break;
            }

            // The segment aliases _txSeg rather than being copied out of it.
            // Nothing overwrites _txSeg until the next TryNext, and that only
            // happens once this segment has been acknowledged — the loop
            // returns below while a confirmed frame is in flight — so the
            // primary's retransmission copy stays valid for as long as it is
            // needed.
            var segment = _txSeg.AsMemory(0, segLen);

            var saved = _pri.RemoteAddr;
            _pri.RemoteAddr = _dest;
            LinkFrame f;
            LinkAction action;
            try
            {
                (f, action) = _pri.Send(segment);
            }
            finally
            {
                _pri.RemoteAddr = saved;
            }

            if (action == LinkAction.Failed)
            {
                throw new Dnp3Exception("stack: link refused the segment");
            }

            WriteFrame(sink, _txFrame, f);

            if (action == LinkAction.Transmit)
            {
                // A confirmed frame: nothing more goes out until the peer
                // answers.
                _awaiting = true;
                return;
            }
        }

        _awaiting = false;
    }

    /// <summary>Called by the session when its link timer expires.</summary>
    /// <remarks>
    /// It retransmits the unacknowledged frame, or gives up once the retry
    /// budget is spent.
    /// </remarks>
    /// <returns>
    /// Whether the transmission failed for good, which the session surfaces as
    /// a failed request rather than a silent stall.
    /// </returns>
    public bool OnTimeout(IByteSink sink)
    {
        if (!_awaiting)
        {
            return false;
        }

        var (f, action) = _pri.OnTimeout();
        switch (action)
        {
            case LinkAction.Transmit:
                WriteFrame(sink, _txFrame, f);
                return false;

            case LinkAction.Failed:
                _awaiting = false;
                _seg.Clear();
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Feeds received octets, invoking <paramref name="onFragment"/> for each
    /// completed application fragment.
    /// </summary>
    /// <remarks>
    /// Link-layer replies — acknowledgements, link status — are written to
    /// <paramref name="sink"/> as they are produced, and an acknowledgement of
    /// our own confirmed frame releases the next segment.
    /// </remarks>
    public void Receive(IByteSink sink, ReadOnlySpan<byte> data, Action<Received> onFragment)
    {
        while (!data.IsEmpty)
        {
            var n = _parser.Write(data);
            data = data[n..];

            Drain(sink, onFragment);

            if (n == 0)
            {
                throw new Dnp3Exception("stack: link: parser buffer full");
            }
        }
    }

    private void Drain(IByteSink sink, Action<Received> onFragment)
    {
        // TryNext returning false is the ordinary case: the buffered octets do
        // not yet make a frame, so there is nothing to do until more arrive.
        // The parser resyncs past corruption itself.
        while (_parser.TryNext(out var f))
        {
            if (!AddressedToUs(f.Header.Dest))
            {
                continue;
            }

            // The destination says the frame is ours to look at; the source
            // says whether it can have come from anywhere at all. An address no
            // station may hold cannot be a sender, and a reply goes back to
            // whatever source it was given — so answering one would put that
            // impossible address on the wire as a destination.
            //
            // This belongs here rather than in the parser: a frame like this is
            // exactly what an operator wants the decoder to show them, so it is
            // dropped where the protocol is acted on, not where it is read.
            if (!LinkConstants.IsValidSource(f.Header.Src))
            {
                continue;
            }

            if (f.Header.Control.Prm)
            {
                var res = _sec.OnFrame(f);
                if (res.Reply is { } reply)
                {
                    // A separate buffer from the transmit path: answering an
                    // inbound frame must not disturb a fragment in flight.
                    WriteFrame(sink, _rxFrame, reply);
                }

                if (res.HasPayload)
                {
                    Deliver(f, res.Payload.Span, onFragment);
                }

                continue;
            }

            // A secondary frame answers something we sent as primary, and must
            // come from the station we are actually exchanging with — _dest,
            // the address the fragment in flight was sent to — or it could
            // complete or advance an exchange it has nothing to do with, forged
            // or merely misrouted from some other station on the line.
            if (f.Header.Src != _dest)
            {
                continue;
            }

            var (next, action) = _pri.OnFrame(f);
            switch (action)
            {
                case LinkAction.Transmit:
                    WriteFrame(sink, _txFrame, next);
                    break;

                case LinkAction.Complete:
                    _awaiting = false;
                    Pump(sink);
                    break;

                case LinkAction.Failed:
                    _awaiting = false;
                    _seg.Clear();
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Runs a segment through reassembly and reports any completed fragment.
    /// </summary>
    private void Deliver(LinkFrame f, ReadOnlySpan<byte> payload, Action<Received> onFragment)
    {
        var res = _reasm.Accept(payload);
        if (!res.Complete)
        {
            return;
        }

        onFragment(new Received
        {
            Fragment = res.Fragment,
            Source = f.Header.Src,
            Dest = f.Header.Dest,
            Broadcast = LinkConstants.IsBroadcast(f.Header.Dest),
        });
    }

    /// <summary>Reports whether a frame is ours to process.</summary>
    private bool AddressedToUs(ushort dest) =>
        dest == _cfg.LocalAddr || LinkConstants.IsBroadcast(dest);

    /// <summary>Encodes a frame into <paramref name="buf"/> and sends it.</summary>
    private static void WriteFrame(IByteSink sink, byte[] buf, LinkFrame f)
    {
        var status = FrameCodec.TryEncode(buf, f.Header, f.Payload.Span, out var written);
        if (status != LinkDecodeStatus.Ok)
        {
            throw status.ToException();
        }

        sink.Write(buf.AsSpan(0, written));
    }

    /// <summary>Sends a keep-alive.</summary>
    /// <remarks>
    /// An idle TCP connection tells you nothing: a peer that has gone away, or
    /// a firewall that has quietly dropped the flow, looks exactly like a peer
    /// with nothing to report. Asking for link status is how a session finds
    /// out before the next poll is due.
    /// </remarks>
    public void SendLinkStatusRequest(IByteSink sink)
    {
        if (_awaiting)
        {
            // A confirmed frame is genuinely in flight; do not interleave.
            //
            // This gates on _awaiting rather than Busy deliberately. A fragment
            // paused mid-way by data flow control leaves segments queued, so
            // Busy stays set — and the probe is exactly what has to go out to
            // elicit the reply that clears DFC and lets those segments move.
            return;
        }

        var (f, action) = _pri.RequestLinkStatus();
        if (action != LinkAction.Transmit)
        {
            return;
        }

        _awaiting = true;
        WriteFrame(sink, _txFrame, f);
    }
}
