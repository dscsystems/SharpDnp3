// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The checks the conformance run's verdicts rest on. One that passes what it
// should not turns a failing outstation into a passing report, so each is
// tested against the reports it must reject.

using System.Globalization;
using System.Text.Json.Serialization;

namespace SharpDnp3.Conformance.Dut;

/// <summary>A measurement as dnp3c parsed it from the outstation's response.</summary>
internal sealed record Point
{
    [JsonPropertyName("kind")]
    public string KindName { get; init; } = "";

    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("value")]
    public double Value { get; init; }

    /// <summary>The flags octet, absent for a variation without one.</summary>
    [JsonPropertyName("flags")]
    public int? Flags { get; init; }

    /// <summary>Milliseconds since 1970, absent for a variation without one.</summary>
    [JsonPropertyName("time")]
    public long? Time { get; init; }

    /// <summary>Whether the point came from an event variation.</summary>
    [JsonPropertyName("event")]
    public bool Event { get; init; }

    [JsonIgnore]
    public Kind Kind => Kinds.Parse(KindName);

    public override string ToString()
    {
        var s = string.Format(CultureInfo.InvariantCulture, "{0} {1} = {2}", KindName, Index, Value);
        if (Flags is { } f)
        {
            s += string.Format(CultureInfo.InvariantCulture, " (flags 0x{0:X2})", f);
        }

        if (Time is { } t)
        {
            s += " at " + DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        return s;
    }
}

internal static class Verify
{
    /// <summary>
    /// How far apart two times may be and still be the same. The outstation
    /// carries milliseconds and the device stamps them, so anything wider than
    /// rounding would be a fault in one or the other.
    /// </summary>
    public const long TimestampTolerance = 1;

    /// <summary>
    /// Compares a value as reported with the value held. An analog reported in
    /// single precision is as close as single precision gets.
    /// </summary>
    public static bool SameValue(Kind k, double reported, double held)
    {
        if (reported == held)
        {
            return true;
        }

        return k is Kind.Analog or Kind.AnalogOutput or Kind.FrozenAnalog
            && Math.Abs(reported - held) <= Math.Max(1e-6, Math.Abs(held) * 1e-6);
    }

