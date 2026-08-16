// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Buffers.Binary;
using System.Globalization;
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
    /// <remarks>
    /// It is where unsolicited responses go before a master has said anything.
    /// Solicited responses always go back to whoever asked, so a session serving
    /// several masters answers each of them correctly whatever this says.
    /// </remarks>
    public ushort RemoteAddr { get; set; }

    /// <summary>Sizes the point database.</summary>
    public DatabaseConfig Database { get; set; } = new();

    /// <summary>Sizes the event buffer.</summary>
    /// <remarks>
    /// Each attached master holds a queue of this size, because what one master
    /// has acknowledged says nothing about what another has seen.
    /// </remarks>
    public EventBufferConfig Events { get; set; } = new();

    /// <summary>
    /// How many masters may be served at once. Zero or one serves one at a
    /// time, which is the usual field configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Above one, the session accepts that many connections and runs a
    /// conversation on each: they share the database, the clock, the command
    /// handler and the counters, and nothing else. Events, sequence numbers,
    /// select-before-operate reservations, unsolicited enables and internal
    /// indications are all per-master, because none of them mean anything
    /// across a connection. A master arriving past the limit is accepted and
    /// immediately disconnected, and counted in
    /// <see cref="OutstationStats.MastersRefused"/>.
    /// </para>
    /// <para>
    /// It requires a channel that accepts connections — <c>TcpServerChannel</c>,
    /// <c>TlsServerChannel</c> or <c>PipeListener</c>. Running it over a channel
    /// that dials is refused rather than quietly serving one master, since a
    /// dialling channel asked for a second connection produces a second
    /// connection to the same peer.
    /// </para>
    /// <para>
    /// Each master costs an event queue of <see cref="Events"/> entries, so size
    /// the two together.
    /// </para>
    /// </remarks>
    public int MaxMasters { get; set; }

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
        if (MaxMasters <= 0)
        {
            MaxMasters = 1;
        }

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

    /// <summary>Masters currently attached.</summary>
    public int MastersAttached;

    /// <summary>The most masters that have been attached at once.</summary>
    public int PeakMastersAttached;

    /// <summary>
    /// Masters turned away because <see cref="OutstationConfig.MaxMasters"/>
    /// was already reached.
    /// </summary>
    public ulong MastersRefused;
}

/// <summary>An outstation.</summary>
/// <remarks>
/// <para>
/// All protocol state lives in the loops started by <see cref="RunAsync"/>.
/// Database updates arrive through <see cref="Update"/>, which is safe to call
/// from anywhere.
/// </para>
/// <para>
/// One session is one device, not one conversation. With
/// <see cref="OutstationConfig.MaxMasters"/> above one it serves that many
/// masters at once over a listening channel, running a conversation per
/// connection against the same database.
/// </para>
/// </remarks>
public sealed partial class OutstationSession
{
    private readonly OutstationConfig _cfg;
    private readonly IOutstationApplication _appl;
    private readonly Database _db;
    private readonly ResponseWriter _writer;
    private readonly IDnp3Logger _log;

    /// <summary>
    /// Tracks whether a master has set our clock, which decides the quality
    /// stamped on the timestamps we report. It is the device's clock, so it is
    /// shared: whichever master sets it, every master's timestamps improve.
    /// </summary>
    private volatile bool _synchronized;

    /// <summary>
    /// Latched while the device has restarted, and used to seed the restart
    /// indication of a master that attaches later.
    /// </summary>
    /// <remarks>
    /// The indication itself is per-master, because it is a handshake: a master
    /// clears it once it has re-baselined, and one master finishing that says
    /// nothing about another. What is shared is the fact of the restart, which
    /// a master attaching afterwards still needs to be told about.
    /// </remarks>
    private volatile bool _deviceRestart;

    /// <summary>Executes the controls themselves.</summary>
    private readonly ICommandHandler _cmds;

    /// <remarks>
    /// Several readers, because every association drains it before answering a
    /// request as well as the pump draining it while the session is idle.
    /// </remarks>
    private readonly Channel<Action<Database>> _updates =
        Channel.CreateBounded<Action<Database>>(new BoundedChannelOptions(64));

