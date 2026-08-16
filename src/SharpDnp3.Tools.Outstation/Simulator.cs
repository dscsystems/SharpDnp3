// Copyright (C) 2026 Ricardo Olsen / DSC Systems.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option)
// any later version. It is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details, in the LICENSE file at
// the root of this repository or at <https://www.gnu.org/licenses/>.
//
// The simulation exists so the outstation has something to report that behaves
// like plant rather than like a random number generator. A master under
// development wants a breaker that stays open after it is tripped, an analog
// that ramps rather than jumping, and a counter that only ever counts up —
// because those are the behaviours its own logic will be wrong about.

using System.Globalization;
using System.Text;
using SharpDnp3.Outstation;

namespace SharpDnp3.Tools.Outstation;

/// <summary>How an analog point moves.</summary>
public enum Signal
{
    /// <summary>Holds whatever it was last set to.</summary>
    Fixed,
    /// <summary>A sine wave between the bounds.</summary>
    Sine,
    /// <summary>A sawtooth from the lower bound to the upper.</summary>
    Ramp,
    /// <summary>A bounded random walk.</summary>
    Walk,
    /// <summary>A square wave between the bounds.</summary>
    Step,
}

/// <summary>One simulated analog point.</summary>
public sealed class AnalogSim
{
    public ushort Index { get; set; }

    public string Name { get; set; } = "";

    public string Units { get; set; } = "";

    public Signal Signal { get; set; } = Signal.Fixed;

    public double Min { get; set; }

    public double Max { get; set; }

    /// <summary>How long a full cycle takes for the periodic shapes.</summary>
    public TimeSpan Period { get; set; }

    /// <summary>The fraction of the range added as random jitter.</summary>
    public double Noise { get; set; }

    /// <summary>
    /// Reported to the master and used to suppress small changes.
    /// </summary>
    public double Deadband { get; set; }

    /// <summary>The event class the point's events go to.</summary>
    public int Class { get; set; } = 1;

    internal double Value;

    internal double Phase;

    /// <summary>Advances one analog point.</summary>
    internal double Next(double elapsedSeconds, Random random)
    {
        var span = Max - Min;
        var mid = (Min + Max) / 2;

        double v;
        switch (Signal)
        {
            case Signal.Sine:
            {
                var period = Period.TotalSeconds <= 0 ? 60 : Period.TotalSeconds;
                v = mid + (span / 2 * Math.Sin((2 * Math.PI * elapsedSeconds / period) + Phase));
                break;
            }

            case Signal.Ramp:
            {
                var period = Period.TotalSeconds <= 0 ? 60 : Period.TotalSeconds;
                var frac = (elapsedSeconds % period) / period;
                v = Min + (span * frac);
                break;
            }

            case Signal.Walk:
            {
                var step = span * 0.02 * ((2 * random.NextDouble()) - 1);
                v = Math.Min(Math.Max(Value + step, Min), Max);
                break;
            }

            case Signal.Step:
            {
                var period = Period.TotalSeconds <= 0 ? 30 : Period.TotalSeconds;
                v = elapsedSeconds % period < period / 2 ? Min : Max;
                break;
            }

            default:
                v = Value;
                if (v == 0)
                {
                    v = mid;
                }

                break;
        }

        if (Noise > 0)
        {
            v += span * Noise * ((2 * random.NextDouble()) - 1);
        }

        return v;
    }
}

/// <summary>
/// A two-state device with a status input and a control output.
/// </summary>
public sealed class BreakerSim
{
    /// <summary>The binary input reporting the breaker's position.</summary>
    public ushort StatusIndex { get; set; }

    /// <summary>The binary output that operates it.</summary>
    public ushort ControlIndex { get; set; }

    public string Name { get; set; } = "";

    /// <summary>The starting position.</summary>
    public bool Closed { get; set; }

    /// <summary>
    /// Refuses commands, standing in for a device that is racked out or under
    /// local control.
    /// </summary>
    public bool Interlocked { get; set; }

    /// <summary>
    /// How long the breaker takes to move. A master that assumes a control
    /// takes effect instantly is wrong about real plant.
    /// </summary>
    public TimeSpan TravelTime { get; set; }

    /// <summary>The event class for its status changes.</summary>
    public int Class { get; set; } = 1;

    internal DateTimeOffset? MovingUntil;

    internal bool Target;
}

/// <summary>An accumulator.</summary>
public sealed class CounterSim
{
    public ushort Index { get; set; }

    public string Name { get; set; } = "";

    /// <summary>How fast it counts.</summary>
    public double PerSecond { get; set; }

