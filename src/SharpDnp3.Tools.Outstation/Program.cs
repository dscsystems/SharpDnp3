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
// dnp3-outstation — a simulated DNP3 outstation with plant behind it.
//
// The points behave like plant: a breaker stays open once tripped and takes
// time to travel, an analog ramps rather than jumping, and a control closes the
// loop — tripping breaker 0 opens breaker 0, which raises a binary input event,
// which the master receives.

using System.Globalization;
using SharpDnp3;
using SharpDnp3.Channels;
using SharpDnp3.Outstation;
using SharpDnp3.Tools.Outstation;

var listen = ":20000";
string? serial = null;
var baud = 9600;
var useUdp = false;
string? tlsCert = null;
string? tlsKey = null;
string? tlsCa = null;
string? configFile = null;
ushort? addressOverride = null;
ushort? masterOverride = null;
var unsolicited = false;
var maxMasters = 1;
var printPoints = false;
var verbose = false;
var quiet = false;
var injections = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-listen" when i + 1 < args.Length: listen = args[++i]; break;
        case "-serial" when i + 1 < args.Length: serial = args[++i]; break;
        case "-baud" when i + 1 < args.Length:
            baud = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "-udp": useUdp = true; break;
        case "-tls-cert" when i + 1 < args.Length: tlsCert = args[++i]; break;
        case "-tls-key" when i + 1 < args.Length: tlsKey = args[++i]; break;
        case "-tls-ca" when i + 1 < args.Length: tlsCa = args[++i]; break;
        case "-config" when i + 1 < args.Length: configFile = args[++i]; break;
        case "-address" when i + 1 < args.Length:
            addressOverride = ushort.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "-master" when i + 1 < args.Length:
            masterOverride = ushort.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "-max-masters" when i + 1 < args.Length:
            maxMasters = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "-inject" when i + 1 < args.Length: injections.Add(args[++i]); break;
        case "-unsolicited": unsolicited = true; break;
        case "-points": printPoints = true; break;
        case "-v": verbose = true; break;
        case "-q": quiet = true; break;
        case "-h" or "--help": Usage(); return 0;
        default:
            Console.Error.WriteLine($"dnp3-outstation: unknown argument {args[i]}");
            Usage();
            return 1;
    }
}

PlantConfig plant;
try
{
    plant = configFile is null ? PlantConfig.Default() : PlantConfig.Load(configFile);
    foreach (var spec in injections)
    {
        PlantConfig.ParseInjection(plant.Inject, spec);
    }
}
catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException)
{
    Console.Error.WriteLine("dnp3-outstation: " + ex.Message);
    return 1;
}

if (addressOverride is { } a)
{
    plant.Address = a;
}

if (masterOverride is { } m)
{
    plant.Master = m;
}

var simulator = new Simulator(plant);

if (printPoints)
{
    Console.Write(simulator.Describe());
    return 0;
}

var level = quiet ? Dnp3LogLevel.Error : verbose ? Dnp3LogLevel.Debug : Dnp3LogLevel.Info;
var log = new TextWriterDnp3Logger(Console.Error, level);

var config = new OutstationConfig
{
    LocalAddr = plant.Address,
    RemoteAddr = plant.Master,
    Database = plant.DatabaseConfig(),
    Events = new EventBufferConfig { MaxEvents = 5000 },
    // Each master holds its own event queue, so this is also a multiplier on
    // the memory the event buffer costs.
    MaxMasters = maxMasters,
    Log = log,
    // A serial line has no framing of its own, so the link-layer handshake is
    // what makes it reliable. Over TCP the transport already guarantees order.
    UseLinkConfirms = serial is not null,
    LinkRetries = 3,
    Unsolicited = new UnsolicitedConfig
    {
        Enabled = unsolicited,
        HoldTime = TimeSpan.FromMilliseconds(200),
        MaxEvents = 50,
    },
};

var commands = new SimulatedCommandHandler(simulator);
var session = new OutstationSession(config, null, commands);
plant.ApplyPointConfig(session.Database);

session.Update(db => db.UpdateOctetString(0, "SHARPDNP3 DEMO RTU"u8));

