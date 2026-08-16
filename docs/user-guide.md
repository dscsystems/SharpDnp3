# SharpDnp3 user guide

How to build a DNP3 master, an outstation, or a tool that reads DNP3 traffic,
using this library.

For the exhaustive list of types and signatures, see the
[API reference](api.md).

**Contents**

- [Install](#install)
- [DNP3 in five minutes](#dnp3-in-five-minutes)
- [Building a master](#building-a-master)
- [Building an outstation](#building-an-outstation)
- [Transports](#transports)
- [Variations and precision](#variations-and-precision)
- [Events, classes and deadbands](#events-classes-and-deadbands)
- [Controls](#controls)
- [Time synchronisation](#time-synchronisation)
- [Testing without hardware](#testing-without-hardware)
- [Decoding traffic](#decoding-traffic)
- [The example tools](#the-example-tools)
- [Logging and observability](#logging-and-observability)
- [Troubleshooting](#troubleshooting)
- [Before you put it in a substation](#before-you-put-it-in-a-substation)

---

## Install

```console
$ dotnet add package SharpDnp3
```

.NET 10 or newer. The library depends on the base class library plus
`System.IO.Ports`, which is reached only from `SerialChannel`.

**Licence: GPLv3 or later.** This is strong copyleft, and it is a library: a
program that links it must be released under the GPL too. That is the intended
effect. If you need to link from something proprietary, this is not the right
licence for you and no exception is offered.

---

## DNP3 in five minutes

If you have used Modbus, the things that will surprise you are here.

**Two roles.** A *master* polls; an *outstation* answers. An outstation is the
device in the field — an RTU, a relay, a meter. The master is the SCADA system.
This library implements both.

**Link addresses, not IP.** Every station has a 16-bit link address, and it is
independent of the IP address or the serial port. A master at address 1 talks to
an outstation at address 10 over a socket that knows nothing about either. Both
ends must agree, and getting them wrong produces silence rather than an error —
frames addressed to nobody are simply dropped. This is the single most common
commissioning problem.

**Points are typed and indexed.** Binary inputs, analog inputs, counters, binary
outputs, analog outputs, each independently indexed from zero. Binary input 3 and
analog input 3 are different points. There is no shared register space.

**Static values and events are different things.** A *static* read returns the
point's present value. An *event* is a record that the value changed, queued by
the outstation when it changed and delivered later. A master that only reads
static values sees the current state but misses everything that happened between
polls — which is the entire point of a sequence-of-events record. Events are how
you learn that a breaker opened and reclosed while you were not looking.

**Classes are how you ask for events.** Every point is assigned to event class 1,
2 or 3 (or to none, suppressing its events). Class 0 is not an event class at
all: it means "all static data". So:

- an **integrity poll** is class 0+1+2+3 — everything, static and queued events,
  which re-baselines the master's whole picture (`Class.All`);
- a **routine event poll** is class 1+2+3 (`Class.Class123`);
- conventionally class 1 is the urgent data and class 3 the least urgent, but
  nothing in the protocol enforces that. It is a configuration convention.

**Unsolicited responses** are the outstation pushing events without being asked.
They have to be enabled at both ends: the outstation must be built with
unsolicited capability, and the master must send ENABLE_UNSOLICITED for the
classes it wants.

**Quality flags travel with every value.** The `Online` bit is the one that
matters most: cleared, the value beside it is not trustworthy. A value of zero
with `Online` clear does not mean the measurement is zero.

**Internal indications (IIN)** are two octets on every response — the outstation's
running health report. `DEVICE_RESTART` means it has restarted and its event
history is gone, so nothing short of an integrity poll will make the master's
picture correct again. `EVENT_BUFFER_OVERFLOW` means events were lost.
`NEED_TIME` means the clock wants setting.

**Confirmation is a real protocol step.** When an outstation sends events it asks
the master to confirm; the events are held until it does, and re-sent if it does
not. This library gets that right on both ends, and it is why events survive a
dropped connection.

---

## Building a master

### The smallest useful master

```csharp
using SharpDnp3;
using SharpDnp3.Channels;
using SharpDnp3.Master;

// Receives everything the master decodes. Deriving from NopHandler means we
// implement only the methods we care about.
internal sealed class PrintingHandler : NopHandler
{
    public override void HandleBinary(HeaderInfo info, IReadOnlyList<Indexed<Binary>> values)
    {
        foreach (var v in values)
        {
            Console.WriteLine(
                $"BI {v.Index} = {v.Value.Value}  " +
                $"{v.Value.Flags.StringFor(PointType.Binary)}  " +
                $"{(info.IsEvent ? "event" : "static")}  {v.Value.Time}");
        }
    }

    public override void HandleAnalog(HeaderInfo info, IReadOnlyList<Indexed<Analog>> values)
    {
        foreach (var v in values)
        {
            Console.WriteLine($"AI {v.Index} = {v.Value.Value}  " +
                              $"{v.Value.Flags.StringFor(PointType.Analog)}");
        }
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var master = new MasterSession(new MasterConfig
{
    LocalAddr = 1,   // our link address
    RemoteAddr = 10, // the outstation's

    // The standard startup sequence: turn unsolicited reporting off, re-baseline
    // with an integrity poll, then turn on the classes we want.
    DisableUnsolOnStartup = true,
    IntegrityOnStartup = true,
    UnsolClassMask = Class.Class123,

    ResponseTimeout = TimeSpan.FromSeconds(5),
    KeepAlive = TimeSpan.FromSeconds(30),
}, new PrintingHandler());

using var channel = new TcpClientChannel("127.0.0.1:20000", Retry.Default);

// RunAsync owns the session loop. It completes when the token is cancelled.
var session = master.RunAsync(channel, cts.Token);

// Poll events every five seconds and re-baseline every five minutes. These
// return as soon as the scan is queued.
await master.AddPeriodicScanAsync(TimeSpan.FromSeconds(5), Class.Class123, cts.Token);
await master.AddPeriodicScanAsync(TimeSpan.FromMinutes(5), Class.All, cts.Token);

try
{
    await session;
}
catch (OperationCanceledException)
{
    // Ctrl-C is how this ends.
}
```

Three things to notice.

`RunAsync` runs until cancelled, so hold the `Task` and cancel the token to stop
it. It reconnects on its own: a dropped socket is not an error you need to
handle, it is the `Retry` policy doing its job. There is no `Stop`, `Close` or
`Dispose` on the session — **cancellation is the shutdown path**.

**Every request method is safe to call from any thread.** `IntegrityPollAsync`,
`ScanRangeAsync`, `DirectOperateAsync` and the rest submit a task to the session
loop and await it, so an ASP.NET endpoint can call them directly. They complete
when the outstation answers, or throw.

**Handler methods run on the session loop.** A handler that writes to a slow
database delays the next poll. If your consumer can be slow, use `ChannelHandler`
instead.

### Consuming updates from a channel

```csharp
var handler = new ChannelHandler(1024);
var master = new MasterSession(config, handler);
var session = master.RunAsync(channel, cts.Token);

await foreach (var u in handler.Updates.ReadAllAsync(cts.Token))
{
    switch (u.Type)
    {
        case PointType.Binary:
            Save(u.Index, u.Binary.Value, u.Binary.Time);
            break;
        case PointType.Analog:
            Save(u.Index, u.Analog.Value, u.Analog.Time);
            break;
        case PointType.Counter:
            Save(u.Index, u.Counter.Value, u.Counter.Time);
            break;
    }
}
```

The session never blocks on this channel. When the consumer falls behind, updates
are **dropped** and counted — a stalled UI must not stall the protocol. If a
complete record matters to you, size the buffer generously and watch
`handler.Dropped`.

### One-off requests

```csharp
// Re-baseline now.
await master.IntegrityPollAsync(ct);

// Read one group over one index range. Variation zero lets the outstation choose
// its default encoding, which is usually what you want.
await master.ScanRangeAsync(30, 0, 0, 15, ct); // analog inputs 0..15

// Only class 1 and 2.
await master.ScanClassesAsync(Class.Class1 | Class.Class2, ct);
```

### Classifying failures

```csharp
try
{
    await master.IntegrityPollAsync(ct);
}
catch (Dnp3TimeoutException)
{
    // the outstation did not answer within ResponseTimeout
}
catch (TaskFailedException)
{
    // retries exhausted
}
catch (OperationCanceledException)
{
    // we gave up, not the device
}
```

Everything the library raises derives from `Dnp3Exception`, so one `catch
(Dnp3Exception)` covers the protocol failures without swallowing programming
errors.

### Watching the session's health

```csharp
var st = master.Stats;
Console.WriteLine($"connected={master.Connected} tasks={st.TasksRun} " +
                  $"failed={st.TasksFailed} timeouts={st.ResponseTimeouts} " +
                  $"restarts={st.RestartsSeen}");

var iin = master.LastIin;
if (iin.HasError())
{
    Console.WriteLine($"outstation reports: {iin}");
}
```

`RestartsSeen` climbing steadily means a device that keeps rebooting — worth an
alarm, because each restart throws away its event buffer.

---

## Building an outstation

An outstation is a database plus two hooks: an `IOutstationApplication` for the
things the stack cannot decide (the clock, restarts) and an `ICommandHandler` for
controls.

```csharp
using SharpDnp3;
using SharpDnp3.Channels;
using SharpDnp3.Outstation;

// Executes controls. It holds the session so that operating an output can also
// update the database, which is what makes the plant appear to react.
internal sealed class Commands : ICommandHandler
{
    public OutstationSession? Session { get; set; }

    // SelectCrob must not operate anything. It answers whether we *would* accept.
    public CommandStatus SelectCrob(ushort index, ControlRelayOutputBlock c) =>
        index >= 4 ? CommandStatus.NotSupported : CommandStatus.Success;

    public CommandStatus OperateCrob(ushort index, ControlRelayOutputBlock c, OperateType op)
    {
        if (index >= 4)
        {
            return CommandStatus.NotSupported;
        }

        var on = c.Code.IsClose() || c.Code.OpType() == ControlCode.LatchOn;
        var now = Timestamp.Now(DateTimeOffset.UtcNow);

        // Close the loop: the output moves, and so does the status point the
        // master reads back. Both changes in one Update, so they are reported as
        // one consistent set.
        Session!.Update(db =>
        {
            db.UpdateBinaryOutputStatus(index, new BinaryOutputStatus(on, Flags.Online, now));
            db.UpdateBinary(index, new Binary(on, Flags.Online, now));
        });
        return CommandStatus.Success;
    }

    public CommandStatus SelectAnalog(ushort index, AnalogOutputCommand v) =>
        index >= 2 ? CommandStatus.NotSupported : CommandStatus.Success;

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

// Supplies the clock and the restart behaviour. Deriving from NopApplication
// gives working defaults for anything we leave out.
internal sealed class Application : NopApplication
{
    public override DateTimeOffset Now() => DateTimeOffset.UtcNow;
    public override bool SupportsWriteTime() => true;
    public override bool WriteAbsoluteTime(DateTimeOffset t) => true; // accept the master's clock
    public override TimeSpan ColdRestart() => TimeSpan.FromSeconds(30);
    public override TimeSpan WarmRestart() => TimeSpan.FromSeconds(2);
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var commands = new Commands();

var outstation = new OutstationSession(new OutstationConfig
{
    LocalAddr = 10, // our link address
    RemoteAddr = 1, // the master's

    Database = new DatabaseConfig
    {
        Binary = 8,
        Analog = 4,
        Counter = 2,
        BinaryOutputStatus = 4,
        AnalogOutputStatus = 2,
        DefaultClass = Class.Class1, // every point's events go to class 1
    },
    Events = new EventBufferConfig { MaxEvents = 5000 },

    // Push events rather than waiting to be polled. The master still has to send
    // ENABLE_UNSOLICITED for the classes it wants.
    Unsolicited = new UnsolicitedConfig
    {
        Enabled = true,
        HoldTime = TimeSpan.FromMilliseconds(200), // coalesce a burst into one response
        MaxEvents = 20,
    },
}, new Application(), commands);

commands.Session = outstation; // the handler needs the session to write back

// Point configuration, before RunAsync: analog 0 carries fractions, so give it a
// float variation, and a deadband so it does not chatter.
if (outstation.Database.TryGetAnalog(0, out _, out var cfg))
{
    cfg.StaticVariation = 5; // g30v5, single precision with flags
    cfg.EventVariation = 7;  // g32v7, single precision with time
    cfg.Deadband = 0.1;
    outstation.Database.Configure(PointType.Analog, 0, cfg);
}

using var channel = new TcpServerChannel(":20000");
var session = outstation.RunAsync(channel, cts.Token);

// Feed the database from wherever the real measurements come from.
using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
while (await timer.WaitForNextTickAsync(cts.Token))
{
    var v = ReadTheFieldSomehow();
    outstation.Update(db => db.UpdateAnalog(
        0, new Analog(v, Flags.Online, Timestamp.Now(DateTimeOffset.UtcNow))));
}
```

### The rules that matter

**Update through `Session.Update`, not through `Database` directly.** The database
is not safe for concurrent use; `Update` runs your action on the session loop,
which serialises it against the protocol. Touching `Database` directly is fine
*before* `RunAsync` starts — that is where point configuration belongs — and a
race afterwards.

**Batch related changes into one `Update`.** A breaker opening and its alarm
asserting should be one call, so they become one consistent set of events rather
than a torn read.

**Set `Time` on your updates.** An event with no timestamp is an event a
sequence-of-events record cannot order. Use `Timestamp.Now(t)` when your clock is
synchronised and `Timestamp.Unsynchronized(t)` when it is not — do not claim
synchronisation you do not have.

**Set `Flags`.** A value with no `Online` bit reads as untrustworthy to every
master. When a source goes away, say so: `Flags.CommLost` rather than a stale
value left in place, and rather than zero.

**A null `ICommandHandler` refuses everything.** `new OutstationSession(config)`
gives you `RejectingCommandHandler`, which answers `NOT_SUPPORTED` to every
control. That is deliberate: an outstation whose controls are not wired up must
say so, not silently report that a breaker operated.

**`Select` must not operate anything.** It answers whether the outstation would
accept the command. The stack holds the reservation for `SelectTimeout` (five
seconds by default) and calls `Operate` when the master follows through.

**Command handlers run on the session loop.** Slow work there stalls the protocol
— but returning success before the operation completes is a claim you cannot take
back. If the plant takes ten seconds to move, the honest answer is usually to
return success on *accepting* the command and report the actual movement through
the status point, which is what the example above does.

---

## Transports

Every transport is an `IChannel`, and sessions do not care which one they were
given.

```csharp
// TCP client — a master dialling out.
IChannel channel = new TcpClientChannel("10.0.0.5:20000", Retry.Default);

// TCP server — an outstation listening. One master at a time.
channel = new TcpServerChannel(":20000");

// Serial. USB adapters disappear and come back; the session reconnects.
channel = new SerialChannel(new SerialConfig
{
    Device = "/dev/ttyUSB0",
    Baud = 9600,
    DataBits = 8,
    Parity = Parity.None,
    StopBits = StopBits.One,
});

// UDP. An empty RemoteAddr means "reply to whoever writes first", which is what
// an outstation wants; a master sets it.
channel = new UdpChannel(new UdpConfig { RemoteAddr = "10.0.0.5:20000" });

// In-process, for tests and demos.
var (masterSide, outstationSide) = Pipe.Create();
```

`Retry.Default` backs off from half a second to a minute with 20% jitter. The
jitter is not decoration: a substation that loses a switch brings every master's
connection down at the same instant, and without jitter they all retry in
lockstep and keep colliding. Use `Retry.None` in tests and one-shot tools, where
a single failed attempt should be an error rather than a loop.

Channels are `IDisposable`; `using` them, or call `Close()`, when the session is
finished with them.

### Serial specifics

Over serial you almost certainly want link-layer confirmation, which is normally
off over TCP:

```csharp
new MasterConfig
{
    UseLinkConfirms = true,
    LinkRetries = 3,
    LinkTimeout = TimeSpan.FromSeconds(2), // scale with the baud rate
}
```

And use `SyncTimeWithDelayAsync` rather than `SyncTimeAsync` — see
[Time synchronisation](#time-synchronisation).

### TLS

```csharp
var tls = new Dnp3TlsConfig
{
    CertFile = "master.crt",
    KeyFile = "master.key",
    CaFile = "ca.crt", // the authority that signed the outstation's certificate
};

using var channel = new TlsClientChannel("10.0.0.5:20000", tls, Retry.Default);
```

Bad certificate paths fail when the channel is constructed, not at connect time.

**Mutual authentication is mandatory and cannot be turned off.** DNP3 carries
controls that operate plant; a channel that authenticates only the server lets
anyone who can reach the port issue them. IEC 62351-3 requires both ends to
present certificates, and these channels refuse to build a configuration that
does not. `MinVersion` defaults to TLS 1.2, the floor IEC 62351 sets.

Secure Authentication v5 is out of scope for this library. Use TLS.

---

## Variations and precision

A *variation* is the encoding a point is reported in. It decides how many bits
the value gets, whether flags come with it, and whether a timestamp does.

The defaults are the widest **lossless** encoding for each type — but "lossless"
is about the wire format, not about your data. **The analog defaults are 32-bit
integers**, so an outstation configured by default reports `123.5` as `123`:

| Type | Static default | Event default |
| --- | --- | --- |
| Binary | g1v2 (with flags) | g2v2 (absolute time) |
| DoubleBitBinary | g3v2 | g4v2 |
| Counter | g20v1 (32-bit, flags) | g22v5 (with time) |
| FrozenCounter | g21v1 | g23v5 |
| Analog | **g30v1 (32-bit integer, flags)** | **g32v3 (32-bit integer, time)** |
| BinaryOutputStatus | g10v2 | g11v2 |
| AnalogOutputStatus | g40v1 | g42v3 |

If a point carries fractions, configure it:

```csharp
if (db.TryGetAnalog(0, out _, out var cfg))
{
    cfg.StaticVariation = 5; // g30v5, single precision with flags
    cfg.EventVariation = 7;  // g32v7, single precision with time
    db.Configure(PointType.Analog, 0, cfg);
}
```

**Read the current config back before changing one field.** `Configure` replaces
the whole `PointConfig`. A zero `StaticVariation` or `EventVariation` falls back
to what was there, but a zero `Class` means `Class.None` — so passing a fresh
struct with only a variation set silently switches that point's events off.

On the master side, `ScanRangeAsync` takes a variation too. Pass zero and let the
outstation choose: it knows which encoding carries its points without loss.

`AnalogRange.FitsIn16` and `FitsIn32` are there for an outstation deciding
whether a requested narrow variation can carry a reading.

---

## Events, classes and deadbands

An event is generated when a point's value or quality changes **and** the point
is assigned to an event class. `Class.None` suppresses a point's events entirely,
which is what you want for a noisy point nobody watches.

For analogs and counters, a *deadband* says how far the value must move before
the change is worth an event:

```csharp
if (db.TryGetAnalog(0, out _, out var cfg))
{
    cfg.Deadband = 0.5;
    db.Configure(PointType.Analog, 0, cfg);
}
```

The comparison is against the value last **reported**, not the value last stored.
That distinction is the difference between a working deadband and the classic
bug: comparing against the stored value lets a point drift indefinitely in
deadband-sized steps without ever reporting, hiding a slow ramp toward a limit.

A master can set deadbands remotely, which is the usual answer to an analog that
chatters:

```csharp
await master.WriteDeadbandAsync(new Dictionary<ushort, float> { [0] = 0.5f, [4] = 10f }, ct);
```

At most 255 points per request — the limit of the one-octet count.

### The event buffer

Events are queued, **selected** when they go into a response, and only **removed**
when the master confirms that response. If the confirmation never arrives they go
back on the queue and are re-sent. An outstation that dropped events at
transmission would lose exactly the data a sequence-of-events record exists to
preserve, and lose it silently.

Size the buffer for the worst burst you expect, not the average:

```csharp
Events = new EventBufferConfig { MaxEvents = 5000 }, // default is 1000
```

On overflow the **oldest** events are discarded and the overflow is latched into
the internal indications, which is the only way the master learns there is a hole
in its record. Watch for it:

```csharp
if (outstation.Events?.Overflowed == true)
{
    Console.WriteLine("event buffer overflowed: the master's record has a gap");
}
```

### Unsolicited reporting

Two switches, one at each end:

```csharp
// Outstation: the device-level capability.
Unsolicited = new UnsolicitedConfig
{
    Enabled = true,
    HoldTime = TimeSpan.FromMilliseconds(200), // a burst becomes one response
    MaxEvents = 20,                            // ...or send at 20 queued, whichever comes first
    ConfirmTimeout = TimeSpan.FromSeconds(5),
    MaxRetries = 3,
},

// Master: the classes it actually wants.
UnsolClassMask = Class.Class123, // enabled automatically after the integrity poll
// or, at any time:
await master.EnableUnsolicitedAsync(Class.Class123, ct);
await master.DisableUnsolicitedAsync(Class.Class123, ct);
```

Setting `HoldTime` to zero sends as soon as an event appears, which turns a
100-point plant trip into 100 responses. After `MaxRetries` unconfirmed re-sends
the outstation gives up and waits for the master to poll instead — so **keep
polling even when you use unsolicited reporting**. Unsolicited is an
optimisation, not a substitute for a poll schedule.

---

## Controls

```csharp
// Select-before-operate: the outstation gets a chance to refuse before anything
// in the substation moves.
var result = await master.SelectAndOperateAsync([Command.Trip(3, 1000)], ct);

// Direct operate: one pass, no reservation.
result = await master.DirectOperateAsync([Command.AnalogOutputFloat32(7, 13.75f)], ct);

// Several points in one request.
result = await master.DirectOperateAsync(
    Command.LatchOn(1),
    Command.LatchOff(2),
    Command.Close(3, 1000));
```

The factories:

| Factory | What it sends |
| --- | --- |
| `Command.Trip(index, pulseMillis)` | CROB, pulse-on with the trip coil |
| `Command.Close(index, pulseMillis)` | CROB, pulse-on with the close coil |
| `Command.LatchOn(index)` / `LatchOff(index)` | CROB, latch |
| `Command.Crob(index, crob)` | any control relay output block you build |
| `Command.AnalogOutputInt16/Int32/Float32/Float64(index, v)` | g41 setpoints |

**Use `SelectAndOperateAsync` for operator-initiated controls on plant that
matters.** The select is not a formality: it is the outstation's opportunity to
say "not that point, not right now" before anything moves, and a rejected select
throws rather than being followed by an operate. `DirectOperateAsync` is the
right choice for automated action, where there is no operator to protect.

The two requests of a select-and-operate are chained internally so nothing can be
scheduled between them — the standard requires the OPERATE to carry the sequence
number one above the SELECT, and a periodic poll landing in the middle would make
the outstation reject the operate with `NO_SELECT`.

### Checking the outcome

A refused control is not an exception. The exchange completed; the outstation
simply said no. A multi-command request can also partially succeed, so check per
point:

```csharp
var result = await master.DirectOperateAsync([Command.LatchOn(1), Command.LatchOn(2)], ct);

if (!result.OK())
{
    for (var i = 0; i < result.Statuses.Count; i++)
    {
        if (!result.Statuses[i].OK())
        {
            Console.WriteLine($"point {result.Commands[i].Index}: " +
                              result.Statuses[i].ToDisplayString());
        }
    }
}
```

`result.OK()` is false unless every status is `Success`, because treating a
partial success as success would tell an operator a breaker operated when it did
not. `result.ThrowIfFailed()` is the shortcut when an exception is what you want,
and `result.Error()` hands back the same exception without throwing it.

`DirectOperateNoReplyAsync` exists for the cases where no answer is wanted.
Nothing comes back, so nothing can be checked — it returns as soon as the request
is on the wire.

---

## Time synchronisation

An outstation asserts `NEED_TIME` when its clock wants setting. Which procedure
you use depends on the link:

```csharp
// Ethernet: write the time directly. Transit delay is negligible against the
// outstation's timestamp resolution.
await master.SyncTimeAsync(ct);

// Serial, or any slow link: measure the turnaround first, then write a time
// already corrected for the one-way transit.
await master.SyncTimeWithDelayAsync(ct);

// Or set an explicit time.
await master.WriteTimeAsync(someTime, ct);
```

Over a 1200 baud link the one-way transit is easily tens of milliseconds. Without
the correction the outstation's clock lands late by that amount, and every event
it stamps goes into the past.

On the outstation side, `IOutstationApplication.SupportsWriteTime` decides whether
clock writes are accepted at all, and `WriteAbsoluteTime` can reject an individual
one. A device with a GPS clock should refuse.

---

## Testing without hardware

`Pipe.Create` connects a full master to a full outstation in memory: the real
link, transport, application and object layers, with no socket and no hardware.
Every integration test in this repository runs over it, and it is the fastest way
to develop against a device you do not have.

```csharp
[Fact]
public async Task MasterReadsWhatTheOutstationHolds()
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

    var (masterSide, outstationSide) = Pipe.Create();

    var outstation = new OutstationSession(new OutstationConfig
    {
        LocalAddr = 10,
        RemoteAddr = 1,
        Database = new DatabaseConfig { Binary = 4, Analog = 2, DefaultClass = Class.Class1 },
    });

    var handler = new RecordingHandler();
    var master = new MasterSession(new MasterConfig
    {
        LocalAddr = 1,
        RemoteAddr = 10,
        ResponseTimeout = TimeSpan.FromSeconds(2),
    }, handler);

    var outTask = outstation.RunAsync(outstationSide, cts.Token);
    var masterTask = master.RunAsync(masterSide, cts.Token);

    outstation.Update(db =>
    {
        db.UpdateBinary(0, new Binary(true, Flags.Online, default));
        db.UpdateAnalog(0, new Analog(42, Flags.Online, default));
    });

    while (!master.Connected)
    {
        await Task.Delay(10, cts.Token);
    }

    await master.IntegrityPollAsync(cts.Token);
    // ...assert on what the handler received

    await cts.CancelAsync();
}
```

Wait for `master.Connected` before asserting on anything timing-sensitive.
`MasterConfig.TimeProvider` takes a `FakeTimeProvider` when a test needs to drive
timeouts without waiting for them.

For a more demanding target, the outstation example simulates plant that behaves
like plant and injects the faults that break masters:

```console
$ dotnet run --project src/SharpDnp3.Tools.Outstation -- -inject event-storm=500
$ dotnet run --project src/SharpDnp3.Tools.Outstation -- -inject restart-after=5m
$ dotnet run --project src/SharpDnp3.Tools.Outstation -- -inject offline-every=30s
```

Those are the conditions a master's error handling is usually wrong about, and
the hardest to arrange with real equipment.

---

## Decoding traffic

`SharpDnp3.Decoding` turns octets into a structured tree — which is what the
terminal explorer, the logs and the decode tool all render, none of them
re-implementing any parsing.

```csharp
using SharpDnp3.Decoding;

var decoder = new Dnp3Decoder(Direction.Rx);

decoder.Feed(bytesFromTheSocket, trace =>
{
    var b = new StringBuilder();
    trace.Render(b, showHex: false);
    Console.Write(b.ToString());

    // Or walk it structurally.
    if (trace.App is { } app)
    {
        for (var i = 0; i < app.Objects.Count; i++)
        {
            foreach (var v in app.Values[i])
            {
                Console.WriteLine($"g{app.Objects[i].Group}v{app.Objects[i].Variation} " +
                                  $"[{v.Index}] {v.Text} {v.Flags}");
            }
        }
    }
});
```

**One `Dnp3Decoder` per direction per connection.** It holds link and transport
state; feeding both directions into one would interleave two independent
transport sequences and produce nonsense. Call `Reset()` when the connection comes
back.

`trace.App` is null for frames that did not complete a fragment — a fragment can
span nine frames, and only the last one finishes it. Always check.

For a single self-contained frame with no session state, use
`Dnp3Decoder.TryDecodeFrame(null, data, out var trace, out var consumed)`.

Decoded values are formatted strings, because every consumer of the decoder wants
text. If you need typed measurements, use the `SharpDnp3.Objects` codecs or run a
real master session.

---

## The example tools

Four programs ship with the library. They are usable tools and also worked
examples — `SharpDnp3.Tools.Master` in particular is a reasonable model for a
real polling application.

Each has its own README with the full flag reference, configuration file format
and output shapes: [`SharpDnp3.Tools.Explorer`](../src/SharpDnp3.Tools.Explorer/README.md),
[`SharpDnp3.Tools.Master`](../src/SharpDnp3.Tools.Master/README.md),
[`SharpDnp3.Tools.Outstation`](../src/SharpDnp3.Tools.Outstation/README.md),
[`SharpDnp3.Tools.Decode`](../src/SharpDnp3.Tools.Decode/README.md). What follows
is the short version.

### The explorer — a terminal browser for one outstation

```console
$ dotnet run --project src/SharpDnp3.Tools.Explorer -- -demo
$ dotnet run --project src/SharpDnp3.Tools.Explorer -- -host 10.0.0.5:20000 -remote 10 -poll 2s
```

Five screens: session overview, the point table, the sequence of events, the
activity log, and a key reference. Points can be filtered on anything in the row
and sorted by value or by quality — worst first, which is how you find the broken
points in a device with a thousand good ones. `e` exports what is on screen,
after the filter and the sort, as CSV.

Controls are deliberate: `enter` on an output opens a dialog naming exactly what
will be sent, select-before-operate by default, with a confirmation before
anything moves. `-direct` and `-no-confirm` turn that off for the situations that
need it, and while `-no-confirm` is in effect the toolbar says so.

`C` edits the connection while it runs — address, both link addresses, timeouts,
poll interval — and applies it by tearing the session down and bringing a new one
up in place. A link address read off a drawing is a guess until something
answers, and restarting the tool to try 11 instead of 10 is how ten minutes of
commissioning becomes an afternoon.

### The master — poller, recorder and control tool

```console
$ dotnet run --project src/SharpDnp3.Tools.Master -- -host 127.0.0.1:20000 -record ./data -listen :8080
$ dotnet run --project src/SharpDnp3.Tools.Master -- -config sites.yaml -v
$ dotnet run --project src/SharpDnp3.Tools.Master -- -host 127.0.0.1:20000 operate trip 0
```

Recording is CSV: `values.csv` for the current picture, `events.csv` for the
sequence of record, kept separate because a sequence-of-events file with poll
data mixed into it is no longer a sequence of events. Files are appended and
flushed per row — a recorder that loses the last minute of an outage to a buffer
is worse than one that writes a little more slowly.

`-listen` serves `/status`, `/points` and `/healthz`.

### The outstation — a simulated RTU

```console
$ dotnet run --project src/SharpDnp3.Tools.Outstation                       # a default substation on :20000
$ dotnet run --project src/SharpDnp3.Tools.Outstation -- -points            # print the point list and exit
$ dotnet run --project src/SharpDnp3.Tools.Outstation -- -config substation.yaml
$ dotnet run --project src/SharpDnp3.Tools.Outstation -- -inject event-storm=500
```

The points behave like plant: a breaker stays open once tripped and takes time to
travel, an analog ramps rather than jumping, and a control closes the loop.

### The decoder — offline frame decoder

```console
$ dotnet run --project src/SharpDnp3.Tools.Decode -- 05 64 05 C0 0A 00 01 00 B1 AC
$ dotnet run --project src/SharpDnp3.Tools.Decode -- -x -f capture.hex
$ cat capture.hex | dotnet run --project src/SharpDnp3.Tools.Decode -- -s
```

It reads Wireshark hex-dump exports directly — the offset column and ASCII gutter
are recognised and dropped. `-s` reassembles fragments spanning several frames;
`-x` adds a hex dump.

---

## Logging and observability

Both sessions take an `IDnp3Logger`. Null discards everything.

```csharp
var log = new TextWriterDnp3Logger(Console.Error, Dnp3LogLevel.Debug);

var master = new MasterSession(new MasterConfig { /* … */ Log = log }, handler);
```

The master tags its logger with `role=master` and the outstation link address, so
several sessions in one process stay distinguishable. Debug level includes
per-frame protocol activity, which is verbose enough to be worth a level switch
in production.

Adapting the interface to `Microsoft.Extensions.Logging` is a dozen lines:

```csharp
internal sealed class MelAdapter(ILogger inner) : IDnp3Logger
{
    public bool IsEnabled(Dnp3LogLevel level) => inner.IsEnabled(Map(level));

    public void Log(Dnp3LogLevel level, string message,
                    params ReadOnlySpan<(string Key, object? Value)> fields)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        var state = new List<KeyValuePair<string, object?>>(fields.Length);
        foreach (var (key, value) in fields)
        {
            state.Add(new KeyValuePair<string, object?>(key, value));
        }

        inner.Log(Map(level), default, state, null, (_, _) => message);
    }

    private static LogLevel Map(Dnp3LogLevel level) => level switch
    {
        Dnp3LogLevel.Debug => LogLevel.Debug,
        Dnp3LogLevel.Info => LogLevel.Information,
        Dnp3LogLevel.Warn => LogLevel.Warning,
        _ => LogLevel.Error,
    };
}
```

For metrics, read `Stats` on either session. The fields worth alarming on:

| Field | Meaning |
| --- | --- |
| `MasterStats.ResponseTimeouts` | the outstation is not answering |
| `MasterStats.TasksFailed` | polls or commands giving up after retries |
| `MasterStats.RestartsSeen` | a device that keeps rebooting; each restart loses its event buffer |
| `MasterStats.Connections` | climbing means a flapping link |
| `OutstationStats.ConfirmTimeouts` | the master is not confirming; events are being re-sent |
| `OutstationStats.MalformedRequests` | something on the wire is wrong |
| `OutstationStats.CommandsRejected` | controls are being refused |

---

## Troubleshooting

**Nothing happens at all — no error, no data.**
Almost always the link addresses. Frames addressed to a station that is not
listening are dropped silently by design. Check `LocalAddr` and `RemoteAddr` on
both ends and that they are mirror images: the master's `RemoteAddr` is the
outstation's `LocalAddr`. The explorer lets you edit them live with `C`, which is
the fastest way to find the right pair.

**The connection establishes, but every poll times out.**
The socket is right and the addresses are not, or the outstation is answering a
different master. Run the decode tool over a capture and look at the link header:
the source and destination addresses are in plain sight.

**Analog values arrive truncated.**
The default analog variations are 32-bit integers. Configure the point for a
float variation — see [Variations and precision](#variations-and-precision).

**A point's events stopped arriving after I configured it.**
`Configure` replaces the whole `PointConfig`, and a zero `Class` is `Class.None`.
Read the config back, change the field, write it back.

**Events arrive late or in bursts.**
That is `HoldTime` doing its job. Lower it, or lower `MaxEvents`, if latency
matters more than the frame count.

**The master's picture is missing changes.**
Either the points are not assigned to an event class, or their deadband is too
wide, or the event buffer overflowed. Check `outstation.Events?.Overflowed` and
the `EVENT_BUFFER_OVERFLOW` indication.

**`DEVICE_RESTART` keeps appearing.**
The device is rebooting. Its event history is gone each time, so only an
integrity poll makes the picture correct again — which `IntegrityOnStartup = true`
does automatically on every reported restart.

**Commands return `NO_SELECT`.**
The select expired, or something was scheduled between the select and the
operate. This library chains them so nothing can interleave, so on a real device
suspect a `SelectTimeout` shorter than the round trip.

**Commands return `NOT_SUPPORTED` from my own outstation.**
You passed no `ICommandHandler`. That gives you `RejectingCommandHandler`.

**Updates from `ChannelHandler` are missing.**
The consumer is not keeping up and updates were dropped. Check `Dropped`, and
either enlarge the buffer or make the consumer faster.

**Intermittent corruption or exceptions inside outstation code.**
Something is touching `Database` from outside the session loop after `RunAsync`
started. Move it into `Session.Update`.

---

## Before you put it in a substation

- The API is not stable yet. Pin a version.
- Nothing here is a certified conformance claim. Check that what you rely on is
  actually implemented before you depend on it in the field.
- A `TcpServerChannel` outstation serves **one master at a time**. If two SCADA
  systems poll the device, that is a session per connection and it is not
  implemented.
- Self-address (0xFFFC) is not supported. Broadcast is received and executed but
  never answered, as the standard requires.
- Use TLS with mutual authentication for anything that leaves a locked cabinet.
  Secure Authentication v5 is out of scope.
- Size the event buffer for the worst burst, and alarm on the overflow
  indication.
- Keep polling even with unsolicited reporting enabled.
- Set `IntegrityOnStartup` so a device restart re-baselines automatically.
- Prefer `SelectAndOperateAsync` for anything an operator initiates.
- Run the test suites if you fork or modify the stack. The parsers face bytes
  from devices you do not control over links that corrupt them, so treat the
  conformance and interop suites as part of the gate rather than an occasional
  extra.

---

## See also

- [API reference](api.md) — every public type and member
- [`SKILL.md`](../SKILL.md) — condensed reference for AI coding agents
- [The example tools](../src/README.md) — four working programs built on this API
