// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Buffers.Binary;
using System.Threading.Channels;
using SharpDnp3.App;
using SharpDnp3.Channels;
using SharpDnp3.Objects;
using SharpDnp3.Stack;

namespace SharpDnp3.Outstation;

/// <summary>Parameterises an outstation session.</summary>
public sealed class OutstationConfig
{
    /// <summary>This outstation's link address.</summary>
    public ushort LocalAddr { get; set; }

    /// <summary>The master's link address.</summary>
    public ushort RemoteAddr { get; set; }

    /// <summary>Sizes the point database.</summary>
    public DatabaseConfig Database { get; set; } = new();

    /// <summary>Sizes the event buffer.</summary>
    public EventBufferConfig Events { get; set; } = new();

    /// <summary>
    /// Caps a response fragment. Zero uses the standard's 2048.
    /// </summary>
    public int MaxTxFragment { get; set; }

    /// <summary>Caps a received request fragment.</summary>
    public int MaxRxFragment { get; set; }

    /// <summary>
    /// How long to wait for an application confirmation before returning the
    /// selected events to the queue for re-sending.
    /// </summary>
    public TimeSpan ConfirmTimeout { get; set; }

    /// <summary>
    /// How long a select-before-operate reservation stays valid. Zero uses five
    /// seconds, which is the conventional default.
    /// </summary>
    public TimeSpan SelectTimeout { get; set; }

    /// <summary>Paces unsolicited reporting.</summary>
    public UnsolicitedConfig Unsolicited { get; set; } = new();

    /// <summary>
    /// Enables link-layer confirmation, normally off over TCP.
    /// </summary>
    public bool UseLinkConfirms { get; set; }

    /// <summary>How many times a confirmed frame is retransmitted.</summary>
    public int LinkRetries { get; set; }

    /// <summary>
    /// How long to wait for a link-layer acknowledgement before retransmitting.
    /// It matters only when <see cref="UseLinkConfirms"/> is set.
    /// </summary>
    public TimeSpan LinkTimeout { get; set; }

    /// <summary>Receives protocol and session events.</summary>
    public IDnp3Logger? Log { get; set; }

    internal void ApplyDefaults()
    {
        if (MaxTxFragment <= 0)
        {
            MaxTxFragment = AppConstants.DefaultMaxFragment;
        }

        if (MaxRxFragment <= 0)
        {
            MaxRxFragment = AppConstants.DefaultMaxFragment;
        }

        if (ConfirmTimeout <= TimeSpan.Zero)
        {
            ConfirmTimeout = TimeSpan.FromSeconds(5);
        }

        if (SelectTimeout <= TimeSpan.Zero)
        {
            SelectTimeout = TimeSpan.FromSeconds(5);
        }

        if (LinkTimeout <= TimeSpan.Zero)
        {
            LinkTimeout = TimeSpan.FromSeconds(1);
        }

        Unsolicited.ApplyDefaults();
        Log ??= NullDnp3Logger.Instance;
    }
}

/// <summary>
/// The hook an outstation implementation provides for the behaviour the stack
/// cannot decide on its own.
/// </summary>
/// <remarks>
/// Every method has a usable default in <see cref="NopApplication"/>, so an
/// implementer overrides only what it cares about.
/// </remarks>
public interface IOutstationApplication
{
    /// <summary>
    /// Returns the outstation's idea of the current time. Tests supply a
    /// virtual clock through this.
    /// </summary>
    DateTimeOffset Now();

    /// <summary>
    /// Called when a master sets the clock. Returning <see langword="false"/>
    /// rejects the request.
    /// </summary>
    bool WriteAbsoluteTime(DateTimeOffset t);

    /// <summary>
    /// Called for COLD_RESTART. The returned duration is how long the
    /// outstation expects to be unavailable, reported back in a group 52 time
    /// delay.
    /// </summary>
    TimeSpan ColdRestart();

    /// <summary>Called for WARM_RESTART.</summary>
    TimeSpan WarmRestart();

    /// <summary>Reports whether the outstation accepts clock writes.</summary>
    bool SupportsWriteTime();
}

/// <summary>An application with sensible defaults.</summary>
public class NopApplication : IOutstationApplication
{
    /// <inheritdoc/>
    public virtual DateTimeOffset Now() => DateTimeOffset.UtcNow;

    /// <inheritdoc/>
    public virtual bool WriteAbsoluteTime(DateTimeOffset t) => true;

