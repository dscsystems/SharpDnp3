// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using SharpDnp3.App;
using SharpDnp3.Objects;

namespace SharpDnp3.Master;

/// <summary>
/// Task priorities. Lower runs first when two tasks are due at the same moment.
/// </summary>
internal static class TaskPriority
{
    /// <summary>Clearing the restart indication comes before anything.</summary>
    public const int Startup = 0;

    /// <summary>An operator waiting on a control beats a poll.</summary>
    public const int Command = 1;

    /// <summary>A full re-baseline.</summary>
    public const int Integrity = 2;

    /// <summary>A routine event poll.</summary>
    public const int Poll = 3;
}

/// <summary>One request the master wants to send.</summary>
/// <remarks>
/// A task is built when it is sent, not when it is queued, so a periodic poll
/// queued once produces a fresh request with a current sequence number every
/// time it runs.
/// </remarks>
internal sealed class MasterTask
{
    /// <summary>A short name for logs.</summary>
    public required string Name { get; init; }

    /// <summary>The function code the request carries.</summary>
    public required FuncCode FuncCode { get; init; }

    /// <summary>Which tasks it outranks when two fall due together.</summary>
    public int Priority { get; init; }

    /// <summary>Appends the task's object headers to the request.</summary>
    public Action<FragmentBuilder>? Build { get; init; }

    /// <summary>Runs for each response fragment, before the confirm.</summary>
    public Action<Fragment>? OnFragment { get; set; }

    /// <summary>Runs when the final response fragment arrives.</summary>
    public Action<Iin>? OnDone { get; set; }

    /// <summary>
    /// Marks a request the outstation will not answer, so the task completes as
    /// soon as it is on the wire.
    /// </summary>
    public bool NoResponse { get; set; }

    /// <summary>
    /// Marks the steps of the startup sequence, so the session can tell when
    /// that sequence is in flight.
    /// </summary>
    public bool Startup { get; set; }

    /// <summary>
    /// When set, returns a task to run immediately after this one succeeds —
    /// bypassing the scheduler so nothing can be interleaved.
    /// </summary>
    /// <remarks>
    /// Select-before-operate needs this. The standard requires the OPERATE to
    /// carry the sequence number one above the SELECT, so a periodic poll
    /// slipping between them would make the outstation reject the operate with
    /// NO_SELECT. Chaining is what guarantees they stay adjacent.
    /// </remarks>
    public Func<MasterTask?>? Next { get; set; }

    /// <summary>
    /// When non-zero, reschedules the task after each run.
    /// </summary>
    public TimeSpan Period { get; set; }

    /// <summary>When the task should next be sent.</summary>
    public DateTimeOffset Due { get; set; }

    /// <summary>When the in-flight request gives up waiting.</summary>
    public DateTimeOffset Deadline { get; set; }

    /// <summary>The application sequence number the request went out with.</summary>
    public byte Seq { get; set; }

    /// <summary>
    /// Receives the outcome, for callers waiting on a one-shot task.
    /// </summary>
    public TaskCompletionSource<bool>? Done { get; set; }

    /// <summary>The sequence the task was pushed in, used as a tiebreaker.</summary>
    public ulong Order { get; set; }

    /// <summary>Reports the outcome to a waiting caller exactly once.</summary>
    public void Finish(Exception? error)
    {
        var done = Done;
        if (done is null)
        {
            return;
        }

        Done = null;
        if (error is null)
        {
            done.TrySetResult(true);
        }
        else
        {
            done.TrySetException(error);
        }
    }

    /// <summary>Returns a copy for rescheduling a periodic task.</summary>
    public MasterTask CloneForPeriod(DateTimeOffset due) => new()
    {
        Name = Name,
        FuncCode = FuncCode,
        Priority = Priority,
        Build = Build,
        OnFragment = OnFragment,
        OnDone = OnDone,
        NoResponse = NoResponse,
        Startup = Startup,
        Next = Next,
        Period = Period,
        Due = due,
        Done = null,
    };
}

