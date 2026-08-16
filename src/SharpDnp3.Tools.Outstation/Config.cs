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
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SharpDnp3.Tools.Outstation;

/// <summary>The simulated plant and the link addresses it answers on.</summary>
public sealed class PlantConfig
{
    /// <summary>This outstation's link address.</summary>
    public ushort Address { get; set; } = 10;

    /// <summary>The master's link address.</summary>
    public ushort Master { get; set; } = 1;

    /// <summary>The breakers, each a status input and a control output.</summary>
    public List<BreakerSim> Breakers { get; set; } = [];

    /// <summary>The analog inputs.</summary>
    public List<AnalogSim> Analogs { get; set; } = [];

    /// <summary>The counters.</summary>
    public List<CounterSim> Counters { get; set; } = [];

    /// <summary>The faults to inject. Not read from YAML; set from flags.</summary>
    [YamlIgnore]
    public Injection Inject { get; set; } = new();

    /// <summary>
    /// The substation the tool simulates when no configuration is given.
    /// </summary>
    /// <remarks>
    /// A feeder bay: two feeders, a bus tie and an earth switch that refuses
    /// commands, with the measurements a bay actually reports.
    /// </remarks>
    public static PlantConfig Default() => new()
    {
        Breakers =
        [
            new BreakerSim
            {
                StatusIndex = 0, ControlIndex = 0, Name = "Feeder 1 breaker",
                Closed = true, TravelTime = TimeSpan.FromMilliseconds(400),
            },
            new BreakerSim
            {
                StatusIndex = 1, ControlIndex = 1, Name = "Feeder 2 breaker",
                Closed = true, TravelTime = TimeSpan.FromMilliseconds(400),
            },
            new BreakerSim
            {
                StatusIndex = 2, ControlIndex = 2, Name = "Bus tie",
                Closed = false, TravelTime = TimeSpan.FromMilliseconds(600),
            },
            new BreakerSim
            {
                StatusIndex = 3, ControlIndex = 3, Name = "Earth switch (racked out)",
                Closed = false, Interlocked = true,
            },
        ],
        Analogs =
        [
            new AnalogSim
            {
                Index = 0, Name = "Bus voltage", Units = "kV", Signal = Signal.Sine,
                Min = 10.8, Max = 11.2, Period = TimeSpan.FromSeconds(45),
                Noise = 0.005, Deadband = 0.02,
            },
            new AnalogSim
            {
                Index = 1, Name = "Feeder 1 current", Units = "A", Signal = Signal.Walk,
                Min = 0, Max = 400, Deadband = 2,
            },
            new AnalogSim
            {
                Index = 2, Name = "Feeder 2 current", Units = "A", Signal = Signal.Walk,
                Min = 0, Max = 400, Deadband = 2,
            },
            new AnalogSim
            {
                Index = 3, Name = "Transformer temperature", Units = "degC", Signal = Signal.Ramp,
                Min = 35, Max = 85, Period = TimeSpan.FromMinutes(5), Deadband = 0.5,
            },
            new AnalogSim
            {
                Index = 4, Name = "Tap position", Units = "", Signal = Signal.Step,
                Min = 7, Max = 9, Period = TimeSpan.FromMinutes(2),
            },
        ],
        Counters =
        [
            new CounterSim { Index = 0, Name = "Feeder 1 energy", PerSecond = 12 },
            new CounterSim { Index = 1, Name = "Feeder 2 energy", PerSecond = 8 },
        ],
    };

    /// <summary>Reads a plant description from YAML.</summary>
    public static PlantConfig Load(string path)
    {
        var text = File.ReadAllText(path);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<PlantConfig>(text)
            ?? throw new InvalidOperationException($"{path}: empty configuration");
    }