    public int Class { get; set; } = 1;

    internal double Value;
}

/// <summary>
/// A fault the simulator can be told to produce, so a master can be tested
/// against the failures that are hard to arrange with real plant.
/// </summary>
public sealed class Injection
{
    /// <summary>Generates this many binary events per second.</summary>
    public int EventStorm { get; set; }

    /// <summary>Asserts the corresponding internal indication.</summary>
    public bool DeviceTrouble { get; set; }

    /// <summary>Makes the outstation report a restart after this long.</summary>
    public TimeSpan RestartAfter { get; set; }

    /// <summary>
    /// Takes points offline periodically, so a master's handling of bad quality
    /// is exercised rather than assumed.
    /// </summary>
    public TimeSpan OfflineEvery { get; set; }
}

/// <summary>Drives an outstation's database.</summary>
public sealed class Simulator
{
    /// <summary>The simulation step, in seconds.</summary>
    public const double TickSeconds = 0.25;

    private readonly Lock _gate = new();
    private readonly Random _random = new();

    private readonly List<AnalogSim> _analogs;
    private readonly List<BreakerSim> _breakers;
    private readonly List<CounterSim> _counters;

    private readonly Injection _inject;
    private readonly DateTimeOffset _start;

    private ushort _stormIndex;
    private DateTimeOffset _lastOffline;
    private bool _offlineNow;

    /// <summary>Builds a simulator from a configuration.</summary>
    public Simulator(PlantConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _analogs = config.Analogs;
        _breakers = config.Breakers;
        _counters = config.Counters;
        _inject = config.Inject;
        _start = DateTimeOffset.UtcNow;
        _lastOffline = _start;

        foreach (var a in _analogs)
        {
            a.Value = (a.Min + a.Max) / 2;

            // Stagger the phases so a rack of points does not move in lockstep,
            // which makes an event stream look artificial and hides ordering
            // bugs.
            a.Phase = _random.NextDouble() * 2 * Math.PI;
        }

        foreach (var b in _breakers)
        {
            b.Target = b.Closed;
        }
    }

    /// <summary>The points the simulator drives.</summary>
    public IReadOnlyList<AnalogSim> Analogs => _analogs;

    /// <summary>The breakers the simulator drives.</summary>
    public IReadOnlyList<BreakerSim> Breakers => _breakers;

    /// <summary>The counters the simulator drives.</summary>
    public IReadOnlyList<CounterSim> Counters => _counters;

    /// <summary>Whether a restart injection is due.</summary>
    public bool RestartDue(DateTimeOffset now, ref DateTimeOffset lastRestart) =>
        _inject.RestartAfter > TimeSpan.Zero && now - lastRestart > _inject.RestartAfter;

    /// <summary>Whether the device-trouble indication was asked for.</summary>
    public bool DeviceTroubleInjected => _inject.DeviceTrouble;

