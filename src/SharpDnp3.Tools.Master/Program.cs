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
// dnp3-master is a SCADA client: it polls outstations, records what they
// report, and issues controls.
//
//   dnp3-master -host 127.0.0.1:20000              # poll one device
//   dnp3-master -config sites.yaml -record ./data  # poll many, record to CSV
//   dnp3-master -host HOST operate latch-on 3      # one control, then exit
//
// Running it polls continuously and, with -listen, exposes the live values over
// HTTP. The operate subcommand is a separate one-shot connection, because
// issuing a control should not require standing up the whole recorder.

using System.Globalization;
using System.Runtime.InteropServices;
using SharpDnp3;
using SharpDnp3.Master;
using SharpDnp3.Tools.Master;

string? configPath = null;
string? host = null;
string? serialDevice = null;
var baud = 9600;
ushort local = 1;
ushort remote = 10;
var poll = TimeSpan.FromSeconds(5);
var integrity = TimeSpan.FromMinutes(5);
var timeout = TimeSpan.FromSeconds(5);
string? record = null;
string? listen = null;
var unsolicited = false;
var verbose = false;
var quiet = false;
var rest = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    try
    {
        switch (args[i])
        {
            case "-config" when i + 1 < args.Length: configPath = args[++i]; break;
            case "-host" when i + 1 < args.Length: host = args[++i]; break;
            case "-serial" when i + 1 < args.Length: serialDevice = args[++i]; break;
            case "-baud" when i + 1 < args.Length:
                baud = int.Parse(args[++i], CultureInfo.InvariantCulture);
                break;
            case "-local" when i + 1 < args.Length:
                local = ushort.Parse(args[++i], CultureInfo.InvariantCulture);
                break;
            case "-remote" when i + 1 < args.Length:
                remote = ushort.Parse(args[++i], CultureInfo.InvariantCulture);
                break;
            case "-poll" when i + 1 < args.Length: poll = GoDuration.Parse(args[++i]); break;
            case "-integrity" when i + 1 < args.Length:
                integrity = GoDuration.Parse(args[++i]);
                break;
            case "-timeout" when i + 1 < args.Length: timeout = GoDuration.Parse(args[++i]); break;
            case "-record" when i + 1 < args.Length: record = args[++i]; break;
            case "-listen" when i + 1 < args.Length: listen = args[++i]; break;
            case "-unsolicited": unsolicited = true; break;
            case "-v": verbose = true; break;
            case "-q": quiet = true; break;
            case "-h" or "--help": Usage(); return 0;
            default:
                // Anything that is not a flag starts the subcommand, and
                // everything after it belongs to that subcommand rather than
                // to this parser.
                if (args[i].StartsWith('-'))
                {
                    Console.Error.WriteLine($"dnp3-master: unknown argument {args[i]}");
                    Usage();
                    return 1;
                }

                rest.AddRange(args[i..]);
                i = args.Length;
                break;
        }
    }
    catch (Exception ex) when (ex is FormatException or OverflowException)
    {
        Console.Error.WriteLine($"dnp3-master: {args[i]}: {ex.Message}");
        return 1;
    }
}

var level = quiet ? Dnp3LogLevel.Error : verbose ? Dnp3LogLevel.Debug : Dnp3LogLevel.Info;
IDnp3Logger log = new TextWriterDnp3Logger(Console.Error, level);

// The operate subcommand takes over entirely: it connects, issues one control,
// reports the outcome and exits.
if (rest.Count > 0)
{
    if (rest[0] != "operate")
    {
        Console.Error.WriteLine($"dnp3-master: unknown command \"{rest[0]}\"; see -help");
        return 1;
    }

    return await OperateAsync(rest.Skip(1).ToList()).ConfigureAwait(false);
}

SitesConfig cfg;
try
{
    cfg = BuildConfig();
    cfg.Validate();
}
catch (Exception ex) when (ex is IOException or FormatException)
{
    Console.Error.WriteLine("dnp3-master: " + ex.Message);
    return 1;
}
catch (MissingTargetException ex)
{
    Console.Error.WriteLine("dnp3-master: " + ex.Message);
    return 1;
}

