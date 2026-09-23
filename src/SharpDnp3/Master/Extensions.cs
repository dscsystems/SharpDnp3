// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Requests a test and commissioning tool needs that a production master does
// not: freezes, class assignment, an explicit restart clear, the two halves of
// select-before-operate issued on their own, and an arbitrary request built
// from object headers.

using SharpDnp3.App;
using SharpDnp3.Objects;

namespace SharpDnp3.Master;

/// <summary>What a counter freeze does to the running counter.</summary>
public enum FreezeMode : byte
{
    /// <summary>Copies the running value into the frozen counter (FC 7).</summary>
    Freeze = 0,

    /// <summary>Copies the running value and then clears it (FC 9).</summary>
    FreezeAndClear,
}

/// <summary>The parsed outcome of a request sent with <see cref="MasterSession.SendRequestAsync"/>.</summary>
public sealed class RawResponse
{
    /// <summary>Every response fragment received, in order.</summary>
    public List<RawFragment> Fragments { get; } = [];

    /// <summary>The internal indications of the final fragment.</summary>
    public Iin Iin { get; internal set; }
}

/// <summary>One response fragment: its header, object headers and the raw octets.</summary>
public sealed record RawFragment(AppHeader Header, IReadOnlyList<ObjectHeader> Objects, ReadOnlyMemory<byte> Raw);

public sealed partial class MasterSession
{
    /// <summary>Freezes counters, optionally clearing them, over an index range or all of them.</summary>
    /// <remarks>
    /// Frozen values are reported as group 21 static data and group 23 events,
    /// so a test typically freezes and then reads class 0 or the group 21 range
    /// to compare the frozen value with the running one.
    /// </remarks>
    public Task FreezeCountersAsync(
        FreezeMode mode,
        bool noAck = false,
        ushort? start = null,
        ushort? stop = null,
        CancellationToken cancellationToken = default)
    {
        var fc = (mode, noAck) switch
        {
            (FreezeMode.Freeze, false) => FuncCode.ImmedFreeze,
            (FreezeMode.Freeze, true) => FuncCode.ImmedFreezeNR,
            (FreezeMode.FreezeAndClear, false) => FuncCode.FreezeClear,
            _ => FuncCode.FreezeClearNR,
        };

        var header = RangeOrAll(20, start, stop);
        var t = new MasterTask
        {
            Name = "freeze-" + fc.ToDisplayString(),
            FuncCode = fc,
            Priority = TaskPriority.Command,
            Build = b => AddExt(b, header),
            NoResponse = noAck,
        };

        return RunTaskAsync(t, cancellationToken);
    }

    /// <summary>Assigns points of one static group to an event class (FC 22).</summary>
    /// <remarks>
    /// <paramref name="cls"/> must be a single class; <see cref="Class.Class0"/>
    /// removes the points from event reporting.
    /// </remarks>
    public Task AssignClassAsync(
        Class cls,
        byte group,
        ushort? start = null,
        ushort? stop = null,
        CancellationToken cancellationToken = default)
    {
        byte classVariation = cls switch
        {
            Class.Class0 => 1,
            Class.Class1 => 2,
            Class.Class2 => 3,
            Class.Class3 => 4,
            _ => throw new BadConfigException("master: dnp3: invalid configuration: assign class takes exactly one class"),
        };

        var points = RangeOrAll(group, start, stop);
        var t = new MasterTask
        {
            Name = "assign-class",
            FuncCode = FuncCode.AssignClass,
            Priority = TaskPriority.Command,
            Build = b =>
            {
                AddExt(b, FragmentFactory.ReadAllObjects(60, classVariation));
                AddExt(b, points);
            },
        };

        return RunTaskAsync(t, cancellationToken);
    }

    /// <summary>Writes zero to IIN1.7, acknowledging a device restart.</summary>
    public Task ClearRestartAsync(CancellationToken cancellationToken = default) =>
        RunTaskAsync(MasterTasks.ClearRestart(), cancellationToken);

