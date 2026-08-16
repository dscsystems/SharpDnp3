---
name: sharpdnp3
description: Build DNP3 (IEEE 1815-2012) masters, outstations and protocol tools in C# with SharpDnp3. Use when writing SCADA clients, RTU/outstation simulators, substation integrations, or anything that reads DNP3 traffic — and when debugging why a DNP3 link is silent, why analog values arrive truncated, or why events stop arriving.
---

# SharpDnp3 for coding agents

A condensed, task-oriented reference for writing application code against
SharpDnp3. It assumes you can read the [API reference](docs/api.md) and
[user guide](docs/user-guide.md) when you need depth; this file is what to know
before you write the first line, and the mistakes to not make.

**Read [§4 Failure modes](#4-failure-modes-read-this-before-writing-code) before
writing an outstation.** Four of the six failures listed there produce working
code that is quietly wrong rather than code that fails to compile.

---

## 1. Orientation

DNP3 connects a SCADA system to substation equipment. Two roles, both
implemented here:

- **master** — polls, receives events, issues controls (the SCADA side);
- **outstation** — holds measurements, answers polls, executes controls (the
  device: an RTU, a relay, a meter).

Five facts that change how you write the code:

1. **Link addresses are not IP addresses.** Each station has a 16-bit link
   address, independent of the socket. The master's `RemoteAddr` must equal the
   outstation's `LocalAddr` and vice versa. Get them wrong and you get
   *silence*, not an error — mismatched frames are dropped by design.
2. **Static values and events are different data.** A static read returns the
   present value; an event is a queued record that the value *changed*. Polling
   only static data loses everything that happened between polls.
3. **Classes are how events are requested.** Points are assigned to event class
   1, 2 or 3. Class 0 is not an event class — it means "all static data". So an
   integrity poll is `Class.All` (0+1+2+3) and a routine event poll is
   `Class.Class123`.
4. **Quality travels with every value.** `Flags.Online` clear means the value
   beside it is not trustworthy. A zero with `Online` clear does not mean the
   measurement is zero.
5. **Every measurement is `(Value, Flags, Timestamp)`** and every measurement
   type is a `readonly record struct`. Point indexes are per type: binary input
   3 and analog input 3 are different points.

### Namespaces

```csharp
using SharpDnp3;            // measurements, Flags, Timestamp, Class, CommandStatus, Iin, exceptions
using SharpDnp3.Master;     // MasterSession, MasterConfig, Command, IMasterHandler
using SharpDnp3.Outstation; // OutstationSession, OutstationConfig, Database, ICommandHandler
using SharpDnp3.Channels;   // TcpClientChannel, TcpServerChannel, TlsClientChannel, SerialChannel, UdpChannel, Pipe, Retry
using SharpDnp3.Decoding;   // Dnp3Decoder, Trace — only for traffic-reading tools
using SharpDnp3.Objects;    // GroupVar, codecs — rarely needed by an application
```

`SharpDnp3.App`, `.Link` and `.Transport` hold layer primitives. They are public
because the decoder exposes them; **if you are naming them in application code,
check first that a session method does not already do what you want.**

---

## 2. Recipes

### 2.1 Master that consumes updates

Prefer this shape over implementing `IMasterHandler` directly. `ChannelHandler`
puts a bounded queue between the session loop and your code, so slow consumers
cannot stall the protocol.

```csharp
using SharpDnp3;
using SharpDnp3.Channels;
using SharpDnp3.Master;

var handler = new ChannelHandler(1024);

var master = new MasterSession(new MasterConfig
{
    LocalAddr = 1,
    RemoteAddr = 10,

    // The standard startup sequence.
    DisableUnsolOnStartup = true,
    IntegrityOnStartup = true,        // also re-runs on every reported restart
    UnsolClassMask = Class.Class123,  // enabled after the integrity poll

    ResponseTimeout = TimeSpan.FromSeconds(5),
    KeepAlive = TimeSpan.FromSeconds(30),
}, handler);

using var cts = new CancellationTokenSource();
using var channel = new TcpClientChannel("10.0.0.5:20000", Retry.Default);

var session = master.RunAsync(channel, cts.Token);   // hold this Task

await master.AddPeriodicScanAsync(TimeSpan.FromSeconds(5), Class.Class123, cts.Token);
await master.AddPeriodicScanAsync(TimeSpan.FromMinutes(5), Class.All, cts.Token);

await foreach (var u in handler.Updates.ReadAllAsync(cts.Token))
{
    switch (u.Type)
    {
        case PointType.Binary:
            Store(u.Index, u.Binary.Value, u.Binary.Flags, u.Binary.Time);
            break;
        case PointType.Analog:
            Store(u.Index, u.Analog.Value, u.Analog.Flags, u.Analog.Time);
            break;
        case PointType.Counter:
            Store(u.Index, u.Counter.Value, u.Counter.Flags, u.Counter.Time);
            break;
    }
}
```

`u.Type` selects which measurement property is meaningful; the others hold
default values. `u.Info.IsEvent` distinguishes an event from static poll data —
a historian needs that distinction, and only the group carries it.

**Cancellation is the shutdown path.** There is no `Stop`, `Close` or `Dispose`
on a session. `RunAsync` reconnects on its own; a dropped socket is not an error
you handle, it is the `Retry` policy working.

**Every request method is thread-safe.** `IntegrityPollAsync`, `ScanRangeAsync`,
`DirectOperateAsync` and the rest hand a task to the session loop and await it,
so an ASP.NET endpoint can call them directly.

### 2.2 Master with a direct handler

Use this only when the consumer is fast — it runs *on the session loop*, and a
slow method here delays the next poll.

```csharp
internal sealed class Printing : NopHandler   // derive; override only what you need
{
    public override void HandleAnalog(HeaderInfo info, IReadOnlyList<Indexed<Analog>> values)
    {
        foreach (var v in values)
        {
            Console.WriteLine(
                $"AI {v.Index} = {v.Value.Value} " +
                $"{v.Value.Flags.StringFor(PointType.Analog)} " +
                $"{(info.IsEvent ? "event" : "static")}");
        }
    }
}
```

`BeginFragment` / `EndFragment` bracket every fragment — the place to open and
close a transaction or batch a UI repaint.

### 2.3 Controls

```csharp
// Operator-initiated control on plant that matters: select-before-operate.
var result = await master.SelectAndOperateAsync([Command.Trip(3, 1000)], ct);

// Automated action: direct operate.
result = await master.DirectOperateAsync([Command.AnalogOutputFloat32(7, 13.75f)], ct);

// Several points in one request.
result = await master.DirectOperateAsync(
    Command.LatchOn(1), Command.LatchOff(2), Command.Close(3, 1000));

if (!result.OK())
{
    for (var i = 0; i < result.Statuses.Count; i++)
    {
        if (!result.Statuses[i].OK())
        {
            Console.WriteLine(
                $"point {result.Commands[i].Index}: {result.Statuses[i].ToDisplayString()}");
        }
    }
}
```

| Factory | Sends |
| --- | --- |
| `Command.Trip(index, pulseMillis)` | CROB, pulse-on with the trip coil |
| `Command.Close(index, pulseMillis)` | CROB, pulse-on with the close coil |
| `Command.LatchOn(index)` / `LatchOff(index)` | CROB, latch |
| `Command.Crob(index, crob)` | any CROB you build |
| `Command.AnalogOutputInt16/Int32/Float32/Float64(index, v)` | g41 setpoints |

**Build commands only through these factories.** They zero the status octet so
the outstation's echo is what fills it in.

**A refused control is not an exception.** The exchange completed and the
outstation said no; the statuses are in `CommandResult`. `OK()` is false unless
*every* command succeeded — a partial success reported as success tells an
operator a breaker operated when it did not. Use `ThrowIfFailed()` when you do
want an exception.

### 2.4 Outstation

```csharp
using SharpDnp3;
using SharpDnp3.Channels;
using SharpDnp3.Outstation;

internal sealed class Commands : ICommandHandler
{
    public OutstationSession? Session { get; set; }

    // SELECT MUST NOT OPERATE. It answers whether we *would* accept.
    public CommandStatus SelectCrob(ushort index, ControlRelayOutputBlock c) =>
        index < 4 ? CommandStatus.Success : CommandStatus.NotSupported;

    public CommandStatus OperateCrob(ushort index, ControlRelayOutputBlock c, OperateType op)
    {
        if (index >= 4)
        {
            return CommandStatus.NotSupported;
        }

        var on = c.Code.IsClose() || c.Code.OpType() == ControlCode.LatchOn;
        var now = Timestamp.Now(DateTimeOffset.UtcNow);

        // Close the loop: the output moves and so does the status point the
        // master reads back. One Update, so they report as one consistent set.
        Session!.Update(db =>
        {
            db.UpdateBinaryOutputStatus(index, new BinaryOutputStatus(on, Flags.Online, now));
            db.UpdateBinary(index, new Binary(on, Flags.Online, now));
        });
        return CommandStatus.Success;
    }

    public CommandStatus SelectAnalog(ushort index, AnalogOutputCommand v) =>
        index < 2 ? CommandStatus.Success : CommandStatus.NotSupported;

    public CommandStatus OperateAnalog(ushort index, AnalogOutputCommand v, OperateType op)
    {
        if (index >= 2)
        {
            return CommandStatus.NotSupported;
        }

        Session!.Update(db => db.UpdateAnalogOutputStatus(
            index,
            new AnalogOutputStatus(v.Value, Flags.Online, Timestamp.Now(DateTimeOffset.UtcNow))));
        return CommandStatus.Success;
    }
}

var commands = new Commands();

var outstation = new OutstationSession(new OutstationConfig
{
    LocalAddr = 10,
    RemoteAddr = 1,
    Database = new DatabaseConfig
    {
        Binary = 8, Analog = 4, Counter = 2,
        BinaryOutputStatus = 4, AnalogOutputStatus = 2,
        DefaultClass = Class.Class1,
    },
    Events = new EventBufferConfig { MaxEvents = 5000 },
    Unsolicited = new UnsolicitedConfig
    {
        Enabled = true,
        HoldTime = TimeSpan.FromMilliseconds(200),  // coalesce a burst
        MaxEvents = 20,
    },
}, new NopApplication(), commands);

commands.Session = outstation;

// Point configuration goes HERE, before RunAsync.
if (outstation.Database.TryGetAnalog(0, out _, out var cfg))
{
    cfg.StaticVariation = 5;  // g30v5, single precision with flags
    cfg.EventVariation = 7;   // g32v7, single precision with time
    cfg.Deadband = 0.1;
    outstation.Database.Configure(PointType.Analog, 0, cfg);
}

using var channel = new TcpServerChannel(":20000");
var session = outstation.RunAsync(channel, cts.Token);

// Then feed it from wherever the measurements come from.
outstation.Update(db => db.UpdateAnalog(
    0, new Analog(11.2, Flags.Online, Timestamp.Now(DateTimeOffset.UtcNow))));
```

`IOutstationApplication` supplies the clock and restart behaviour; derive from
`NopApplication` and override `Now()`, `SupportsWriteTime()`,
`WriteAbsoluteTime()`, `ColdRestart()`, `WarmRestart()` as needed.

### 2.5 Test without hardware

```csharp
var (masterSide, outstationSide) = Pipe.Create();
```

Two `IChannel`s wired to each other in memory — the real link, transport,
application and object layers, no socket, no device. This is how every
integration test in the repository runs, and the fastest way to develop against
a device you do not have.

```csharp
var outTask = outstation.RunAsync(outstationSide, cts.Token);
var masterTask = master.RunAsync(masterSide, cts.Token);

while (!master.Connected)          // wait before asserting anything timing-sensitive
{
    await Task.Delay(10, cts.Token);
}

await master.IntegrityPollAsync(cts.Token);
```

Set `MasterConfig.TimeProvider` to a `FakeTimeProvider` to drive timeouts
without waiting for them.

For a harder target, run the example outstation with faults injected:

```console
$ dotnet run --project src/SharpDnp3.Tools.Outstation -- -inject event-storm=500
$ dotnet run --project src/SharpDnp3.Tools.Outstation -- -inject restart-after=5m
$ dotnet run --project src/SharpDnp3.Tools.Outstation -- -inject offline-every=30s
```

### 2.6 Read raw traffic

```csharp
using SharpDnp3.Decoding;

var decoder = new Dnp3Decoder(Direction.Rx);

decoder.Feed(bytesFromTheSocket, trace =>
{
    var b = new StringBuilder();
    trace.Render(b, showHex: false);
    Console.Write(b.ToString());

    if (trace.App is { } app)          // null unless this frame COMPLETED a fragment
    {
        for (var i = 0; i < app.Objects.Count; i++)
        {
            foreach (var v in app.Values[i])
            {
                Console.WriteLine($"{app.Objects[i].Group}v{app.Objects[i].Variation} " +
                                  $"[{v.Index}] {v.Text}");
            }
        }
    }
});
```

**One decoder per direction per connection.** It holds link and transport state;
feeding both directions into one interleaves two independent transport sequences
and produces nonsense. `Reset()` when the connection comes back.

Decoded values are formatted *text*, because every consumer of this namespace
wants text. If you need typed measurements, run a `MasterSession`.

---

## 3. Transports

```csharp
IChannel c = new TcpClientChannel("10.0.0.5:20000", Retry.Default);  // master dialling out
c = new TcpServerChannel(":20000");                                  // outstation listening
c = new UdpChannel(new UdpConfig { RemoteAddr = "10.0.0.5:20000" });
c = new SerialChannel(new SerialConfig { Device = "/dev/ttyUSB0", Baud = 9600 });
c = new TlsClientChannel("10.0.0.5:20000",
        new Dnp3TlsConfig { CertFile = "m.crt", KeyFile = "m.key", CaFile = "ca.crt" },
        Retry.Default);
var (a, b) = Pipe.Create();
```

- Address strings are `host:port`; an empty host (`":20000"`) binds every
  interface, dual-stack. `BoundAddress` reports the real port after binding to
  port `0`.
- `Retry.Default` is 500 ms → 60 s, factor 2, 20 % jitter. Use `Retry.None` in
  tests and one-shot tools. The jitter is not decoration: without it every
  master that lost the same switch retries in lockstep and keeps colliding.
- **TLS is mutually authenticated and that cannot be turned off.** DNP3 carries
  controls that operate plant. A configuration without a cert, key and CA is
  refused at construction.
- **`TcpServerChannel` serves one master at a time.** Several concurrent masters
  needs a session per connection, which is not implemented.
- Over serial, set `UseLinkConfirms = true`, `LinkRetries = 3` and a
  `LinkTimeout` scaled to the baud rate — and use `SyncTimeWithDelayAsync`, not
  `SyncTimeAsync`.

---

## 4. Failure modes (read this before writing code)

### 4.1 Analog values arrive truncated

**The default analog variations are 32-bit integers.** An outstation left at its
defaults reports `11.2` as `11`. This is the single most common surprise, and it
looks like a library bug rather than a configuration choice.

```csharp
if (db.TryGetAnalog(0, out _, out var cfg))
{
    cfg.StaticVariation = 5;  // g30v5, single precision with flags
    cfg.EventVariation = 7;   // g32v7, single precision with time
    db.Configure(PointType.Analog, 0, cfg);
}
```

Defaults, all types:

| Type | Static | Event |
| --- | --- | --- |
| Binary | g1v2 | g2v2 |
| DoubleBitBinary | g3v2 | g4v2 |
| Counter | g20v1 | g22v5 |
| FrozenCounter | g21v1 | g23v5 |
| Analog | **g30v1 (32-bit integer)** | **g32v3 (32-bit integer)** |
| BinaryOutputStatus | g10v2 | g11v2 |
| AnalogOutputStatus | g40v1 | g42v3 |

On the master side, pass variation `0` to `ScanRangeAsync` and let the
outstation pick an encoding that does not lose its own data.

### 4.2 `Configure` replaces the whole `PointConfig`

A zero `StaticVariation` or `EventVariation` falls back to what was there — but
a zero `Class` is `Class.None`, which **switches that point's events off
entirely**, and a zero `Deadband` is a real zero.

```csharp
// WRONG — silently suppresses every event from this point.
db.Configure(PointType.Analog, 0, new PointConfig { StaticVariation = 5 });

// RIGHT — read it back, change the field, write it back.
if (db.TryGetAnalog(0, out _, out var cfg))
{
    cfg.StaticVariation = 5;
    db.Configure(PointType.Analog, 0, cfg);
}
```

### 4.3 Touching `Database` after `RunAsync` started

`Database` is **not safe for concurrent use**. Configure it before `RunAsync`;
after that, every modification goes through `Session.Update(db => ...)`, which
runs on the session loop. Direct access afterwards is a race that shows up as
intermittent corruption.

Batch related changes into **one** `Update` — a breaker opening and its alarm
asserting should become one consistent set of events, not a torn read.

### 4.4 A missing `ICommandHandler` refuses everything

`new OutstationSession(config)` installs `RejectingCommandHandler`, which answers
`NOT_SUPPORTED` to every control. That is deliberate — an outstation whose
controls are not wired up must say so rather than report that a breaker
operated. If controls return `NOT_SUPPORTED` from your own outstation, this is
why.

Likewise: **`SelectCrob` and `SelectAnalog` must not operate anything.** They
report whether the outstation *would* accept. Operating in select defeats the
entire two-pass sequence.

### 4.5 Timestamps and quality left at default

```csharp
db.UpdateAnalog(0, new Analog(v, Flags.Online, Timestamp.Now(DateTimeOffset.UtcNow)));
```

- A `default` timestamp is `TimestampQuality.Invalid` — an event a
  sequence-of-events record cannot order.
- Use `Timestamp.Now(t)` only when the clock is synchronised;
  `Timestamp.Unsynchronized(t)` otherwise. Do not claim synchronisation you do
  not have.
- `Timestamp.Now` takes the time rather than reading the clock, so a session
  driven by a `TimeProvider` stays deterministic.
- Set `Flags`. When a source goes away, say `Flags.CommLost` — do not leave a
  stale value in place, and do not write zero.

### 4.6 Silence with no error at all

Almost always the link addresses. The master's `RemoteAddr` must be the
outstation's `LocalAddr` and vice versa; mismatched frames are dropped
silently by design. `dnp3-explorer` can edit both live with `C`, and
`dnp3-decode` shows them in the link header of a capture.

### Other things that behave in a way worth knowing

- **`ChannelHandler` drops updates rather than blocking** when the consumer
  falls behind, and counts them in `Dropped`. A stalled UI must not stall the
  protocol; if a complete record matters, size the buffer generously and watch
  the counter.
- **Keep polling even with unsolicited reporting enabled.** After `MaxRetries`
  unconfirmed re-sends the outstation gives up and waits to be polled.
  Unsolicited is an optimisation, not a substitute for a poll schedule.
- **Octet strings: the variation number *is* the length.** Changing the string's
  length changes the reported variation. That is legal and masters must cope.
  `UpdateOctetString` takes a span: `db.UpdateOctetString(0, "RTU-1"u8)`.
- **`trace.App` is null** for any frame that did not complete a fragment. A
  fragment can span nine frames. Always check.
- **Event buffer overflow discards the oldest events** and latches the
  indication — the only way a master learns its record has a hole. Alarm on it.
- `AddPeriodicScanAsync` completes when the scan is *queued*, not when it first
  runs. Failures never stop it.
- `RestartAsync` returns when the request was *accepted*, not when the device is
  back.

---

## 5. Cheat sheet

### Exceptions — all derive from `Dnp3Exception`

| Type | Means |
| --- | --- |
| `Dnp3TimeoutException` | no answer within `ResponseTimeout` |
| `TaskFailedException` | retries exhausted |
| `MalformedException` | bytes that are not valid DNP3 |
| `NotSupportedByPeerException` | the peer refused the function |
| `BadConfigException` | bad arguments: empty class mask, `start > stop`, no commands |
| `ClosedException` / `NoConnectionException` / `ChannelClosedException` | nothing connected |

`catch (OperationCanceledException)` is *you* giving up, not the device.

### Flags

`Online` (0x01), `Restart` (0x02), `CommLost` (0x04), `RemoteForced` (0x08),
`LocalForced` (0x10) are common to every type. The top three bits are
type-specific and reused: `ChatterFilter`/`Rollover`/`OverRange` all share 0x20.
So render with `flags.StringFor(pointType)`, not `flags.ToString()`, whenever
the type is known. `IsGood()` is online, not restarting, not comm-lost, not
forced.

### Stats worth alarming on

| Field | Meaning |
| --- | --- |
| `MasterStats.ResponseTimeouts` | the outstation is not answering |
| `MasterStats.RestartsSeen` | a device rebooting; each restart loses its event buffer |
| `MasterStats.Connections` | climbing means a flapping link |
| `OutstationStats.ConfirmTimeouts` | the master is not confirming; events are being re-sent |
| `OutstationStats.MalformedRequests` | something on the wire is wrong |

### Logging

Implement `IDnp3Logger` (three members) or use
`new TextWriterDnp3Logger(Console.Error, Dnp3LogLevel.Debug)`. It is a small
interface rather than a framework dependency; the
[user guide](docs/user-guide.md#logging-and-observability) has a
`Microsoft.Extensions.Logging` adapter in a dozen lines.

---

## 6. Verify your work

```console
# Everything. Warnings are errors, so a warning is a failure.
$ dotnet build SharpDnp3.sln
$ dotnet test SharpDnp3.sln

# Or one project at a time, when the whole solution is more than you need.
$ dotnet build src/SharpDnp3/SharpDnp3.csproj
$ dotnet test tests/SharpDnp3.Tests/SharpDnp3.Tests.csproj
$ dotnet test tests/SharpDnp3.Conformance.Tests/SharpDnp3.Conformance.Tests.csproj

# Run your code against a simulated substation.
$ dotnet run --project src/SharpDnp3.Tools.Outstation
$ dotnet run --project src/SharpDnp3.Tools.Master -- -host 127.0.0.1:20000 -listen :8080
$ curl -s localhost:8080/points

# Look at what actually went over the wire.
$ dotnet run --project src/SharpDnp3.Tools.Decode -- -f capture.hex
```

`dnp3-explorer -demo` runs a full master and outstation in one process over an
in-memory pipe — useful for eyeballing behaviour without any setup. The
[example tools](src/README.md) each have their own README.

---

## 7. If you are changing the library itself

- **Never hand-edit `Generated.*.cs`.** `src/SharpDnp3/Objects/Generated.Codecs.cs`,
  `Generated.Descriptors.cs` and `src/SharpDnp3/App/Generated.Sizes.cs` come from
  `src/SharpDnp3/Objects/Spec/dnp3_objects.yaml`. Change the spec, then:
  `dotnet run --project build/SharpDnp3.Generator -- -root .` (add `-check` to
  verify without writing).
- One directory per protocol layer: `Link/`, `Transport/`, `App/`, `Objects/`,
  then `Master/`, `Outstation/`, `Channel/`, `Decoder/`.
- House style, visible throughout: GPL header on every file, XML doc comments on
  public members, braces on every block including single statements,
  `CultureInfo.InvariantCulture` on every format call, nullable enabled,
  warnings as errors.
- Comments explain *why*, not what. The existing ones are worth matching in
  register — they justify decisions rather than narrate code.
- Parsers face bytes from devices you do not control over links that corrupt
  them. Treat the conformance suite as part of the gate, not an occasional
  extra.

---

## See also

- [User guide](docs/user-guide.md) — the same ground at length, for humans
- [API reference](docs/api.md) — every public type and member
- [Example tools](src/README.md) — four working programs built on this API
