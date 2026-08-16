// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Globalization;

namespace SharpDnp3.Link;

/// <summary>Tells a session what the <see cref="Primary"/> state machine wants next.</summary>
internal enum LinkAction : byte
{
    /// <summary>
    /// Nothing to do; the frame was not for this half, or was absorbed without
    /// changing state.
    /// </summary>
    None = 0,

    /// <summary>
    /// Transmit the returned frame and arm the response timer. The session must
    /// call <see cref="Primary.OnTimeout"/> if the timer expires.
    /// </summary>
    Transmit,

    /// <summary>
    /// The queued payload was delivered and acknowledged. Any pending timer
    /// should be cancelled.
    /// </summary>
    Complete,

    /// <summary>
    /// The transmission failed after exhausting retries. The link is left
    /// unreset so the next send re-establishes it.
    /// </summary>
    Failed,
}

/// <summary>Naming helpers for <see cref="LinkAction"/>.</summary>
internal static class LinkActionExtensions
{
    /// <summary>Renders the action using the protocol tools' spelling.</summary>
    public static string ToDisplayString(this LinkAction action) => action switch
    {
        LinkAction.None => "none",
        LinkAction.Transmit => "transmit",
        LinkAction.Complete => "complete",
        LinkAction.Failed => "failed",
        _ => "Action(?)",
    };
}

/// <summary>The state of the primary half's handshake.</summary>
internal enum PrimaryState : byte
{
    /// <summary>Nothing in flight.</summary>
    Idle = 0,

    /// <summary>A RESET_LINK_STATES is awaiting its ACK.</summary>
    WaitLinkReset,

    /// <summary>Confirmed user data is awaiting its ACK.</summary>
    WaitConfirm,

    /// <summary>A REQUEST_LINK_STATUS is awaiting its reply.</summary>
    WaitStatus,
}

/// <summary>Naming helpers for <see cref="PrimaryState"/>.</summary>
internal static class PrimaryStateExtensions
{
    /// <summary>Renders the state using the protocol tools' spelling.</summary>
    public static string ToDisplayString(this PrimaryState state) => state switch
    {
        PrimaryState.Idle => "idle",
        PrimaryState.WaitLinkReset => "wait-link-reset",
        PrimaryState.WaitConfirm => "wait-confirm",
        PrimaryState.WaitStatus => "wait-status",
        _ => "priState(?)",
    };
}

/// <summary>The transmitting half of the link layer.</summary>
/// <remarks>
/// <para>
/// With confirmations enabled it runs the handshake the standard requires:
/// reset the link, then send each frame with an alternating frame count bit
/// and wait for an ACK, retransmitting on timeout. With confirmations disabled
/// it is a thin pass-through, which is the normal configuration over TCP where
/// the transport already guarantees ordered delivery.
/// </para>
/// <para>
/// It owns no timer. The session arms one when the primary returns
/// <see cref="LinkAction.Transmit"/> and calls <see cref="OnTimeout"/> when it
/// fires.
/// </para>
/// </remarks>
internal sealed class Primary
{
    /// <summary>This station's link address.</summary>
    public ushort LocalAddr { get; set; }

    /// <summary>The peer's link address.</summary>
    public ushort RemoteAddr { get; set; }

    /// <summary>Sets the DIR bit on transmitted frames.</summary>
    public bool IsMaster { get; set; }

    /// <summary>
    /// Enables the confirmed handshake. Over TCP this is normally
    /// <see langword="false"/>; over serial it is normally
    /// <see langword="true"/>.
    /// </summary>
    public bool UseConfirms { get; set; }

    /// <summary>
    /// How many times a frame is retransmitted after a timeout before the
    /// transmission fails.
    /// </summary>
    public int MaxRetries { get; set; }

    private PrimaryState _state;
    private bool _linkUp;
    private bool _fcb;
    private int _retries;
    private byte[]? _pending;
    private LinkFrame _lastSent;
    private bool _dfc;

