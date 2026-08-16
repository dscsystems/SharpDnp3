# The example tools

Four command-line programs ship with SharpDnp3. They are real tools — the kind
you want in a substation when something is not answering — and they are also the
worked examples for the library. Between them they exercise every part of the
public API.

| Tool | What it is | Read |
| --- | --- | --- |
| **dnp3-explorer** | A terminal browser for one outstation: connect, poll, inspect points, issue controls | [README](SharpDnp3.Tools.Explorer/README.md) |
| **dnp3-master** | A poller, CSV recorder, HTTP status API and one-shot control tool | [README](SharpDnp3.Tools.Master/README.md) |
| **dnp3-outstation** | A simulated RTU with plant that behaves like plant, and injectable faults | [README](SharpDnp3.Tools.Outstation/README.md) |
| **dnp3-decode** | An offline decoder: hex in, a decoded protocol tree out | [README](SharpDnp3.Tools.Decode/README.md) |

All four are `dotnet run --project src/<name>`. Arguments to the tool go after
`--`:

```console
$ dotnet run --project src/SharpDnp3.Tools.Outstation -- -points
```

---

## Try the whole thing in three terminals

No hardware, no configuration.

```console
# 1. A simulated substation on :20000
$ dotnet run --project src/SharpDnp3.Tools.Outstation

# 2. Browse it
$ dotnet run --project src/SharpDnp3.Tools.Explorer -- -host 127.0.0.1:20000

# 3. Or poll and record it
$ dotnet run --project src/SharpDnp3.Tools.Master -- \
    -host 127.0.0.1:20000 -record ./data -listen :8080
$ curl -s localhost:8080/points
```

Even shorter — the explorer can run its own outstation in-process, over an
in-memory pipe:

```console
$ dotnet run --project src/SharpDnp3.Tools.Explorer -- -demo
```

That is the real link, transport, application and object layers with no socket
and no device.

---

## Which one is the example you want

**Building a polling application?** Read `SharpDnp3.Tools.Master`. It is the
closest to a production SCADA client: several sessions at once, one consumer per
session, `ChannelHandler` draining into a recorder, drop counters watched,
graceful shutdown on SIGTERM.

**Building an outstation or a device simulator?** Read
`SharpDnp3.Tools.Outstation`. `Simulator.cs` shows the pattern that matters —
controls change simulated plant, plant changes the database, the database
produces events, and the master sees the loop close.

**Building a UI or anything that consumes updates?** Read
`SharpDnp3.Tools.Explorer`. It is the most demanding consumer: an update loop
fed by a bounded channel, protocol actions dispatched off the render loop, and a
session that can be torn down and rebuilt while it runs.

**Parsing or displaying DNP3 traffic?** Read `SharpDnp3.Tools.Decode`. It is
about 200 lines because all the work is in `SharpDnp3.Decoding` — which is the
point.

---

## What they share

- **Go-style durations** everywhere a time is accepted: `500ms`, `30s`, `5m`,
  `1h`. A bare number is seconds.
- **`-h` / `--help`** prints a full flag reference.
- **Ctrl-C is the shutdown path**, matching the library, where cancelling the
  token is how a session stops. `dnp3-master` also handles SIGTERM, because a
  recorder is normally run under something that stops it that way.
- **Link address defaults** are master `1`, outstation `10`, so the tools work
  against each other out of the box.
- **Logging** goes to standard error, so it never contaminates piped output.
  On `dnp3-master` and `dnp3-outstation`, `-v` adds per-frame protocol activity
  and `-q` leaves only errors. `dnp3-explorer` shows the same records on its own
  log screen instead; `dnp3-decode` has no session to log, and uses `-q` to drop
  its summary line.

Each tool is GPLv3, like the library.