    /// <summary>Checks a reported point against the outstation's database.</summary>
    private static string? MatchesHeld(Point p, Snapshot held)
    {
        if (!held.TryValue(p.Kind, p.Index, out var v, out var flags))
        {
            return $"{p}: the device has no such point";
        }

        if (!SameValue(p.Kind, p.Value, v))
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}: the device holds {1}", p, v);
        }

        if (p.Flags is { } f)
        {
            var mask = (byte)~p.Kind.StateMask();
            if ((f & mask) != (flags & mask))
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}: the device holds flags 0x{1:X2}", p, flags & mask);
            }
        }

        return null;
    }

    /// <summary>
    /// Checks static values: each reported point has the value the device
    /// holds, and, where every point of the kinds should be there, none is
    /// missing or repeated.
    /// </summary>
    public static void Static(IReadOnlyList<Point> points, Snapshot held, IReadOnlyList<Kind> complete)
    {
        var errs = new List<string>();
        var seen = new Dictionary<(Kind, int), int>();
        foreach (var p in points)
        {
            if (p.Event)
            {
                errs.Add($"{p}: an event among static values");
                continue;
            }

            seen[(p.Kind, p.Index)] = seen.GetValueOrDefault((p.Kind, p.Index)) + 1;
            if (MatchesHeld(p, held) is { } err)
            {
                errs.Add(err);
            }
        }

        foreach (var k in complete)
        {
            for (var i = 0; i < held.Count(k); i++)
            {
                switch (seen.GetValueOrDefault((k, i)))
                {
                    case 0:
                        errs.Add($"{k.Name()} {i} is missing");
                        break;
                    case > 1 and var n:
                        errs.Add($"{k.Name()} {i} is reported {n} times");
                        break;
                }
            }
        }

        Throw(errs);
    }

    /// <summary>Reports whether a reported event is a generated one.</summary>
    public static bool SameEvent(Point p, DutEvent e)
    {
        if (p.Kind != e.Kind || p.Index != e.Index || !SameValue(p.Kind, p.Value, e.Value))
        {
            return false;
        }

        if (p.Flags is { } f)
        {
            var mask = (byte)~p.Kind.StateMask();
            if ((f & mask) != (e.Flags & mask))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The outstation has to report a point's events in the order they
    /// happened; across points, only a binary type's order is meaningful, as a
    /// sequence of events.
    /// </summary>
    private static bool InOrderAcrossPoints(Kind k) => k is Kind.Binary or Kind.DoubleBit;

    /// <summary>
    /// Checks that the reported events are exactly the expected ones: each
    /// expected event once, nothing else, in an order the standard allows, and
    /// with timestamps whose gaps are the gaps between when they happened.
    /// </summary>
    public static void Events(IReadOnlyList<Point> points, IReadOnlyList<DutEvent> expected)
    {
        var errs = new List<string>();
        var used = new bool[expected.Count];

        // position[i] is where in expected points[i] matched.
        var position = new int[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            position[i] = -1;
            if (!p.Event)
            {
                errs.Add($"{p}: a static value among events");
                continue;
            }

            // The earliest unused match: the order check below then sees the
            // order the events were matched in, not an arbitrary one.
            for (var j = 0; j < expected.Count; j++)
            {
                if (!used[j] && SameEvent(p, expected[j]))
                {
                    (used[j], position[i]) = (true, j);
                    break;
                }
            }

            if (position[i] < 0)
            {
                errs.Add($"{p}: no such event was generated");
            }
        }

        for (var j = 0; j < expected.Count; j++)
        {
            if (!used[j])
            {
                errs.Add($"missing event: {expected[j]}");
            }
        }

        Throw(errs);

        // Order: a later report of the same point, or of another point of a
        // binary type, must not be an earlier event.
        for (var i = 0; i < points.Count; i++)
        {
            for (var j = i + 1; j < points.Count; j++)
            {
                var (a, b) = (expected[position[i]], expected[position[j]]);
                var related = a.Kind == b.Kind && (a.Index == b.Index || InOrderAcrossPoints(a.Kind));
                if (related && position[j] < position[i])
                {
                    errs.Add($"{b} is reported after {a}, which happened later");
                }
            }
        }

        if (Gaps(points, expected, position) is { } gap)
        {
            errs.Add(gap);
        }

        Throw(errs);
    }

    /// <summary>
    /// Checks the events' timestamps: none is compared with an absolute clock,
    /// but each gap between two must be the gap between when they were
    /// generated.
    /// </summary>
    private static string? Gaps(IReadOnlyList<Point> points, IReadOnlyList<DutEvent> expected, int[] position)
    {
        var first = -1;
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].Time is not { } t)
            {
                continue;
            }

            if (first < 0)
            {
                first = i;
                continue;
            }

            var want = (long)(expected[position[i]].At - expected[position[first]].At).TotalMilliseconds;
            var got = t - points[first].Time!.Value;
            var d = got - want;
            if (d > TimestampTolerance || d < -TimestampTolerance)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} is {1} ms after {2}; it was generated {3} ms after it",
                    points[i], got, points[first], want);
            }
        }

        return null;
    }

    /// <summary>
    /// Checks the events of the points changed in the latest batch: each
    /// point's last event reports its present value, and any before it reports
    /// one of the values it was given since the procedure began.
    /// </summary>
    public static void CurrentWithEarlier(
        IReadOnlyList<Point> points, IReadOnlyList<DutEvent> latest, IReadOnlyList<DutEvent> all, Snapshot held)
    {
        var errs = new List<string>();
        var changed = new HashSet<(Kind, int)>(latest.Select(e => (e.Kind, e.Index)));
        var last = new Dictionary<(Kind, int), Point>();
        foreach (var p in points)
        {
            if (!p.Event)
            {
                errs.Add($"{p}: a static value among events");
                continue;
            }

            if (!all.Any(e => SameEvent(p, e)))
            {
                errs.Add($"{p}: no such event was generated");
            }

            last[(p.Kind, p.Index)] = p;
        }

        foreach (var (k, i) in changed)
        {
            if (!last.TryGetValue((k, i), out var p))
            {
                errs.Add($"{k.Name()} {i} changed, and has no event");
                continue;
            }

            held.TryValue(k, i, out var v, out _);
            if (!SameValue(k, p.Value, v))
            {
                errs.Add(string.Format(
                    CultureInfo.InvariantCulture, "{0}: the last event for the point; its present value is {1}", p, v));
            }
        }

        Throw(errs);
    }

    /// <summary>Checks only that each reported event was generated.</summary>
    public static void Generated(IReadOnlyList<Point> points, IReadOnlyList<DutEvent> all) =>
        Throw([.. points.Where(p => !all.Any(e => SameEvent(p, e))).Select(p => $"{p}: no such event was generated")]);

    /// <summary>Keeps the events of the kinds given.</summary>
    public static List<DutEvent> OfKinds(IEnumerable<DutEvent> events, IReadOnlyCollection<Kind> kinds) =>
        [.. events.Where(e => kinds.Contains(e.Kind))];

    /// <summary>Lists items for a log line.</summary>
    public static string Describe<T>(IEnumerable<T> items)
    {
        var s = string.Join("; ", items.Select(i => i?.ToString()));
        return s.Length == 0 ? "none" : s;
    }

    /// <summary>Fails with every error found, one per line.</summary>
    public static void Throw(List<string> errs)
    {
        if (errs.Count > 0)
        {
            throw new DutException(string.Join('\n', errs));
        }
    }
}
