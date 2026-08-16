# dnp3-explorer

A terminal browser for one DNP3 outstation. Connect, poll, see what came back,
and issue controls — the things you actually want when pointing at an unfamiliar
device and asking *"what is this thing reporting, and does it respond?"*

```console
$ dotnet run --project src/SharpDnp3.Tools.Explorer -- -demo
```

`-demo` runs a full simulated outstation **inside the same process**, connected
over an in-memory pipe: the real link, transport, application and object layers,
with no socket and no hardware. It is the fastest way to see what the tool does,
and it is a working demonstration of `Pipe.Create()`.

Against a real device:

```console
$ dotnet run --project src/SharpDnp3.Tools.Explorer -- -host 10.0.0.5:20000 -remote 10 -poll 2s
```

---

## The five screens

`1`–`5`, or `tab`, or click a tab.

1. **Overview** — the session at a glance, in four panels: **Session** (what is
   being talked to, over what, link state, how long it has been up, the
   outstation's internal indications, and the current control mode), **Database**
   (how many points of each type have been seen), **Traffic** (tasks run,
   succeeded, failed) and **Recent activity**. The header carries the connection
   state, a message rate meter and the clock.
2. **Points** — every point the device has reported, one row each:
   `POINT VALUE TREND QUALITY AGE TIMESTAMP`. Points not updated within `-stale`
   (30 s by default) are faded, so a device that has gone quiet looks like one.
3. **Events** — the sequence of events, newest last:
   `RECEIVED POINT VALUE CL QUALITY SOURCE TIMESTAMP`, where `CL` is the class it
   arrived under and `SOURCE` the group and variation.
4. **Log** — what this tool has been doing: connects, polls, controls, protocol
   errors.
5. **Help** — the full key and mouse reference, also on `?`.

---

## Keys

| Key | Does |
| --- | --- |
| `1`–`5`, `tab` | change screen |
| `↑` `↓` `j` `k` | move the cursor |
| `pgup` `pgdn` | move a page |
| `home` `end` `g` `G` | first and last row |
| `/` | filter the list |
| `esc` | clear the filter, close a dialog |
| `f` | follow the newest row |
| `d`, `enter` | inspector, or act on the row |
| `<` `>` | change the sort column |
| `r` | reverse the sort |
| `x` | clear this list |
| `e` | export the list as CSV |
| `q`, `ctrl+c` | quit |

### Protocol actions

| Key | Does |
| --- | --- |
| `i` | integrity poll (classes 0–3) |
| `p` | poll event classes 1, 2, 3 |
| `s` | range scan a group |
| `t` | set the outstation clock |
| `T` | set it, measuring the link delay first (for serial) |
| `u` / `U` | enable / disable unsolicited reporting |
| `R` | restart the outstation |
| `c` / `o` | close / open the selected binary output |
| `enter` | control dialog, or write a setpoint |
| `b` | write an analog deadband |
| `S` | switch between select-before-operate and direct operate |
| `C` | change the connection |

### Mouse

Click a tab, a row, a column heading or a footer button. Click a **selected** row
again to act on it, right-click one for the inspector, scroll with the wheel
(including over the tabs), and drag the scrollbar. `-no-mouse` turns it off.

---

## Usage

```
dnp3-explorer -host HOST:PORT [flags]
dnp3-explorer -demo
```

### Connection — all of it editable while running, with `C`

| Flag | Default | Effect |
| --- | --- | --- |
| `-host ADDR` | | outstation address |
| `-serial PORT` | | a serial port instead of TCP |
| `-baud RATE` | `9600` | serial line rate |
| `-local N` | `1` | master link address |
| `-remote N` | `10` | outstation link address |
| `-poll DUR` | `5s` | event class poll interval; `0` disables |
| `-timeout DUR` | `5s` | response timeout |
| `-demo` | | run a simulated outstation in-process |

### Interface

| Flag | Default | Effect |
| --- | --- | --- |
| `-no-mouse` | | disable the mouse |
| `-inline` | | draw inline instead of taking the whole terminal |
| `-stale DUR` | `30s` | fade points not updated for this long |

### Controls

| Flag | Default | Effect |
| --- | --- | --- |
| `-direct` | | direct operate instead of select-before-operate |
| `-no-confirm` | | issue controls without asking first |
| `-pulse MS` | `1000` | pulse duration for trip and close |

---

## The parts worth stealing

### Editing the connection while it runs

`C` opens an editor for the address, both link addresses, the timeout and the
poll interval; applying it tears the session down and brings a new one up in
place.

This is the feature that saves the most time in the field. **A link address read
off a drawing is a guess until something answers.** Restarting a tool to try 11
instead of 10, then 12, then 1, is how ten minutes of commissioning becomes an
afternoon — and mismatched link addresses produce silence rather than an error,
so guessing is exactly what you end up doing.

The session is brought up through the same path a reconnect uses, so the path an
operator reaches for after changing an address is the one that has been
exercised continuously since startup rather than a second implementation that
gets used once.

### Controls that are hard to issue by accident

`enter` on an output opens a dialog naming exactly what will be sent — the
point, the operation, the pulse duration — and asks for confirmation before
anything moves. The default is **select-before-operate**, so the outstation gets
its chance to refuse before the plant does anything.

`-direct` and `-no-confirm` turn that off for the situations that need it — and
while `-no-confirm` is in effect the toolbar carries a standing
`! controls send immediately` warning that has no key and cannot be dismissed.
The one moment an operator needs to be told that is the moment they have stopped
expecting a dialog to appear.

`S` toggles select-before-operate at runtime, and the current mode is on screen.

### Sorting by quality, worst first

`<` and `>` change the sort column: point (type and index, the natural order),
value, quality, age, or the outstation's timestamp. Quality sorts **worst
first**, which is how you find the four broken points in a device with a
thousand good ones.

Combined with `/` — which filters on anything in the row — that is usually the
whole diagnostic session: filter to the feeder you care about, sort by quality,
read the top three rows.

### Export what you are looking at

`e` writes the current list as CSV **after the filter and the sort, not before**,
so what lands in the file is the view you were looking at rather than something
you have to reconstruct. The file is timestamped: `dnp3-points-20260816-143022.csv`.

Each screen exports its own shape — points with quality, timestamps, update and
event counts and the group/variation; events with their class; the log with its
levels. The answer to "what is this device reporting" usually has to leave the
terminal, and it goes into a commissioning report or an email to a vendor.

### The demo outstation

`-demo` builds six binaries, six analogs, two counters, two binary outputs, two
analog outputs and two octet strings, with the analogs on class 2 and a deadband,
and drives them. Controls work against it.

It is rebuilt **per connection** rather than once for the process, so
reconnecting to it behaves like reconnecting to anything else: it comes up
fresh, which is the honest outcome, since the pipe the old one was reached over
has been closed.

---

## How it works

An Elm-shaped loop, which is worth a look if you are building any UI on this
library. `Model.Update(msg)` is the only thing that mutates state, and it
returns an optional `Cmd` — a thunk that is run **off** the loop, on a task pool
thread, and whose result is pushed back in as another message. A slow outstation
therefore never freezes the interface.

Messages arrive on a bounded channel with `DropWrite`, so when the interface
falls behind, messages are dropped rather than blocking the session that
produced them. That is the same trade `ChannelHandler` makes in the library, for
the same reason: **an operator's scrollback is not worth a missed poll.**

| File | What is in it |
| --- | --- |
| `Model.cs` | state, `Update`, filtering, sorting |
| `View.cs` | rendering every screen, and the help |
| `Layout.cs` | the frame, columns and scrollbar arithmetic |
| `Connection.cs` | the session, its supervisor, and the message pump |
| `Link.cs` | connection parameters and their validation |
| `Terminal.cs` | raw mode, the alternate screen, input decoding |
| `Mouse.cs` | hit-testing the frame |
| `Form.cs` | the connection editor |
| `Export.cs` | CSV output |
| `Theme.cs` | colours, padding, truncation |
| `Demo.cs` | the in-process outstation |

The alternate screen is the default because the tool lays out a fixed frame and
fills the terminal with it, which is what makes a table, a scrollbar and a
footer possible at all. `-inline` gives back scrollback-friendly drawing for the
times when leaving the session in the terminal history is worth more than the
layout — logging a commissioning run, or a terminal that cannot switch screens.

See [the user guide](../../docs/user-guide.md#building-a-master) for the master
API this is built on.
