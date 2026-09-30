// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using SharpDnp3.App;
using SharpDnp3.Objects;

namespace SharpDnp3.Master;

public sealed partial class MasterSession
{
    /// <summary>
    /// Turns the request-error indications on a response into an exception, or
    /// returns <see langword="null"/> if it carries none.
    /// </summary>
    internal static Exception? Rejection(string what, Iin iin)
    {
        if (iin.Has(Iin.NoFuncCodeSupport))
        {
            return new NotSupportedByPeerException($"master: {what}: dnp3: not supported by peer");
        }

        if (iin.Has(Iin.ParameterError) || iin.Has(Iin.ObjectUnknown))
        {
            return new RejectedException($"master: {what}: dnp3: request rejected by peer (IIN {iin})");
        }

        return null;
    }

    /// <summary>Runs a task and reports an outstation's refusal as an exception.</summary>
    private Task RunCheckedAsync(string what, MasterTask t, CancellationToken cancellationToken)
    {
        t.OnDone = iin => t.Failure = Rejection(what, iin);
        return RunTaskAsync(t, cancellationToken);
    }

    /// <summary>Sets the outstation's clock using the LAN procedure.</summary>
    /// <remarks>
    /// The master sends RECORD_CURRENT_TIME, noting its own clock as the request
    /// goes out, then writes that time as group 50 variation 3. The outstation
    /// notes when the first request arrived and adds however long it has held
    /// it, so the transit delay is measured rather than assumed — without the
    /// master having to know what it was. The two requests are chained so
    /// nothing is scheduled between them.
    /// </remarks>
    public Task SyncTimeRecordedAsync(CancellationToken cancellationToken = default)
    {
        var recorded = default(DateTimeOffset);
        var record = new MasterTask
        {
            Name = "record-current-time",
            FuncCode = FuncCode.RecordCurrentTime,
            Priority = TaskPriority.Startup,
            Build = _ => recorded = _time.GetUtcNow(),
        };
        record.Next = () =>
        {
            var write = MasterTasks.WriteRecordedTime(recorded);
            write.OnDone = iin => write.Failure = Rejection("recorded time write", iin);
            return write;
        };

        return RunTaskAsync(record, cancellationToken);
    }

    /// <summary>
    /// Asks the outstation to freeze every counter at a time, and again every
    /// interval when that is non-zero. A time already past with no interval is
    /// refused by the outstation with PARAMETER_ERROR.
    /// </summary>
    public Task FreezeAtTimeAsync(
        DateTimeOffset at,
        TimeSpan interval = default,
        CancellationToken cancellationToken = default)
    {
        if (interval < TimeSpan.Zero || interval.TotalMilliseconds > uint.MaxValue)
        {
            throw new BadConfigException($"master: dnp3: invalid configuration: freeze interval {interval}");
        }

        return RunCheckedAsync("freeze at time", MasterTasks.FreezeAtTime(at, interval), cancellationToken);
    }

    /// <summary>
    /// Writes one existing attribute. The outstation must advertise the
    /// attribute as writable and the value must retain its configured type.
    /// </summary>
    public Task WriteAttributeAsync(DeviceAttribute attribute, CancellationToken cancellationToken = default)
    {
        var data = new List<byte>();
        AttributeObjects.Append(data, attribute);
        var t = new MasterTask
        {
            Name = "write-attribute",
            FuncCode = FuncCode.Write,
            Priority = TaskPriority.Command,
            Build = b => AddExt(b, new ObjectHeader
            {
                Group = 0,
                Variation = attribute.Variation,
                Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.StartStop8),
                Range = new ObjectRange
                {
                    Spec = RangeSpec.StartStop8,
                    Start = attribute.Set,
                    Stop = attribute.Set,
                    Count = 1,
                },
                Data = data.ToArray(),
            }),
        };
        t.OnDone = iin =>
        {
            if (iin.HasAny(Iin.RequestErrorMask))
            {
                t.Failure = new RejectedException($"master: writing attribute: {iin}");
            }
        };

        return RunTaskAsync(t, cancellationToken);
    }
}
