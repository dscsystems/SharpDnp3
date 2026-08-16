# dnp3-decode

Hex in, a decoded protocol tree out. An offline decoder for DNP3 octets — the
tool you reach for when you have a capture, a vendor log, or four lines pasted
out of a device console, and you need to know what they say.

```console
$ dotnet run --project src/SharpDnp3.Tools.Decode -- 05 64 05 C0 0A 00 01 00 B1 AC
```

```
--  link  MSTR→OUTS RESET_LINK_STATES  1→10  len=5  frame=10B

1 frame(s), 0 error(s), 10 octets
```

It has no network and no session — it never connects to anything. Everything it
knows comes from the octets it was handed.

---

## Usage

```
dnp3-decode [flags] <hex octets...>
dnp3-decode [flags] -
dnp3-decode [flags] -f FILE
```

| Flag | Effect |
| --- | --- |
| `-f FILE` | read hex from a file |
| `-x` | include a hex dump of each frame |
| `-s` | treat the input as one continuous stream and reassemble fragments |
| `-q` | suppress the trailing summary line |
| `-h`, `--help` | usage |

With no arguments and a redirected stdin, it reads stdin — so the obvious shell
idiom works without `-`.

**Exit codes:** `0` success, `1` bad or missing input, `2` the input decoded but
contained protocol errors. That makes it usable as a check in a script.

---

## Input is read leniently, but not blindly

The whole point is to accept text that was written for a human. Hex dumps are
the hard case: the ASCII gutter is full of letters that are also hex digits
("cafe" in a log line is four of them), and the offset column looks like two
perfectly good octets. Grabbing every hex digit would invent data.

So the rules are explicit:

- everything from a `#` onwards is a comment;
- columns are separated by runs of **two or more spaces**;
- a leading column of 4–8 hex digits is an offset, and is dropped;
- if columns remain after that, the last is an ASCII gutter, and is dropped;
- what survives is split on whitespace, `,`, `:`, `-` and `|`, and each token is
  kept only if it is entirely hex digits after an optional `0x`.

All of these work unedited:

```
05 64 05 C0 0A 00 01 00 B1 AC
0x05,0x64,0x05,0xC0
0564 05c0 0a00
0000  05 64 05 c0 0a 00 01 00  b1 ac                    .d........
```

An odd number of hex digits is an error naming the line, rather than a silently
dropped nibble.

---

## Two modes

**Frame at a time** (the default) decodes each frame independently. Use it for
frames captured out of context — a few lines pasted from a ticket, where there
is no reason to think you have a whole conversation. When a frame does not parse
at an offset, it skips an octet and looks for the next delimiter, the same way
the streaming parser resynchronises.

**Streaming** (`-s`) keeps link and transport state across frames, so a fragment
split over several frames is reassembled and its application layer is decoded on
the frame that completes it. Use it for a capture of a whole session. It also
reports octets discarded at the link layer and segments discarded at the
transport layer — the things that tell you the line itself is unhealthy.

A fragment can span nine frames. In frame-at-a-time mode the intermediate frames
legitimately show link and transport information and no application layer; that
is not an error.

---

## A worked example

`testdata/sample.hex` is a short master/outstation exchange:

```console
$ dotnet run --project src/SharpDnp3.Tools.Decode -- -f testdata/sample.hex
```

```
--  link  MSTR→OUTS RESET_LINK_STATES  1→10  len=5  frame=10B

--  link  OUTS→MSTR ACK  10→1  len=5  frame=10B

--  link  MSTR→OUTS UNCONFIRMED_USER_DATA  1→10  len=20  frame=27B
      transport  seq=00 FIR|FIN
      application  READ seq=00 FIR FIN
        g60v2  0x06(none,all-objects) all            0 object(s)
        g60v3  0x06(none,all-objects) all            0 object(s)
        g60v4  0x06(none,all-objects) all            0 object(s)
        g60v1  0x06(none,all-objects) all            0 object(s)

--  link  OUTS→MSTR UNCONFIRMED_USER_DATA  10→1  len=30  frame=39B
      transport  seq=00 FIR|FIN
      application  RESPONSE seq=00 FIR FIN iin=CLASS_1_EVENTS|DEVICE_RESTART
        g1v2  0x00(none,start-stop8) [0..3]         4 object(s)  4 octets
          [0] ON  ONLINE
          [1] OFF  ONLINE
          [2] ON  ONLINE
          [3] OFF  ONLINE
        g30v2  0x00(none,start-stop8) [0..1]         2 object(s)  6 octets
          [0] 300  ONLINE
          [1] 400  ONLINE

--  link  MSTR→OUTS UNCONFIRMED_USER_DATA  1→10  len=24  frame=33B
      transport  seq=00 FIR|FIN
      application  DIRECT_OPERATE seq=01 FIR FIN
        g12v1  0x17(index8,count8)    count=1        1 object(s)  12 octets
          [3] PULSE_ON|TRIP count=1 on=1000ms off=0ms → SUCCESS

5 frame(s), 0 error(s), 119 octets
```

Reading that top to bottom: the master resets the link, the outstation
acknowledges, the master runs an integrity poll (the four class objects of
group 60), the outstation answers with four binaries and two analogs **and the
`DEVICE_RESTART` indication** — it has rebooted and its event history is gone —
and then the master trips the breaker at index 3.

The indentation is the layer tree: link, then transport, then application, then
the objects and their values. Which layer a problem lives in is usually the
first thing you need to know, so it is the first thing the output shows.

---

## What it is good for

**Commissioning.** The link header carries the source and destination addresses
in plain sight. When a device is silent, a capture and this tool answer "is it
even being addressed?" in one step — and mismatched link addresses are the most
common commissioning fault there is.

**Bug reports.** A vendor's "it does not work" attachment is usually a hex dump.
This turns it into something you can argue with.

**Regression checks.** The exit code is `2` when the input contains protocol
errors, so a saved capture can become a test.

**Understanding the protocol.** Decoding a real exchange, one layer at a time,
is a faster way to learn DNP3 framing than reading about it.

---

## Reading a live capture

```console
$ tcpdump -i any -s0 -x port 20000 | dnp3-decode -s -
```

Note that `-x` on `tcpdump` emits the whole IP and TCP headers too, which will
resynchronise noisily. For anything careful, capture to a file, export just the
DNP3 payload from Wireshark (Follow TCP Stream → Show data as Hex Dump), and
decode that with `-s`.

---

## How it works

All of the decoding is in the library, in `SharpDnp3.Decoding`:

```csharp
Dnp3Decoder.TryDecodeFrame(null, data, out var trace, out var consumed); // one frame
new Dnp3Decoder().Feed(data, trace => ...);                              // a stream
trace.Render(builder, showHex);
```

The tool is two files — argument parsing, `HexInput` for the lenient hex
reading, and a loop that renders traces. Everything protocol-shaped is
`Trace`, a structured tree rather than log strings, which is why the terminal
explorer, the session logs and this tool all render the same decode without any
of them re-implementing a parser.

See [`docs/api.md`](../../docs/api.md#namespace-sharpdnp3decoding) for the
namespace, and [`HexInput.cs`](HexInput.cs) if you ever need to read hex written
by humans in another program.
