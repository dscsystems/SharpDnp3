// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Everything an outstation holds per attached master.
//
// One outstation is one device: one point database, one clock, one set of
// controls. It is not one conversation. A substation with a control centre and
// an engineering workstation on the same RTU has two masters polling the same
// device, and almost every piece of protocol state between them is private —
// the application sequence number, the events each has yet to acknowledge, the
// select-before-operate reservation, the indications each has yet to be told.
//
// Splitting that state out of the session is what makes serving several masters
// a matter of running the same loop several times rather than a special case
// threaded through every handler.

using SharpDnp3.Channels;
using SharpDnp3.Stack;

namespace SharpDnp3.Outstation;

/// <summary>One master's conversation with the outstation.</summary>
internal sealed class Association
{
    /// <summary>Distinguishes the association in the log.</summary>
    public required int Id { get; init; }

    /// <summary>The connection this master arrived on.</summary>
    public required Stream Connection { get; init; }

    /// <summary>The link and transport state for that connection.</summary>
    public required ProtocolStack Stack { get; init; }

    /// <summary>Where the stack puts octets waiting to be transmitted.</summary>
    public BufferSink Sink { get; } = new();

    /// <summary>
    /// This master's event queue. Selection and confirmation are per-master, so
    /// each gets its own copy of the event stream.
    /// </summary>
    public required EventBuffer Events { get; init; }

    /// <summary>The log, scoped to this association.</summary>
    public required IDnp3Logger Log { get; init; }

    /// <summary>A description of the far end, for the log.</summary>
    public required string Peer { get; init; }

    /// <summary>
    /// The link address to send unsolicited responses to.
    /// </summary>
    /// <remarks>
    /// A solicited response goes back to whoever asked, which the request
    /// carries. An unsolicited one has no request to answer, so it starts at
    /// the configured master address and moves to whatever address this
    /// connection's master actually uses once it says something. Without that,
    /// an outstation with two masters would send both of their unsolicited
    /// responses to the one in its configuration.
    /// </remarks>
    public ushort RemoteAddr;

    /// <summary>
    /// Set once this master has said something, which is when
    /// <see cref="RemoteAddr"/> stops being a guess.
    /// </summary>
    public bool RemoteKnown;

    /// <summary>
    /// The indications owed to this master. They are per-master because they
    /// report what happened to <em>its</em> requests, and because the restart
    /// indication is cleared by the master that has finished acting on it.
    /// </summary>
    public Iin Iin;

    /// <summary>The classes this master has enabled for unsolicited reporting.</summary>
    public Class UnsolClasses;

    /// <summary>Set while a response to this master awaits its confirmation.</summary>
    public bool AwaitingConfirm;

    /// <summary>The sequence number of the unconfirmed response.</summary>
    public byte ConfirmSeq;

    /// <summary>When the unconfirmed response gives up waiting.</summary>
    public DateTimeOffset ConfirmDeadline;

    /// <summary>When an unacknowledged link frame should be retried.</summary>
    public DateTimeOffset LinkDeadline;

    /// <summary>
    /// This master's select-before-operate reservation. Keeping it private to
    /// the association is what stops one master operating on another's select.
    /// </summary>
    public Selection Sel { get; } = new();

    /// <summary>This master's unsolicited reporting state.</summary>
    public UnsolState Unsol { get; } = new();

    /// <summary>
    /// When this master's RECORD_CURRENT_TIME arrived, which it reads back to
    /// work out the transit delay.
    /// </summary>
    public DateTimeOffset? RecordedTime;

    /// <summary>
    /// Gates unsolicited reporting: it is set for as long as the connection is
    /// being served.
    /// </summary>
    public bool Connected;

    /// <summary>
    /// Set from outside the association's loop when the device restarts, and
    /// acted on by that loop at its next tick.
    /// </summary>
    /// <remarks>
    /// A restart is a device-wide event raised from whatever thread called
    /// <see cref="OutstationSession.Restart"/>, but the indication it produces
    /// belongs to each master separately. Latching a flag here keeps every
    /// mutation of association state on the association's own loop.
    /// </remarks>
    public volatile bool RestartPending;

    /// <summary>Names the far end of a connection as well as it can be named.</summary>
    public static string Describe(Stream conn, IChannel channel) =>
        (conn as IPeerEndpoint)?.Peer ?? channel.ToString() ?? "unknown";
}
