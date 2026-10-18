// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Globalization;
using SharpDnp3.Outstation;

namespace SharpDnp3.Conformance.Dut;

/// <summary>A type of point.</summary>
internal enum Kind
{
    Binary,
    DoubleBit,
    Counter,
    FrozenCounter,
    Analog,
    FrozenAnalog,
    BinaryOutput,
    AnalogOutput,
}

/// <summary>The kinds as the plugin names them on the wire.</summary>
internal static class Kinds
{
    private static readonly Dictionary<string, Kind> ByName = new(StringComparer.Ordinal)
    {
        ["binary"] = Kind.Binary,
        ["doubleBit"] = Kind.DoubleBit,
        ["counter"] = Kind.Counter,
        ["frozenCounter"] = Kind.FrozenCounter,
        ["analog"] = Kind.Analog,
        ["frozenAnalog"] = Kind.FrozenAnalog,
        ["binaryOutputStatus"] = Kind.BinaryOutput,
        ["analogOutputStatus"] = Kind.AnalogOutput,
    };

    private static readonly Dictionary<Kind, string> Names = ByName.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>Every input type in an event class, in the order the class generators use.</summary>
    public static readonly Kind[] Inputs = [Kind.Binary, Kind.DoubleBit, Kind.Counter, Kind.FrozenCounter, Kind.Analog];

    public static Kind Parse(string? name) =>
        name is not null && ByName.TryGetValue(name, out var k)
            ? k
            : throw new DutException($"no such point kind: {name}");

    public static string Name(this Kind k) => Names[k];

    public static PointType PointType(this Kind k) => k switch
    {
        Kind.Binary => SharpDnp3.PointType.Binary,
        Kind.DoubleBit => SharpDnp3.PointType.DoubleBitBinary,
        Kind.Counter => SharpDnp3.PointType.Counter,
        Kind.FrozenCounter => SharpDnp3.PointType.FrozenCounter,
        Kind.Analog => SharpDnp3.PointType.Analog,
        Kind.FrozenAnalog => SharpDnp3.PointType.FrozenAnalog,
        Kind.BinaryOutput => SharpDnp3.PointType.BinaryOutputStatus,
        Kind.AnalogOutput => SharpDnp3.PointType.AnalogOutputStatus,
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    /// <summary>
    /// The part of a flags octet that is the point's value rather than its
    /// quality, for the types that carry their state there.
    /// </summary>
    public static byte StateMask(this Kind k) => k switch
    {
        Kind.Binary or Kind.BinaryOutput => 0x80,
        Kind.DoubleBit => 0xC0,
        _ => 0,
    };
}

/// <summary>Every point's value at one moment.</summary>
internal sealed class Snapshot
{
    public List<Binary> Binary { get; set; } = [];

    public List<DoubleBitBinary> DoubleBit { get; set; } = [];

    public List<Counter> Counter { get; set; } = [];

    public List<FrozenCounter> FrozenCounter { get; set; } = [];

    public List<Analog> Analog { get; set; } = [];

    public List<BinaryOutputStatus> BinaryOutput { get; set; } = [];

    public List<AnalogOutputStatus> AnalogOutput { get; set; } = [];

    /// <summary>
    /// A point's value and flags as a plain number, how the checks compare
    /// them: a binary's state as 0 or 1, a double-bit's as 0 to 3.
    /// </summary>
    public bool TryValue(Kind k, int i, out double value, out byte flags)
    {
        (value, flags) = (0, 0);
        if (i < 0 || i >= Count(k))
        {
            return false;
        }

        (value, flags) = k switch
        {
            Kind.Binary => (Num(Binary[i].Value), Binary[i].Flags.Value),
            Kind.DoubleBit => ((double)DoubleBit[i].Value, DoubleBit[i].Flags.Value),
            Kind.Counter => (Counter[i].Value, Counter[i].Flags.Value),
            Kind.FrozenCounter => (FrozenCounter[i].Value, FrozenCounter[i].Flags.Value),
            Kind.Analog => (Analog[i].Value, Analog[i].Flags.Value),
            Kind.BinaryOutput => (Num(BinaryOutput[i].Value), BinaryOutput[i].Flags.Value),
            Kind.AnalogOutput => (AnalogOutput[i].Value, AnalogOutput[i].Flags.Value),
            _ => (0d, (byte)0),
        };
        return true;
    }

