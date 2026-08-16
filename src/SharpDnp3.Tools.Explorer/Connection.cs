// Copyright (C) 2026 Ricardo Olsen / DSC Systems.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option)
// any later version. It is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details, in the LICENSE file at
// the root of this repository or at <https://www.gnu.org/licenses/>.

using System.Globalization;
using System.Text;
using System.Threading.Channels;
using SharpDnp3.Master;
using SharpDnp3.Objects;

namespace SharpDnp3.Tools.Explorer;

/// <summary>Anything the model can be told about.</summary>
public interface IMsg
{
}

/// <summary>
/// Drives the age column, the clock and the rate meter without needing a repaint
/// on every protocol event.
/// </summary>
public sealed record TickMsg(DateTimeOffset At) : IMsg;

/// <summary>One keystroke, named the way the reader names it.</summary>
public sealed record KeyMsg(string Key) : IMsg;

/// <summary>One measurement arriving from the outstation.</summary>
public sealed record UpdateMsg : IMsg
{
    /// <summary>Which measurement type it is.</summary>
    public PointType Type { get; init; }

    /// <summary>The point index.</summary>
    public ushort Index { get; init; }

    /// <summary>The value, rendered.</summary>
    public string Value { get; init; } = "";

    /// <summary>The value as a number, when it has one.</summary>
    public double Num { get; init; }

    /// <summary>Whether <see cref="Num"/> means anything.</summary>
    public bool HasNum { get; init; }

    /// <summary>The quality octet.</summary>
    public Flags Flags { get; init; }

    /// <summary>The outstation's timestamp.</summary>
    public Timestamp Stamp { get; init; }

    /// <summary>Whether it arrived as an event.</summary>
    public bool IsEvent { get; init; }

    /// <summary>The event class it came from.</summary>
    public Class Class { get; init; }

    /// <summary>The group and variation it was encoded as.</summary>
    public GroupVar GV { get; init; }
}

/// <summary>Reports session state.</summary>
public sealed record StatusMsg : IMsg
{
    /// <summary>What to show in the header.</summary>
    public string Text { get; init; } = "";

    /// <summary>Whether the link is up.</summary>
    public bool Connected { get; init; }

    /// <summary>The session's counters.</summary>
    public MasterStats Stats { get; init; }

    /// <summary>The internal indications last reported.</summary>
    public string Iin { get; init; } = "";

    /// <summary>Whatever went wrong, if anything did.</summary>
    public string Error { get; init; } = "";
}

/// <summary>A line for the activity log.</summary>
public sealed record LogMsg(string Level, string Text) : IMsg;

/// <summary>Reports the outcome of something the operator asked for.</summary>
public sealed record CommandResultMsg(string Text, bool Ok = false) : IMsg
{
    /// <summary>How loudly the outcome should be reported.</summary>
    public string Level => Ok ? "ok" : "error";
}

/// <summary>
/// One control the operator can issue, carried with the words that will be shown
/// to them before it goes out.
/// </summary>
public readonly record struct ControlOp(string Label, Command Command)
{
    /// <summary>Describes the control and the mode it would be issued in.</summary>
    public string Describe(bool sbo) => Label + " — " + Model.ControlMode(sbo);

    /// <summary>Builds the plain close or open of a binary output.</summary>
    public static ControlOp Latch(ushort index, bool closing) => closing
        ? new ControlOp(
            string.Format(CultureInfo.InvariantCulture, "close BO {0} (latch on)", index),
            Command.LatchOn(index))
        : new ControlOp(
            string.Format(CultureInfo.InvariantCulture, "open BO {0} (latch off)", index),
            Command.LatchOff(index));
}

/// <summary>The boundary between the DNP3 session and the UI.</summary>
/// <remarks>
/// Everything crosses it as a message on a channel. The session tasks never
/// touch the model, and the model never calls into the session synchronously: a
/// request is a <see cref="Cmd"/> that runs off the update loop and returns a
/// result message. A poll that takes five seconds therefore costs nothing but
/// five seconds of that one command — the UI keeps repainting throughout.
/// </remarks>
public sealed class Connection
{
    /// <summary>
    /// Bounds any one operator request. It is generous compared with the
    /// session's own response timeout because a scan of a large device is
    /// several fragments, each of which gets that timeout.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly Channel<IMsg> _msgs;
    private readonly Lock _gate = new();
    private MasterSession? _session;
    private LinkParams _current;
    private long _dropped;

    /// <summary>Creates the boundary, with the queue the UI drains.</summary>
    public Connection(Channel<IMsg> msgs, CancellationToken cancellationToken)
    {
        _msgs = msgs;
        Token = cancellationToken;
    }

