// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Threading.Channels;
using SharpDnp3.App;
using SharpDnp3.Channels;
using SharpDnp3.Objects;
using SharpDnp3.Stack;

namespace SharpDnp3.Master;

/// <summary>Parameterises a master session.</summary>
public sealed class MasterConfig
{
    /// <summary>This master's link address.</summary>
    public ushort LocalAddr { get; set; }

    /// <summary>The outstation's link address.</summary>
    public ushort RemoteAddr { get; set; }

    /// <summary>How long to wait for an outstation to answer.</summary>
    public TimeSpan ResponseTimeout { get; set; }

    /// <summary>How long to wait before retrying a failed task.</summary>
    public TimeSpan TaskRetryPeriod { get; set; }

    /// <summary>
    /// Runs a class 0+1+2+3 poll when the session starts and whenever the
    /// outstation reports a restart.
    /// </summary>
    public bool IntegrityOnStartup { get; set; }

    /// <summary>
    /// Sends a disable-unsolicited request before the integrity poll, which is
    /// the standard's startup sequence.
    /// </summary>
    public bool DisableUnsolOnStartup { get; set; }

    /// <summary>
    /// The set of classes to enable for unsolicited reporting after the
    /// integrity poll. <see cref="Class.None"/> enables none.
    /// </summary>
    public Class UnsolClassMask { get; set; }

    /// <summary>Caps request fragments.</summary>
    public int MaxTxFragment { get; set; }

    /// <summary>Caps response fragments.</summary>
    public int MaxRxFragment { get; set; }

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

    /// <summary>
    /// Probes an idle link this often with a link status request. Zero disables
    /// it.
    /// </summary>
    /// <remarks>
    /// An idle TCP connection is indistinguishable from a peer that has gone
    /// away: both are silent. Without a probe, a master notices only when its
    /// next poll times out, which on a slow schedule can be minutes.
    /// </remarks>
    public TimeSpan KeepAlive { get; set; }

    /// <summary>Receives protocol and session events.</summary>
    public IDnp3Logger? Log { get; set; }

    /// <summary>
    /// Supplies the clock, so tests can drive a session without waiting.
    /// </summary>
    public TimeProvider? TimeProvider { get; set; }

    internal void ApplyDefaults()
    {
        if (ResponseTimeout <= TimeSpan.Zero)
        {
            ResponseTimeout = TimeSpan.FromSeconds(5);
        }

        if (TaskRetryPeriod <= TimeSpan.Zero)
        {
            TaskRetryPeriod = TimeSpan.FromSeconds(5);
        }

        if (MaxTxFragment <= 0)
        {
            MaxTxFragment = AppConstants.DefaultMaxFragment;
        }

        if (MaxRxFragment <= 0)
        {
            MaxRxFragment = AppConstants.DefaultMaxFragment;
        }

        if (LinkTimeout <= TimeSpan.Zero)
        {
            LinkTimeout = TimeSpan.FromSeconds(1);
        }

        Log ??= NullDnp3Logger.Instance;
        TimeProvider ??= TimeProvider.System;
    }
}

/// <summary>Counts what a session has done.</summary>
public record struct MasterStats
{
    /// <summary>Requests put on the wire.</summary>
    public ulong TasksRun;

    /// <summary>Requests that completed successfully.</summary>
    public ulong TasksSucceeded;

    /// <summary>Requests that failed.</summary>
    public ulong TasksFailed;

    /// <summary>Requests the outstation never answered.</summary>
    public ulong ResponseTimeouts;

    /// <summary>Application fragments received.</summary>
    public ulong FragmentsRx;

    /// <summary>Unsolicited responses received.</summary>
    public ulong Unsolicited;

    /// <summary>Connections established.</summary>
    public ulong Connections;

    /// <summary>Times the outstation reported a restart.</summary>
    public ulong RestartsSeen;
}

/// <summary>A master's connection to one outstation.</summary>
/// <remarks>
/// All protocol state lives in the loop started by
/// <see cref="RunAsync"/>. The request methods are safe to call from anywhere:
/// they hand a task to that loop and wait for it to finish.
/// </remarks>
public sealed partial class MasterSession
{
    private readonly MasterConfig _cfg;
    private readonly IMasterHandler _handler;
    private readonly IDnp3Logger _log;
    private readonly TimeProvider _time;
    private ProtocolStack? _stack;
    private readonly BufferSink _sink = new();