    /// <summary>
    /// Returns a database sized to hold every point the plant describes.
    /// </summary>
    public SharpDnp3.Outstation.DatabaseConfig DatabaseConfig()
    {
        var maxBinary = 0;
        var maxControl = 0;
        foreach (var b in Breakers)
        {
            maxBinary = Math.Max(maxBinary, b.StatusIndex + 1);
            maxControl = Math.Max(maxControl, b.ControlIndex + 1);
        }

        var maxAnalog = 0;
        foreach (var a in Analogs)
        {
            maxAnalog = Math.Max(maxAnalog, a.Index + 1);
        }

        var maxCounter = 0;
        foreach (var c in Counters)
        {
            maxCounter = Math.Max(maxCounter, c.Index + 1);
        }

        return new SharpDnp3.Outstation.DatabaseConfig
        {
            Binary = maxBinary,
            BinaryOutputStatus = maxControl,
            Analog = maxAnalog,
            AnalogOutputStatus = maxAnalog,
            Counter = maxCounter,
            FrozenCounter = maxCounter,
            OctetString = 1,
            DefaultClass = Class.Class1,
        };
    }

    /// <summary>
    /// Applies each point's class and deadband to a freshly built database.
    /// </summary>
    public void ApplyPointConfig(SharpDnp3.Outstation.Database db)
    {
        ArgumentNullException.ThrowIfNull(db);

        foreach (var a in Analogs)
        {
            db.Configure(PointType.Analog, a.Index, new SharpDnp3.Outstation.PointConfig
            {
                Class = ClassOf(a.Class),
                Deadband = a.Deadband,
            });
        }

        foreach (var b in Breakers)
        {
            db.Configure(PointType.Binary, b.StatusIndex, new SharpDnp3.Outstation.PointConfig
            {
                Class = ClassOf(b.Class),
            });
            db.Configure(
                PointType.BinaryOutputStatus, b.ControlIndex, new SharpDnp3.Outstation.PointConfig
                {
                    Class = ClassOf(b.Class),
                });
        }

        foreach (var c in Counters)
        {
            db.Configure(PointType.Counter, c.Index, new SharpDnp3.Outstation.PointConfig
            {
                Class = ClassOf(c.Class),
            });
        }
    }

    private static Class ClassOf(int n) => n switch
    {
        1 => Class.Class1,
        2 => Class.Class2,
        3 => Class.Class3,
        _ => Class.None,
    };

    /// <summary>Parses a <c>-inject key=value</c> flag onto the injection set.</summary>
    public static void ParseInjection(Injection inject, string spec)
    {
        ArgumentNullException.ThrowIfNull(inject);

        var eq = spec.IndexOf('=', StringComparison.Ordinal);
        var key = eq < 0 ? spec : spec[..eq];
        var value = eq < 0 ? "" : spec[(eq + 1)..];

        switch (key)
        {
            case "event-storm":
                inject.EventStorm = int.Parse(value, CultureInfo.InvariantCulture);
                break;

            case "device-trouble":
                inject.DeviceTrouble = true;
                break;

            case "restart-after":
                inject.RestartAfter = ParseDuration(value);
                break;

            case "offline-every":
                inject.OfflineEvery = ParseDuration(value);
                break;

            default:
                throw new FormatException($"unknown injection \"{key}\"");
        }
    }

    /// <summary>Parses a Go-style duration: 500ms, 30s, 5m, 1h.</summary>
    public static TimeSpan ParseDuration(string s)
    {
        ArgumentException.ThrowIfNullOrEmpty(s);

        var total = TimeSpan.Zero;
        var i = 0;

        while (i < s.Length)
        {
            var start = i;
            while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] is '.' or '-' or '+'))
            {
                i++;
            }

            if (start == i)
            {
                throw new FormatException($"\"{s}\" is not a duration");
            }

            var number = double.Parse(s[start..i], CultureInfo.InvariantCulture);

            var unitStart = i;
            while (i < s.Length && char.IsAsciiLetter(s[i]))
            {
                i++;
            }

            var unit = s[unitStart..i];
            total += unit switch
            {
                "ns" => TimeSpan.FromTicks((long)(number / 100)),
                "us" or "µs" => TimeSpan.FromTicks((long)(number * 10)),
                "ms" => TimeSpan.FromMilliseconds(number),
                "s" => TimeSpan.FromSeconds(number),
                "m" => TimeSpan.FromMinutes(number),
                "h" => TimeSpan.FromHours(number),
                _ => throw new FormatException($"\"{s}\" has an unknown time unit \"{unit}\""),
            };
        }

        return total;
    }
}
