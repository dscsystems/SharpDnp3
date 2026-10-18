// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The device under test for the dnp3c conformance harness.
//
// dnp3c (github.com/stepfunc/dnp3c) runs the DNP Users Group's IED
// certification procedures against an outstation. The procedures ask for
// things only the device's owner can do — cycle its power, generate events,
// check that an output really operated — and dnp3c hands those to an
// integration plugin. Ours, in ../plugin, is a thin shim that forwards each
// call here over HTTP, so everything that knows about SharpDnp3 is in C#:
//
//     SharpDnp3.Conformance.Dut -listen 127.0.0.1:20000 -control 127.0.0.1:20080
//
// The DNP3 side is a SharpDnp3 outstation on -listen. The control side takes a
// JSON request per plugin call on -control and answers with what was done or
// why a check failed. Nothing on the control side is a DNP3 concern; it exists
// only so a Java harness can drive a .NET outstation.

using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using SharpDnp3;
using SharpDnp3.Conformance.Dut;

var listen = "127.0.0.1:20000";
var control = "127.0.0.1:20080";
var level = 3;
var verbose = false;

for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length
        ? args[++i]
        : throw new ArgumentException($"{args[i]} needs a value");

    switch (args[i].TrimStart('-'))
    {
        case "listen":
            listen = Next();
            break;
        case "control":
            control = Next();
            break;
        case "level":
            level = int.Parse(Next(), CultureInfo.InvariantCulture);
            break;
        case "v":
            verbose = true;
            break;
        default:
            Console.Error.WriteLine(
                "usage: SharpDnp3.Conformance.Dut [-listen address] [-control address] [-level 2|3] [-v]");
            return 2;
    }
}

if (level is not (2 or 3))
{
    Console.Error.WriteLine($"dut: -level {level}: the DUT reports at subset level 2 or 3");
    return 1;
}

var log = new TextWriterDnp3Logger(Console.Error, verbose ? Dnp3LogLevel.Debug : Dnp3LogLevel.Warn);
var device = new Device(listen, level, log);
try
{
    device.Start();
}
catch (DutException ex)
{
    Console.Error.WriteLine("dut: " + ex.Message);
    return 1;
}

using var listener = new HttpListener();
listener.Prefixes.Add($"http://{control}/");
listener.Start();

using var stop = new CancellationTokenSource();
void Shutdown(PosixSignalContext ctx)
{
    ctx.Cancel = true;
    stop.Cancel();
    // Best effort: the process is finishing either way.
    listener.Stop();
}

using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, Shutdown);
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Shutdown);

Console.WriteLine($"dut: DNP3 on {listen}, control on {control}");
var rpc = new Rpc(device);
while (!stop.IsCancellationRequested)
{
    HttpListenerContext context;
    try
    {
        context = await listener.GetContextAsync();
    }
    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
    {
        break;
    }

    // One call at a time, as the plugin makes them: a procedure's steps are
    // sequential, and each acts on what the one before it left.
    await rpc.ServeAsync(context);
}

device.Stop();
return 0;
