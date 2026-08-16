# dnp3-master

A SCADA client: it polls outstations, records what they report, serves the live
picture over HTTP, and issues controls.

```console
$ dotnet run --project src/SharpDnp3.Tools.Master -- -host 127.0.0.1:20000
```

Of the four example tools this is the one closest to a production application —
several sessions at once, a bounded queue per session, a recorder that can be
trusted afterwards, and a shutdown that does not lose the last rows. If you are
building a polling application against this library, read
[`Runner.cs`](Runner.cs) first.

---

## Usage

```
dnp3-master [flags]
dnp3-master [flags] operate <control> <index> [value]
```

Running it polls continuously. The `operate` subcommand is a separate one-shot
connection that issues a single control and exits — issuing a control should not
require standing up the whole recorder.

### Sites

| Flag | Default | Effect |
| --- | --- | --- |
| `-config FILE` | | read sites from YAML |
| `-host ADDR` | | poll a single outstation over TCP |
| `-serial PORT` | | poll a single outstation over serial |
| `-baud RATE` | `9600` | serial line rate |
| `-local N` | `1` | master link address |
| `-remote N` | `10` | outstation link address |

One of `-config`, `-host` or `-serial` is required.

### Polling

| Flag | Default | Effect |
| --- | --- | --- |
| `-poll DUR` | `5s` | event class (1, 2, 3) poll interval; `0` disables |
| `-integrity DUR` | `5m` | integrity poll interval; `0` disables |
| `-timeout DUR` | `5s` | response timeout |
| `-unsolicited` | off | enable unsolicited reporting |

Durations are Go-style: `500ms`, `30s`, `5m`, `1h`. A bare number is seconds.

### Output

| Flag | Effect |
| --- | --- |
| `-record DIR` | write `values.csv` and `events.csv` into DIR |
| `-listen ADDR` | serve the live picture over HTTP |
| `-v` / `-q` | more or less logging, on standard error |

---

## Polling and recording

```console
$ dnp3-master -host 127.0.0.1:20000 -record ./data -listen :8080
Polling 1 site(s) as master 1:
  127.0.0.1:20000  outstation 10    TCP 127.0.0.1:20000  poll 5s  integrity 5m
Recording to ./data/values.csv and ./data/events.csv
Press Ctrl-C to stop.
```

Every session starts with the standard sequence — disable unsolicited, integrity
poll, then enable the classes that were asked for — and re-runs the integrity
poll whenever the outstation reports a restart. That last part matters: a
restart means the device's event history is gone, so only a full re-baseline
makes the master's picture correct again.

On Ctrl-C (or SIGTERM) it prints where each site ended up:

```
Final state:
  substation-a     up   CLASS_1_EVENTS           tasks 8/8  timeouts 0
  substation-b     up   CLASS_1_EVENTS           tasks 8/8  timeouts 0
```

### The recording

Two CSV files, appended and **flushed per row**. A recorder that loses the last
minute of an outage to a buffer is worse than one that writes a little more
slowly. Restarting the tool appends rather than truncating.

`values.csv` — the current picture, every measurement that arrived:

```csv
received,site,type,index,value,quality,timestamp,source
2026-08-16T07:59:07.5440001-03:00,site-a,Binary,0,ON,ONLINE,,static
2026-08-16T07:59:07.5463012-03:00,site-a,BinaryOutputStatus,0,ON,ONLINE,,static
```

`events.csv` — the sequence of record, events only, without the `source` column:

```csv
received,site,type,index,value,quality,timestamp
2026-08-16T07:59:07.5517677-03:00,site-a,OctetString,0,SHARPDNP3 DEMO RTU,ONLINE,
2026-08-16T07:59:07.5520512-03:00,site-a,Analog,0,10,ONLINE,2026-08-16T10:59:04.5400000+00:00
```

They are kept separate deliberately: a sequence-of-events file with static poll
data mixed into it is no longer a sequence of events.

Two columns are worth understanding. `received` is when this master decoded the
value; `timestamp` is what the outstation stamped it with, and is **empty when
the outstation sent none** — static reads usually carry no time. When both are
present and they disagree, the outstation's clock is the thing to look at.
`quality` is the named flags; for binaries the state bit is dropped, because the
value column already says `ON` or `OFF`.

CSV rather than a database on purpose. A recorder that needs a schema migration
before it can start is a recorder that does not get used during an outage, and
every engineer already has a tool that opens CSV.

### The HTTP API

`-listen :8080` serves three endpoints, GET only.

**`/status`** — per-site connection state and counters:

```json
[
  {
    "name": "127.0.0.1:20000",
    "target": "TCP 127.0.0.1:20000",
    "connected": true,
    "indications": "CLASS_1_EVENTS",
    "tasks_run": 8,
    "tasks_succeeded": 8,
    "tasks_failed": 0,
    "response_timeouts": 0,
    "unsolicited": 1,
    "restarts_seen": 0,
    "connections": 1
  }
]
```

`restarts_seen` climbing is worth an alarm — each restart threw away an event
buffer. `connections` climbing means a flapping link. `indications` is the
outstation's own health report.

**`/points`** — the latest value of every point, `?site=NAME` to filter:

```json
[
  {
    "site": "127.0.0.1:20000",
    "type": "Analog",
    "index": 0,
    "value": "10",
    "quality": "ONLINE",
    "good": true,
    "timestamp": "2026-08-16T10:59:13.0370000+00:00",
    "received": "2026-08-16T07:59:13.3492882-03:00",
    "source": "event"
  }
]
```

Sorted by site, type and index, so a diff of two responses shows what changed
rather than dictionary order. `good` is the single quality question a control
room actually asks.