    /// <summary>How many points of a kind the snapshot holds.</summary>
    public int Count(Kind k) => k switch
    {
        Kind.Binary => Binary.Count,
        Kind.DoubleBit => DoubleBit.Count,
        Kind.Counter => Counter.Count,
        Kind.FrozenCounter => FrozenCounter.Count,
        Kind.Analog => Analog.Count,
        Kind.BinaryOutput => BinaryOutput.Count,
        Kind.AnalogOutput => AnalogOutput.Count,
        _ => 0,
    };

    public static double Num(bool b) => b ? 1 : 0;
}

/// <summary>A change the device made that the outstation reports as an event.</summary>
internal sealed record DutEvent(Kind Kind, int Index, double Value, byte Flags, DateTimeOffset At)
{
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "{0} {1} = {2} (flags 0x{3:X2}) at {4:HH:mm:ss.fff}",
        Kind.Name(), Index, Value, Flags, At.UtcDateTime);
}

/// <summary>
/// The events generated since the procedure started, in batches: one per
/// plugin call, which is what the checks compare with.
/// </summary>
internal sealed class Tracker
{
    private readonly List<List<DutEvent>> _batches = [];

    /// <summary>
    /// How many of the latest batch's events a check of its first event has
    /// accounted for, so a check of the rest knows where to start.
    /// </summary>
    public int Checked { get; set; }

    /// <summary>The counts recordCurrentCounterValues noted.</summary>
    public Snapshot? Counters { get; set; }

    /// <summary>generateTimeEvent's event.</summary>
    public DutEvent? TimeEvent { get; set; }

    public void Add(List<DutEvent> batch)
    {
        _batches.Add(batch);
        Checked = 0;
    }

    public List<DutEvent> Latest() => _batches.Count == 0 ? [] : [.. _batches[^1]];

    public List<DutEvent> All() => [.. _batches.SelectMany(b => b)];
}

/// <summary>One point to change, and what to change it to.</summary>
/// <param name="Next">Computes the point's new value from its present one.</param>
/// <param name="Flags">If set, replaces the point's flags.</param>
internal sealed record Change(Kind Kind, int Index, Func<double, double> Next, byte? Flags = null);

/// <summary>The value changes the generators use.</summary>
internal static class Changes
{
    public static readonly Func<double, double> Same = v => v;

    public static readonly Func<double, double> Toggle = v => 1 - v;

    /// <summary>
    /// Moves a double-bit input between its two determined states, which is
    /// what a breaker does.
    /// </summary>
    public static readonly Func<double, double> FlipDoubleBit = v =>
        (DoubleBit)(byte)v == DoubleBit.DeterminedOn
            ? (double)DoubleBit.DeterminedOff
            : (double)DoubleBit.DeterminedOn;

    public static readonly Func<double, double> CountUp = v => v + 1;

    /// <summary>
    /// Moves an analog input by more than its deadband, staying in the range of
    /// a 16-bit variation so every variation can carry it.
    /// </summary>
    public static readonly Func<double, double> StepAnalog = v => v >= 30000 ? 1 : v + 7;

    /// <summary>The generators' change for a kind of input.</summary>
    public static Func<double, double> NextFor(Kind k) => k switch
    {
        Kind.Binary => Toggle,
        Kind.DoubleBit => FlipDoubleBit,
        Kind.Counter or Kind.FrozenCounter => CountUp,
        Kind.Analog => StepAnalog,
        _ => throw new DutException($"cannot generate events for {k.Name()} points"),
    };

    /// <summary>
    /// n changes of a kind, spread over its points in index order and wrapping
    /// round, so a point changes again only once every other has.
    /// </summary>
    public static List<Change> Of(Kind k, int n)
    {
        var next = NextFor(k);
        var output = new List<Change>(Math.Max(n, 0));
        for (var i = 0; i < n; i++)
        {
            output.Add(new Change(k, i % Device.Generated(k), next));
        }

        return output;
    }

    /// <summary>
    /// The input types in an event class, the first being what the class
    /// generators change.
    /// </summary>
    public static List<Kind> ClassKinds(Class mask) =>
        [.. Kinds.Inputs.Where(k => (Device.EventClass[k] & mask) != 0)];
}

/// <summary>A failure the plugin reports as the step's.</summary>
internal sealed class DutException(string message) : Exception(message);