    /// <summary>Measures the outstation's turnaround with DELAY_MEASURE (FC 23).</summary>
    /// <returns>The processing delay the outstation reported, and the measured round trip.</returns>
    public async Task<(TimeSpan OutstationDelay, TimeSpan RoundTrip)> MeasureDelayAsync(
        CancellationToken cancellationToken = default)
    {
        uint ms = 0;
        var sent = _time.GetUtcNow();
        var got = sent;
        var t = MasterTasks.DelayMeasure(v => ms = v);
        t.OnDone = _ => got = _time.GetUtcNow();
        await RunTaskAsync(t, cancellationToken).ConfigureAwait(false);
        return (TimeSpan.FromMilliseconds(ms), got - sent);
    }

    /// <summary>Asks for a restart and returns the delay the outstation reported.</summary>
    public async Task<TimeSpan> RestartWithDelayAsync(
        RestartMode mode,
        CancellationToken cancellationToken = default)
    {
        uint ms = 0;
        var t = MasterTasks.Restart(mode);
        t.OnFragment = frag =>
        {
            foreach (var h in frag.Objects)
            {
                if (h.Group == 52 && h.Data.Length >= 2)
                {
                    ms = CommandObjects.ParseTimeDelay(h.Variation, h.Data.Span);
                }
            }
        };

        await RunTaskAsync(t, cancellationToken).ConfigureAwait(false);
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>Sends SELECT alone and returns the outstation's echo.</summary>
    /// <remarks>
    /// For testing the outstation's select handling — select timeouts, an
    /// operate that does not match, an operate with no select. A control on
    /// real plant should use <see cref="SelectAndOperateAsync(IReadOnlyList{Command}, CancellationToken)"/>.
    /// </remarks>
    public Task<CommandResult> SelectOnlyAsync(
        IReadOnlyList<Command> commands,
        CancellationToken cancellationToken = default) =>
        SingleCommandAsync(FuncCode.Select, commands, cancellationToken);

    /// <summary>Sends OPERATE alone and returns the outstation's echo.</summary>
    /// <remarks>
    /// Its sequence number is whatever the session issues next, so it matches a
    /// preceding <see cref="SelectOnlyAsync"/> only if nothing else was sent in
    /// between — which is exactly the property a test of NO_SELECT exercises.
    /// </remarks>
    public Task<CommandResult> OperateOnlyAsync(
        IReadOnlyList<Command> commands,
        CancellationToken cancellationToken = default) =>
        SingleCommandAsync(FuncCode.Operate, commands, cancellationToken);

    private async Task<CommandResult> SingleCommandAsync(
        FuncCode fc,
        IReadOnlyList<Command> commands,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
        {
            throw new BadConfigException("master: dnp3: invalid configuration: no commands");
        }

        var result = new CommandResult { Commands = commands };
        await RunTaskAsync(MasterTasks.CommandTask(fc, commands, result), cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Sends an arbitrary request built from object headers and returns every
    /// response fragment.
    /// </summary>
    /// <remarks>
    /// The request goes through the session's scheduler and sequence
    /// numbering, so it cannot collide with a poll. Measurements in the
    /// response still reach the handler as for any other response.
    /// </remarks>
    public async Task<RawResponse> SendRequestAsync(
        FuncCode fc,
        IReadOnlyList<ObjectHeader> objects,
        bool noResponse = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (fc.IsResponse() || fc == FuncCode.Confirm)
        {
            throw new BadConfigException("master: dnp3: invalid configuration: not a request function code");
        }

        var response = new RawResponse();
        var t = new MasterTask
        {
            Name = "raw-" + fc.ToDisplayString(),
            FuncCode = fc,
            Priority = TaskPriority.Command,
            Build = b =>
            {
                foreach (var h in objects)
                {
                    AddExt(b, h);
                }
            },
            OnFragment = f => response.Fragments.Add(new RawFragment(f.Header, [.. f.Objects], f.Raw)),
            OnDone = iin => response.Iin = iin,
            NoResponse = noResponse,
        };

        await RunTaskAsync(t, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private static ObjectHeader RangeOrAll(byte group, ushort? start, ushort? stop) =>
        start is { } s && stop is { } e
            ? FragmentFactory.ReadRange(group, 0, s, e)
            : FragmentFactory.ReadAllObjects(group, 0);

    private static void AddExt(FragmentBuilder b, ObjectHeader h)
    {
        if (!b.TryAddObject(h))
        {
            throw AppParseStatus.FragmentTooLarge.ToException();
        }
    }
}