    /// <summary>The token that ends everything when the tool is quitting.</summary>
    public CancellationToken Token { get; }

    /// <summary>The supervisor that owns the session's lifecycle.</summary>
    public Supervisor Supervisor { get; set; } = null!;

    /// <summary>
    /// How many messages the UI was too slow to take, which is worth admitting
    /// rather than hiding: a table quietly missing rows is worse than a table
    /// that says it is missing rows.
    /// </summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Installs a newly built session as the live one.</summary>
    public void Adopt(MasterSession session, LinkParams p)
    {
        lock (_gate)
        {
            _session = session;
            _current = p;
        }
    }

    /// <summary>Returns the live session, or null while a reconnect is in flight.</summary>
    public MasterSession? Session
    {
        get
        {
            lock (_gate)
            {
                return _session;
            }
        }
    }

    /// <summary>The parameters the live session was built with.</summary>
    public LinkParams Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// What the header shows, so an operator with several terminals open knows
    /// which device this one is pointed at.
    /// </summary>
    public string Target => Current.Target;

    /// <summary>Names the link for the overview panel.</summary>
    public string Transport => Current.Transport;

    /// <summary>Delivers a message to the UI, dropping it if the UI has fallen behind.</summary>
    /// <remarks>
    /// Dropping is deliberate. The alternative is blocking the session on a slow
    /// terminal, which would stall the protocol — an operator's scrollback is
    /// not worth a missed poll.
    /// </remarks>
    public void Push(IMsg msg)
    {
        if (!_msgs.Writer.TryWrite(msg))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Rebuilds the session with new parameters.</summary>
    /// <remarks>
    /// It runs as a command, off the update loop, because tearing a session down
    /// means waiting for its tasks: doing that in the loop would freeze the
    /// interface for exactly as long as the old link took to notice it was
    /// closed.
    /// </remarks>
    public Cmd Reconnect(LinkParams p) => async () =>
    {
        await Supervisor.StartAsync(p).ConfigureAwait(false);
        return new CommandResultMsg("connecting to " + p.Target, true);
    };

    // ---------- Actions ----------
    //
    // Each action is a Cmd: it runs off the update loop and reports back as a
    // message, so a slow outstation never freezes the interface.

    /// <summary>
    /// Wraps a session call as a command, naming it for the log either way.
    /// </summary>
    /// <remarks>
    /// The session is resolved when the command runs rather than when it is
    /// built, because a reconnect replaces it: a command holding the old one
    /// would be sent to a device the operator has already navigated away from.
    /// </remarks>
    public Cmd Do(string what, Func<MasterSession, CancellationToken, Task> fn) => async () =>
    {
        var session = Session;
        if (session is null)
        {
            return new CommandResultMsg(what + ": not connected");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cts.CancelAfter(RequestTimeout);

        try
        {
            await fn(session, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new CommandResultMsg(what + " failed: timed out");
        }
        catch (Exception ex) when (ex is Dnp3Exception or IOException)
        {
            return new CommandResultMsg(what + " failed: " + ex.Message);
        }

        return new CommandResultMsg(what + " complete", true);
    };

    /// <summary>Reads every class, re-baselining the master's picture.</summary>
    public Cmd IntegrityPoll() =>
        Do("integrity poll", static (s, ct) => s.IntegrityPollAsync(ct));

    /// <summary>Reads the event classes.</summary>
    public Cmd ClassPoll() =>
        Do("class 1/2/3 poll", static (s, ct) => s.ScanClassesAsync(Class.Class123, ct));

    /// <summary>Reads one contiguous range of one group.</summary>
    public Cmd RangeScan(byte group, byte variation, ushort start, ushort stop)
    {
        var what = string.Format(
            CultureInfo.InvariantCulture,
            "range scan g{0}v{1} {2}-{3}", group, variation, start, stop);

        return Do(what, (s, ct) => s.ScanRangeAsync(group, variation, start, stop, ct));
    }

    /// <summary>
    /// Sets the outstation's clock, optionally measuring the link delay first —
    /// which is what a slow serial link needs and Ethernet does not.
    /// </summary>
    public Cmd SyncTime(bool withDelay) => withDelay
        ? Do("time sync (with delay measurement)",
            static (s, ct) => s.SyncTimeWithDelayAsync(ct))
        : Do("time sync", static (s, ct) => s.SyncTimeAsync(ct));

    /// <summary>Turns unsolicited reporting on or off for classes 1, 2 and 3.</summary>
    public Cmd Unsolicited(bool enable) => enable
        ? Do("enable unsolicited 1/2/3",
            static (s, ct) => s.EnableUnsolicitedAsync(Class.Class123, ct))
        : Do("disable unsolicited 1/2/3",
            static (s, ct) => s.DisableUnsolicitedAsync(Class.Class123, ct));

    /// <summary>Asks the outstation to restart.</summary>
    public Cmd Restart(RestartMode mode) =>
        Do(mode.ToDisplayString() + " restart", (s, ct) => s.RestartAsync(mode, ct));

    /// <summary>Writes one analog input's deadband.</summary>
    public Cmd WriteDeadband(ushort index, float v)
    {
        var what = string.Format(
            CultureInfo.InvariantCulture, "deadband AI {0} = {1}", index, v);

        return Do(what, (s, ct) =>
            s.WriteDeadbandAsync(new Dictionary<ushort, float> { [index] = v }, ct));
    }

    /// <summary>Issues a control, either select-before-operate or direct.</summary>
    /// <remarks>
    /// Select-before-operate is the default because this is an interactive tool
    /// driven by a person: the select is the outstation's opportunity to refuse
    /// before anything in the substation moves. Direct operate is offered
    /// because some devices do not implement select, and a tool that cannot talk
    /// to them is no use in front of one.
    /// </remarks>
    public Cmd Operate(ControlOp op, bool sbo) => async () =>
    {
        var session = Session;
        if (session is null)
        {
            return new CommandResultMsg(op.Label + ": not connected");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cts.CancelAfter(RequestTimeout);

        try
        {
            var result = sbo
                ? await session.SelectAndOperateAsync([op.Command], cts.Token).ConfigureAwait(false)
                : await session.DirectOperateAsync([op.Command], cts.Token).ConfigureAwait(false);

            if (!result.OK())
            {
                // A refusal is not an error in the transport sense, and
                // reporting it as success because the exchange completed is how
                // an operator comes to believe a breaker moved when it did not.
                return new CommandResultMsg(
                    $"{op.Label} refused: {result.Error()?.Message ?? "no status"}");
            }

            return new CommandResultMsg(op.Label + ": accepted", true);
        }
        catch (OperationCanceledException)
        {
            return new CommandResultMsg(op.Label + " failed: timed out");
        }
        catch (Exception ex) when (ex is Dnp3Exception or IOException)
        {
            return new CommandResultMsg($"{op.Label} failed: {ex.Message}");
        }
    };

    /// <summary>Renders a binary state.</summary>
    public static string BoolText(bool v) => v ? "ON" : "OFF";

    /// <summary>Renders a binary state as a number, so it can be trended.</summary>
    public static double BoolNum(bool v) => v ? 1 : 0;

    /// <summary>Renders a number for a table cell.</summary>
    public static string FormatFloat(double v)
    {
        if (v == Math.Truncate(v) && v is < 1e15 and > -1e15)
        {
            return ((long)v).ToString(CultureInfo.InvariantCulture);
        }

        return v.ToString("F3", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Renders an octet string for a table cell, showing the bytes of anything
    /// that is not text rather than letting control characters loose in the
    /// terminal.
    /// </summary>
    public static string Printable(byte[]? b)
    {
        if (b is null || b.Length == 0)
        {
            return "";
        }

        foreach (var c in b)
        {
            if (c is < 0x20 or > 0x7e)
            {
                return Convert.ToHexString(b).ToLowerInvariant();
            }
        }

        return Encoding.ASCII.GetString(b);
    }
}

/// <summary>Feeds the UI from the session.</summary>
/// <remarks>
/// It holds its own session rather than reading the connection's, because a
/// handler belongs to the session it was built with: reading the live one would
/// report the successor's statistics against the predecessor's fragments during
/// a reconnect.
/// </remarks>
public sealed class UiHandler : NopHandler
{
    private readonly Connection _conn;

    /// <summary>Creates a handler feeding <paramref name="conn"/>.</summary>
    public UiHandler(Connection conn) => _conn = conn;

    /// <summary>The session this handler belongs to.</summary>
    public MasterSession? Session { get; set; }

    /// <inheritdoc/>
    public override void BeginFragment(ResponseInfo info)
    {
        _conn.Push(new StatusMsg
        {
            Text = "connected",
            Connected = true,
            Stats = Session?.Stats ?? default,
            Iin = info.Iin.ToString(),
        });
    }

    /// <inheritdoc/>
    public override void HandleBinary(HeaderInfo info, IReadOnlyList<Indexed<Binary>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
        {
            _conn.Push(new UpdateMsg
            {
                Type = PointType.Binary,
                Index = v.Index,
                Value = Connection.BoolText(v.Value.Value),
                Num = Connection.BoolNum(v.Value.Value),
                HasNum = true,
                Flags = v.Value.Flags,
                Stamp = v.Value.Time,
                IsEvent = info.IsEvent,
                Class = info.Class,
                GV = info.GV,
            });
        }
    }

    /// <inheritdoc/>
    public override void HandleDoubleBit(
        HeaderInfo info, IReadOnlyList<Indexed<DoubleBitBinary>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
        {
            _conn.Push(new UpdateMsg
            {
                Type = PointType.DoubleBitBinary,
                Index = v.Index,
                Value = v.Value.Value.ToDisplayString(),
                Flags = v.Value.Flags,
                Stamp = v.Value.Time,
                IsEvent = info.IsEvent,
                Class = info.Class,
                GV = info.GV,
            });
        }
    }

    /// <inheritdoc/>
    public override void HandleCounter(HeaderInfo info, IReadOnlyList<Indexed<Counter>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
        {
            _conn.Push(new UpdateMsg
            {
                Type = PointType.Counter,
                Index = v.Index,
                Value = v.Value.Value.ToString(CultureInfo.InvariantCulture),
                Num = v.Value.Value,
                HasNum = true,
                Flags = v.Value.Flags,
                Stamp = v.Value.Time,
                IsEvent = info.IsEvent,
                Class = info.Class,
                GV = info.GV,
            });
        }
    }

    /// <inheritdoc/>
    public override void HandleFrozenCounter(
        HeaderInfo info, IReadOnlyList<Indexed<FrozenCounter>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
        {
            _conn.Push(new UpdateMsg
            {
                Type = PointType.FrozenCounter,
                Index = v.Index,
                Value = v.Value.Value.ToString(CultureInfo.InvariantCulture),
                Num = v.Value.Value,
                HasNum = true,
                Flags = v.Value.Flags,
                Stamp = v.Value.Time,
                IsEvent = info.IsEvent,
                Class = info.Class,
                GV = info.GV,
            });
        }
    }

    /// <inheritdoc/>
    public override void HandleAnalog(HeaderInfo info, IReadOnlyList<Indexed<Analog>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
        {
            _conn.Push(new UpdateMsg
            {
                Type = PointType.Analog,
                Index = v.Index,
                Value = Connection.FormatFloat(v.Value.Value),
                Num = v.Value.Value,
                HasNum = true,
                Flags = v.Value.Flags,
                Stamp = v.Value.Time,
                IsEvent = info.IsEvent,
                Class = info.Class,
                GV = info.GV,
            });
        }
    }

    /// <inheritdoc/>
    public override void HandleBinaryOutputStatus(
        HeaderInfo info, IReadOnlyList<Indexed<BinaryOutputStatus>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
        {
            _conn.Push(new UpdateMsg
            {
                Type = PointType.BinaryOutputStatus,
                Index = v.Index,
                Value = Connection.BoolText(v.Value.Value),
                Num = Connection.BoolNum(v.Value.Value),
                HasNum = true,
                Flags = v.Value.Flags,
                Stamp = v.Value.Time,
                IsEvent = info.IsEvent,
                Class = info.Class,
                GV = info.GV,
            });
        }
    }

    /// <inheritdoc/>
    public override void HandleAnalogOutputStatus(
        HeaderInfo info, IReadOnlyList<Indexed<AnalogOutputStatus>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
        {
            _conn.Push(new UpdateMsg
            {
                Type = PointType.AnalogOutputStatus,
                Index = v.Index,
                Value = Connection.FormatFloat(v.Value.Value),
                Num = v.Value.Value,
                HasNum = true,
                Flags = v.Value.Flags,
                Stamp = v.Value.Time,
                IsEvent = info.IsEvent,
                Class = info.Class,
                GV = info.GV,
            });
        }
    }

    /// <summary>
    /// Shows the strings a device reports about itself — point names, firmware
    /// versions, serial numbers.
    /// </summary>
    /// <remarks>
    /// They are the fastest way to find out what an unfamiliar device actually
    /// is, so they belong in the table rather than in a debug log.
    /// </remarks>
    public override void HandleOctetString(HeaderInfo info, IReadOnlyList<Indexed<byte[]>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
        {
            _conn.Push(new UpdateMsg
            {
                Type = PointType.OctetString,
                Index = v.Index,
                Value = Connection.Printable(v.Value),
                Flags = Flags.Online,
                IsEvent = info.IsEvent,
                Class = info.Class,
                GV = info.GV,
            });
        }
    }
}
