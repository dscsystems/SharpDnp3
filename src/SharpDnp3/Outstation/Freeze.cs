// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using SharpDnp3.App;
using SharpDnp3.Objects;

namespace SharpDnp3.Outstation;

public sealed partial class OutstationSession
{
    /// <summary>Bounds how many FREEZE_AT_TIME requests are held at once.</summary>
    private const int MaxFreezeSchedules = 16;

    /// <summary>One FREEZE_AT_TIME request waiting for its moment.</summary>
    private sealed class FreezeSchedule
    {
        /// <summary>When the counters are frozen next.</summary>
        public DateTimeOffset Next;

        /// <summary>Repeats the freeze; zero freezes once.</summary>
        public TimeSpan Interval;

        /// <summary>The counters to freeze, as inclusive index ranges.</summary>
        public List<(ushort Start, ushort Stop)> Ranges = [];

        /// <summary>The analogs to freeze, as inclusive index ranges.</summary>
        public List<(ushort Start, ushort Stop)> AnalogRanges = [];
    }

    private readonly List<FreezeSchedule> _freezes = [];
    private readonly Lock _freezeGate = new();

    /// <summary>Schedules a freeze.</summary>
    /// <remarks>
    /// <para>
    /// The request leads with a group 50 variation 2 object — the time of the
    /// first freeze and the interval between repeats in milliseconds — followed
    /// by counter headers naming what to freeze. A request with no counter
    /// headers freezes every counter, as an immediate freeze does.
    /// </para>
    /// <para>
    /// A time already past is refused with PARAMETER_ERROR when nothing repeats,
    /// since there is no moment left to freeze at; with an interval it is moved
    /// on to the next multiple that has not passed.
    /// </para>
    /// </remarks>
    private void OnFreezeAtTime(Association a, Fragment frag)
    {
        var haveTime = false;
        var first = default(DateTimeOffset);
        var interval = TimeSpan.Zero;
        List<(ushort, ushort)> ranges = [];
        List<(ushort, ushort)> analogRanges = [];
        var named = false;

        foreach (var h in frag.Objects)
        {
            if (h.Group == 50 && h.Variation == 2 && !haveTime)
            {
                var skip = h.Qualifier.IndexPrefix.Octets();
                if (h.Count != 1 || h.Data.Length < skip + CommandObjects.Time48Size + 4)
                {
                    a.Iin = a.Iin.Set(Iin.ParameterError);
                    return;
                }

                var d = h.Data.Span[skip..];
                first = CommandObjects.ParseTime48(d).Time;
                var ms = d[6] | ((uint)d[7] << 8) | ((uint)d[8] << 16) | ((uint)d[9] << 24);
                interval = TimeSpan.FromMilliseconds(ms);
                haveTime = true;
            }
            else if (h.Group == 30)
            {
                named = true;
                if (!TryForEachPointRun(h, (s, e) => analogRanges.Add((s, e))))
                {
                    a.Iin = a.Iin.Set(Iin.ParameterError);
                    return;
                }
            }
            else if (h.Group == 20)
            {
                named = true;
                if (!TryForEachPointRun(h, (s, e) => ranges.Add((s, e))))
                {
                    a.Iin = a.Iin.Set(Iin.ParameterError);
                    return;
                }
            }
            else
            {
                a.Iin = a.Iin.Set(Iin.ObjectUnknown);
                return;
            }
        }

        if (!haveTime)
        {
            a.Iin = a.Iin.Set(Iin.ParameterError);
            return;
        }

        if (!named)
        {
            ranges = [(0, 0xFFFF)];
            analogRanges = [(0, 0xFFFF)];
        }

        var now = _appl.Now();
        var next = first;
        if (next < now)
        {
            if (interval <= TimeSpan.Zero)
            {
                a.Iin = a.Iin.Set(Iin.ParameterError);
                return;
            }

            var periods = (long)((now - first) / interval) + 1;
            next = first + (periods * interval);
        }

        lock (_freezeGate)
        {
            // The same freeze asked for twice is one freeze: the request is
            // understood, and the operation is already waiting to run.
            foreach (var f in _freezes)
            {
                if (f.Next == next && f.Interval == interval &&
                    f.Ranges.SequenceEqual(ranges) && f.AnalogRanges.SequenceEqual(analogRanges))
                {
                    a.Iin = a.Iin.Set(Iin.AlreadyExecuting);
                    return;
                }
            }

            if (_freezes.Count >= MaxFreezeSchedules)
            {
                a.Iin = a.Iin.Set(Iin.ParameterError);
                return;
            }

            _freezes.Add(new FreezeSchedule
            {
                Next = next,
                Interval = interval,
                Ranges = ranges,
                AnalogRanges = analogRanges,
            });
        }

        a.Log.Log(
            Dnp3LogLevel.Debug,
            "freeze scheduled",
            ("at", next), ("interval", interval), ("ranges", ranges.Count));
    }

    /// <summary>Performs every scheduled freeze that has come due.</summary>
    private void RunFreezes(DateTimeOffset now)
    {
        lock (_freezeGate)
        {
            if (_freezes.Count == 0)
            {
                return;
            }

            for (var i = _freezes.Count - 1; i >= 0; i--)
            {
                var f = _freezes[i];
                if (now < f.Next)
                {
                    continue;
                }

                // The frozen values carry the moment they were scheduled for,
                // which is what a master that asked for a freeze at 12:00:00
                // means by the time of the frozen value, however late the tick
                // that ran it.
                var at = _synchronized ? Timestamp.Now(f.Next) : Timestamp.Unsynchronized(f.Next);
                foreach (var (start, stop) in f.Ranges)
                {
                    _db.FreezeCounters(start, stop, at);
                }

                foreach (var (start, stop) in f.AnalogRanges)
                {
                    _db.FreezeAnalogs(start, stop, at, false);
                }

                lock (_gate)
                {
                    _stats.ScheduledFreezes++;
                }

                if (f.Interval <= TimeSpan.Zero)
                {
                    _freezes.RemoveAt(i); // one-shot: done
                    continue;
                }

                var periods = (long)((now - f.Next) / f.Interval) + 1;
                f.Next += periods * f.Interval;
            }
        }
    }
}
