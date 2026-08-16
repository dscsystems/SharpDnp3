# SharpDnp3

A DNP3 (IEEE Std 1815-2012) master and outstation in C#, with no native
dependencies.

DNP3 is the protocol most of the electric utility industry uses between a SCADA
system and the equipment in a substation. This library implements both ends of
it: the **master** that polls, receives events and issues controls, and the
**outstation** that holds measurements, answers polls and executes those
controls. Both run over TCP, TLS, UDP, serial or an in-process pipe.

```console
$ dotnet add package SharpDnp3
```

.NET 10 or newer. One assembly, `SharpDnp3.dll`. The only package reference is
`System.IO.Ports`, and it is reached only from `SerialChannel`.

**Status: pre-1.0.** The version is `0.1.0` and the API is not stable yet. Pin a
version. Nothing here is a certified conformance claim — see
[before you put it in a substation](docs/user-guide.md#before-you-put-it-in-a-substation).

---

## A master in twenty lines

```csharp
using SharpDnp3;
using SharpDnp3.Channels;
using SharpDnp3.Master;

var handler = new ChannelHandler();

var master = new MasterSession(new MasterConfig
{
    LocalAddr = 1,    // this master's link address
    RemoteAddr = 10,  // the outstation's
    IntegrityOnStartup = true,
}, handler);

using var cts = new CancellationTokenSource();
using var channel = new TcpClientChannel("127.0.0.1:20000", Retry.Default);

var session = master.RunAsync(channel, cts.Token);   // runs until cancelled
await master.AddPeriodicScanAsync(TimeSpan.FromSeconds(5), Class.Class123, cts.Token);

await foreach (var u in handler.Updates.ReadAllAsync(cts.Token))
{
    Console.WriteLine($"{u.Type} {u.Index} = {u.Analog.Value} {u.Analog.Flags}");
}
```

## And the outstation it talks to

```csharp
using SharpDnp3;
using SharpDnp3.Channels;
using SharpDnp3.Outstation;

var outstation = new OutstationSession(new OutstationConfig
{
    LocalAddr = 10,
    RemoteAddr = 1,
    Database = new DatabaseConfig { Binary = 8, Analog = 4, DefaultClass = Class.Class1 },
});

using var channel = new TcpServerChannel(":20000");
var session = outstation.RunAsync(channel, cts.Token);

outstation.Update(db => db.UpdateAnalog(
    0, new Analog(11.2, Flags.Online, Timestamp.Now(DateTimeOffset.UtcNow))));
```

## Try it without any hardware

`Pipe.Create()` connects a real master to a real outstation in memory — the
actual link, transport, application and object layers, with no socket and no
device. Every integration test in this repository runs over it.

```csharp
var (masterSide, outstationSide) = Pipe.Create();
```

Or point the terminal explorer at its own built-in simulated substation:

```console
$ dotnet run --project src/SharpDnp3.Tools.Explorer -- -demo
```

---

## What is implemented

| | |
| --- | --- |
| **Roles** | Master and outstation, both complete sessions with their own task scheduling |
| **Transports** | TCP client and server, TLS (mutually authenticated), UDP, serial, in-process pipe |
| **Reads** | Integrity polls, class polls, range scans, periodic scans |
| **Events** | Class 1/2/3 assignment, deadbands, confirmation-backed event buffer, unsolicited reporting |
| **Controls** | CROB and analog output setpoints, direct operate, select-before-operate, no-reply operate |
| **Time** | LAN and serial (delay-measured) synchronisation, `NEED_TIME` handling, relative-time (CTO) objects |
| **Objects** | Groups 1–4, 10–13, 20–23, 30–34, 40–43, 50–52, 60, 80, 110–111, generated from a declarative spec |
| **Tooling** | Structured protocol decoder, four working command-line programs |

Deliberately out of scope: Secure Authentication v5 (use TLS), file transfer,
datasets, and serving several concurrent masters from one outstation session.

---

## Repository layout

| Path | What it is |
| --- | --- |
| `SharpDnp3.sln` | All nine projects, grouped into `src`, `tests` and `build` folders |
| `src/SharpDnp3/` | The library — the only thing that ships as a package |
| `src/SharpDnp3.Tools.Explorer/` | `dnp3-explorer`, a terminal browser for one outstation |
| `src/SharpDnp3.Tools.Master/` | `dnp3-master`, a poller, recorder and control tool |
| `src/SharpDnp3.Tools.Outstation/` | `dnp3-outstation`, a simulated RTU with plant behind it |
| `src/SharpDnp3.Tools.Decode/` | `dnp3-decode`, an offline frame decoder |
| `tests/SharpDnp3.Tests/` | Unit and in-memory integration tests |
| `tests/SharpDnp3.Conformance.Tests/` | Hand-built fragments checked against what the standard requires |
| `tests/SharpDnp3.Interop.Tests/` | Runs against go-dnp3 and opendnp3; skips when they are absent |
| `build/SharpDnp3.Generator/` | Generates the object codec table from the YAML spec |
| `docs/` | The reference documentation |

Inside the library, one directory per protocol layer: `Link/`, `Transport/`,
`App/`, `Objects/`, then `Master/`, `Outstation/`, `Channel/`, `Decoder/`.

---

## Documentation

- **[User guide](docs/user-guide.md)** — how to build a master, an outstation,
  or a tool that reads DNP3 traffic. Start here.
- **[API reference](docs/api.md)** — every public type and member, namespace by
  namespace.
- **[SKILL.md](SKILL.md)** — the same ground condensed for AI coding agents:
  the patterns, the gotchas that bite, and the commands to verify with.
- **[The example tools](src/README.md)** — four working programs, each with its
  own README.

New to DNP3 itself? [DNP3 in five
minutes](docs/user-guide.md#dnp3-in-five-minutes) covers the things that
surprise people arriving from Modbus.

---

## Building and testing

`SharpDnp3.sln` covers all nine projects — the library, the four tools, the
three test suites and the generator:

```console
$ dotnet build SharpDnp3.sln
$ dotnet test  SharpDnp3.sln
```

Or work on one project at a time:

```console
$ dotnet build src/SharpDnp3/SharpDnp3.csproj
$ dotnet test  tests/SharpDnp3.Tests/SharpDnp3.Tests.csproj
$ dotnet test  tests/SharpDnp3.Conformance.Tests/SharpDnp3.Conformance.Tests.csproj
```

Warnings are errors (`Directory.Build.props`), so a build that warns does not
pass.

The interop suite runs this stack against other implementations. It **skips**
rather than fails when they are not present, because an interop test that could
not reach its peer has proved nothing:

```console
$ GO_DNP3_BIN=/path/to/go-dnp3/bin OPENDNP3_BIN=/path/to/opendnp3/build/bin \
    dotnet test tests/SharpDnp3.Interop.Tests/SharpDnp3.Interop.Tests.csproj
```

### The generated object table

`src/SharpDnp3/Objects/Generated.*.cs` and `src/SharpDnp3/App/Generated.Sizes.cs`
are produced from `src/SharpDnp3/Objects/Spec/dnp3_objects.yaml`, the single
source of truth for every group, variation, size and field layout. **Do not edit
them by hand.** Change the spec and regenerate:

```console
$ dotnet run --project build/SharpDnp3.Generator -- -root .
$ dotnet run --project build/SharpDnp3.Generator -- -root . -check   # verify, don't write
```

The output is committed, so nobody consuming the library ever runs the
generator.

---

## Licence

GPL-3.0-or-later. See [LICENSE](LICENSE).

This is strong copyleft, and it is a library: **a program that links it must be
released under the GPL too.** That is the intended effect. If you need to link
from something proprietary, this is not the right licence for you, and no
exception is offered.

Copyright © 2026 Ricardo Olsen / DSC Systems.