**`/healthz`** — `ok`.

The snapshot is separate from the recording because they answer different
questions: the recording is what happened, the snapshot is what is true now.

---

## Issuing a control

```console
$ dnp3-master -host 127.0.0.1:20000 operate trip 0
[0] CROB{PULSE_ON|TRIP count=1 on=1000ms off=0ms status=SUCCESS}: [0]=SUCCESS

$ dnp3-master -host 127.0.0.1:20000 operate analog 0 11.0
[0] 11 (float32): [0]=SUCCESS

$ dnp3-master -host 127.0.0.1:20000 operate trip 3
[3] CROB{...}: select rejected: master: command failed: index 3: NOT_SUPPORTED
```

| Control | Sends |
| --- | --- |
| `latch-on N` | latch a binary output on |
| `latch-off N` | latch it off |
| `trip N` | pulse the trip coil, 1000 ms |
| `close N` | pulse the close coil, 1000 ms |
| `analog N VALUE` | write a float32 setpoint |

Exit status is `0` when every command succeeded and `1` otherwise, so it drops
into a script.

**It always uses select-before-operate.** Someone typing this at a prompt is a
person issuing a control, and the select is the outstation's chance to refuse
before anything in the substation moves — as the third example shows, on a point
that is interlocked. It also skips the integrity poll a normal session runs:
this connects to issue one control and leave, and polling a device first would
be noise on the wire and in its log.

---

## Polling several sites

```console
$ dnp3-master -config sites.yaml
Polling 2 site(s) as master 1:
  substation-a     outstation 10    TCP 127.0.0.1:20779  poll 2s  integrity 5m
  substation-b     outstation 11    TCP 127.0.0.1:20780  poll 5s  integrity 5m
Press Ctrl-C to stop.
```

```yaml
local: 1                   # this master's link address, shared by every site

defaults:                  # what a site inherits when it says nothing
  poll: 5s
  integrity: 5m
  timeout: 5s
  keep_alive: 30s
  sync_clock: true
  unsolicited: false
  link_confirms: false

sites:
  - name: substation-a     # identifies the site in logs, CSV rows and the API
    host: 127.0.0.1:20000
    address: 10            # the outstation's link address
    poll: 2s               # overrides the default

  - name: substation-b
    host: 10.0.0.5:20000
    address: 11
    unsolicited: true

  - name: pole-top-rtu
    serial: /dev/ttyUSB0
    baud: 9600
    address: 12
    # link_confirms defaults to true on a serial site

  - name: secure-site
    host: 10.0.0.9:20000
    address: 13
    tls:
      cert: master.crt
      key: master.key
      ca: outstation-ca.crt
      server_name: outstation.example
```

Keys are `lower_underscored` and durations are Go-style. Per-site `local`
overrides the master address for that site alone.

**Write the `defaults:` block.** The flag defaults (`5s` poll, `5m` integrity)
belong to the flags, not to the file: a configuration file that omits both
`defaults.poll` and a per-site `poll` leaves them at zero, and **zero disables
that poll**. The startup banner is where to check — a site that will never poll
prints `poll 0s  integrity 0s`, and a session with neither is one that connects,
runs its integrity poll once, and then reports nothing further except
unsolicited events.

**Unmatched keys are an error, not a shrug.** A typo has to fail at startup
rather than be discovered later as a poll that was never actually configured.
The same instinct runs through the rest of the validation, all of which happens
before a single connection is attempted:

- a site with neither a host nor a serial port, or with both;
- two sites sharing a name (names identify rows in the recording);
- a missing outstation address;
- a master and outstation address that are the same number;
- TLS with a missing cert, key or CA — *"a channel that does not verify its peer
  lets anyone who can reach the port operate plant"*;
- TLS on a serial port.

Serial sites turn on link-layer confirmation automatically: a serial line has no
transport-layer delivery guarantee, so the link handshake is what makes it
reliable, and defaulting it on beats making every serial configuration remember
it.

---

## How it works

The structure is the part worth copying.

**One session per site, three tasks each** ([`Runner.cs`](Runner.cs)):
`RunSessionAsync` owns the session, `ConsumeAsync` drains its updates, and
`ScheduleAsync` sets up the periodic polls. One consumer per session, not one
shared drain loop — a slow recorder then delays only its own site.

**A bounded channel between protocol and application.** Each site gets a
`ChannelHandler(4096)`. The session never blocks on the consumer; when the
consumer falls behind, updates are **dropped and counted**. The consumer watches
`Handler.Dropped` and logs when it moves, because an operator reading a
recording with holes in it deserves to know they are there:

```
WARN  updates dropped; the recorder is not keeping up site=substation-a dropped=53 total=53
```

That is the right trade — a stalled recorder must not stall the protocol — but
it is not free, and it should be visible. If a complete record matters more than
latency, enlarge the buffer.

**Everything cancels from one token.** Ctrl-C and SIGTERM both cancel it; the
recorder is closed in a `finally`. SIGTERM is handled because a recorder is
normally run under something that stops it that way, and a recorder killed
outright is a recording with its last rows missing.

**The logger is decorated, not replaced.** `SiteLogger` wraps the shared
`IDnp3Logger` and prepends `site=NAME` to every record, so several sessions in
one process stay distinguishable in one log stream. It is nine lines, and a good
model for adapting `IDnp3Logger` to anything else.

**Configuration validates before it connects** ([`Config.cs`](Config.cs)), and
defaults are folded into a *copy* of each site, so the file's own view is never
mutated.

See [SKILL.md §2.1](../../SKILL.md#21-master-that-consumes-updates) for the
minimal version of this, and
[the user guide](../../docs/user-guide.md#building-a-master) for the full
treatment.