    /// <summary>The application sequence number for solicited requests.</summary>
    private byte _seq;

    /// <summary>
    /// Tracks the outstation's unsolicited sequence space, which is separate
    /// from the solicited one.
    /// </summary>
    private byte _unsolSeq;

    private bool _hasUnsolSeq;

    /// <summary>
    /// Mirrors the outstation's clock state, taken from NEED_TIME.
    /// </summary>
    private bool _synchronized;

    private readonly Scheduler _sched = new();
    private MasterTask? _inflight;

    private readonly Channel<MasterTask> _submit =
        Channel.CreateBounded<MasterTask>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
        });

    private readonly Lock _gate = new();
    private MasterStats _stats;
    private bool _connected;
    private Iin _lastIin;

    /// <summary>When octets last arrived, which paces the keep-alive.</summary>
    private DateTimeOffset _lastRx;

    /// <summary>When an unacknowledged link frame should be retried.</summary>
    private DateTimeOffset _linkDeadline;

    /// <summary>
    /// Set while the startup sequence is in flight.
    /// </summary>
    /// <remarks>
    /// It has to exist because the sequence is triggered by an indication that
    /// is still set until the sequence's own first step clears it: without the
    /// guard, every response arriving mid-sequence starts another one.
    /// </remarks>
    private bool _startupActive;

    /// <summary>
    /// Creates a master session. Pass a null handler for
    /// <see cref="NopHandler"/>.
    /// </summary>
    public MasterSession(MasterConfig config, IMasterHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.ApplyDefaults();
        _cfg = config;
        _handler = handler ?? new NopHandler();
        _time = config.TimeProvider!;
        _log = new ScopedLogger(
            config.Log!, ("role", "master"), ("outstation", config.RemoteAddr));

        // Assume the outstation's clock is good until it says otherwise, so a
        // capture from a healthy device is not littered with unsynchronized
        // stamps.
        _synchronized = true;
    }

    /// <summary>Returns a snapshot of the session counters.</summary>
    public MasterStats Stats
    {
        get
        {
            lock (_gate)
            {
                return _stats;
            }
        }
    }

    /// <summary>
    /// The internal indications from the most recent response.
    /// </summary>
    public Iin LastIin
    {
        get
        {
            lock (_gate)
            {
                return _lastIin;
            }
        }
    }

    /// <summary>Reports whether a connection is currently established.</summary>
    public bool Connected
    {
        get
        {
            lock (_gate)
            {
                return _connected;
            }
        }
    }

    private void SetConnected(bool v)
    {
        lock (_gate)
        {
            _connected = v;
        }
    }

    /// <summary>Connects and polls until the token is cancelled.</summary>
    public async Task RunAsync(IChannel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        _stack = new ProtocolStack(new StackConfig
        {
            LocalAddr = _cfg.LocalAddr,
            RemoteAddr = _cfg.RemoteAddr,
            IsMaster = true,
            UseConfirms = _cfg.UseLinkConfirms,
            MaxRetries = _cfg.LinkRetries,
            MaxRxFragment = _cfg.MaxRxFragment,
        });

        // Cancelling the token is how RunAsync is asked to stop, and a closed
        // channel is the same instruction arriving from the other direction.
        // Both end the loop by returning: a shutdown that reports itself as a
        // failure makes every caller write the same "unless I asked for it"
        // check.
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
                // Qualified: System.Threading.Channels declares a type of the
                // same name, and this is the transport's, not the queue's.
                return;
            }

            lock (_gate)
            {
                _stats.Connections++;
            }

            _log.Log(Dnp3LogLevel.Info, "connected", ("channel", channel.ToString()));

            _stack.Reset();
            SetConnected(true);
            _lastRx = _time.GetUtcNow();
            StartupSequence();

            try
            {
                await ServeAsync(conn, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                SetConnected(false);
                conn.Dispose();
                FailInflight(new NoConnectionException());
                _log.Log(Dnp3LogLevel.Info, "disconnected");
            }
        }
    }

    /// <summary>
    /// Queues the tasks the standard requires after connecting or after the
    /// outstation reports a restart.
    /// </summary>
    /// <remarks>
    /// The order is not negotiable: clear the restart indication, stop
    /// unsolicited reporting, take a complete picture, then re-enable
    /// unsolicited. Running the integrity poll before disabling unsolicited
    /// would race an event stream against the poll and produce a picture that
    /// is neither.
    /// </remarks>
    private void StartupSequence()
    {
        _sched.Clear();

        // The steps are chained rather than queued so nothing can be
        // interleaved between them. Queuing them relies on scheduler ordering
        // to keep the sequence intact, and a user-submitted poll arriving
        // mid-sequence would then land between the disable and the integrity
        // read — exactly the race the ordering exists to prevent.
        var steps = new List<MasterTask> { MasterTasks.ClearRestart() };

        if (_cfg.DisableUnsolOnStartup)
        {
            steps.Add(MasterTasks.Unsolicited(false, Class.Class123));
        }

        if (_cfg.IntegrityOnStartup)
        {
            steps.Add(MasterTasks.Scan(Class.All));
        }

        if (_cfg.UnsolClassMask != 0)
        {
            steps.Add(MasterTasks.Unsolicited(true, _cfg.UnsolClassMask));
        }

        for (var i = 0; i < steps.Count; i++)
        {
            steps[i].Startup = true;
            if (i < steps.Count - 1)
            {
                var next = steps[i + 1];
                steps[i].Next = () => next;
            }
        }

        _startupActive = true;
        Enqueue(steps[0]);
    }

    /// <summary>Schedules a task to run as soon as the session is free.</summary>
    private void Enqueue(MasterTask t)
    {
        t.Due = _time.GetUtcNow();
        _sched.Push(t);
    }

    /// <summary>Runs one connection.</summary>
    private async Task ServeAsync(Stream conn, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linked.Token;

        // The read loop only moves octets. Everything that touches protocol
        // state — the link state machines, the reassembler, the scheduler —
        // runs here, so the stack needs no locking and a send can never
        // interleave with the processing of an inbound frame.
        var rx = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
        {
            SingleWriter = true,
            SingleReader = true,
        });

        var readTask = ReadIntoAsync(conn, rx.Writer, ct);

        Task<bool>? rxWait = null;
        Task<bool>? submitWait = null;

        try
        {
            while (true)
            {
                RunDueTask();
                await FlushAsync(conn, ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested)
                {
                    return;
                }

                rxWait ??= rx.Reader.WaitToReadAsync(ct).AsTask();
                submitWait ??= _submit.Reader.WaitToReadAsync(ct).AsTask();

                var delay = Task.Delay(NextWakeup(), _time, ct);
                var completed = await Task.WhenAny(rxWait, submitWait, delay, readTask)
                    .ConfigureAwait(false);

                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (completed == readTask)
                {
                    // The peer closed or the socket failed; either way this
                    // connection is over.
                    return;
                }

                if (completed == rxWait)
                {
                    rxWait = null;
                    while (rx.Reader.TryRead(out var chunk))
                    {
                        _lastRx = _time.GetUtcNow();
                        try
                        {
                            _stack!.Receive(_sink, chunk, OnFragment);
                        }
                        catch (Dnp3Exception ex)
                        {
                            _log.Log(Dnp3LogLevel.Warn, "receive failed", ("err", ex.Message));
                            return;
                        }
                    }

                    continue;
                }

                if (completed == submitWait)
                {
                    submitWait = null;
                    while (_submit.Reader.TryRead(out var t))
                    {
                        Enqueue(t);
                    }

                    continue;
                }

                // The timer fired.
                if (CheckLinkTimeout())
                {
                    continue;
                }

                CheckTimeout();
                CheckKeepAlive();
            }
        }
        catch (OperationCanceledException)
        {
            // A cancelled session is a clean shutdown.
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            rx.Writer.TryComplete();

            // Surface nothing from the read loop: it ends with the connection.
            try
            {
                await readTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
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

                // Copy: the buffer is reused on the next read.
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
    /// <returns>
    /// Whether the link layer handled the tick, so the caller does not also age
    /// out the application request that is still legitimately in flight.
    /// </returns>
    private bool CheckLinkTimeout()
    {
        if (!_stack!.Pending || _time.GetUtcNow() < _linkDeadline)
        {
            return false;
        }

        bool failed;
        try
        {
            failed = _stack.OnTimeout(_sink);
        }
        catch (Dnp3Exception ex)
        {
            _log.Log(Dnp3LogLevel.Warn, "link retransmission failed", ("err", ex.Message));
            return false;
        }

        _linkDeadline = _time.GetUtcNow() + _cfg.LinkTimeout;
        if (failed)
        {
            _log.Log(Dnp3LogLevel.Warn, "link layer gave up on a frame");
            FailInflight(new Dnp3TimeoutException());
            return false;
        }

        return true;
    }

    /// <summary>
    /// Probes an idle link so a peer that has gone away is noticed before the
    /// next poll is due.
    /// </summary>
    private void CheckKeepAlive()
    {
        if (_cfg.KeepAlive <= TimeSpan.Zero || _inflight is not null || _stack!.Busy)
        {
            return;
        }

        if (_time.GetUtcNow() - _lastRx < _cfg.KeepAlive)
        {
            return;
        }

        _lastRx = _time.GetUtcNow();
        try
        {
            _stack.SendLinkStatusRequest(_sink);
        }
        catch (Dnp3Exception ex)
        {
            _log.Log(Dnp3LogLevel.Warn, "keep-alive failed", ("err", ex.Message));
            return;
        }

        _linkDeadline = _time.GetUtcNow() + _cfg.LinkTimeout;
        _log.Log(Dnp3LogLevel.Debug, "keep-alive sent");
    }

    /// <summary>Returns how long to sleep before something needs doing.</summary>
    private TimeSpan NextWakeup()
    {
        var now = _time.GetUtcNow();
        var floor = TimeSpan.FromMilliseconds(1);

        if (_inflight is not null)
        {
            return Max(_inflight.Deadline - now, floor);
        }

        if (_stack is not null && _stack.Pending)
        {
            return Max(_linkDeadline - now, floor);
        }

        if (_sched.Peek() is { } t)
        {
            return Max(t.Due - now, floor);
        }

        if (_cfg.KeepAlive > TimeSpan.Zero)
        {
            return Max(_lastRx + _cfg.KeepAlive - now, floor);
        }

        return TimeSpan.FromHours(1);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>Sends the next due task if nothing is in flight.</summary>
    private void RunDueTask()
    {
        if (_inflight is not null)
        {
            return;
        }

        var t = _sched.Peek();
        if (t is null || t.Due > _time.GetUtcNow())
        {
            return;
        }

        _sched.Pop();
        SendTask(t);
    }

    /// <summary>
    /// Builds and transmits one task, making it the in-flight request.
    /// </summary>
    /// <remarks>
    /// Chained tasks come through here too, which is what keeps a select and
    /// its operate on consecutive sequence numbers.
    /// </remarks>
    private void SendTask(MasterTask t)
    {
        _seq = (byte)((_seq + 1) % AppConstants.SeqModulus);

        byte[] fragment;
        try
        {
            var b = new FragmentBuilder(_cfg.MaxTxFragment);
            b.SetHeader(new AppHeader(
                new AppControl(Fir: true, Fin: true, Con: false, Uns: false, Seq: _seq),
                t.FuncCode,
                Iin.None));

            t.Build?.Invoke(b);
            fragment = b.ToArray();
        }
        catch (Dnp3Exception ex)
        {
            CompleteTask(t, ex);
            return;
        }

        try
        {
            _stack!.Send(_sink, fragment);
        }
        catch (Dnp3Exception ex)
        {
            _log.Log(Dnp3LogLevel.Warn, "send failed", ("task", t.Name), ("err", ex.Message));
            CompleteTask(t, ex);
            return;
        }

        t.Seq = _seq;
        lock (_gate)
        {
            _stats.TasksRun++;
        }

        _log.Log(Dnp3LogLevel.Debug, "task sent", ("task", t.Name), ("seq", _seq));

        if (t.NoResponse)
        {
            // Nothing will come back, so there is nothing to wait for.
            _inflight = null;
            CompleteTask(t, null);
            return;
        }

        t.Deadline = _time.GetUtcNow() + _cfg.ResponseTimeout;
        _inflight = t;
    }

    /// <summary>
    /// Fails the in-flight task when the outstation did not answer.
    /// </summary>
    private void CheckTimeout()
    {
        if (_inflight is null || _time.GetUtcNow() < _inflight.Deadline)
        {
            return;
        }

        var t = _inflight;
        _inflight = null;
        lock (_gate)
        {
            _stats.ResponseTimeouts++;
        }

        _log.Log(Dnp3LogLevel.Warn, "response timeout", ("task", t.Name), ("seq", t.Seq));
        CompleteTask(t, new Dnp3TimeoutException());
    }

    /// <summary>Finishes a task, rescheduling it if it is periodic.</summary>
    private void CompleteTask(MasterTask t, Exception? error)
    {
        // The startup sequence ends when its last step finishes, or when any
        // step fails — leaving the flag set would suppress the re-baseline a
        // genuine later restart needs.
        if (t.Startup && (error is not null || t.Next is null))
        {
            _startupActive = false;
        }

        lock (_gate)
        {
            if (error is not null)
            {
                _stats.TasksFailed++;
            }
            else
            {
                _stats.TasksSucceeded++;
            }
        }

        if (t.Period > TimeSpan.Zero)
        {
            // A periodic task keeps its slot in the schedule whether or not
            // this run succeeded; a failed poll should not stop the next one.
            _sched.Push(t.CloneForPeriod(_time.GetUtcNow() + t.Period));
        }

        t.Finish(error);
    }

    /// <summary>
    /// Abandons the in-flight task when the connection drops.
    /// </summary>
    private void FailInflight(Exception error)
    {
        if (_inflight is null)
        {
            return;
        }

        var t = _inflight;
        _inflight = null;
        CompleteTask(t, error);
    }

    /// <summary>Routes an incoming fragment.</summary>
    private void OnFragment(Received r)
    {
        lock (_gate)
        {
            _stats.FragmentsRx++;
        }

        var status = FragmentParser.ParseFragment(null, r.Fragment, out var frag, out var error);
        if (status != AppParseStatus.Ok)
        {
            _log.Log(Dnp3LogLevel.Warn, "malformed response", ("err", error));
            return;
        }

        if (!frag.Header.IsResponse)
        {
            _log.Log(
                Dnp3LogLevel.Debug,
                "ignoring a non-response fragment",
                ("func", frag.Header.Func.ToDisplayString()));
            return;
        }

        ObserveIin(frag.Header.Iin);

        if (frag.Header.Func == FuncCode.UnsolicitedResponse)
        {
            OnUnsolicited(frag);
            return;
        }

        OnSolicited(frag);
    }

    /// <summary>Reacts to the indications on every response.</summary>
    private void ObserveIin(Iin iin)
    {
        lock (_gate)
        {
            _lastIin = iin;
        }

        // NEED_TIME means the outstation's clock is not set, so the timestamps
        // it reports cannot be treated as synchronized.
        _synchronized = !iin.Has(Iin.NeedTime);

        if (!iin.Has(Iin.DeviceRestart))
        {
            return;
        }

        if (_startupActive)
        {
            // The sequence already running is the response to this. Its first
            // step is the write that clears the indication, so every fragment
            // until then still carries it — reacting again would restart the
            // sequence on its own output, indefinitely.
            return;
        }

        lock (_gate)
        {
            _stats.RestartsSeen++;
        }

        _log.Log(
            Dnp3LogLevel.Info,
            "outstation reported a restart; re-running the startup sequence");

        // The outstation's event buffer is gone, so the master's picture is
        // stale in a way no incremental poll can fix. Only a full re-baseline
        // restores it.
        StartupSequence();
    }

    /// <summary>Handles a response to a request we sent.</summary>
    private void OnSolicited(Fragment frag)
    {
        var t = _inflight;
        if (t is null)
        {
            _log.Log(
                Dnp3LogLevel.Debug,
                "response with nothing in flight",
                ("seq", frag.Header.Control.Seq));
            return;
        }

        if (frag.Header.Control.Seq != t.Seq)
        {
            // A response for a request we have already given up on. Acting on
            // it would attribute stale data to the current poll.
            _log.Log(
                Dnp3LogLevel.Debug,
                "response sequence mismatch",
                ("got", frag.Header.Control.Seq), ("want", t.Seq));
            return;
        }

        Deliver(frag, unsolicited: false);
        t.OnFragment?.Invoke(frag);

        if (frag.Header.Control.Con)
        {
            SendConfirm(frag.Header.Control.Seq, unsolicited: false);
        }

        if (!frag.Header.Control.Fin)
        {
            // More fragments are coming. Extend the deadline rather than
            // completing, so a large integrity poll is not cut off midway.
            t.Deadline = _time.GetUtcNow() + _cfg.ResponseTimeout;
            return;
        }

        _inflight = null;
        t.OnDone?.Invoke(frag.Header.Iin);

        // A chained task runs immediately rather than going back to the
        // scheduler, so nothing can be interleaved between the two.
        if (t.Next is not null)
        {
            var nt = t.Next();
            if (nt is not null)
            {
                nt.Done = t.Done;
                t.Done = null;
                CompleteTask(t, null);
                SendTask(nt);
                return;
            }
        }

        CompleteTask(t, null);
    }

    /// <summary>Handles a fragment the outstation sent on its own.</summary>
    private void OnUnsolicited(Fragment frag)
    {
        lock (_gate)
        {
            _stats.Unsolicited++;
        }

        var seq = frag.Header.Control.Seq;
        var duplicate = _hasUnsolSeq && seq == _unsolSeq;
        _unsolSeq = seq;
        _hasUnsolSeq = true;

        if (duplicate)
        {
            // Our confirm was lost, not the data. Confirm again but do not
            // deliver the measurements a second time.
            _log.Log(Dnp3LogLevel.Debug, "duplicate unsolicited response", ("seq", seq));
        }
        else
        {
            Deliver(frag, unsolicited: true);
        }

        if (frag.Header.Control.Con)
        {
            SendConfirm(seq, unsolicited: true);
        }
    }

    /// <summary>Answers a fragment that asked to be confirmed.</summary>
    private void SendConfirm(byte seq, bool unsolicited)
    {
        var dst = new List<byte>(AppConstants.RequestHeaderSize);
        HeaderCodec.AppendHeader(dst, new AppHeader(
            new AppControl(Fir: true, Fin: true, Con: false, Uns: unsolicited, Seq: seq),
            FuncCode.Confirm,
            Iin.None));

        try
        {
            _stack!.Send(_sink, [.. dst]);
        }
        catch (Dnp3Exception ex)
        {
            _log.Log(Dnp3LogLevel.Warn, "confirm failed", ("err", ex.Message), ("seq", seq));
        }
    }

    /// <summary>
    /// Decodes a fragment's measurements and hands them to the handler.
    /// </summary>
    private void Deliver(Fragment frag, bool unsolicited)
    {
        var info = new ResponseInfo
        {
            Iin = frag.Header.Iin,
            Unsolicited = unsolicited,
            Sequence = frag.Header.Control.Seq,
            Received = _time.GetUtcNow(),
        };

        _handler.BeginFragment(info);
        try
        {
            var ctx = new Context { Synchronized = _synchronized };
            foreach (var h in frag.Objects)
            {
                // A group 51 object sets the base for the relative-time events
                // that follow it in this fragment.
                if (h.Group == 51 && h.Data.Length >= CommandObjects.Time48Size)
                {
                    ctx = ctx.WithCto(CommandObjects.ParseTime48(h.Data.Span).Time);
                    continue;
                }

                Dispatcher.Dispatch(_handler, h, ctx);
            }
        }
        finally
        {
            _handler.EndFragment(info);
        }
    }
}
