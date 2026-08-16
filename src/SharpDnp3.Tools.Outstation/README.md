# dnp3-outstation

A simulated DNP3 outstation with plant behind it. Point a master at it and get a
device that behaves the way field equipment behaves — and that can be told to
misbehave on demand.

```console
$ dotnet run --project src/SharpDnp3.Tools.Outstation
```

That is a substation feeder bay on `:20000`, link address 10, expecting a master
at address 1. No configuration needed.

The point of it is the *behaviour*. A breaker stays open once tripped and takes
time to travel. An analog ramps rather than jumping. A counter only ever counts
up. An earth switch refuses commands because it is racked out. A control closes
the loop: tripping breaker 0 opens breaker 0, which raises a binary input event,
which the master receives. Those are the behaviours a master's own logic will be
wrong about, and a random number generator will not find the bugs.

---

## The default plant

```console
$ dotnet run --project src/SharpDnp3.Tools.Outstation -- -points
```

```
Simulated plant

  Breakers (binary input / binary output)
    BI   0 / BO   0  Feeder 1 breaker (closed)
    BI   1 / BO   1  Feeder 2 breaker (closed)
    BI   2 / BO   2  Bus tie (open)
    BI   3 / BO   3  Earth switch (racked out) (open)  [interlocked: commands refused]

  Analogs
    AI   0  Bus voltage  sine 10.8..11.2 kV
    AI   1  Feeder 1 current  walk 0..400 A
    AI   2  Feeder 2 current  walk 0..400 A
    AI   3  Transformer temperature  ramp 35..85 degC
    AI   4  Tap position  step 7..9

  Counters
    CT   0  Feeder 1 energy  12/s
    CT   1  Feeder 2 energy  8/s
```

Plus one octet string at index 0 carrying the device name, `SHARPDNP3 DEMO RTU`
— the sort of identity string real devices report in group 110.

Every point defaults to event class 1. The simulation ticks four times a second.

---

## Usage

```
dnp3-outstation [flags]
```

### Transport (TCP by default)

| Flag | Default | Effect |
| --- | --- | --- |
| `-listen ADDR` | `:20000` | listen address; an empty host binds every interface |
| `-udp` | | UDP instead of TCP |
| `-serial PORT` | | a serial port instead of TCP |
| `-baud RATE` | `9600` | serial line rate |
| `-tls-cert FILE` | | certificate; with `-tls-key` and `-tls-ca`, enables TLS |
| `-tls-key FILE` | | private key |
| `-tls-ca FILE` | | authority used to verify the master |

Over serial the tool turns on link-layer confirmation automatically: a serial
line has no framing of its own, so the link handshake is what makes it reliable.
Over TCP the transport already guarantees order.

TLS is mutually authenticated — all three files or none.

### Device

| Flag | Default | Effect |
| --- | --- | --- |
| `-config FILE` | | read the simulated plant from YAML |
| `-address N` | `10` | override the outstation link address |
| `-master N` | `1` | override the master link address |
| `-unsolicited` | off | push events without being polled |
| `-max-masters N` | `1` | serve up to N masters at once, over TCP or TLS |
| `-points` | | print the point list and exit |

`-address` and `-master` override the configuration file, so one plant
description can be started several times on different addresses.

`-max-masters` above one makes the tool behave like an RTU with a control centre
and an engineering workstation on it: one plant, one database, and a separate
conversation per master. Each master holds its own event queue, so the event
buffer's memory multiplies; a master arriving past the limit is disconnected and
logged. It needs TCP or TLS — a serial line or a UDP socket has one peer.

### Fault injection (repeatable)

| Flag | Effect |
| --- | --- |
| `-inject event-storm=N` | generate N binary events per second |
| `-inject restart-after=DUR` | report a restart every DUR |
| `-inject offline-every=DUR` | flip every point to comm-lost, and back, every DUR |
| `-inject device-trouble` | assert the `DEVICE_TROUBLE` indication |

### Logging

`-v` logs protocol activity per frame; `-q` logs only errors. Both go to
standard error.

---

## Testing a master properly

This is what the tool is for. These four conditions are where master
implementations are usually wrong, and all of them are painful to arrange with
real equipment.

```console
# Can the master keep up? Does it notice when it cannot?
$ dnp3-outstation -inject event-storm=500

# Does a restart trigger a re-baseline, or does the master keep a stale picture?
$ dnp3-outstation -inject restart-after=5m

# Does bad quality reach the operator, or is a comm-lost value displayed as data?
$ dnp3-outstation -inject offline-every=30s

# Everything at once, with unsolicited reporting.
$ dnp3-outstation -unsolicited -inject event-storm=200 -inject offline-every=45s -v
```

**`event-storm`** pushes events far faster than a 5000-event buffer can hold, so
the buffer overflows, the oldest events are discarded and
`EVENT_BUFFER_OVERFLOW` is latched into the internal indications. A master that
ignores that indication has a hole in its record and does not know it.

**`restart-after`** asserts `DEVICE_RESTART`. The event history is gone, so no
incremental poll can recover it — only a full integrity poll will. A master with
`IntegrityOnStartup = true` handles this automatically; one without it silently
keeps a picture that is wrong.

**`offline-every`** flips every point between `Flags.Online` and
`Flags.CommLost`. The values keep arriving; what changes is whether they can be
trusted. A UI that renders a comm-lost value as a number is showing an operator
a measurement that does not exist.

---

## Controls

Breakers respond to trip, close, latch-on and latch-off, and the response is
plant-shaped rather than instant:

- a breaker with a `travel_time` reports its **old** position until it arrives,
  so a master that assumes a control takes effect immediately reads the wrong
  state for a few hundred milliseconds;
- a breaker already moving answers `ALREADY_ACTIVE`;
- an `interlocked` breaker answers `NOT_SUPPORTED` — to `SELECT` as well as to
  `OPERATE`, which is exactly what select-before-operate exists to catch;
- anything but trip, close or a latch is `NOT_SUPPORTED`.

Analog outputs write a setpoint. A value outside the point's configured range is
`OUT_OF_RANGE`; a successful write pins the point to `fixed` so it holds what
was written instead of resuming its waveform.

Try it with the master tool:

```console
$ dnp3-master -host 127.0.0.1:20000 operate trip 0
[0] CROB{PULSE_ON|TRIP count=1 on=1000ms off=0ms status=SUCCESS}: [0]=SUCCESS

$ dnp3-master -host 127.0.0.1:20000 operate trip 3
[3] CROB{...}: select rejected: master: command failed: index 3: NOT_SUPPORTED
```

Index 3 is the racked-out earth switch, refused at the select — before anything
would have moved.

---

## Describing your own plant

```console
$ dnp3-outstation -config substation.yaml
```

```yaml
address: 10          # this outstation's link address
master: 1            # the master's

breakers:
  - name: Feeder 1 breaker
    status_index: 0          # the binary input reporting position
    control_index: 0         # the binary output that operates it
    closed: true             # starting position
    travel_time: 00:00:00.400  # how long it takes to move — hh:mm:ss[.fff]
    class: 1                 # event class for its status changes
  - name: Earth switch
    status_index: 1
    control_index: 1
    closed: false
    interlocked: true        # refuses every command

analogs:
  - name: Bus voltage
    index: 0
    units: kV
    signal: Sine             # Fixed | Sine | Ramp | Walk | Step
    min: 10.8
    max: 11.2
    period: 00:00:45         # a full cycle, for the periodic shapes
    noise: 0.005             # fraction of the range added as jitter
    deadband: 0.02           # suppresses changes smaller than this
    class: 1

counters:
  - name: Feeder 1 energy
    index: 0
    per_second: 12
    class: 1
```

Keys are `lower_underscored`. The database is sized automatically from the
highest index each list mentions, and an octet string is always allocated at
index 0.

**Durations in this file are .NET `TimeSpan` format — `hh:mm:ss[.fff]` — not the
Go-style spelling the flags use.** `travel_time: 400ms` is accepted on an
`-inject` flag but *not* here; write `00:00:00.400`. The two loaders differ:
`dnp3-master` registers a duration converter for its YAML and this tool does
not. Worse, a duration this loader cannot read currently escapes as an unhandled
`YamlException` rather than a readable error, so a malformed plant file reports
a stack trace. Keep to `hh:mm:ss` and it does not arise.

The signal shapes:

| `signal` | Behaviour |
| --- | --- |
| `Fixed` | holds whatever it was last set to — the shape an analog output writes into |
| `Sine` | a sine wave between `min` and `max` over `period` |
| `Ramp` | a sawtooth from `min` to `max` over `period` |
| `Walk` | a bounded random walk, stepping 2 % of the range per tick |
| `Step` | a square wave between `min` and `max` |

Phases are staggered randomly at startup, so a rack of points does not move in
lockstep. Event streams that move in lockstep look artificial and hide ordering
bugs.

Unknown YAML keys are ignored here (unlike `dnp3-master`, which rejects them),
so a plant file can carry documentation fields the tool does not read.

---

## One thing to expect: truncated analogs

Poll this outstation with a default master and the bus voltage reads `10`, not
`10.9`:

```console
$ curl -s localhost:8080/points | head
    "type": "Analog",
    "index": 0,
    "value": "10",
```

That is not a simulator bug and not a library bug. The tool leaves the analog
points on their default variations, which are **32-bit integers** (g30v1 static,
g32v3 event), and the encoding does exactly what it says. It is left that way
deliberately, because it is the most common real-world surprise in DNP3 and a
master ought to meet it somewhere safe.

To carry fractions, configure the point for a float variation — see
[Variations and precision](../../docs/user-guide.md#variations-and-precision).

---

## How it works

Three pieces, and the shape is worth copying for a real outstation:

- **`Config.cs`** — the plant description and its YAML mapping, plus
  `DatabaseConfig()`, which sizes the database from the points described, and
  `ApplyPointConfig()`, which sets each point's class and deadband **before**
  `RunAsync` starts. That ordering is the rule: configure the database first,
  then run.
- **`Simulator.cs`** — the plant model. `Apply()` writes the current state into
  the database inside a single `session.Update(...)`, so everything that changed
  in one tick becomes one consistent set of events rather than a torn read.
  `Operate()` and `WouldAccept()` are the two halves of a control: `WouldAccept`
  is what `SELECT` calls and it **does not move anything**.
- **`Program.cs`** — argument parsing, channel selection, and the tick loop.

`SimulatedCommandHandler` is a compact example of `ICommandHandler`: it maps
trip/close/latch onto the plant and returns a `CommandStatus` that is honest
about what happened. Reporting success for a command an interlock refused would
tell an operator the breaker moved when it did not.

See [SKILL.md §2.4](../../SKILL.md#24-outstation) for the minimal outstation,
and [the user guide](../../docs/user-guide.md#building-an-outstation) for the
full treatment.