    /// <inheritdoc/>
    public virtual TimeSpan ColdRestart() => TimeSpan.Zero;

    /// <inheritdoc/>
    public virtual TimeSpan WarmRestart() => TimeSpan.Zero;

    /// <inheritdoc/>
    public virtual bool SupportsWriteTime() => true;
}

/// <summary>Counts what a session has done.</summary>
public record struct OutstationStats
{
    /// <summary>Request fragments received.</summary>
    public ulong RequestsReceived;

    /// <summary>Responses sent, counting a multi-fragment series once.</summary>
    public ulong ResponsesSent;

    /// <summary>Response fragments put on the wire.</summary>
    public ulong FragmentsSent;

    /// <summary>Application confirmations received.</summary>
    public ulong ConfirmsReceived;

    /// <summary>Responses the master never confirmed.</summary>
    public ulong ConfirmTimeouts;

    /// <summary>Requests carrying a function code we do not implement.</summary>
    public ulong UnknownFunction;

    /// <summary>Requests that would not parse.</summary>
    public ulong MalformedRequests;

    /// <summary>Connections established.</summary>
    public ulong Connections;

    /// <summary>Controls the handler accepted.</summary>
    public ulong CommandsExecuted;

    /// <summary>Controls the handler refused.</summary>
    public ulong CommandsRejected;

    /// <summary>Unsolicited responses sent.</summary>
    public ulong UnsolicitedSent;

    /// <summary>Unsolicited responses the master never confirmed.</summary>
    public ulong UnsolicitedTimeouts;
}

/// <summary>An outstation.</summary>
/// <remarks>
/// All protocol state lives in the loop started by <see cref="RunAsync"/>.
/// Database updates arrive through <see cref="Update"/>, which is safe to call
/// from anywhere.
/// </remarks>
public sealed partial class OutstationSession
{
    private readonly OutstationConfig _cfg;
    private readonly IOutstationApplication _appl;
    private readonly Database _db;
    private readonly ResponseWriter _writer;
    private readonly IDnp3Logger _log;
    private ProtocolStack? _stack;
    private readonly BufferSink _sink = new();

    /// <summary>
    /// The internal indications the outstation reports. It is touched only from
    /// the session loop.
    /// </summary>
    private Iin _iin;

    /// <summary>
    /// Tracks whether the master has set our clock, which decides the quality
    /// stamped on the timestamps we report.
    /// </summary>
    private bool _synchronized;

    /// <summary>
    /// The mask of classes the master has enabled for unsolicited reporting.
    /// </summary>
    private Class _unsolClasses;

    /// <summary>
    /// Set while a response is awaiting an application confirmation.
    /// </summary>
    private bool _awaitingConfirm;

    private byte _confirmSeq;
    private DateTimeOffset _confirmDeadline;

    /// <summary>The live select-before-operate reservation.</summary>
    private readonly Selection _sel = new();

    /// <summary>Executes the controls themselves.</summary>
    private readonly ICommandHandler _cmds;

    /// <summary>The unsolicited reporting state.</summary>
    private readonly UnsolState _unsol = new();

    /// <summary>
    /// Gates unsolicited reporting: an outstation with no master attached has
    /// nowhere to send.
    /// </summary>
    private bool _connected;

    /// <summary>When an unacknowledged link frame should be retried.</summary>
    private DateTimeOffset _linkDeadline;

    /// <summary>
    /// When the last RECORD_CURRENT_TIME request arrived, which a master reads
    /// back as group 50 variation 3 to work out the transit delay before
    /// setting the clock.
    /// </summary>
    private DateTimeOffset? _recordedTime;