    /// <summary>
    /// Returns the primary to its initial state and drops any queued payload. A
    /// session calls this when the connection is re-established.
    /// </summary>
    public void Reset()
    {
        _state = PrimaryState.Idle;
        _linkUp = false;
        _fcb = false;
        _retries = 0;
        _pending = null;
        _dfc = false;
    }

    /// <summary>
    /// Reports whether a transmission is in flight. Callers must not call
    /// <see cref="Send"/> while it is <see langword="true"/>.
    /// </summary>
    public bool Busy => _state != PrimaryState.Idle;

    /// <summary>Reports whether the link has been reset and confirmed.</summary>
    public bool LinkUp => _linkUp;

    /// <summary>
    /// Reports whether the peer last signalled DFC, meaning its buffers are
    /// full and user data must not be sent until it clears.
    /// </summary>
    public bool DataFlowControl => _dfc;

    /// <summary>The current handshake state.</summary>
    internal PrimaryState State => _state;

    /// <summary>How many times the in-flight frame has been retransmitted.</summary>
    public int Retries => _retries;

    /// <summary>
    /// Queues <paramref name="payload"/> for transmission and returns the first
    /// frame to put on the wire.
    /// </summary>
    /// <remarks>
    /// When confirmations are disabled the returned action is
    /// <see cref="LinkAction.Complete"/> alongside the frame: transmit it and
    /// consider the send finished. When they are enabled the action is
    /// <see cref="LinkAction.Transmit"/>, and the send is not finished until a
    /// later call returns <see cref="LinkAction.Complete"/> or
    /// <see cref="LinkAction.Failed"/>.
    /// </remarks>
    public (LinkFrame Frame, LinkAction Action) Send(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.Length > LinkConstants.MaxPayload)
        {
            throw LinkDecodeStatus.PayloadTooLong.ToException(
                string.Format(CultureInfo.InvariantCulture, "{0} octets", payload.Length));
        }

        if (Busy)
        {
            throw new Dnp3Exception(string.Format(
                CultureInfo.InvariantCulture,
                "link: primary busy in state {0}",
                _state.ToDisplayString()));
        }

        _retries = 0;

        if (!UseConfirms)
        {
            var f = BuildFrame(LinkFunction.UnconfirmedUserData, fcb: false, fcv: false, payload);
            _lastSent = f;
            return (f, LinkAction.Complete);
        }

        _pending = payload;
        if (!_linkUp)
        {
            _state = PrimaryState.WaitLinkReset;
            var f = BuildFrame(LinkFunction.ResetLinkStates, fcb: false, fcv: false, null);
            _lastSent = f;
            return (f, LinkAction.Transmit);
        }

