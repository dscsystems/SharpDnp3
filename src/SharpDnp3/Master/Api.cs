// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Globalization;
using SharpDnp3.App;

namespace SharpDnp3.Master;

// The request methods below are safe to call from any thread. Each hands a
// task to the session loop and waits for it to finish, so no caller ever
// touches protocol state directly.
public sealed partial class MasterSession
{
    /// <summary>Submits a task and waits for its outcome.</summary>
    /// <remarks>
    /// The caller keeps its own reference to the completion source rather than
    /// reading it back off the task. Once the task is submitted it belongs to
    /// the session loop, which clears the field when it finishes — reading it
    /// here afterwards would be a race on state the session owns.
    /// </remarks>
    private async Task RunTaskAsync(MasterTask t, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        t.Done = done;

        await _submit.Writer.WriteAsync(t, cancellationToken).ConfigureAwait(false);

        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), done);

        await done.Task.ConfigureAwait(false);
    }

    /// <summary>Reads the given classes once and waits for the response.</summary>
    /// <remarks>
    /// An integrity poll is <see cref="Class.All"/>; a routine event poll is
    /// <see cref="Class.Class123"/>.
    /// </remarks>
    public Task ScanClassesAsync(Class mask, CancellationToken cancellationToken = default)
    {
        if (mask == 0)
        {
            throw new BadConfigException("master: dnp3: invalid configuration: empty class mask");
        }

        return RunTaskAsync(MasterTasks.Scan(mask), cancellationToken);
    }

    /// <summary>
    /// Reads every class, which re-baselines the master's picture of the
    /// outstation.
    /// </summary>
    public Task IntegrityPollAsync(CancellationToken cancellationToken = default) =>
        ScanClassesAsync(Class.All, cancellationToken);

    /// <summary>
    /// Reads a contiguous index range of one group and variation.
    /// </summary>
    /// <remarks>
    /// Pass variation zero to let the outstation choose its default, which is
    /// what a master normally wants: the outstation knows which encoding
    /// carries its points without loss.
    /// </remarks>
    public Task ScanRangeAsync(
        byte group,
        byte variation,
        ushort start,
        ushort stop,
        CancellationToken cancellationToken = default)
    {
        if (start > stop)
        {
            throw new BadConfigException(string.Format(
                CultureInfo.InvariantCulture,
                "master: dnp3: invalid configuration: start {0} above stop {1}", start, stop));
        }

        return RunTaskAsync(MasterTasks.RangeScan(group, variation, start, stop), cancellationToken);
    }

    /// <summary>Schedules a recurring class poll.</summary>
    /// <remarks>
    /// It returns as soon as the scan is queued; the poll then runs for the
    /// life of the session. Failures do not stop it — a poll that fails because
    /// the link dropped must keep trying once the link returns.
    /// </remarks>
    public async Task AddPeriodicScanAsync(
        TimeSpan period,
        Class mask,
        CancellationToken cancellationToken = default)
    {
        if (period <= TimeSpan.Zero)
        {
            throw new BadConfigException(
                "master: dnp3: invalid configuration: period must be positive");
        }

        if (mask == 0)
        {
            throw new BadConfigException("master: dnp3: invalid configuration: empty class mask");
        }

        var t = MasterTasks.Scan(mask);
        t.Period = period;

        await _submit.Writer.WriteAsync(t, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the outstation to restart and waits for its acknowledgement.
    /// </summary>
    /// <remarks>
    /// The outstation answers with how long it expects to be unavailable before
    /// it actually restarts, so a successful return means the request was
    /// accepted, not that the device is back.
    /// </remarks>
    public Task RestartAsync(RestartMode mode, CancellationToken cancellationToken = default) =>
        RunTaskAsync(MasterTasks.Restart(mode), cancellationToken);

    /// <summary>Sets the outstation's clock.</summary>
    public Task WriteTimeAsync(DateTimeOffset t, CancellationToken cancellationToken = default) =>
        RunTaskAsync(MasterTasks.WriteTime(t), cancellationToken);

    /// <summary>
    /// Sets the outstation's clock to the current time using the LAN procedure,
    /// which writes the time directly.
    /// </summary>
    /// <remarks>
    /// It assumes the transit delay is negligible against the outstation's
    /// timestamp resolution — true over Ethernet, not over a slow serial link.
    /// Use <see cref="SyncTimeWithDelayAsync"/> there.
    /// </remarks>
    public Task SyncTimeAsync(CancellationToken cancellationToken = default) =>
        WriteTimeAsync(_time.GetUtcNow(), cancellationToken);

    /// <summary>
    /// Sets the outstation's clock using the serial procedure: measure the
    /// turnaround with DELAY_MEASURE, then write a time already corrected for
    /// it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The correction is the round trip less the outstation's own processing
    /// delay, halved — the one-way transit. Without it the outstation's clock
    /// lands late by that amount, which over a 1200 baud link is easily tens of
    /// milliseconds and puts every event it stamps into the past.
    /// </para>
    /// <para>
    /// The two requests are chained so nothing can be scheduled between them; a
    /// poll landing in the middle would be measured into the correction.
    /// </para>
    /// </remarks>
    public Task SyncTimeWithDelayAsync(CancellationToken cancellationToken = default)
    {
        uint outstationDelay = 0;
        DateTimeOffset gotAt = default;

        var measure = MasterTasks.DelayMeasure(ms => outstationDelay = ms);
        measure.OnDone = _ => gotAt = _time.GetUtcNow();

        var sentAt = _time.GetUtcNow();
        measure.Next = () =>
        {
            // Round trip minus what the outstation says it spent, halved.
            var roundTrip = gotAt - sentAt;
            var processing = TimeSpan.FromMilliseconds(outstationDelay);
            var oneWay = roundTrip - processing;
            if (oneWay < TimeSpan.Zero)
            {
                oneWay = TimeSpan.Zero;
            }
            else
            {
                oneWay /= 2;
            }

            _log.Log(
                Dnp3LogLevel.Debug,
                "serial time sync",
                ("round_trip", roundTrip),
                ("outstation_delay", processing),
                ("one_way", oneWay));

            return MasterTasks.WriteTime(_time.GetUtcNow() + oneWay);
        };

        return RunTaskAsync(measure, cancellationToken);
    }

    /// <summary>Sets the analog deadbands of one or more points.</summary>
    /// <remarks>
    /// A deadband is how a master tells an outstation how much a point must
    /// move before it is worth an event. Raising it is the usual answer to an
    /// analog that chatters; lowering it is how a point that matters gets
    /// reported promptly.
    /// </remarks>
    public Task WriteDeadbandAsync(
        IReadOnlyDictionary<ushort, float> deadbands,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deadbands);

        if (deadbands.Count == 0)
        {
            throw new BadConfigException("master: dnp3: invalid configuration: no deadbands");
        }

        if (deadbands.Count > 255)
        {
            throw new BadConfigException(string.Format(
                CultureInfo.InvariantCulture,
                "master: dnp3: invalid configuration: {0} deadbands exceeds the 255 a one-octet count can carry",
                deadbands.Count));
        }

        return RunTaskAsync(MasterTasks.WriteDeadband(deadbands), cancellationToken);
    }

    /// <summary>Turns on unsolicited reporting for a set of classes.</summary>
    public Task EnableUnsolicitedAsync(Class mask, CancellationToken cancellationToken = default) =>
        RunTaskAsync(MasterTasks.Unsolicited(true, mask), cancellationToken);

    /// <summary>Turns off unsolicited reporting for a set of classes.</summary>
    public Task DisableUnsolicitedAsync(Class mask, CancellationToken cancellationToken = default) =>
        RunTaskAsync(MasterTasks.Unsolicited(false, mask), cancellationToken);

    // ---------- Controls ----------

    /// <summary>Executes commands immediately, without a select.</summary>
    /// <remarks>
    /// It is the right choice for an automated action. An operator-initiated
    /// control on plant that matters should use
    /// <see cref="SelectAndOperateAsync(IReadOnlyList{Command}, CancellationToken)"/>,
    /// which gives the outstation a chance to refuse before anything moves.
    /// </remarks>
    public async Task<CommandResult> DirectOperateAsync(
        IReadOnlyList<Command> commands,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);

        if (commands.Count == 0)
        {
            throw new BadConfigException("master: dnp3: invalid configuration: no commands");
        }

        var result = new CommandResult { Commands = commands };
        await RunTaskAsync(
            MasterTasks.CommandTask(FuncCode.DirectOperate, commands, result),
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>Executes commands immediately, without a select.</summary>
    public Task<CommandResult> DirectOperateAsync(params Command[] commands) =>
        DirectOperateAsync(commands, CancellationToken.None);

    /// <summary>Executes commands and asks for no response.</summary>
    /// <remarks>
    /// Nothing comes back, so nothing can be checked: this returns as soon as
    /// the request is on the wire. Use it only where the outcome genuinely does
    /// not need confirming.
    /// </remarks>
    public Task DirectOperateNoReplyAsync(
        IReadOnlyList<Command> commands,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);

        if (commands.Count == 0)
        {
            throw new BadConfigException("master: dnp3: invalid configuration: no commands");
        }

        var result = new CommandResult { Commands = commands };
        var t = MasterTasks.CommandTask(FuncCode.DirectOperateNR, commands, result);
        t.NoResponse = true;
        return RunTaskAsync(t, cancellationToken);
    }

    /// <summary>
    /// Runs the two-pass control sequence: select, check the outstation
    /// accepted it, then operate.
    /// </summary>
    /// <remarks>
    /// The select is not a formality. It is the outstation's opportunity to say
    /// "not that point, not right now" before anything in the substation moves,
    /// and a failed select must never be followed by an operate.
    /// </remarks>
    public async Task<CommandResult> SelectAndOperateAsync(
        IReadOnlyList<Command> commands,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);

        if (commands.Count == 0)
        {
            throw new BadConfigException("master: dnp3: invalid configuration: no commands");
        }

        var selectResult = new CommandResult { Commands = commands };
        var operateResult = new CommandResult { Commands = commands };

        var sel = MasterTasks.CommandTask(FuncCode.Select, commands, selectResult);

        // Chaining rather than submitting two tasks: the standard requires the
        // OPERATE to carry the sequence number one above the SELECT, so
        // anything scheduled between them — a periodic poll, say — would make
        // the outstation reject the operate with NO_SELECT.
        sel.Next = () => selectResult.OK()
            ? MasterTasks.CommandTask(FuncCode.Operate, commands, operateResult)
            : null;

        await RunTaskAsync(sel, cancellationToken).ConfigureAwait(false);

        var selErr = selectResult.Error();
        if (selErr is not null)
        {
            throw new Dnp3Exception("select rejected: " + selErr.Message, selErr);
        }

        return operateResult;
    }

    /// <summary>Runs the two-pass control sequence.</summary>
    public Task<CommandResult> SelectAndOperateAsync(params Command[] commands) =>
        SelectAndOperateAsync(commands, CancellationToken.None);
}