Recorder? recorder = null;
if (!string.IsNullOrEmpty(record))
{
    try
    {
        recorder = new Recorder(record);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("dnp3-master: opening the recording: " + ex.Message);
        return 1;
    }
}

var snapshot = new Snapshot();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// A recorder is normally run under something that stops it with SIGTERM, and a
// recorder killed outright is a recording with its last rows missing.
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    cts.Cancel();
});

Console.WriteLine(string.Format(
    CultureInfo.InvariantCulture,
    "Polling {0} site(s) as master {1}:", cfg.Sites.Count, cfg.Local));

foreach (var raw in cfg.Sites)
{
    var r = cfg.Resolved(raw);
    Console.WriteLine(string.Format(
        CultureInfo.InvariantCulture,
        "  {0,-16} outstation {1,-5} {2}  poll {3}  integrity {4}",
        r.Name, r.Address, r.Describe(),
        GoDuration.ToString(r.Poll), GoDuration.ToString(r.Integrity)));
}

if (!string.IsNullOrEmpty(record))
{
    Console.WriteLine($"Recording to {record}/values.csv and {record}/events.csv");
}

Console.WriteLine("Press Ctrl-C to stop.");

try
{
    await Runner.RunAsync(cfg, recorder, snapshot, log, listen, cts.Token).ConfigureAwait(false);
}
finally
{
    // Rows are flushed as they are written, so this reports only a failure to
    // close the files. It is still worth saying: the whole point of a recording
    // is that it can be trusted afterwards, and a recorder that could not close
    // its own record should not exit quietly.
    try
    {
        recorder?.Dispose();
    }
    catch (IOException ex)
    {
        log.Log(Dnp3LogLevel.Error, "closing the recording", ("err", ex.Message));
    }
}

Console.WriteLine();
Console.WriteLine("Final state:");
Console.Write(snapshot.Summary());
return 0;

// Turns the flags into a configuration, or loads one from a file.
SitesConfig BuildConfig()
{
    if (!string.IsNullOrEmpty(configPath))
    {
        return SitesConfig.Load(configPath);
    }

    if (string.IsNullOrEmpty(host) && string.IsNullOrEmpty(serialDevice))
    {
        Usage();
        throw new MissingTargetException("give -config, -host, or -serial");
    }

    var c = SitesConfig.Default();
    c.Local = local;
    c.Defaults.Poll = poll;
    c.Defaults.Integrity = integrity;
    c.Defaults.Timeout = timeout;
    c.Defaults.Unsolicited = unsolicited;

    c.Sites =
    [
        new Site
        {
            Name = string.IsNullOrEmpty(host) ? serialDevice! : host,
            Host = host ?? "",
            Serial = serialDevice ?? "",
            Baud = baud,
            Address = remote,
        },
    ];
    return c;
}