        return SendPending();
    }

    /// <summary>
    /// Builds a keep-alive frame. The session sends it on an idle link to
    /// detect a peer that has gone away without closing the connection.
    /// </summary>
    public (LinkFrame Frame, LinkAction Action) RequestLinkStatus()
    {
        if (Busy)
        {
            throw new Dnp3Exception(string.Format(
                CultureInfo.InvariantCulture,
                "link: primary busy in state {0}",
                _state.ToDisplayString()));
        }

        _state = PrimaryState.WaitStatus;
        _retries = 0;
        var f = BuildFrame(LinkFunction.RequestLinkStatus, fcb: false, fcv: false, null);
        _lastSent = f;
        return (f, LinkAction.Transmit);
    }

    /// <summary>Emits the queued payload as confirmed user data.</summary>
    private (LinkFrame Frame, LinkAction Action) SendPending()
    {
        _state = PrimaryState.WaitConfirm;
        var f = BuildFrame(LinkFunction.ConfirmedUserData, _fcb, fcv: true, _pending);
        _lastSent = f;
        return (f, LinkAction.Transmit);
    }

    /// <summary>Processes a reply from the peer's secondary station.</summary>
    /// <remarks>
    /// Frames that are primary messages are ignored: those belong to the
    /// <see cref="Secondary"/> half and are routed there by the session.
    /// </remarks>
    public (LinkFrame Frame, LinkAction Action) OnFrame(LinkFrame f)
    {
        if (f.Header.Control.Prm)
        {
            return (default, LinkAction.None);
        }

        _dfc = f.Header.Control.Dfc;

        switch (_state)
        {
            case PrimaryState.WaitLinkReset:
                switch (f.Header.Control.Func)
                {
                    case LinkFunction.Ack:
                        // The peer's secondary has reset. Its expected frame
                        // count bit is now 1, so ours must start there too.
                        _linkUp = true;
                        _fcb = true;
                        _retries = 0;
                        return SendPending();

                    case LinkFunction.Nack:
                    case LinkFunction.NotSupported:
                        return Fail();

                    default:
                        return (default, LinkAction.None);
                }

            case PrimaryState.WaitConfirm:
                switch (f.Header.Control.Func)
                {
                    case LinkFunction.Ack:
                        _fcb = !_fcb;
                        _state = PrimaryState.Idle;
                        _pending = null;
                        _retries = 0;
                        return (default, LinkAction.Complete);

                    case LinkFunction.Nack:
                        // The peer says its link is not reset. Start the
                        // handshake again rather than retrying the data frame,
                        // which would only be NACKed once more.
                        _linkUp = false;
                        _state = PrimaryState.WaitLinkReset;
                        _retries = 0;
                        var next = BuildFrame(LinkFunction.ResetLinkStates, fcb: false, fcv: false, null);
                        _lastSent = next;
                        return (next, LinkAction.Transmit);

                    case LinkFunction.NotSupported:
                        return Fail();

                    default:
                        return (default, LinkAction.None);
                }

            case PrimaryState.WaitStatus:
                switch (f.Header.Control.Func)
                {
                    // LinkStatus (11) and Ack (0) both close out the probe.
                    case LinkFunction.LinkStatus:
                    case LinkFunction.Ack:
                        _state = PrimaryState.Idle;
                        _retries = 0;
                        return (default, LinkAction.Complete);

                    case LinkFunction.Nack:
                    case LinkFunction.NotSupported:
                        return Fail();

                    default:
                        return (default, LinkAction.None);
                }

            default:
                return (default, LinkAction.None);
        }
    }

    /// <summary>
    /// Called by the session when the response timer expires. It retransmits
    /// the last frame until <see cref="MaxRetries"/> is exhausted.
    /// </summary>
    public (LinkFrame Frame, LinkAction Action) OnTimeout()
    {
        if (_state == PrimaryState.Idle)
        {
            return (default, LinkAction.None);
        }

        if (_retries >= MaxRetries)
        {
            return Fail();
        }

        _retries++;

        // Retransmit verbatim: the frame count bit must not advance, or the
        // peer will treat the retry as new data.
        return (_lastSent, LinkAction.Transmit);
    }

    /// <summary>
    /// Abandons the transmission and tears down link state, so the next
    /// <see cref="Send"/> re-runs the reset handshake.
    /// </summary>
    private (LinkFrame Frame, LinkAction Action) Fail()
    {
        _state = PrimaryState.Idle;
        _linkUp = false;
        _pending = null;
        _retries = 0;
        return (default, LinkAction.Failed);
    }

    private LinkFrame BuildFrame(LinkFunction fn, bool fcb, bool fcv, byte[]? payload)
    {
        var length = LinkConstants.MinLength + (payload?.Length ?? 0);
        return new LinkFrame
        {
            Header = new LinkHeader(
                Control: new Control(
                    Dir: IsMaster,
                    Prm: true,
                    Fcb: fcb,
                    Fcv: fcv,
                    Func: fn),
                Dest: RemoteAddr,
                Src: LocalAddr,
                Length: (byte)length),
            Payload = payload ?? ReadOnlyMemory<byte>.Empty,
        };
    }
}