/// <summary>The task constructors, one per request the master can make.</summary>
internal static class MasterTasks
{
    /// <summary>
    /// Writes zero to internal indication index 7, which is how a master tells
    /// an outstation it has seen the restart.
    /// </summary>
    /// <remarks>
    /// Until this is done the outstation keeps asserting DEVICE_RESTART on
    /// every response, and a master that reacts to that indication would re-run
    /// its startup sequence forever.
    /// </remarks>
    public static MasterTask ClearRestart() => new()
    {
        Name = "clear-restart",
        FuncCode = FuncCode.Write,
        Priority = TaskPriority.Startup,
        Build = b => Add(b, new ObjectHeader
        {
            Group = 80,
            Variation = 1,
            Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.StartStop8),
            Range = new ObjectRange { Spec = RangeSpec.StartStop8, Start = 7, Stop = 7, Count = 1 },
            Data = new byte[] { 0x00 }, // one packed bit, cleared
        }),
    };

    /// <summary>
    /// Enables or disables unsolicited reporting for a set of classes.
    /// </summary>
    public static MasterTask Unsolicited(bool enable, Class mask) => new()
    {
        Name = enable ? "enable-unsolicited" : "disable-unsolicited",
        FuncCode = enable ? FuncCode.EnableUnsolicited : FuncCode.DisableUnsolicited,
        Priority = TaskPriority.Startup,
        Build = b =>
        {
            foreach (var (cls, variation) in EventClassVariations)
            {
                if ((mask & cls) == 0)
                {
                    continue;
                }

                Add(b, FragmentFactory.ReadAllObjects(60, variation));
            }
        },
    };

    private static readonly (Class Class, byte Variation)[] EventClassVariations =
        [(Class.Class1, 2), (Class.Class2, 3), (Class.Class3, 4)];

    /// <summary>Reads a set of classes.</summary>
    /// <remarks>
    /// The class order matters. Events are read before static data so that a
    /// value which changed during the poll is reported as an event <em>and</em>
    /// then as its current static value, rather than the other way round —
    /// which would leave the master holding the pre-change value as the latest.
    /// </remarks>
    public static MasterTask Scan(Class mask) => new()
    {
        Name = "scan-" + mask.ToDisplayString(),
        FuncCode = FuncCode.Read,
        Priority = ScanPriority(mask),
        Build = b =>
        {
            (Class Class, byte Variation)[] order =
                [(Class.Class1, 2), (Class.Class2, 3), (Class.Class3, 4), (Class.Class0, 1)];

            foreach (var (cls, variation) in order)
            {
                if ((mask & cls) == 0)
                {
                    continue;
                }

                Add(b, FragmentFactory.ReadAllObjects(60, variation));
            }
        },
    };

    private static int ScanPriority(Class mask) =>
        (mask & Class.Class0) != 0 ? TaskPriority.Integrity : TaskPriority.Poll;

    /// <summary>
    /// Reads a contiguous index range of one group and variation.
    /// </summary>
    public static MasterTask RangeScan(byte group, byte variation, ushort start, ushort stop) => new()
    {
        Name = "scan-range",
        FuncCode = FuncCode.Read,
        Priority = TaskPriority.Poll,
        Build = b => Add(b, FragmentFactory.ReadRange(group, variation, start, stop)),
    };

    /// <summary>Asks the outstation to restart.</summary>
    public static MasterTask Restart(RestartMode mode) => new()
    {
        Name = "restart-" + mode.ToDisplayString(),
        FuncCode = mode == RestartMode.Warm ? FuncCode.WarmRestart : FuncCode.ColdRestart,
        Priority = TaskPriority.Command,
        Build = null,
    };

    /// <summary>Sets the outstation's clock.</summary>
    public static MasterTask WriteTime(DateTimeOffset t)
    {
        var ms = Dnp3Time.ToDnp3(t);
        return new MasterTask
        {
            Name = "write-time",
            FuncCode = FuncCode.Write,
            Priority = TaskPriority.Startup,
            Build = b => Add(b, new ObjectHeader
            {
                Group = 50,
                Variation = 1,
                Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.Count8),
                Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
                Data = new[]
                {
                    (byte)ms, (byte)(ms >> 8), (byte)(ms >> 16),
                    (byte)(ms >> 24), (byte)(ms >> 32), (byte)(ms >> 40),
                },
            }),
        };
    }

    /// <summary>
    /// Asks the outstation how long it takes to turn a request around, which is
    /// the first half of the serial time-synchronisation procedure.
    /// </summary>
    public static MasterTask DelayMeasure(Action<uint> onDelayMillis) => new()
    {
        Name = "delay-measure",
        FuncCode = FuncCode.DelayMeasure,
        Priority = TaskPriority.Startup,
        Build = null,
        OnFragment = frag =>
        {
            foreach (var h in frag.Objects)
            {
                if (h.Group == 52 && h.Data.Length >= 2)
                {
                    onDelayMillis(CommandObjects.ParseTimeDelay(h.Variation, h.Data.Span));
                    return;
                }
            }
        },
    };

    /// <summary>Sets the analog deadbands of a set of points.</summary>
    public static MasterTask WriteDeadband(IReadOnlyDictionary<ushort, float> deadbands)
    {
        // Sorted so the request is deterministic, which matters when comparing
        // captures and when an outstation logs what it was told.
        var indexes = new List<ushort>(deadbands.Keys);
        indexes.Sort();

        return new MasterTask
        {
            Name = "write-deadband",
            FuncCode = FuncCode.Write,
            Priority = TaskPriority.Command,
            Build = b =>
            {
                // One index byte and four value bytes per deadband.
                var data = new List<byte>(5 * indexes.Count);
                foreach (var i in indexes)
                {
                    data.Add((byte)i);
                    ObjectConvert.AppendSingle(data, deadbands[i]);
                }

                Add(b, new ObjectHeader
                {
                    Group = 34,
                    Variation = 3, // single precision
                    Qualifier = Qualifier.Make(IndexPrefix.Index1, RangeSpec.Count8),
                    Range = new ObjectRange { Spec = RangeSpec.Count8, Count = (uint)indexes.Count },
                    Data = data.ToArray(),
                });
            },
        };
    }

    /// <summary>Builds the task for one command request.</summary>
    public static MasterTask CommandTask(
        FuncCode fc,
        IReadOnlyList<Command> cmds,
        CommandResult result) => new()
        {
            Name = "command-" + fc.ToDisplayString(),
            FuncCode = fc,
            Priority = TaskPriority.Command,
            Build = b => CommandCodec.BuildCommands(b, cmds),
            OnFragment = frag => CommandCodec.ParseCommandStatuses(frag, result.Statuses),
        };

    private static void Add(FragmentBuilder b, ObjectHeader h)
    {
        if (!b.TryAddObject(h))
        {
            throw AppParseStatus.FragmentTooLarge.ToException();
        }
    }
}

