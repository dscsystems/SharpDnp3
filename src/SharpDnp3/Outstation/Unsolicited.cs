// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using SharpDnp3.App;
using SharpDnp3.Objects;

namespace SharpDnp3.Outstation;

/// <summary>The outstation's unsolicited reporting state.</summary>
/// <remarks>
/// Unsolicited responses have their own sequence space, separate from the
/// solicited one, and their own confirmation: an outstation that mixed the two
/// would confirm a poll with an event acknowledgement and drop data.
/// </remarks>
internal sealed class UnsolState
{
    /// <summary>The next unsolicited sequence number to use.</summary>
    public byte Seq;

    /// <summary>Records that the initial null unsolicited response has been sent.</summary>
    public bool NullSent;

    /// <summary>Records that the master answered the null response.</summary>
    public bool NullConfirmed;

    /// <summary>Set while a response is unconfirmed.</summary>
    public bool Awaiting;

    /// <summary>The sequence number of the unconfirmed response.</summary>
    public byte AwaitSeq;

    /// <summary>When the unconfirmed response gives up waiting.</summary>
    public DateTimeOffset Deadline;

    /// <summary>Counts consecutive unconfirmed attempts.</summary>
    public int Retries;

    /// <summary>
    /// The earliest a further unsolicited response may be sent, which is how a
    /// device that has given up retrying backs off.
    /// </summary>
    public DateTimeOffset NextAllowed;

    /// <summary>
    /// When the oldest unreported event appeared, which starts the hold-time
    /// clock. It is null when nothing is waiting.
    /// </summary>
    public DateTimeOffset? FirstEventAt;

    /// <summary>Returns the state to what it is after a restart.</summary>
    public void Reset()
    {
        Seq = 0;
        NullSent = false;
        NullConfirmed = false;
        Awaiting = false;
        AwaitSeq = 0;
        Deadline = default;
        Retries = 0;
        NextAllowed = default;
        FirstEventAt = null;
    }
}

/// <summary>Paces unsolicited reporting.</summary>
public sealed class UnsolicitedConfig
{
    /// <summary>
    /// Allows the outstation to send unsolicited responses at all.
    /// </summary>
    /// <remarks>
    /// The master still has to enable the individual classes with
    /// ENABLE_UNSOLICITED; this is the device-level switch that says the
    /// outstation is capable of it.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// How long to wait after an event before transmitting, so a burst of
    /// changes becomes one response rather than twenty. Zero sends as soon as
    /// an event appears.
    /// </summary>
    public TimeSpan HoldTime { get; set; }

    /// <summary>
    /// Transmits as soon as this many events are queued, regardless of the hold
    /// time. Zero means no threshold.
    /// </summary>
    public int MaxEvents { get; set; }

    /// <summary>
    /// How long to wait for the master's confirmation before retrying.
    /// </summary>
    public TimeSpan ConfirmTimeout { get; set; }

    /// <summary>
    /// How many times an unconfirmed response is re-sent before the outstation
    /// gives up and waits for the master to poll instead.
    /// </summary>
    public int MaxRetries { get; set; }

    internal void ApplyDefaults()
    {
        if (ConfirmTimeout <= TimeSpan.Zero)
        {
            ConfirmTimeout = TimeSpan.FromSeconds(5);
        }

        if (MaxRetries <= 0)
        {
            MaxRetries = 3;
        }
    }
}

public sealed partial class OutstationSession
{
    /// <summary>
    /// Decides whether to transmit an unsolicited response and does so if the
    /// moment is right.
    /// </summary>
    /// <remarks>
    /// It runs on the session's tick, which is what lets the hold time and the
    /// confirm timeout be enforced without a timer per event.
    /// </remarks>
    private void PollUnsolicited(DateTimeOffset now)
    {
        if (!_cfg.Unsolicited.Enabled || !_connected)
        {
            return;
        }

        // An unconfirmed response is either still in its window or has run out.
        if (_unsol.Awaiting)
        {
            if (now < _unsol.Deadline)
            {
                return;
            }

            OnUnsolicitedTimeout(now);
            return;
        }

        // The null unsolicited response comes first and comes before any data.
        //
        // Its job is to tell a master that has just connected — or reconnected
        // to an outstation that restarted — that this outstation exists and is
        // asserting DEVICE_RESTART, without gambling event data on a session
        // the master may not be ready for.
        if (!_unsol.NullConfirmed)
        {
            if (!_unsol.NullSent || now > _unsol.NextAllowed)
            {
                SendUnsolicited([], now, isNull: true);
            }

            return;
        }

        if (_unsolClasses == 0 || now < _unsol.NextAllowed)
        {
            return;
        }

        var pending = _db.Events?.Count(_unsolClasses) ?? 0;
        if (pending == 0)
        {
            return;
        }

        // Hold briefly so a burst of changes becomes one response, unless
        // enough events have piled up that waiting no longer helps.
        if (_cfg.Unsolicited.HoldTime > TimeSpan.Zero)
        {
            _unsol.FirstEventAt ??= now;

            var enough = _cfg.Unsolicited.MaxEvents > 0 && pending >= _cfg.Unsolicited.MaxEvents;
            if (!enough && now - _unsol.FirstEventAt.Value < _cfg.Unsolicited.HoldTime)
            {
                return;
            }
        }

        _unsol.FirstEventAt = null;

        var events = _db.Events?.Select(_unsolClasses, 64) ?? [];
        if (events.Count == 0)
        {
            return;
        }

        SendUnsolicited(events, now, isNull: false);
    }

