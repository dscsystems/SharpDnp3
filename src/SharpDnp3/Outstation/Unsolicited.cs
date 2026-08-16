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
    /// It runs on the association's tick, which is what lets the hold time and
    /// the confirm timeout be enforced without a timer per event. Each master
    /// enables its own classes and acknowledges its own responses, so this runs
    /// once per attached master against that master's own event queue.
    /// </remarks>
    private void PollUnsolicited(Association a, DateTimeOffset now)
    {
        if (!_cfg.Unsolicited.Enabled || !a.Connected)
        {
            return;
        }

        if (_cfg.MaxMasters > 1 && !a.RemoteKnown)
        {
            // With one master the configured address is the master's address,
            // so the announcement can go out the moment the connection is up.
            // With several it is a guess that is wrong for all but one of them,
            // and an announcement sent to the wrong link address is not
            // received at all — so this waits until the master has said
            // something and named itself.
            return;
        }

        // An unconfirmed response is either still in its window or has run out.
        if (a.Unsol.Awaiting)
        {
            if (now < a.Unsol.Deadline)
            {
                return;
            }

            OnUnsolicitedTimeout(a, now);
            return;
        }

        // The null unsolicited response comes first and comes before any data.
        //
        // Its job is to tell a master that has just connected — or reconnected
        // to an outstation that restarted — that this outstation exists and is
        // asserting DEVICE_RESTART, without gambling event data on a session
        // the master may not be ready for.
        if (!a.Unsol.NullConfirmed)
        {
            if (!a.Unsol.NullSent || now > a.Unsol.NextAllowed)
            {
                SendUnsolicited(a, [], now, isNull: true);
            }

            return;
        }

        if (a.UnsolClasses == 0 || now < a.Unsol.NextAllowed)
        {
            return;
        }

        var pending = a.Events.Count(a.UnsolClasses);
        if (pending == 0)
        {
            return;
        }

        // Hold briefly so a burst of changes becomes one response, unless
        // enough events have piled up that waiting no longer helps.
        if (_cfg.Unsolicited.HoldTime > TimeSpan.Zero)
        {
            a.Unsol.FirstEventAt ??= now;

            var enough = _cfg.Unsolicited.MaxEvents > 0 && pending >= _cfg.Unsolicited.MaxEvents;
            if (!enough && now - a.Unsol.FirstEventAt.Value < _cfg.Unsolicited.HoldTime)
            {
                return;
            }
        }

        a.Unsol.FirstEventAt = null;

        var events = a.Events.Select(a.UnsolClasses, 64);
        if (events.Count == 0)
        {
            return;
        }

        SendUnsolicited(a, events, now, isNull: false);
    }

    /// <summary>Retries or gives up on an unconfirmed response.</summary>
    private void OnUnsolicitedTimeout(Association a, DateTimeOffset now)
    {
        a.Unsol.Awaiting = false;
        a.Unsol.Retries++;

        lock (_gate)
        {
            _stats.UnsolicitedTimeouts++;
        }

        // The events go back in the queue whether or not we retry. If we give
        // up, the master's next poll collects them — losing them because
        // unsolicited delivery failed would defeat the point of the
        // confirmation.
        var requeued = a.Events.Unselect();

        if (a.Unsol.Retries > _cfg.Unsolicited.MaxRetries)
        {
            a.Log.Log(
                Dnp3LogLevel.Warn,
                "giving up on unsolicited reporting until the master polls",
                ("retries", a.Unsol.Retries), ("events_requeued", requeued));

            a.Unsol.Retries = 0;
            a.Unsol.NextAllowed = now + _cfg.Unsolicited.ConfirmTimeout;
            return;
        }

        a.Log.Log(
            Dnp3LogLevel.Debug,
            "unsolicited response unconfirmed; retrying",
            ("attempt", a.Unsol.Retries), ("events_requeued", requeued));
    }

    /// <summary>Transmits one unsolicited response.</summary>
    private void SendUnsolicited(
        Association a,
        IReadOnlyList<Event> events,
        DateTimeOffset now,
        bool isNull)
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

        a.Unsol.Seq = (byte)((a.Unsol.Seq + 1) % AppConstants.SeqModulus);

        var frag = new List<byte>(AppConstants.ResponseHeaderSize + body.Length);
        HeaderCodec.AppendHeader(frag, new AppHeader(
            new AppControl(Fir: true, Fin: true, Con: true, Uns: true, Seq: a.Unsol.Seq),
            FuncCode.UnsolicitedResponse,
            CurrentIin(a)));
        frag.AddRange(body);

        try
        {
            // Addressed rather than sent to the configured master: with several
            // attached, each one's unsolicited responses have to go to its own
            // link address.
            a.Stack.SendTo(a.Sink, a.RemoteAddr, [.. frag]);
        }
        catch (Dnp3Exception ex)
        {
            // The events are still selected; the retry path requeues them.
            a.Log.Log(Dnp3LogLevel.Warn, "unsolicited transmission failed", ("err", ex.Message));
            return;
        }

        a.Unsol.Awaiting = true;
        a.Unsol.AwaitSeq = a.Unsol.Seq;
        a.Unsol.Deadline = now + _cfg.Unsolicited.ConfirmTimeout;
        a.Unsol.NullSent = a.Unsol.NullSent || isNull;

        lock (_gate)
        {
            _stats.UnsolicitedSent++;
        }

        a.Log.Log(
            Dnp3LogLevel.Debug,
            "unsolicited response sent",
            ("seq", a.Unsol.Seq), ("events", events.Count), ("null", isNull));
    }

    /// <summary>Handles a confirmation of an unsolicited response.</summary>
    private void OnUnsolicitedConfirm(Association a, AppHeader h)
    {
        if (!a.Unsol.Awaiting || h.Control.Seq != a.Unsol.AwaitSeq)
        {
            a.Log.Log(
                Dnp3LogLevel.Debug,
                "unexpected unsolicited confirm",
                ("seq", h.Control.Seq), ("awaiting", a.Unsol.Awaiting));
            return;
        }

        a.Unsol.Awaiting = false;
        a.Unsol.Retries = 0;

        if (!a.Unsol.NullConfirmed)
        {
            // The master has acknowledged our existence; data may now flow.
            a.Unsol.NullConfirmed = true;
            a.Log.Log(Dnp3LogLevel.Debug, "null unsolicited response confirmed");
            return;
        }

        var n = a.Events.Confirm();
        a.Log.Log(Dnp3LogLevel.Debug, "unsolicited events confirmed", ("count", n));
    }
}