// Issues a single control and reports the outcome.
async Task<int> OperateAsync(List<string> operands)
{
    if (operands.Count < 2)
    {
        Console.Error.WriteLine(
            "dnp3-master: usage: dnp3-master -host HOST operate <control> <index> [value]");
        return 1;
    }

    if (string.IsNullOrEmpty(host) && string.IsNullOrEmpty(serialDevice))
    {
        Console.Error.WriteLine("dnp3-master: operate needs -host or -serial");
        return 1;
    }

    ushort index;
    try
    {
        index = ushort.Parse(operands[1], CultureInfo.InvariantCulture);
    }
    catch (Exception ex) when (ex is FormatException or OverflowException)
    {
        Console.Error.WriteLine($"dnp3-master: index \"{operands[1]}\": {ex.Message}");
        return 1;
    }

    Command command;
    switch (operands[0])
    {
        case "latch-on": command = Command.LatchOn(index); break;
        case "latch-off": command = Command.LatchOff(index); break;
        case "trip": command = Command.Trip(index, 1000); break;
        case "close": command = Command.Close(index, 1000); break;
        case "analog":
            if (operands.Count < 3)
            {
                Console.Error.WriteLine(
                    "dnp3-master: analog needs a value: operate analog <index> <value>");
                return 1;
            }

            if (!float.TryParse(
                operands[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                Console.Error.WriteLine($"dnp3-master: value \"{operands[2]}\" is not a number");
                return 1;
            }

            command = Command.AnalogOutputFloat32(index, value);
            break;

        default:
            Console.Error.WriteLine(
                $"dnp3-master: unknown control \"{operands[0]}\"; " +
                "try latch-on, latch-off, trip, close or analog");
            return 1;
    }

    var site = new Site
    {
        Name = "operate",
        Host = host ?? "",
        Serial = serialDevice ?? "",
        Baud = baud,
        Local = local,
        Address = remote,
        Timeout = timeout,
    };

    using var opCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        opCts.Cancel();
    };

    using var channel = Runner.BuildChannel(site);

    var session = new MasterSession(new MasterConfig
    {
        LocalAddr = local,
        RemoteAddr = remote,
        ResponseTimeout = timeout,
        // No integrity poll: this connects to issue one control and leave, and
        // polling a device first would be noise on the wire and in its log.
        Log = log,
    });

    var run = session.RunAsync(channel, opCts.Token);

    try
    {
        // Wait for the link before issuing anything.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline && !session.Connected)
        {
            await Task.Delay(20, opCts.Token).ConfigureAwait(false);
        }

        if (!session.Connected)
        {
            Console.Error.WriteLine($"dnp3-master: could not connect to {site.Describe()}");
            return 1;
        }

        using var opTimeout = CancellationTokenSource.CreateLinkedTokenSource(opCts.Token);
        opTimeout.CancelAfter(TimeSpan.FromSeconds(30));

        // Select-before-operate: this is a person issuing a control, and the
        // select is the outstation's chance to refuse before anything moves.
        var result = await session.SelectAndOperateAsync([command], opTimeout.Token)
            .ConfigureAwait(false);

        Console.WriteLine($"{command}: {result}");
        return result.OK() ? 0 : 1;
    }
    catch (Exception ex) when (ex is Dnp3Exception or OperationCanceledException or IOException)
    {
        Console.WriteLine($"{command}: {ex.Message}");
        return 1;
    }
    finally
    {
        await opCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await run.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or Dnp3Exception or IOException)
        {
            // The session was told to stop; how it noticed is not news.
        }
    }
}

static void Usage() => Console.Error.Write(
    """
    dnp3-master — poll DNP3 outstations, record what they report, issue controls

    Usage:
      dnp3-master [flags]
      dnp3-master [flags] operate <control> <index> [value]

    Sites:
      -config FILE     read sites from YAML
      -host ADDR       poll a single outstation over TCP
      -serial PORT     poll a single outstation over serial
      -baud RATE       serial line rate                  (default 9600)
      -local N         master link address               (default 1)
      -remote N        outstation link address           (default 10)

    Polling:
      -poll DUR        event class poll interval         (default 5s, 0 disables)
      -integrity DUR   integrity poll interval           (default 5m, 0 disables)
      -timeout DUR     response timeout                  (default 5s)
      -unsolicited     enable unsolicited reporting

    Output:
      -record DIR      write values.csv and events.csv into DIR
      -listen ADDR     serve live values over HTTP
      -v / -q          more or less logging

    Controls (operate connects, issues one control, and exits):
      latch-on N       latch a binary output on
      latch-off N      latch it off
      trip N           pulse the trip coil
      close N          pulse the close coil
      analog N VALUE   write an analog setpoint

    HTTP endpoints:
      /status          per-site connection state and counters
      /points          the latest value of every point (?site=NAME to filter)
      /healthz

    Examples:
      dnp3-master -host 127.0.0.1:20000 -record ./data -listen :8080
      dnp3-master -config sites.yaml -v
      dnp3-master -host 127.0.0.1:20000 operate trip 0

    """);

namespace SharpDnp3.Tools.Master
{
    /// <summary>No device was named, so there is nothing to poll.</summary>
    internal sealed class MissingTargetException : Exception
    {
        public MissingTargetException(string message) : base(message) { }

        public MissingTargetException() { }

        public MissingTargetException(string message, Exception inner) : base(message, inner) { }
    }
}