    private readonly Channel<Action<Database>> _updates =
        Channel.CreateBounded<Action<Database>>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
        });

    private readonly Lock _gate = new();
    private OutstationStats _stats;

    /// <summary>Creates an outstation session.</summary>
    /// <remarks>
    /// A null application uses <see cref="NopApplication"/>. A null command
    /// handler uses <see cref="RejectingCommandHandler"/>, which refuses every
    /// control — an outstation whose controls are not wired up must say so
    /// rather than silently report success.
    /// </remarks>
    public OutstationSession(
        OutstationConfig config,
        IOutstationApplication? application = null,
        ICommandHandler? commandHandler = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.ApplyDefaults();
        _cfg = config;
        _appl = application ?? new NopApplication();
        _cmds = commandHandler ?? new RejectingCommandHandler();

        var events = new EventBuffer(config.Events);
        _db = new Database(config.Database, events);
        _writer = new ResponseWriter(_db);
        _log = new ScopedLogger(config.Log!, ("role", "outstation"), ("addr", config.LocalAddr));

        // A fresh outstation reports a restart until the master clears it.
        // Suppressing that would deny the master the one signal that says "my
        // event history is gone, re-poll everything".
        _iin = Iin.DeviceRestart;
    }

    /// <summary>Makes the outstation report a restart to its master.</summary>
    /// <remarks>
    /// It is what a device calls when it has genuinely restarted, and what a
    /// simulator calls to produce the condition on demand. The restart
    /// indication is the only signal that tells a master its whole picture is
    /// stale — the event history is gone, so no incremental poll can recover it
    /// and only a full re-baseline will do.
    /// </remarks>
    public void Restart() => Update(_ =>
    {
        _iin = _iin.Set(Iin.DeviceRestart);
        _synchronized = false;
        _db.Events?.Reset();
        _unsol.Reset();
    });

    /// <summary>
    /// The point database. Prefer <see cref="Update"/> for modifications, which
    /// serialises them with the session loop.
    /// </summary>
    public Database Database => _db;

    /// <summary>The event buffer.</summary>
    public EventBuffer? Events => _db.Events;

    /// <summary>Returns a snapshot of the session counters.</summary>
    public OutstationStats Stats
    {
        get
        {
            lock (_gate)
            {
                return _stats;
            }
        }
    }

    /// <summary>Applies an action to the database from the session loop.</summary>
    /// <remarks>
    /// Batching changes in one call is what makes a set of related updates — a
    /// breaker opening and its alarm asserting — produce one consistent set of
    /// events rather than a torn read.
    /// </remarks>
    public void Update(Action<Database> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!_updates.Writer.TryWrite(action))
        {
            // The queue is full, which means the session is not running or is
            // wedged. Apply directly rather than dropping the update: the
            // database takes its own lock.
            action(_db);
        }
    }

    /// <summary>Connects and serves until the token is cancelled.</summary>
    public async Task RunAsync(IChannel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        _stack = new ProtocolStack(new StackConfig
        {
            LocalAddr = _cfg.LocalAddr,
            RemoteAddr = _cfg.RemoteAddr,
            IsMaster = false,
            UseConfirms = _cfg.UseLinkConfirms,
            MaxRetries = _cfg.LinkRetries,
            MaxRxFragment = _cfg.MaxRxFragment,
        });

        // Cancelling the token is how RunAsync is asked to stop, and a closed
        // channel is the same instruction arriving from the other direction.
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Stream conn;
            try
            {
                conn = await channel.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Channels.ChannelClosedException)
            {
                return;
            }

            lock (_gate)
            {
                _stats.Connections++;
            }

            _log.Log(Dnp3LogLevel.Info, "connected", ("channel", channel.ToString()));

            _stack.Reset();
            try
            {
                await ServeAsync(conn, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                conn.Dispose();
                _log.Log(Dnp3LogLevel.Info, "disconnected");
            }
        }
    }

    /// <summary>Runs one connection until it fails or the token is cancelled.</summary>
    private async Task ServeAsync(Stream conn, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linked.Token;

        // The read loop only moves octets. Everything that touches protocol
        // state runs here, so the stack needs no locking and a response can
        // never interleave with the processing of an inbound frame.
        var rx = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
        {
            SingleWriter = true,
            SingleReader = true,
        });

        var readTask = ReadIntoAsync(conn, rx.Writer, ct);

        _connected = true;
        _unsol.Reset();

        Task<bool>? rxWait = null;
        Task<bool>? updateWait = null;

        try
        {
            // Announce ourselves before servicing anything. The null unsolicited
            // response exists to tell a master "I am here and I have restarted",
            // and waiting for the first tick would let the master's own startup
            // sequence clear the restart indication first — leaving the
            // announcement carrying nothing worth announcing.
            PollUnsolicited(_appl.Now());
            await FlushAsync(conn, ct).ConfigureAwait(false);

            // The tick drives the confirm timeout, the select timeout and the
            // unsolicited hold time, so it has to be short relative to all
            // three.
            var tick = TimeSpan.FromMilliseconds(50);

            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                rxWait ??= rx.Reader.WaitToReadAsync(ct).AsTask();
                updateWait ??= _updates.Reader.WaitToReadAsync(ct).AsTask();

                var delay = Task.Delay(tick, ct);
                var completed = await Task.WhenAny(rxWait, updateWait, delay, readTask)
                    .ConfigureAwait(false);

                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (completed == readTask)
                {
                    return;
                }

                if (completed == updateWait)
                {
                    updateWait = null;
                    while (_updates.Reader.TryRead(out var fn))
                    {
                        fn(_db);
                    }
                }
                else if (completed == rxWait)
                {
                    rxWait = null;
                    while (rx.Reader.TryRead(out var chunk))
                    {
                        try
                        {
                            _stack!.Receive(_sink, chunk, r => Handle(r));
                        }
                        catch (Dnp3Exception ex)
                        {
                            _log.Log(Dnp3LogLevel.Warn, "receive failed", ("err", ex.Message));
                            return;
                        }
                    }
                }
                else
                {
                    var now = _appl.Now();
                    CheckLinkTimeout();
                    CheckConfirmTimeout();
                    CheckSelectTimeout(now);
                    PollUnsolicited(now);
                }

                await FlushAsync(conn, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // A cancelled session is a clean shutdown.
        }
        finally
        {
            _connected = false;
            await linked.CancelAsync().ConfigureAwait(false);
            rx.Writer.TryComplete();

            try
            {
                await readTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Expected when the connection drops or the session stops.
            }
        }
    }

    /// <summary>Moves octets from the connection to the session loop.</summary>
    private static async Task ReadIntoAsync(
        Stream conn,
        ChannelWriter<byte[]> writer,
        CancellationToken cancellationToken)
    {
        var buf = new byte[ProtocolStack.ReadChunk];
        try
        {
            while (true)
            {
                var n = await conn.ReadAsync(buf, cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    return;
                }

                await writer.WriteAsync(buf[..n].ToArray(), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    /// <summary>Writes whatever the stack queued to the connection.</summary>
    private async Task FlushAsync(Stream conn, CancellationToken cancellationToken)
    {
        if (_sink.IsEmpty)
        {
            return;
        }

        var pending = _sink.Pending.ToArray();
        _sink.Clear();
        await conn.WriteAsync(pending, cancellationToken).ConfigureAwait(false);
        await conn.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Retransmits an unacknowledged link frame.</summary>
    private void CheckLinkTimeout()
    {
        if (!_stack!.Pending || _appl.Now() < _linkDeadline)
        {
            return;
        }

        bool failed;
        try
        {
            failed = _stack.OnTimeout(_sink);
        }
        catch (Dnp3Exception ex)
        {
            _log.Log(Dnp3LogLevel.Warn, "link retransmission failed", ("err", ex.Message));
            return;
        }

        _linkDeadline = _appl.Now() + _cfg.LinkTimeout;
        if (failed)
        {
            _log.Log(Dnp3LogLevel.Warn, "link layer gave up on a response");
        }
    }

    /// <summary>
    /// Returns selected events to the queue when the master never confirmed the
    /// response that carried them.
    /// </summary>
    private void CheckConfirmTimeout()
    {
        if (!_awaitingConfirm || _appl.Now() < _confirmDeadline)
        {
            return;
        }

        _awaitingConfirm = false;
        var n = _db.Events?.Unselect() ?? 0;

        lock (_gate)
        {
            _stats.ConfirmTimeouts++;
        }

        _log.Log(
            Dnp3LogLevel.Warn,
            "application confirm timed out; events requeued",
            ("events", n));
    }

    /// <summary>Dispatches one request fragment.</summary>
    private void Handle(Received r)
    {
        lock (_gate)
        {
            _stats.RequestsReceived++;
        }

        var status = FragmentParser.ParseFragment(null, r.Fragment, out var frag, out var error);
        if (status != AppParseStatus.Ok)
        {
            lock (_gate)
            {
                _stats.MalformedRequests++;
            }

            _log.Log(Dnp3LogLevel.Warn, "malformed request", ("err", error));

            // A fragment we cannot parse cannot be answered meaningfully: we do
            // not know its sequence number's validity or what it asked for. The
            // parameter-error indication rides on the next response instead.
            _iin = _iin.Set(Iin.ParameterError);
            return;
        }

        if (r.Broadcast)
        {
            // A broadcast request is executed but never answered — every
            // outstation answering at once would collide. The next response
            // carries the broadcast indication instead.
            _iin = _iin.Set(Iin.Broadcast);
        }

        switch (frag.Header.Func)
        {
            case FuncCode.Confirm:
                // Solicited and unsolicited responses have separate sequence
                // spaces, so the UNS bit decides which one this acknowledges.
                // Confusing them would drop events the master never received.
                if (frag.Header.Control.Uns)
                {
                    OnUnsolicitedConfirm(frag.Header);
                }
                else
                {
                    OnConfirm(frag.Header);
                }

                return;

            case FuncCode.Read:
                OnRead(r, frag);
                return;

            case FuncCode.Write:
                OnWrite(r, frag);
                return;

            case FuncCode.DelayMeasure:
                OnDelayMeasure(r, frag);
                return;

            case FuncCode.RecordCurrentTime:
                OnRecordCurrentTime(r, frag);
                return;

            case FuncCode.ColdRestart:
            case FuncCode.WarmRestart:
                OnRestart(r, frag);
                return;

            case FuncCode.EnableUnsolicited:
            case FuncCode.DisableUnsolicited:
                OnUnsolicitedControl(r, frag);
                return;

            case FuncCode.AssignClass:
                OnAssignClass(r, frag);
                return;

            case FuncCode.Select:
            case FuncCode.Operate:
            case FuncCode.DirectOperate:
            case FuncCode.DirectOperateNR:
                OnCommand(r, frag);
                return;

            case FuncCode.ImmedFreeze:
            case FuncCode.ImmedFreezeNR:
                _db.FreezeCounters();
                if (frag.Header.Func.NoReply() || r.Broadcast)
                {
                    return;
                }

                Respond(r, frag.Header, []);
                return;

            default:
                lock (_gate)
                {
                    _stats.UnknownFunction++;
                }

                _iin = _iin.Set(Iin.NoFuncCodeSupport);
                if (r.Broadcast)
                {
                    return;
                }

                Respond(r, frag.Header, []);
                return;
        }
    }

    /// <summary>Clears the events the confirmed response carried.</summary>
    private void OnConfirm(AppHeader h)
    {
        lock (_gate)
        {
            _stats.ConfirmsReceived++;
        }

        if (!_awaitingConfirm || h.Control.Seq != _confirmSeq)
        {
            // A confirm for a response we are not waiting on. Ignoring it is
            // right: acting on it would drop events the master never received.
            _log.Log(
                Dnp3LogLevel.Debug,
                "unexpected confirm",
                ("seq", h.Control.Seq), ("awaiting", _awaitingConfirm));
            return;
        }

        _awaitingConfirm = false;
        var n = _db.Events?.Confirm() ?? 0;
        _log.Log(Dnp3LogLevel.Debug, "events confirmed", ("count", n));
    }

    /// <summary>Answers a read request.</summary>
    private void OnRead(Received r, Fragment frag)
    {
        var ctx = new Context { Synchronized = _synchronized };
        var b = new ResponseBuilder(_cfg.MaxTxFragment, ctx);

        var selected = new List<Event>();

        foreach (var h in frag.Objects)
        {
            if (h.Group == 60)
            {
                switch (h.Variation)
                {
                    case 1: // class 0: all static data
                        foreach (var staticType in ResponseWriter.StaticTypes)
                        {
                            _writer.BuildStaticRange(b, staticType, 0, 0, 0xFFFF);
                        }

                        break;

                    case 2:
                    case 3:
                    case 4: // event classes 1, 2 and 3
                        var mask = (Class)((byte)Class.Class1 << (h.Variation - 2));
                        selected.AddRange(_db.Events?.Select(mask, 512) ?? []);
                        break;

                    default:
                        break;
                }

                continue;
            }

            if (h.Group == 50 && h.Variation == 3)
            {
                // The second half of the LAN time-sync procedure: hand back the
                // time the RECORD_CURRENT_TIME request arrived.
                if (_recordedTime is not { } recorded)
                {
                    _iin = _iin.Set(Iin.ParameterError);
                    continue;
                }

                var data = new List<byte>(CommandObjects.Time48Size);
                CommandObjects.AppendTime48(data, Timestamp.Now(recorded));

                b.Add(new ObjectHeader
                {
                    Group = 50,
                    Variation = 3,
                    Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.Count8),
                    Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
                    Data = data.ToArray(),
                });
                continue;
            }

            if (!TryPointTypeForGroup(h.Group, out var pt))
            {
                _iin = _iin.Set(Iin.ObjectUnknown);
                continue;
            }

            ushort start = 0;
            ushort stop = 0xFFFF;
            if (h.Range.Spec.IsStartStop())
            {
                start = (ushort)h.Range.Start;
                stop = (ushort)h.Range.Stop;
            }

            _writer.BuildStaticRange(b, pt, h.Variation, start, stop);
        }

        if (selected.Count > 0)
        {
            _writer.BuildEvents(b, selected);
        }

        SendFragments(r, frag.Header, b.Done(), selected.Count > 0);
    }

    /// <summary>Handles the write function code.</summary>
    private void OnWrite(Received r, Fragment frag)
    {
        foreach (var h in frag.Objects)
        {
            if (h.Group == 80 && h.Variation == 1)
            {
                // A master clears DEVICE_RESTART by writing zero to index 7.
                // This is the handshake that ends the restart sequence.
                _iin = _iin.Clear(Iin.DeviceRestart);
                _log.Log(Dnp3LogLevel.Debug, "device restart indication cleared by master");
                continue;
            }

            if (h.Group == 50 && h.Variation == 3)
            {
                // The second half of the LAN time-synchronisation procedure.
                //
                // The master sent RECORD_CURRENT_TIME, we noted when it
                // arrived, and it is now telling us what its own clock read at
                // that moment. The correction is that value plus however long
                // we have taken since — which is what makes this procedure
                // better than a plain clock write: the transit delay is
                // measured rather than assumed.
                if (!_appl.SupportsWriteTime())
                {
                    _iin = _iin.Set(Iin.NoFuncCodeSupport);
                    continue;
                }

                if (_recordedTime is not { } reference)
                {
                    // No RECORD_CURRENT_TIME preceded this, so there is no
                    // reference to correct against.
                    _iin = _iin.Set(Iin.ParameterError);
                    continue;
                }

                if (h.Data.Length < CommandObjects.Time48Size)
                {
                    _iin = _iin.Set(Iin.ParameterError);
                    continue;
                }

                var recorded = CommandObjects.ParseTime48(h.Data.Span);
                var elapsed = _appl.Now() - reference;
                if (_appl.WriteAbsoluteTime(recorded.Time + elapsed))
                {
                    _synchronized = true;
                    _iin = _iin.Clear(Iin.NeedTime);
                    _recordedTime = null;
                    _log.Log(
                        Dnp3LogLevel.Debug,
                        "clock set by the recorded-time procedure",
                        ("recorded_at", recorded.Time), ("elapsed", elapsed));
                }
                else
                {
                    _iin = _iin.Set(Iin.ParameterError);
                }

                continue;
            }

            if (h.Group == 50 && h.Variation == 1)
            {
                if (!_appl.SupportsWriteTime())
                {
                    _iin = _iin.Set(Iin.NoFuncCodeSupport);
                    continue;
                }

                if (h.Data.Length < CommandObjects.Time48Size)
                {
                    _iin = _iin.Set(Iin.ParameterError);
                    continue;
                }

                var ts = CommandObjects.ParseTime48(h.Data.Span);
                if (_appl.WriteAbsoluteTime(ts.Time))
                {
                    _synchronized = true;
                    _iin = _iin.Clear(Iin.NeedTime);
                    _log.Log(Dnp3LogLevel.Debug, "clock set by master", ("time", ts.Time));
                }
                else
                {
                    _iin = _iin.Set(Iin.ParameterError);
                }

                continue;
            }

            if (h.Group == 34)
            {
                WriteDeadbands(h);
                continue;
            }

            _iin = _iin.Set(Iin.ObjectUnknown);
        }

        if (r.Broadcast)
        {
            return;
        }

        Respond(r, frag.Header, []);
    }

    /// <summary>Applies a group 34 analog deadband write.</summary>
    /// <remarks>
    /// A deadband is how a master tells an outstation how much a point must
    /// move before it is worth an event, which is the only lever it has over a
    /// chattering analog short of dropping the point from its class.
    /// </remarks>
    private void WriteDeadbands(ObjectHeader h)
    {
        if (!ObjectRegistry.TryLookup(GroupVar.GV(h.Group, h.Variation), out var d))
        {
            _iin = _iin.Set(Iin.ObjectUnknown);
            return;
        }

        if (!d.TrySizeOctets(out var size) || size == 0)
        {
            _iin = _iin.Set(Iin.ObjectUnknown);
            return;
        }

        var prefixLen = 0;
        var p = h.Qualifier.IndexPrefix;
        if (p.IsIndex())
        {
            prefixLen = p.Octets();
        }

        var data = h.Data.Span;
        var off = 0;
        for (uint i = 0; i < h.Count; i++)
        {
            if (off + prefixLen + size > data.Length)
            {
                _iin = _iin.Set(Iin.ParameterError);
                return;
            }

            var index = (ushort)h.Range.IndexOf(i);
            if (prefixLen > 0)
            {
                index = (ushort)ReadPrefix(data[off..], prefixLen);
                off += prefixLen;
            }

            var value = DecodeDeadband(h.Variation, data.Slice(off, size));
            off += size;

            if (!_db.TryGetAnalog(index, out _, out var cfg))
            {
                _iin = _iin.Set(Iin.ParameterError);
                continue;
            }

            cfg.Deadband = value;
            _db.Configure(PointType.Analog, index, cfg);
            _log.Log(Dnp3LogLevel.Debug, "deadband written", ("index", index), ("value", value));
        }
    }

    /// <summary>Reads one group 34 value.</summary>
    private static double DecodeDeadband(byte variation, ReadOnlySpan<byte> buf) => variation switch
    {
        1 => BinaryPrimitives.ReadUInt16LittleEndian(buf),
        2 => BinaryPrimitives.ReadUInt32LittleEndian(buf),
        3 => BinaryPrimitives.ReadSingleLittleEndian(buf),
        _ => 0,
    };

    /// <summary>
    /// Answers with the fine time delay, which a master uses to estimate the
    /// round trip before setting the clock.
    /// </summary>
    private void OnDelayMeasure(Received r, Fragment frag)
    {
        var body = new List<byte>();
        ObjectHeaderCodec.AppendObjectHeader(body, new ObjectHeader
        {
            Group = 52,
            Variation = 2,
            Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
            Data = new byte[] { 0, 0 }, // no processing delay to declare
        });

        Respond(r, frag.Header, [.. body]);
    }

    /// <summary>Notes when the request arrived.</summary>
    /// <remarks>
    /// <para>
    /// This is the first half of the standard's LAN time-synchronisation
    /// procedure: the master sends it, the outstation records the arrival time,
    /// and the master then reads that time back as group 50 variation 3 to work
    /// out how long the message took to get there. An outstation that refuses
    /// it leaves that master unable to set the clock at all.
    /// </para>
    /// <para>
    /// The standard says to record the time the <em>first octet</em> arrived.
    /// This records the time the fragment was dispatched, which is later by the
    /// time it took to receive and reassemble the frame — negligible over
    /// Ethernet, and on a slow serial link the delay-measure procedure is the
    /// right one to use anyway.
    /// </para>
    /// </remarks>
    private void OnRecordCurrentTime(Received r, Fragment frag)
    {
        _recordedTime = _appl.Now();
        _log.Log(Dnp3LogLevel.Debug, "current time recorded", ("time", _recordedTime));

        if (r.Broadcast)
        {
            return;
        }

        Respond(r, frag.Header, []);
    }

    /// <summary>
    /// Answers a restart request with how long the outstation expects to be
    /// unavailable.
    /// </summary>
    private void OnRestart(Received r, Fragment frag)
    {
        TimeSpan d;
        if (frag.Header.Func == FuncCode.ColdRestart)
        {
            d = _appl.ColdRestart();
            _db.Events?.Reset();
        }
        else
        {
            d = _appl.WarmRestart();
        }

        _iin = _iin.Set(Iin.DeviceRestart);
        _synchronized = false;

        var ms = (long)d.TotalMilliseconds;
        if (ms > 0xFFFF)
        {
            ms = 0xFFFF;
        }
        else if (ms < 0)
        {
            ms = 0;
        }

        var body = new List<byte>();
        ObjectHeaderCodec.AppendObjectHeader(body, new ObjectHeader
        {
            Group = 52,
            Variation = 2,
            Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
            Data = new[] { (byte)ms, (byte)(ms >> 8) },
        });

        if (r.Broadcast)
        {
            return;
        }

        Respond(r, frag.Header, [.. body]);
    }

    /// <summary>
    /// Records the enable or disable, answering truthfully that the request was
    /// understood.
    /// </summary>
    private void OnUnsolicitedControl(Received r, Fragment frag)
    {
        var enable = frag.Header.Func == FuncCode.EnableUnsolicited;
        foreach (var h in frag.Objects)
        {
            if (h.Group != 60 || h.Variation is < 2 or > 4)
            {
                _iin = _iin.Set(Iin.ObjectUnknown);
                continue;
            }

            var cls = (Class)((byte)Class.Class1 << (h.Variation - 2));
            if (enable)
            {
                _unsolClasses |= cls;
            }
            else
            {
                _unsolClasses &= ~cls;
            }
        }

        if (r.Broadcast)
        {
            return;
        }

        Respond(r, frag.Header, []);
    }

    /// <summary>Moves point types between event classes.</summary>
    private void OnAssignClass(Received r, Fragment frag)
    {
        var cls = Class.None;
        foreach (var h in frag.Objects)
        {
            if (h.Group == 60)
            {
                cls = h.Variation switch
                {
                    // Class 0 means "no events".
                    1 => Class.None,
                    2 or 3 or 4 => (Class)((byte)Class.Class1 << (h.Variation - 2)),
                    _ => cls,
                };
                continue;
            }

            if (TryPointTypeForGroup(h.Group, out var assigned))
            {
                _db.AssignClass(assigned, cls);
            }
            else
            {
                _iin = _iin.Set(Iin.ObjectUnknown);
            }
        }

        if (r.Broadcast)
        {
            return;
        }

        Respond(r, frag.Header, []);
    }

    /// <summary>Sends a single-fragment response carrying a body.</summary>
    private void Respond(Received r, AppHeader req, byte[] body) =>
        SendFragments(r, req, [body], false);

    /// <summary>
    /// Emits a response, splitting it across fragments as needed.
    /// </summary>
    /// <remarks>
    /// Every fragment but the last carries FIN clear. A fragment carrying
    /// events sets CON, because only a confirmation lets the outstation drop
    /// them.
    /// </remarks>
    private void SendFragments(Received r, AppHeader req, List<byte[]> bodies, bool hasEvents)
    {
        if (r.Broadcast)
        {
            return;
        }

        for (var i = 0; i < bodies.Count; i++)
        {
            var last = i == bodies.Count - 1;

            // Intermediate fragments must be confirmed or the master cannot
            // pace the series; the final one only needs it when it carries
            // events.
            var needConfirm = !last || hasEvents;

            var ctrl = new AppControl(
                Fir: i == 0,
                Fin: last,
                Con: needConfirm,
                Uns: false,
                Seq: req.Control.Seq);

            var frag = new List<byte>(AppConstants.ResponseHeaderSize + bodies[i].Length);
            HeaderCodec.AppendHeader(frag, new AppHeader(ctrl, FuncCode.Response, CurrentIin()));
            frag.AddRange(bodies[i]);

            _stack!.SendTo(_sink, r.Source, [.. frag]);

            lock (_gate)
            {
                _stats.FragmentsSent++;
            }

            if (needConfirm)
            {
                _awaitingConfirm = true;
                _confirmSeq = ctrl.Seq;
                _confirmDeadline = _appl.Now() + _cfg.ConfirmTimeout;
            }
        }

        lock (_gate)
        {
            _stats.ResponsesSent++;
        }

        // The broadcast indication reports only the request that arrived by
        // broadcast, so it is cleared once reported.
        _iin = _iin.Clear(Iin.Broadcast);
    }

    /// <summary>
    /// Assembles the indications to report, folding in the event state that
    /// changes between responses.
    /// </summary>
    private Iin CurrentIin()
    {
        var iin = _iin;

        var classes = _db.Events?.Classes() ?? Class.None;
        if ((classes & Class.Class1) != 0)
        {
            iin = iin.Set(Iin.Class1Events);
        }

        if ((classes & Class.Class2) != 0)
        {
            iin = iin.Set(Iin.Class2Events);
        }

        if ((classes & Class.Class3) != 0)
        {
            iin = iin.Set(Iin.Class3Events);
        }

        if (_db.Events?.Overflowed == true)
        {
            iin = iin.Set(Iin.EventBufferOverflow);
        }

        if (!_synchronized)
        {
            iin = iin.Set(Iin.NeedTime);
        }

        return iin;
    }

    /// <summary>Maps a static object group to its measurement type.</summary>
    private static bool TryPointTypeForGroup(byte group, out PointType pt)
    {
        switch (group)
        {
            case 1 or 2: pt = PointType.Binary; return true;
            case 3 or 4: pt = PointType.DoubleBitBinary; return true;
            case 10 or 11: pt = PointType.BinaryOutputStatus; return true;
            case 20 or 22: pt = PointType.Counter; return true;
            case 21 or 23: pt = PointType.FrozenCounter; return true;
            case 30 or 32: pt = PointType.Analog; return true;
            case 40 or 42: pt = PointType.AnalogOutputStatus; return true;
            default: pt = PointType.Unknown; return false;
        }
    }
}