    /// <summary>Retries or gives up on an unconfirmed response.</summary>
    private void OnUnsolicitedTimeout(DateTimeOffset now)
    {
        _unsol.Awaiting = false;
        _unsol.Retries++;

        lock (_gate)
        {
            _stats.UnsolicitedTimeouts++;
        }

        // The events go back in the queue whether or not we retry. If we give
        // up, the master's next poll collects them — losing them because
        // unsolicited delivery failed would defeat the point of the
        // confirmation.
        var requeued = _db.Events?.Unselect() ?? 0;

        if (_unsol.Retries > _cfg.Unsolicited.MaxRetries)
        {
            _log.Log(
                Dnp3LogLevel.Warn,
                "giving up on unsolicited reporting until the master polls",
                ("retries", _unsol.Retries), ("events_requeued", requeued));

            _unsol.Retries = 0;
            _unsol.NextAllowed = now + _cfg.Unsolicited.ConfirmTimeout;
            return;
        }

        _log.Log(
            Dnp3LogLevel.Debug,
            "unsolicited response unconfirmed; retrying",
            ("attempt", _unsol.Retries), ("events_requeued", requeued));
    }

    /// <summary>Transmits one unsolicited response.</summary>
    private void SendUnsolicited(IReadOnlyList<Event> events, DateTimeOffset now, bool isNull)
    {
        var ctx = new Context { Synchronized = _synchronized };
        var b = new ResponseBuilder(_cfg.MaxTxFragment, ctx);
        if (!isNull)
        {
            _writer.BuildEvents(b, events);
        }

        var bodies = b.Done();

        // An unsolicited response is a single fragment. If the events do not
        // fit, the rest stay queued for the next one rather than being split
        // across a series the master would have to reassemble without having
        // asked for it.
        var body = bodies[0];

        _unsol.Seq = (byte)((_unsol.Seq + 1) % AppConstants.SeqModulus);

        var frag = new List<byte>(AppConstants.ResponseHeaderSize + body.Length);
        HeaderCodec.AppendHeader(frag, new AppHeader(
            new AppControl(Fir: true, Fin: true, Con: true, Uns: true, Seq: _unsol.Seq),
            FuncCode.UnsolicitedResponse,
            CurrentIin()));
        frag.AddRange(body);

        try
        {
            _stack!.Send(_sink, [.. frag]);
        }
        catch (Dnp3Exception ex)
        {
            // The events are still selected; the retry path requeues them.
            _log.Log(Dnp3LogLevel.Warn, "unsolicited transmission failed", ("err", ex.Message));
            return;
        }

        _unsol.Awaiting = true;
        _unsol.AwaitSeq = _unsol.Seq;
        _unsol.Deadline = now + _cfg.Unsolicited.ConfirmTimeout;
        _unsol.NullSent = _unsol.NullSent || isNull;

        lock (_gate)
        {
            _stats.UnsolicitedSent++;
        }

        _log.Log(
            Dnp3LogLevel.Debug,
            "unsolicited response sent",
            ("seq", _unsol.Seq), ("events", events.Count), ("null", isNull));
    }

    /// <summary>Handles a confirmation of an unsolicited response.</summary>
    private void OnUnsolicitedConfirm(AppHeader h)
    {
        if (!_unsol.Awaiting || h.Control.Seq != _unsol.AwaitSeq)
        {
            _log.Log(
                Dnp3LogLevel.Debug,
                "unexpected unsolicited confirm",
                ("seq", h.Control.Seq), ("awaiting", _unsol.Awaiting));
            return;
        }

        _unsol.Awaiting = false;
        _unsol.Retries = 0;

        if (!_unsol.NullConfirmed)
        {
            // The master has acknowledged our existence; data may now flow.
            _unsol.NullConfirmed = true;
            _log.Log(Dnp3LogLevel.Debug, "null unsolicited response confirmed");
            return;
        }

        var n = _db.Events?.Confirm() ?? 0;
        _log.Log(Dnp3LogLevel.Debug, "unsolicited events confirmed", ("count", n));
    }
}