/// <summary>Orders pending tasks by when they are due, then by priority.</summary>
/// <remarks>
/// A heap rather than a sorted list because a busy master with several periodic
/// polls re-queues a task on every run, and re-sorting each time is the one
/// place this loop could get quadratic.
/// </remarks>
internal sealed class Scheduler
{
    private readonly PriorityQueue<MasterTask, TaskKey> _queue = new();
    private readonly List<MasterTask> _tracked = [];
    private ulong _pushN;

    /// <summary>The heap key: due time, then priority, then push order.</summary>
    private readonly record struct TaskKey(DateTimeOffset Due, int Priority, ulong Order)
        : IComparable<TaskKey>
    {
        public int CompareTo(TaskKey other)
        {
            var c = Due.CompareTo(other.Due);
            if (c != 0)
            {
                return c;
            }

            c = Priority.CompareTo(other.Priority);
            return c != 0 ? c : Order.CompareTo(other.Order);
        }
    }

    /// <summary>Adds a task to the queue.</summary>
    public void Push(MasterTask t)
    {
        // The push order is the final tiebreaker, so tasks that are due at the
        // same instant with the same priority run in the order they were
        // queued. Without it a heap orders equal keys arbitrarily, and a
        // startup sequence whose steps share a priority would run in a
        // different order each time.
        _pushN++;
        t.Order = _pushN;
        _queue.Enqueue(t, new TaskKey(t.Due, t.Priority, t.Order));
        _tracked.Add(t);
    }

    /// <summary>Removes and returns the next task, or null when empty.</summary>
    public MasterTask? Pop()
    {
        if (!_queue.TryDequeue(out var t, out _))
        {
            return null;
        }

        _tracked.Remove(t);
        return t;
    }

    /// <summary>Returns the next task without removing it.</summary>
    public MasterTask? Peek() => _queue.TryPeek(out var t, out _) ? t : null;

    /// <summary>How many tasks are queued.</summary>
    public int Count => _queue.Count;

    /// <summary>
    /// Drops every pending task, failing anything a caller is waiting on.
    /// </summary>
    /// <remarks>
    /// This runs when the outstation reports a restart: the queued work was
    /// aimed at a device state that no longer exists.
    /// </remarks>
    public void Clear()
    {
        foreach (var t in _tracked)
        {
            t.Finish(new TaskFailedException());
        }

        _tracked.Clear();
        _queue.Clear();
    }
}