IChannel channel;
try
{
    channel = BuildChannel();
}
catch (Exception ex) when (ex is Dnp3Exception or IOException or FormatException)
{
    Console.Error.WriteLine("dnp3-outstation: " + ex.Message);
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.Write(simulator.Describe());
Console.WriteLine();
Console.WriteLine(string.Format(
    CultureInfo.InvariantCulture,
    "Outstation {0} listening for {1} on {2}",
    plant.Address,
    maxMasters > 1
        ? string.Format(CultureInfo.InvariantCulture, "up to {0} masters", maxMasters)
        : string.Format(CultureInfo.InvariantCulture, "master {0}", plant.Master),
    channel));
Console.WriteLine("Press Ctrl-C to stop.");

var run = session.RunAsync(channel, cts.Token);
var tick = SimulateAsync(session, simulator, plant, cts.Token);

try
{
    await Task.WhenAll(run, tick);
}
catch (OperationCanceledException)
{
    // A Ctrl-C is a clean shutdown.
}
finally
{
    channel.Dispose();
}

return 0;

IChannel BuildChannel()
{
    if (serial is not null)
    {
        return new SerialChannel(new SerialConfig { Device = serial, Baud = baud });
    }

    if (useUdp)
    {
        return new UdpChannel(new UdpConfig { LocalAddr = listen });
    }

    if (tlsCert is not null || tlsKey is not null || tlsCa is not null)
    {
        return new TlsServerChannel(listen, new Dnp3TlsConfig
        {
            CertFile = tlsCert ?? "",
            KeyFile = tlsKey ?? "",
            CaFile = tlsCa ?? "",
        });
    }

    return new TcpServerChannel(listen);
}

// Drives the plant on a fixed tick, and applies whatever faults were injected.
static async Task SimulateAsync(
    OutstationSession session,
    Simulator simulator,
    PlantConfig plant,
    CancellationToken cancellationToken)
{
    var period = TimeSpan.FromSeconds(Simulator.TickSeconds);
    var lastRestart = DateTimeOffset.UtcNow;

    using var timer = new PeriodicTimer(period);
    try
    {
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var now = DateTimeOffset.UtcNow;
            simulator.Apply(session, now);

            if (simulator.RestartDue(now, ref lastRestart))
            {
                lastRestart = now;
                session.Restart();
            }
        }
    }
    catch (OperationCanceledException)
    {
        // Shutting down.
    }
}

static void Usage() => Console.Error.Write(
    """
    dnp3-outstation — a simulated DNP3 outstation with plant behind it

    Usage:
      dnp3-outstation [flags]

    Transport (TCP by default):
      -listen ADDR        listen address                      (default :20000)
      -udp                use UDP instead of TCP
      -serial PORT        use a serial port
      -baud RATE          serial line rate                    (default 9600)
      -tls-cert FILE      certificate; with -tls-key and -tls-ca, enables TLS
      -tls-key FILE       private key
      -tls-ca FILE        authority used to verify the master

    Device:
      -config FILE        read the simulated plant from YAML
      -address N          override the outstation link address
      -master N           override the master link address
      -unsolicited        push events without being polled
      -max-masters N      serve up to N masters at once, over TCP or TLS
                          (default 1; each holds its own event queue)
      -points             print the point list and exit

    Fault injection (repeatable):
      -inject event-storm=N       generate N binary events per second
      -inject restart-after=DUR   report a restart every DUR
      -inject offline-every=DUR   flip points to comm-lost every DUR
      -inject device-trouble      assert the DEVICE_TROUBLE indication

    Logging:
      -v                  log protocol activity
      -q                  log nothing but errors

    Examples:
      dnp3-outstation
      dnp3-outstation -config substation.yaml -unsolicited -v
      dnp3-outstation -max-masters 4
      dnp3-outstation -inject event-storm=500 -inject offline-every=30s

    """);

namespace SharpDnp3.Tools.Outstation
{
    /// <summary>Routes controls into the plant simulation.</summary>
    internal sealed class SimulatedCommandHandler : ICommandHandler
    {
        private readonly Simulator _simulator;

        public SimulatedCommandHandler(Simulator simulator) => _simulator = simulator;

        public CommandStatus SelectCrob(ushort index, ControlRelayOutputBlock c) =>
            _simulator.WouldAccept(index);

        public CommandStatus OperateCrob(ushort index, ControlRelayOutputBlock c, OperateType op)
        {
            // A trip opens and a close closes; a latch says so directly. Anything
            // else is a code this plant does not model.
            if (c.Code.IsTrip())
            {
                return _simulator.Operate(index, close: false, DateTimeOffset.UtcNow);
            }

            if (c.Code.IsClose())
            {
                return _simulator.Operate(index, close: true, DateTimeOffset.UtcNow);
            }

            var op4 = c.Code.OpType();
            if (op4 == ControlCode.LatchOn)
            {
                return _simulator.Operate(index, close: true, DateTimeOffset.UtcNow);
            }

            if (op4 == ControlCode.LatchOff)
            {
                return _simulator.Operate(index, close: false, DateTimeOffset.UtcNow);
            }

            return CommandStatus.NotSupported;
        }

        public CommandStatus SelectAnalog(ushort index, AnalogOutputCommand v) =>
            CommandStatus.Success;

        public CommandStatus OperateAnalog(ushort index, AnalogOutputCommand v, OperateType op) =>
            _simulator.SetAnalog(index, v.Value);
    }
}