    /// <summary>
    /// Writes the current simulated state into the outstation's database.
    /// </summary>
    public void Apply(OutstationSession session, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            var elapsed = (now - _start).TotalSeconds;

            // The offline injection flips quality for a while, then restores it.
            if (_inject.OfflineEvery > TimeSpan.Zero && now - _lastOffline > _inject.OfflineEvery)
            {
                _offlineNow = !_offlineNow;
                _lastOffline = now;
            }

            var flags = _offlineNow ? Flags.CommLost : Flags.Online;
            var stamp = Timestamp.Now(now);

            session.Update(db =>
            {
                foreach (var a in _analogs)
                {
                    a.Value = a.Next(elapsed, _random);
                    db.UpdateAnalog(a.Index, new Analog(a.Value, flags, stamp));
                }

                foreach (var b in _breakers)
                {
                    // A breaker mid-travel keeps its old position until it
                    // arrives.
                    if (b.MovingUntil is { } until && now > until)
                    {
                        b.Closed = b.Target;
                        b.MovingUntil = null;
                    }

                    db.UpdateBinary(b.StatusIndex, new Binary(b.Closed, flags, stamp));
                    db.UpdateBinaryOutputStatus(
                        b.ControlIndex, new BinaryOutputStatus(b.Closed, flags, stamp));
                }

                foreach (var c in _counters)
                {
                    c.Value += c.PerSecond * TickSeconds;
                    db.UpdateCounter(c.Index, new Counter((uint)c.Value, flags, stamp));
                }

                // An event storm exercises the master's ability to keep up, and
                // the outstation's buffer-overflow reporting when it cannot.
                if (_inject.EventStorm > 0 && _breakers.Count > 0)
                {
                    var per = (int)(_inject.EventStorm * TickSeconds);
                    for (var i = 0; i < per; i++)
                    {
                        var b = _breakers[_stormIndex % _breakers.Count];
                        _stormIndex++;
                        db.UpdateBinary(
                            b.StatusIndex, new Binary(_stormIndex % 2 == 0, Flags.Online, stamp));
                    }
                }
            });
        }
    }

    /// <summary>Applies a control to the simulated plant.</summary>
    /// <remarks>
    /// This is what makes the simulator worth having: a trip actually opens the
    /// breaker, which raises a status event, which the master then receives —
    /// the whole loop a real integration exercises.
    /// </remarks>
    public CommandStatus Operate(ushort controlIndex, bool close, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var b in _breakers)
            {
                if (b.ControlIndex != controlIndex)
                {
                    continue;
                }

                if (b.Interlocked)
                {
                    // A real interlock refuses; reporting success would tell an
                    // operator the breaker moved when it did not.
                    return CommandStatus.NotSupported;
                }

                if (b.MovingUntil is not null)
                {
                    return CommandStatus.AlreadyActive;
                }

                b.Target = close;
                if (b.TravelTime > TimeSpan.Zero)
                {
                    b.MovingUntil = now + b.TravelTime;
                }
                else
                {
                    b.Closed = close;
                }

                return CommandStatus.Success;
            }

            return CommandStatus.NotSupported;
        }
    }

    /// <summary>
    /// Reports whether a control would be accepted, without applying it.
    /// </summary>
    /// <remarks>
    /// This is what a SELECT needs: the outstation's chance to say "not that
    /// point, not right now" before anything moves. A select that operated
    /// would defeat the entire two-pass sequence.
    /// </remarks>
    public CommandStatus WouldAccept(ushort controlIndex)
    {
        lock (_gate)
        {
            foreach (var b in _breakers)
            {
                if (b.ControlIndex != controlIndex)
                {
                    continue;
                }

                if (b.Interlocked)
                {
                    return CommandStatus.NotSupported;
                }

                return b.MovingUntil is not null
                    ? CommandStatus.AlreadyActive
                    : CommandStatus.Success;
            }

            return CommandStatus.NotSupported;
        }
    }

    /// <summary>Applies an analog output command to a simulated setpoint.</summary>
    public CommandStatus SetAnalog(ushort index, double value)
    {
        lock (_gate)
        {
            foreach (var a in _analogs)
            {
                if (a.Index != index)
                {
                    continue;
                }

                if (value < a.Min || value > a.Max)
                {
                    return CommandStatus.OutOfRange;
                }

                a.Signal = Signal.Fixed;
                a.Value = value;
                return CommandStatus.Success;
            }

            return CommandStatus.NotSupported;
        }
    }

    /// <summary>
    /// Renders the simulated plant, so a user starting the tool can see what the
    /// point indexes mean without reading the config file.
    /// </summary>
    public string Describe()
    {
        lock (_gate)
        {
            var b = new StringBuilder();
            b.Append("Simulated plant\n");

            if (_breakers.Count > 0)
            {
                b.Append("\n  Breakers (binary input / binary output)\n");
                foreach (var br in _breakers)
                {
                    var state = br.Closed ? "closed" : "open";
                    var extra = br.Interlocked ? "  [interlocked: commands refused]" : "";
                    b.Append(CultureInfo.InvariantCulture,
                        $"    BI {Pad(br.StatusIndex)} / BO {Pad(br.ControlIndex)}  {br.Name} ({state}){extra}\n");
                }
            }

            if (_analogs.Count > 0)
            {
                b.Append("\n  Analogs\n");
                foreach (var a in _analogs)
                {
                    b.Append(CultureInfo.InvariantCulture,
                        $"    AI {Pad(a.Index)}  {a.Name}  {a.Signal.ToString().ToLowerInvariant()} {Trim(a.Min)}..{Trim(a.Max)} {a.Units}\n");
                }
            }

            if (_counters.Count > 0)
            {
                b.Append("\n  Counters\n");
                foreach (var c in _counters)
                {
                    b.Append(CultureInfo.InvariantCulture,
                        $"    CT {Pad(c.Index)}  {c.Name}  {Trim(c.PerSecond)}/s\n");
                }
            }

            return b.ToString();
        }
    }

    private static string Pad(ushort index) =>
        index.ToString(CultureInfo.InvariantCulture).PadLeft(3);

    private static string Trim(double v) =>
        v == Math.Truncate(v)
            ? ((long)v).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.###", CultureInfo.InvariantCulture);
}