    /// <summary>
    /// Serialises draining, so a drain that finds the queue empty knows every
    /// earlier update has been applied rather than merely dequeued.
    /// </summary>
    private readonly Lock _drainGate = new();

    private readonly Lock _gate = new();
    private OutstationStats _stats;

    /// <summary>The masters currently attached. Guarded by <see cref="_gate"/>.</summary>
    private readonly List<Association> _assocs = [];

    private int _nextAssocId;

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
        _deviceRestart = true;
    }

    /// <summary>Makes the outstation report a restart to its masters.</summary>
    /// <remarks>
    /// It is what a device calls when it has genuinely restarted, and what a
    /// simulator calls to produce the condition on demand. The restart
    /// indication is the only signal that tells a master its whole picture is
    /// stale — the event history is gone, so no incremental poll can recover it
    /// and only a full re-baseline will do. Every attached master is told, and
    /// so is any that attaches before one of them clears it.
    /// </remarks>
    public void Restart()
    {
        _deviceRestart = true;
        _synchronized = false;
        _db.ResetEvents();

        foreach (var a in Attached())
        {
            // The association's own loop applies it, so nothing but that loop
            // ever writes its protocol state.
            a.RestartPending = true;
        }
    }

    /// <summary>
    /// The point database. Prefer <see cref="Update"/> for modifications, which
    /// serialises them with the session's own work.
    /// </summary>
    public Database Database => _db;

    /// <summary>The event queue.</summary>
    /// <remarks>
    /// Each attached master holds its own, so this returns the sole master's
    /// queue when one is attached and the queue events accumulate in while none
    /// is. With several attached there is no single answer and it returns the
    /// unattached queue, which is empty; read
    /// <see cref="OutstationStats.MastersAttached"/> before drawing conclusions
    /// from it.
    /// </remarks>
    public EventBuffer? Events
    {
        get
        {
            lock (_gate)
            {
                if (_assocs.Count == 1)
                {
                    return _assocs[0].Events;
                }
            }

            return _db.Events;
        }
    }

    /// <summary>How many masters are attached right now.</summary>
    public int MastersAttached
    {
        get
        {
            lock (_gate)
            {
                return _assocs.Count;
            }
        }
    }

    /// <summary>Returns a snapshot of the session counters.</summary>
    public OutstationStats Stats
    {
        get
        {
            lock (_gate)
            {
                var s = _stats;
                s.MastersAttached = _assocs.Count;
                return s;
            }
        }
    }

    /// <summary>Applies an action to the database, serialised with the session.</summary>
    /// <remarks>
    /// Batching changes in one call is what makes a set of related updates — a
    /// breaker opening and its alarm asserting — produce one consistent set of
    /// events rather than a torn read. The action runs holding the database's
    /// lock, so no master can be answered halfway through it.
    /// </remarks>
    public void Update(Action<Database> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!_updates.Writer.TryWrite(action))
        {
            // The queue is full, which means the session is not running or is
            // wedged. Apply directly rather than dropping the update.
            Apply(action);
        }
    }

    /// <summary>Runs one update action under the database's lock.</summary>
    private void Apply(Action<Database> action)
    {
        using var scope = _db.EnterScope();
        action(_db);
    }

    /// <summary>Returns the attached associations.</summary>
    private Association[] Attached()
    {
        lock (_gate)
        {
            return [.. _assocs];
        }
    }

    /// <summary>Connects and serves until the token is cancelled.</summary>
    /// <remarks>
    /// With <see cref="OutstationConfig.MaxMasters"/> at one it serves a master,
    /// waits for the next, and repeats. Above one it accepts up to that many at
    /// once and serves each on its own loop.
    /// </remarks>
    public async Task RunAsync(IChannel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (_cfg.MaxMasters > 1 && !channel.SupportsConcurrentConnections)
        {
            throw new BadConfigException(string.Format(
                CultureInfo.InvariantCulture,
                "outstation: MaxMasters is {0} but {1} produces one peer at a time; " +
                "serving several masters needs a listening channel",
                _cfg.MaxMasters,
                channel));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linked.Token;

        // Updates are pumped for as long as the session runs rather than only
        // while a master is attached, so events that happen during an outage
        // are queued for whoever attaches next instead of piling up unapplied.
        var pump = PumpUpdatesAsync(ct);

        try
        {
            if (_cfg.MaxMasters > 1)
            {
                await ServeManyAsync(channel, ct).ConfigureAwait(false);
            }
            else
            {
                await ServeOneAsync(channel, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }
    }

    /// <summary>Serves one master at a time, reconnecting after each.</summary>
    private async Task ServeOneAsync(IChannel channel, CancellationToken cancellationToken)
    {
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

            await ServeConnectionAsync(channel, conn, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Accepts up to the configured number of masters and serves each.</summary>
    private async Task ServeManyAsync(IChannel channel, CancellationToken cancellationToken)
    {
        // The semaphore is the connection limit.
        using var slots = new SemaphoreSlim(_cfg.MaxMasters, _cfg.MaxMasters);
        var live = new List<Task>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
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
                catch (Exception ex) when (ex is Dnp3Exception or IOException or SystemException)
                {
                    // One master failing to arrive — a refused TLS handshake,
                    // say — must not take down the masters already attached.
                    _log.Log(Dnp3LogLevel.Warn, "accept failed", ("err", ex.Message));
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (!slots.Wait(0, cancellationToken))
                {
                    // A master past the limit is turned away rather than left
                    // to time out. Refusing it is a fact the operator can see
                    // in a log and the master can see as a disconnection;
                    // leaving the connection open and unserved looks to both
                    // sides like an outstation that has gone mute.
                    lock (_gate)
                    {
                        _stats.MastersRefused++;
                    }

                    _log.Log(
                        Dnp3LogLevel.Warn,
                        "refusing a master: the connection limit is reached",
                        ("peer", Association.Describe(conn, channel)),
                        ("limit", _cfg.MaxMasters));

                    conn.Dispose();
                    continue;
                }

                live.RemoveAll(t => t.IsCompleted);
                live.Add(ServeAcceptedAsync(channel, conn, slots, cancellationToken));
            }
        }
        finally
        {
            // The connections are cancelled with the session's token; this only
            // waits for their loops to unwind so RunAsync does not return with
            // masters still being served.
            try
            {
                await Task.WhenAll(live).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or Dnp3Exception or IOException)
            {
                // Whatever went wrong has already been logged per connection.
            }
        }
    }

    /// <summary>Serves one accepted connection and frees its slot afterwards.</summary>
    private async Task ServeAcceptedAsync(
        IChannel channel,
        Stream conn,
        SemaphoreSlim slots,
        CancellationToken cancellationToken)
    {
        // Yield first so the accept loop is back waiting on the listener rather
        // than running this connection's opening turn.
        await Task.Yield();

        try
        {
            await ServeConnectionAsync(channel, conn, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The session is stopping.
        }
        catch (Exception ex)
        {
            // Deliberately everything. This task is one master's conversation;
            // whatever ends it badly must end that conversation and no other.
            _log.Log(Dnp3LogLevel.Error, "connection failed", ("err", ex.Message));
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>Attaches a master, serves it, and detaches it.</summary>
    private async Task ServeConnectionAsync(
        IChannel channel,
        Stream conn,
        CancellationToken cancellationToken)
    {
        var a = Attach(channel, conn);
        try
        {
            await ServeAsync(a, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Detach(a);
            conn.Dispose();
            a.Log.Log(Dnp3LogLevel.Info, "disconnected");
        }
    }

    /// <summary>Builds the state for one master and registers it.</summary>
    private Association Attach(IChannel channel, Stream conn)
    {
        var id = Interlocked.Increment(ref _nextAssocId);
        var peer = Association.Describe(conn, channel);

        var a = new Association
        {
            Id = id,
            Connection = conn,
            Peer = peer,
            Events = new EventBuffer(_cfg.Events),
            Log = new ScopedLogger(_log, ("conn", id), ("peer", peer)),
            Stack = new ProtocolStack(new StackConfig
            {
                LocalAddr = _cfg.LocalAddr,
                RemoteAddr = _cfg.RemoteAddr,
                IsMaster = false,
                UseConfirms = _cfg.UseLinkConfirms,
                MaxRetries = _cfg.LinkRetries,
                MaxRxFragment = _cfg.MaxRxFragment,
            }),
            RemoteAddr = _cfg.RemoteAddr,

            // A master that attaches after a restart nobody has cleared yet has
            // to be told about it too: its picture is as stale as everyone
            // else's.
            Iin = _deviceRestart ? Iin.DeviceRestart : Iin.None,
        };

        // Subscribing hands over whatever queued while nothing was attached, so
        // a master that reconnects still gets the events it missed.
        _db.Subscribe(a.Events);

        int attached;
        lock (_gate)
        {
            _assocs.Add(a);
            attached = _assocs.Count;
            _stats.Connections++;
            if (attached > _stats.PeakMastersAttached)
            {
                _stats.PeakMastersAttached = attached;
            }
        }

        a.Log.Log(Dnp3LogLevel.Info, "connected", ("masters", attached));
        return a;
    }

    /// <summary>Unregisters a master and returns its queue if it was the last.</summary>
    private void Detach(Association a)
    {
        lock (_gate)
        {
            _assocs.Remove(a);
        }

        _db.Unsubscribe(a.Events);
    }

    /// <summary>Applies queued database updates until the session stops.</summary>
    /// <remarks>
    /// It exists for the quiet times. While a master is being served the
    /// association drains the queue itself before answering, which is what
    /// makes an update that was queued before a request arrived visible to the
    /// response to it.
    /// </remarks>
    private async Task PumpUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _updates.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                DrainUpdates();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Applies everything waiting on the update queue.</summary>
    private void DrainUpdates()
    {
        // Under the drain lock rather than merely reading the queue: a request
        // being answered has to know the earlier updates are applied, not just
        // that somebody has taken them off the queue.
        lock (_drainGate)
        {
            while (_updates.Reader.TryRead(out var fn))
            {
                try
                {
                    Apply(fn);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A caller's update throwing must not stop the session
                    // applying the rest of them.
                    _log.Log(Dnp3LogLevel.Error, "update failed", ("err", ex.Message));
                }
            }
        }
    }

    /// <summary>Runs one connection until it fails or the token is cancelled.</summary>
    private async Task ServeAsync(Association a, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linked.Token;

        // The read loop only moves octets. Everything that touches this
        // association's protocol state runs here, so its stack needs no locking
        // and a response can never interleave with the processing of an inbound
        // frame.
        var rx = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
        {
            SingleWriter = true,
            SingleReader = true,
        });

        var readTask = ReadIntoAsync(a.Connection, rx.Writer, ct);

        a.Connected = true;
        a.Unsol.Reset();

        Task<bool>? rxWait = null;

        try
        {
            // Announce ourselves before servicing anything. The null unsolicited
            // response exists to tell a master "I am here and I have restarted",
            // and waiting for the first tick would let the master's own startup
            // sequence clear the restart indication first — leaving the
            // announcement carrying nothing worth announcing.
            using (_db.EnterScope())
            {
                PollUnsolicited(a, _appl.Now());
            }

            await FlushAsync(a, ct).ConfigureAwait(false);

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

                var delay = Task.Delay(tick, ct);
                var completed = await Task.WhenAny(rxWait, delay, readTask).ConfigureAwait(false);

                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (completed == readTask)
                {
                    return;
                }

                if (completed == rxWait)
                {
                    rxWait = null;
                    if (!Receive(a, rx.Reader))
                    {
                        return;
                    }
                }
                else
                {
                    var now = _appl.Now();
                    using (_db.EnterScope())
                    {
                        ApplyPendingRestart(a);
                        CheckLinkTimeout(a);
                        CheckConfirmTimeout(a);
                        CheckSelectTimeout(a, now);
                        PollUnsolicited(a, now);
                    }
                }

                await FlushAsync(a, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // A cancelled session is a clean shutdown.
        }
        finally
        {
            a.Connected = false;
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

    /// <summary>
    /// Feeds whatever the read loop has produced through the stack. Returns
    /// false when the connection must be dropped.
    /// </summary>
    private bool Receive(Association a, ChannelReader<byte[]> reader)
    {
        // Updates queued before this request arrived are applied first: a
        // master that polls after the application has reported a change must be
        // told about the change, not about the state before it.
        //
        // Before the scope below, not inside it. Draining takes the drain lock
        // and then the database's; doing it while already holding the database
        // would be the reverse order, and two of these running at once would
        // deadlock.
        DrainUpdates();

        // One scope around the whole batch: a request is answered from a
        // database that is not being modified underneath it.
        using var scope = _db.EnterScope();

        while (reader.TryRead(out var chunk))
        {
            try
            {
                a.Stack.Receive(a.Sink, chunk, r => Handle(a, r));
            }
            catch (Dnp3Exception ex)
            {
                a.Log.Log(Dnp3LogLevel.Warn, "receive failed", ("err", ex.Message));
                return false;
            }
        }

        return true;
    }

    /// <summary>Picks up a restart raised from outside this loop.</summary>
    private void ApplyPendingRestart(Association a)
    {
        if (!a.RestartPending)
        {
            return;
        }

        a.RestartPending = false;
        a.Iin = a.Iin.Set(Iin.DeviceRestart);
        a.Sel.Clear();
        a.Unsol.Reset();
        a.AwaitingConfirm = false;
        a.Log.Log(Dnp3LogLevel.Info, "device restarted; reporting it to this master");
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
    private static async Task FlushAsync(Association a, CancellationToken cancellationToken)
    {
        if (a.Sink.IsEmpty)
        {
            return;
        }

        var pending = a.Sink.Pending.ToArray();
        a.Sink.Clear();
        await a.Connection.WriteAsync(pending, cancellationToken).ConfigureAwait(false);
        await a.Connection.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Retransmits an unacknowledged link frame.</summary>
    private void CheckLinkTimeout(Association a)
    {
        if (!a.Stack.Pending || _appl.Now() < a.LinkDeadline)
        {
            return;
        }

        bool failed;
        try
        {
            failed = a.Stack.OnTimeout(a.Sink);
        }
        catch (Dnp3Exception ex)
        {
            a.Log.Log(Dnp3LogLevel.Warn, "link retransmission failed", ("err", ex.Message));
            return;
        }

        a.LinkDeadline = _appl.Now() + _cfg.LinkTimeout;
        if (failed)
        {
            a.Log.Log(Dnp3LogLevel.Warn, "link layer gave up on a response");
        }
    }

    /// <summary>
    /// Returns selected events to the queue when the master never confirmed the
    /// response that carried them.
    /// </summary>
    private void CheckConfirmTimeout(Association a)
    {
        if (!a.AwaitingConfirm || _appl.Now() < a.ConfirmDeadline)
        {
            return;
        }

        a.AwaitingConfirm = false;
        var n = a.Events.Unselect();

        lock (_gate)
        {
            _stats.ConfirmTimeouts++;
        }

        a.Log.Log(
            Dnp3LogLevel.Warn,
            "application confirm timed out; events requeued",
            ("events", n));
    }

    /// <summary>Dispatches one request fragment.</summary>
    private void Handle(Association a, Received r)
    {
        // Before anything is answered: a restart raised a moment ago has to be
        // reported in this response rather than waiting for the next tick, or a
        // master that polls immediately after one is told the device is fine.
        ApplyPendingRestart(a);

        lock (_gate)
        {
            _stats.RequestsReceived++;
        }

        // Whatever address this master uses is where its unsolicited responses
        // go from now on, whether or not the configuration guessed it right.
        a.RemoteAddr = r.Source;
        a.RemoteKnown = true;

        var status = FragmentParser.ParseFragment(null, r.Fragment, out var frag, out var error);
        if (status != AppParseStatus.Ok)
        {
            lock (_gate)
            {
                _stats.MalformedRequests++;
            }

            a.Log.Log(Dnp3LogLevel.Warn, "malformed request", ("err", error));

            // A fragment we cannot parse cannot be answered meaningfully: we do
            // not know its sequence number's validity or what it asked for. The
            // parameter-error indication rides on the next response instead.
            a.Iin = a.Iin.Set(Iin.ParameterError);
            return;
        }

        if (r.Broadcast)
        {
            // A broadcast request is executed but never answered — every
            // outstation answering at once would collide. The next response
            // carries the broadcast indication instead.
            a.Iin = a.Iin.Set(Iin.Broadcast);
        }

        switch (frag.Header.Func)
        {
            case FuncCode.Confirm:
                // Solicited and unsolicited responses have separate sequence
                // spaces, so the UNS bit decides which one this acknowledges.
                // Confusing them would drop events the master never received.
                if (frag.Header.Control.Uns)
                {
                    OnUnsolicitedConfirm(a, frag.Header);
                }
                else
                {
                    OnConfirm(a, frag.Header);
                }

                return;

            case FuncCode.Read:
                OnRead(a, r, frag);
                return;

            case FuncCode.Write:
                OnWrite(a, r, frag);
                return;

            case FuncCode.DelayMeasure:
                OnDelayMeasure(a, r, frag);
                return;

            case FuncCode.RecordCurrentTime:
                OnRecordCurrentTime(a, r, frag);
                return;

            case FuncCode.ColdRestart:
            case FuncCode.WarmRestart:
                OnRestart(a, r, frag);
                return;

            case FuncCode.EnableUnsolicited:
            case FuncCode.DisableUnsolicited:
                OnUnsolicitedControl(a, r, frag);
                return;

            case FuncCode.AssignClass:
                OnAssignClass(a, r, frag);
                return;

            case FuncCode.Select:
            case FuncCode.Operate:
            case FuncCode.DirectOperate:
            case FuncCode.DirectOperateNR:
                OnCommand(a, r, frag);
                return;

            case FuncCode.ImmedFreeze:
            case FuncCode.ImmedFreezeNR:
                _db.FreezeCounters();
                if (frag.Header.Func.NoReply() || r.Broadcast)
                {
                    return;
                }

                Respond(a, r, frag.Header, []);
                return;

            default:
                lock (_gate)
                {
                    _stats.UnknownFunction++;
                }

                a.Iin = a.Iin.Set(Iin.NoFuncCodeSupport);
                if (r.Broadcast)
                {
                    return;
                }

                Respond(a, r, frag.Header, []);
                return;
        }
    }

    /// <summary>Clears the events the confirmed response carried.</summary>
    private void OnConfirm(Association a, AppHeader h)
    {
        lock (_gate)
        {
            _stats.ConfirmsReceived++;
        }

        if (!a.AwaitingConfirm || h.Control.Seq != a.ConfirmSeq)
        {
            // A confirm for a response we are not waiting on. Ignoring it is
            // right: acting on it would drop events the master never received.
            a.Log.Log(
                Dnp3LogLevel.Debug,
                "unexpected confirm",
                ("seq", h.Control.Seq), ("awaiting", a.AwaitingConfirm));
            return;
        }

        a.AwaitingConfirm = false;
        var n = a.Events.Confirm();
        a.Log.Log(Dnp3LogLevel.Debug, "events confirmed", ("count", n));
    }

    /// <summary>Answers a read request.</summary>
    private void OnRead(Association a, Received r, Fragment frag)
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
                        selected.AddRange(a.Events.Select(mask, 512));
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
                if (a.RecordedTime is not { } recorded)
                {
                    a.Iin = a.Iin.Set(Iin.ParameterError);
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
                a.Iin = a.Iin.Set(Iin.ObjectUnknown);
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

        SendFragments(a, r, frag.Header, b.Done(), selected.Count > 0);
    }

    /// <summary>Handles the write function code.</summary>
    private void OnWrite(Association a, Received r, Fragment frag)
    {
        foreach (var h in frag.Objects)
        {
            if (h.Group == 80 && h.Variation == 1)
            {
                // A master clears DEVICE_RESTART by writing zero to index 7.
                // This is the handshake that ends the restart sequence — for
                // this master. Another that has not re-baselined keeps its own
                // indication until it does the same.
                a.Iin = a.Iin.Clear(Iin.DeviceRestart);
                _deviceRestart = false;
                a.Log.Log(Dnp3LogLevel.Debug, "device restart indication cleared by master");
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
                    a.Iin = a.Iin.Set(Iin.NoFuncCodeSupport);
                    continue;
                }

                if (a.RecordedTime is not { } reference)
                {
                    // No RECORD_CURRENT_TIME preceded this, so there is no
                    // reference to correct against.
                    a.Iin = a.Iin.Set(Iin.ParameterError);
                    continue;
                }

                if (h.Data.Length < CommandObjects.Time48Size)
                {
                    a.Iin = a.Iin.Set(Iin.ParameterError);
                    continue;
                }

                var recorded = CommandObjects.ParseTime48(h.Data.Span);
                var elapsed = _appl.Now() - reference;
                if (_appl.WriteAbsoluteTime(recorded.Time + elapsed))
                {
                    _synchronized = true;
                    a.Iin = a.Iin.Clear(Iin.NeedTime);
                    a.RecordedTime = null;
                    a.Log.Log(
                        Dnp3LogLevel.Debug,
                        "clock set by the recorded-time procedure",
                        ("recorded_at", recorded.Time), ("elapsed", elapsed));
                }
                else
                {
                    a.Iin = a.Iin.Set(Iin.ParameterError);
                }

                continue;
            }

            if (h.Group == 50 && h.Variation == 1)
            {
                if (!_appl.SupportsWriteTime())
                {
                    a.Iin = a.Iin.Set(Iin.NoFuncCodeSupport);
                    continue;
                }

                if (h.Data.Length < CommandObjects.Time48Size)
                {
                    a.Iin = a.Iin.Set(Iin.ParameterError);
                    continue;
                }

                var ts = CommandObjects.ParseTime48(h.Data.Span);
                if (_appl.WriteAbsoluteTime(ts.Time))
                {
                    _synchronized = true;
                    a.Iin = a.Iin.Clear(Iin.NeedTime);
                    a.Log.Log(Dnp3LogLevel.Debug, "clock set by master", ("time", ts.Time));
                }
                else
                {
                    a.Iin = a.Iin.Set(Iin.ParameterError);
                }

                continue;
            }

            if (h.Group == 34)
            {
                WriteDeadbands(a, h);
                continue;
            }

            a.Iin = a.Iin.Set(Iin.ObjectUnknown);
        }

        if (r.Broadcast)
        {
            return;
        }

        Respond(a, r, frag.Header, []);
    }

    /// <summary>Applies a group 34 analog deadband write.</summary>
    /// <remarks>
    /// A deadband is how a master tells an outstation how much a point must
    /// move before it is worth an event, which is the only lever it has over a
    /// chattering analog short of dropping the point from its class. It is a
    /// property of the point rather than of the conversation, so a deadband one
    /// master writes applies to what every master is told.
    /// </remarks>
    private void WriteDeadbands(Association a, ObjectHeader h)
    {
        if (!ObjectRegistry.TryLookup(GroupVar.GV(h.Group, h.Variation), out var d))
        {
            a.Iin = a.Iin.Set(Iin.ObjectUnknown);
            return;
        }

        if (!d.TrySizeOctets(out var size) || size == 0)
        {
            a.Iin = a.Iin.Set(Iin.ObjectUnknown);
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
                a.Iin = a.Iin.Set(Iin.ParameterError);
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
                a.Iin = a.Iin.Set(Iin.ParameterError);
                continue;
            }

            cfg.Deadband = value;
            _db.Configure(PointType.Analog, index, cfg);
            a.Log.Log(Dnp3LogLevel.Debug, "deadband written", ("index", index), ("value", value));
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
    private void OnDelayMeasure(Association a, Received r, Fragment frag)
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

        Respond(a, r, frag.Header, [.. body]);
    }

    /// <summary>Notes when the request arrived.</summary>
    /// <remarks>
    /// <para>
    /// This is the first half of the standard's LAN time-synchronisation
    /// procedure: the master sends it, the outstation records the arrival time,
    /// and the master then reads that time back as group 50 variation 3 to work
    /// out how long the message took to get there. An outstation that refuses
    /// it leaves that master unable to set the clock at all. The recorded time
    /// belongs to the master that asked for it, so two masters running the
    /// procedure at once do not overwrite each other's reference.
    /// </para>
    /// <para>
    /// The standard says to record the time the <em>first octet</em> arrived.
    /// This records the time the fragment was dispatched, which is later by the
    /// time it took to receive and reassemble the frame — negligible over
    /// Ethernet, and on a slow serial link the delay-measure procedure is the
    /// right one to use anyway.
    /// </para>
    /// </remarks>
    private void OnRecordCurrentTime(Association a, Received r, Fragment frag)
    {
        a.RecordedTime = _appl.Now();
        a.Log.Log(Dnp3LogLevel.Debug, "current time recorded", ("time", a.RecordedTime));

        if (r.Broadcast)
        {
            return;
        }

        Respond(a, r, frag.Header, []);
    }

    /// <summary>
    /// Answers a restart request with how long the outstation expects to be
    /// unavailable.
    /// </summary>
    /// <remarks>
    /// A restart is the device restarting, not one conversation ending: every
    /// other attached master is told about it too, because their event history
    /// has gone with everyone else's.
    /// </remarks>
    private void OnRestart(Association a, Received r, Fragment frag)
    {
        TimeSpan d;
        if (frag.Header.Func == FuncCode.ColdRestart)
        {
            d = _appl.ColdRestart();
            _db.ResetEvents();
        }
        else
        {
            d = _appl.WarmRestart();
        }

        _deviceRestart = true;
        _synchronized = false;
        a.Iin = a.Iin.Set(Iin.DeviceRestart);

        foreach (var other in Attached())
        {
            if (!ReferenceEquals(other, a))
            {
                other.RestartPending = true;
            }
        }

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

        Respond(a, r, frag.Header, [.. body]);
    }

    /// <summary>
    /// Records the enable or disable, answering truthfully that the request was
    /// understood.
    /// </summary>
    private void OnUnsolicitedControl(Association a, Received r, Fragment frag)
    {
        var enable = frag.Header.Func == FuncCode.EnableUnsolicited;
        foreach (var h in frag.Objects)
        {
            if (h.Group != 60 || h.Variation is < 2 or > 4)
            {
                a.Iin = a.Iin.Set(Iin.ObjectUnknown);
                continue;
            }

            var cls = (Class)((byte)Class.Class1 << (h.Variation - 2));
            if (enable)
            {
                a.UnsolClasses |= cls;
            }
            else
            {
                a.UnsolClasses &= ~cls;
            }
        }

        if (r.Broadcast)
        {
            return;
        }

        Respond(a, r, frag.Header, []);
    }

    /// <summary>Moves point types between event classes.</summary>
    private void OnAssignClass(Association a, Received r, Fragment frag)
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
                a.Iin = a.Iin.Set(Iin.ObjectUnknown);
            }
        }

        if (r.Broadcast)
        {
            return;
        }

        Respond(a, r, frag.Header, []);
    }

    /// <summary>Sends a single-fragment response carrying a body.</summary>
    private void Respond(Association a, Received r, AppHeader req, byte[] body) =>
        SendFragments(a, r, req, [body], false);

    /// <summary>
    /// Emits a response, splitting it across fragments as needed.
    /// </summary>
    /// <remarks>
    /// Every fragment but the last carries FIN clear. A fragment carrying
    /// events sets CON, because only a confirmation lets the outstation drop
    /// them.
    /// </remarks>
    private void SendFragments(
        Association a,
        Received r,
        AppHeader req,
        List<byte[]> bodies,
        bool hasEvents)
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
            HeaderCodec.AppendHeader(frag, new AppHeader(ctrl, FuncCode.Response, CurrentIin(a)));
            frag.AddRange(bodies[i]);

            a.Stack.SendTo(a.Sink, r.Source, [.. frag]);

            lock (_gate)
            {
                _stats.FragmentsSent++;
            }

            if (needConfirm)
            {
                a.AwaitingConfirm = true;
                a.ConfirmSeq = ctrl.Seq;
                a.ConfirmDeadline = _appl.Now() + _cfg.ConfirmTimeout;
            }
        }

        lock (_gate)
        {
            _stats.ResponsesSent++;
        }

        // The broadcast indication reports only the request that arrived by
        // broadcast, so it is cleared once reported.
        a.Iin = a.Iin.Clear(Iin.Broadcast);
    }

    /// <summary>
    /// Assembles the indications to report to one master, folding in the event
    /// state that changes between responses.
    /// </summary>
    private Iin CurrentIin(Association a)
    {
        var iin = a.Iin;

        var classes = a.Events.Classes();
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

        if (a.Events.Overflowed)
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
