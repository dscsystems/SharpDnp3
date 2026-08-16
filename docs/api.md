# SharpDnp3 API reference

Complete reference for the public API, namespace by namespace.

For a task-oriented introduction — how to build a master, how to build an
outstation, how to test either without hardware — read the
[user guide](user-guide.md) first. This document is the reference you come back
to.

```csharp
using SharpDnp3;
```

**Status: the API is not yet stable.** The package version is `0.1.0`. Names and
signatures may change before 1.0.

## Namespaces

| Namespace | What it is |
| --- | --- |
| `SharpDnp3` | Value types shared by the whole stack: measurements, quality flags, timestamps, commands, internal indications, exceptions, logging |
| `SharpDnp3.Master` | The master role: polls outstations, receives events, issues controls |
| `SharpDnp3.Outstation` | The outstation role: holds measurements, answers polls, executes controls |
| `SharpDnp3.Channels` | Transports: TCP, TLS, UDP, serial, in-process pipe |
| `SharpDnp3.Decoding` | Structured protocol traces for logging and tooling |
| `SharpDnp3.Objects` | Group/variation codecs and the object descriptor table |
| `SharpDnp3.App`, `SharpDnp3.Link`, `SharpDnp3.Transport` | The layer primitives — headers, function codes, qualifiers, framing constants. Public, but an application normally names nothing from them |

Everything ships in one assembly, `SharpDnp3.dll`, targeting .NET 10.

---

# Namespace SharpDnp3

The root namespace holds the value types every other namespace speaks in. It has
no sessions and no I/O.

## Measurements

Every measurement carries a value, a quality octet and a timestamp. Static
variations simply leave the timestamp invalid. All of them are `readonly record
struct`, so they compare by value and can be built positionally or with object
initialisers.

```csharp
public readonly record struct Binary(bool Value, Flags Flags, Timestamp Time);
public readonly record struct DoubleBitBinary(DoubleBit Value, Flags Flags, Timestamp Time);
public readonly record struct Counter(uint Value, Flags Flags, Timestamp Time);       // wraps rather than saturating
public readonly record struct FrozenCounter(uint Value, Flags Flags, Timestamp Time); // captured at a freeze
public readonly record struct Analog(double Value, Flags Flags, Timestamp Time);
public readonly record struct BinaryOutputStatus(bool Value, Flags Flags, Timestamp Time);
public readonly record struct AnalogOutputStatus(double Value, Flags Flags, Timestamp Time);
```

An octet string is a plain `byte[]`; the only thing the library adds is the
length limit:

```csharp
public static class OctetString
{
    public const int MaxOctetStringLen = 255;
}
```

The stack carries every analog variation — 16-bit, 32-bit, single and double
precision — as a `double` and narrows only at the encoding boundary. A point
configured for a 32-bit integer variation will report `123.5` as `123`; that is
the encoding doing what it says, not a bug. See
[Variations and precision](user-guide.md#variations-and-precision).

```csharp
public readonly record struct Indexed<T>(ushort Index, T Value);
```

`Indexed<T>` pairs a measurement with the point index it was reported at.
Handler methods receive `IReadOnlyList<Indexed<T>>`.

### DoubleBit

```csharp
public enum DoubleBit : byte
{
    Intermediate  = 0, // both contacts open: the device is moving
    DeterminedOff = 1, // open
    DeterminedOn  = 2, // closed
    Indeterminate = 3, // both contacts closed, which is impossible
}

public static string ToDisplayString(this DoubleBit value); // "Intermediate", "Off", "On", "Indeterminate"
```

The numeric values are the on-the-wire encoding. A double-bit input reports a
two-contact device — typically a breaker with separate open and closed auxiliary
contacts — and can therefore distinguish a device in transit or miswired from one
that is definitely open or definitely closed.

### PointType

```csharp
public enum PointType : byte
{
    Unknown = 0,
    Binary,
    DoubleBitBinary,
    Counter,
    FrozenCounter,
    Analog,
    BinaryOutputStatus,
    AnalogOutputStatus,
    OctetString,
}
```

## Flags

```csharp
public readonly struct Flags : IEquatable<Flags>
{
    public Flags(byte value);
    public byte Value { get; }
}
```

The quality octet accompanying most measurements. The low five bits mean the
same thing for every measurement type; the upper three are type-specific. The
bits are static properties rather than enum members, because the same bit has
different names on different point types:

```csharp
// Common to every measurement type.
Flags.Online        // 0x01 — the point is being read from the field
Flags.Restart       // 0x02 — not updated since the device restarted
Flags.CommLost      // 0x04 — communication with the point's source has failed
Flags.RemoteForced  // 0x08 — forced by a downstream device
Flags.LocalForced   // 0x10 — forced by the outstation itself
Flags.None          // 0x00

// Type-specific. Each is valid only for the types named.
Flags.ChatterFilter // 0x20 — binary, double-bit: toggling faster than the filter allows
Flags.Rollover      // 0x20 — counters (deprecated by the standard, still emitted)
Flags.Discontinuity // 0x40 — counters: not comparable against the previous reading
Flags.OverRange     // 0x20 — analogs: the value exceeds the point's range
Flags.ReferenceErr  // 0x40 — analogs: the digitising reference is inaccurate
Flags.StateBit      // 0x80 — binaries: carries the value itself, as in g1v2
```

Note that `0x20` and `0x40` are reused with different meanings per type. This is
why `Flags` stores the raw octet and leaves naming to the accessors.

```csharp
public bool Has(Flags mask);      // every bit in mask is set
public bool HasAny(Flags mask);   // any bit in mask is set
public Flags Set(Flags mask);
public Flags Clear(Flags mask);
public bool IsGood();
public override string ToString();
public string StringFor(PointType type);

public static Flags operator |(Flags a, Flags b);
public static Flags operator &(Flags a, Flags b);
public static Flags operator ^(Flags a, Flags b);
public static Flags operator ~(Flags a);
public static implicit operator Flags(byte value);
public static explicit operator byte(Flags flags);
```

`IsGood` is online, not restarting, not comm-lost, and not forced from either
end. A cleared `Online` bit is the single most important quality signal in DNP3:
the value present alongside it is not trustworthy.

`ToString` renders the upper bits by position, since their meaning depends on the
point type. Use `StringFor` when the type is known:

```csharp
var f = Flags.Online | Flags.OverRange;
f.ToString();                        // "ONLINE|BIT5"
f.StringFor(PointType.Analog);       // "ONLINE|OVER_RANGE"
```

An unset `Flags` renders as an em dash, not `0x00`.

## Timestamps

```csharp
public enum TimestampQuality : byte
{
    Invalid = 0,      // carried no time at all
    Unsynchronized,   // the source clock was not synced
    Synchronized,     // the source clock was synced
}

public readonly record struct Timestamp
{
    public DateTimeOffset Time { get; init; }
    public TimestampQuality Quality { get; init; }
    public bool IsValid { get; }   // Quality != Invalid

    public static Timestamp Now(DateTimeOffset time);            // synchronized
    public static Timestamp Unsynchronized(DateTimeOffset time);
    public static Timestamp NoTime();                            // the default value
}
```

The time and its trustworthiness are kept together deliberately. A DNP3
measurement can carry a perfectly well-formed timestamp from an outstation whose
clock has never been set, and a consumer that ignores the quality will file that
event under 1970 or under whatever the drifted clock says.

`Timestamp.Now` takes the time rather than reading the clock, so a session driven
by a `TimeProvider` stays deterministic.

```csharp
public static class Dnp3Time
{
    public const long MaxDnp3Time = (1L << 48) - 1;             // year 10889

    public static ulong ToDnp3(DateTimeOffset time);            // ms since epoch, clamped to 48 bits
    public static DateTimeOffset FromDnp3(ulong milliseconds);  // UTC; bits above the low 48 ignored
}
```

Times before the epoch clamp to zero.

## Classes

```csharp
[Flags]
public enum Class : byte
{
    None     = 0,      // assign a point to no class: its events are suppressed
    Class0   = 1 << 0, // static data
    Class1   = 1 << 1, // event class 1, conventionally the most urgent
    Class2   = 1 << 2,
    Class3   = 1 << 3,
    Class123 = Class1 | Class2 | Class3,
    All      = Class0 | Class123,
}

public static bool Has(this Class value, Class mask);
public static string ToDisplayString(this Class value); // "0+1+2+3", or "none"
```

Masks are how polls are expressed. An integrity poll is `Class.All`; a routine
event poll is `Class.Class123`.

## Controls

```csharp
public readonly struct ControlCode : IEquatable<ControlCode>
{
    public ControlCode(byte value);
    public byte Value { get; }

    // Operation types, in the low nibble.
    public static ControlCode Nul      { get; } // 0x00
    public static ControlCode PulseOn  { get; } // 0x01
    public static ControlCode PulseOff { get; } // 0x02
    public static ControlCode LatchOn  { get; } // 0x03
    public static ControlCode LatchOff { get; } // 0x04

    // Trip/close modifiers, which pair with an operation type to drive the two
    // coils of a breaker.
    public static ControlCode Close { get; }    // 0x80
    public static ControlCode Trip  { get; }    // 0x40

    public ControlCode OpType();
    public bool IsTrip();
    public bool IsClose();
    public bool IsClear();
    public override string ToString();          // e.g. "PULSE_ON|TRIP"

    public static ControlCode operator |(ControlCode a, ControlCode b);
    public static ControlCode operator &(ControlCode a, ControlCode b);
    public static implicit operator ControlCode(byte value);
    public static explicit operator byte(ControlCode code);
}
```

```csharp
public readonly record struct ControlRelayOutputBlock
{
    public ControlCode Code { get; init; }
    public byte Count { get; init; }        // how many times to execute; zero is legal and means "do nothing"
    public uint OnTime { get; init; }       // milliseconds, used by the pulse operations
    public uint OffTime { get; init; }
    public CommandStatus Status { get; init; } // meaningful only on a response echo
}

public readonly record struct AnalogOutputInt16(short Value, CommandStatus Status = CommandStatus.Success);   // g41v2
public readonly record struct AnalogOutputInt32(int Value, CommandStatus Status = CommandStatus.Success);     // g41v1
public readonly record struct AnalogOutputFloat32(float Value, CommandStatus Status = CommandStatus.Success); // g41v3
public readonly record struct AnalogOutputFloat64(double Value, CommandStatus Status = CommandStatus.Success);// g41v4
```

Do not build these by hand to send. Use the [`Command` factory
methods](#building-commands), which zero the status octet so the outstation's
echo is what fills it in.

## CommandStatus

```csharp
public enum CommandStatus : byte
{
    Success = 0,
    Timeout = 1,
    NoSelect = 2,
    FormatError = 3,
    NotSupported = 4,
    AlreadyActive = 5,
    HardwareError = 6,
    Local = 7,
    TooManyOps = 8,
    NotAuthorized = 9,
    AutomationInhibit = 10,
    ProcessingLimited = 11,
    OutOfRange = 12,
    DownstreamLocal = 13,
    AlreadyComplete = 14,
    Blocked = 15,
    Canceled = 16,
    BlockedOtherMaster = 17,
    DownstreamFail = 18,
    NonParticipating = 126,
    Undefined = 127,
}

public static bool OK(this CommandStatus status);              // status == Success
public static string ToDisplayString(this CommandStatus status); // "SUCCESS", "NOT_SUPPORTED", …
```

## Restart

```csharp
public enum RestartMode : byte
{
    Cold = 0, // reinitialise completely, as though power cycled
    Warm,     // reinitialise only the communications process
}

public static string ToDisplayString(this RestartMode mode); // "cold" / "warm"
```

## Internal indications

```csharp
public readonly struct Iin : IEquatable<Iin>
{
    public Iin(ushort value);
    public ushort Value { get; }

    public static Iin Broadcast { get; }           // 0x0001
    public static Iin Class1Events { get; }        // 0x0002
    public static Iin Class2Events { get; }        // 0x0004
    public static Iin Class3Events { get; }        // 0x0008
    public static Iin NeedTime { get; }            // 0x0010
    public static Iin LocalControl { get; }        // 0x0020
    public static Iin DeviceTrouble { get; }       // 0x0040
    public static Iin DeviceRestart { get; }       // 0x0080
    public static Iin NoFuncCodeSupport { get; }   // 0x0100
    public static Iin ObjectUnknown { get; }       // 0x0200
    public static Iin ParameterError { get; }      // 0x0400
    public static Iin EventBufferOverflow { get; } // 0x0800
    public static Iin AlreadyExecuting { get; }    // 0x1000
    public static Iin ConfigCorrupt { get; }       // 0x2000
    public static Iin Reserved1 { get; }
    public static Iin Reserved2 { get; }
    public static Iin None { get; }
    public static Iin EventClassMask { get; }
    public static Iin ErrorMask { get; }

    public static Iin Parse(byte iin1, byte iin2);
    public (byte Iin1, byte Iin2) Octets();
    public bool Has(Iin mask);
    public bool HasAny(Iin mask);
    public Iin Set(Iin mask);
    public Iin Clear(Iin mask);
    public bool HasEvents();     // any of the three event-class bits
    public bool HasError();      // any of the error bits
    public Iin EventClasses();
    public override string ToString(); // "CLASS_1_EVENTS|DEVICE_RESTART", or "—" when clear

    public static Iin operator |(Iin a, Iin b);
    public static Iin operator &(Iin a, Iin b);
    public static Iin operator ~(Iin a);
}
```

Two octets on every response — the outstation's running health report. Unlike the
Go implementation this type is public and fully nameable, so you can store it,
pass it and test individual bits.

## Helpers

```csharp
public static class AnalogRange
{
    public static bool FitsIn16(double value); // representable as int16 without loss
    public static bool FitsIn32(double value);
}
```

An outstation needs these when a master requests a narrow variation.

## Exceptions

Layer code raises detailed messages inside these types, so a caller can classify
a failure by catching rather than by matching strings.

```csharp
public class Dnp3Exception : Exception;                    // the base of everything below

public class MalformedException : Dnp3Exception;           // bytes that are not valid DNP3
public class Dnp3TimeoutException : Dnp3Exception;         // no answer within ResponseTimeout
public class ClosedException : Dnp3Exception;              // the session or channel is shut down
public class NotSupportedByPeerException : Dnp3Exception;   // the peer refused the function
public class BadConfigException : Dnp3Exception;           // bad arguments: empty class mask, start > stop, no commands
public class TaskFailedException : Dnp3Exception;          // retries exhausted
public class NoConnectionException : Dnp3Exception;        // nothing is connected

public sealed class ChannelClosedException : Dnp3Exception; // in SharpDnp3.Channels
```

Every request method on `MasterSession` throws these; per-command refusals are
*not* exceptions, they are statuses in the [`CommandResult`](#commandresult).

```csharp
try
{
    await master.IntegrityPollAsync(ct);
}
catch (Dnp3TimeoutException)      { /* the outstation did not answer */ }
catch (TaskFailedException)       { /* retries exhausted */ }
catch (OperationCanceledException){ /* we gave up, not the device */ }
```

## Logging

```csharp
public enum Dnp3LogLevel { Debug, Info, Warn, Error }

public interface IDnp3Logger
{
    bool IsEnabled(Dnp3LogLevel level);
    void Log(Dnp3LogLevel level, string message, params ReadOnlySpan<(string Key, object? Value)> fields);
}

public sealed class NullDnp3Logger : IDnp3Logger      // NullDnp3Logger.Instance discards everything
public sealed class TextWriterDnp3Logger : IDnp3Logger // one line per record
{
    public TextWriterDnp3Logger(TextWriter writer, Dnp3LogLevel minimum = Dnp3LogLevel.Info);
}
```

A small interface of its own rather than a dependency on a logging framework:
the library carries no third-party packages, and adapting this to
`Microsoft.Extensions.Logging` or Serilog is a dozen lines. Records are
structured — a message plus key/value pairs — because a protocol log is searched,
not read.

---

# Namespace SharpDnp3.Channels

The physical layer beneath a session: the thing that produces a byte stream and
reproduces it after a failure.

```csharp
public interface IChannel : IDisposable
{
    Task<Stream> ConnectAsync(CancellationToken cancellationToken);
    void Close();
}
```

`ConnectAsync` blocks until a connection is available or the token is cancelled.
A session calls it again after every disconnection, so implementations own their
own reconnect timing. Every transport DNP3 runs over reduces to this contract,
which is what lets the session layer be written once.

Every channel below also overrides `ToString()` with something an operator can
read in a log — `"tcp 10.0.0.5:20000"`, `"serial /dev/ttyUSB0"`.

## The channels

```csharp
public sealed class TcpClientChannel : IChannel
{
    public TcpClientChannel(string address, Retry retry);
    public TimeSpan ConnectTimeout { get; init; } // default 10s
}

public sealed class TcpServerChannel : IChannel
{
    public TcpServerChannel(string address);
    public IPEndPoint? BoundAddress { get; }      // null until it has bound
}

public sealed class TlsClientChannel : IChannel
{
    public TlsClientChannel(string address, Dnp3TlsConfig tls, Retry retry);
    public TimeSpan ConnectTimeout { get; init; }
}

public sealed class TlsServerChannel : IChannel
{
    public TlsServerChannel(string address, Dnp3TlsConfig tls);
    public IPEndPoint? BoundAddress { get; }
}

public sealed class UdpChannel : IChannel
{
    public UdpChannel(UdpConfig config);
    public IPEndPoint? BoundAddress { get; }
}

public sealed class SerialChannel : IChannel
{
    public SerialChannel(SerialConfig config);
}

public static class Pipe
{
    public static (IChannel A, IChannel B) Create();
}

public sealed class PipeListener : IDisposable
{
    public IChannel Server { get; }   // the listening end
    public IChannel Connect();        // one more peer
}
```

Every `IChannel` reports whether it can produce more than one peer:

```csharp
bool SupportsConcurrentConnections { get; }  // default false
```

`TcpServerChannel`, `TlsServerChannel` and `PipeListener.Server` say yes: each
accept is a different master. The channels that dial — TCP client, TLS client,
UDP, serial, `Pipe` — say no, because asking one of them for a second connection
produces a second connection to the same peer, not a second peer. An outstation
configured with `MaxMasters` above one over a channel that says no is refused at
`RunAsync` rather than quietly serving one master.

`BoundAddress` is how a test asks for port `0` and finds out which port it got.

`Pipe.Create` returns two channels connected to each other in memory. This is
what every integration test runs over and what the explorer's demo mode uses: a
full master and outstation talking through the real link, transport and
application layers, with no socket and no hardware.

`PipeListener` is the same idea for several masters: `Server` behaves like a
listening socket and every `Connect()` is another peer dialling it, so a
multi-master outstation can be tested without one.

Address strings are `host:port`; an empty host (`":20000"`) binds every
interface, dual-stack.

## Retry

```csharp
public readonly record struct Retry
{
    public TimeSpan Min { get; init; }
    public TimeSpan Max { get; init; }   // zero means uncapped
    public double Factor { get; init; }
    public double Jitter { get; init; }  // fraction of the delay to randomise, 0 to 1

    public static Retry Default { get; } // 500ms → 60s, factor 2, jitter 0.2
    public static Retry None { get; }    // connect once and give up

    public TimeSpan Delay(int n);
    public Task SleepAsync(int attempt, CancellationToken cancellationToken);
}
```

`Retry.None` connects once and gives up, which is what tests and one-shot tools
want.

The jitter matters more than it looks. A substation that loses a switch brings
every master's connection down at the same instant; without jitter they all retry
in lockstep and keep colliding, turning one outage into a self-sustaining
thundering herd.

`SerialChannel` does not take a `Retry`: the port is reopened on the next
`ConnectAsync`, and how long to wait between attempts is the session's business.

## Dnp3TlsConfig

```csharp
public sealed class Dnp3TlsConfig
{
    public string CertFile { get; set; }        // this end's certificate
    public string KeyFile { get; set; }         // this end's private key
    public string CaFile { get; set; }          // the authority that signs the peer's certificate
    public string ServerName { get; set; }      // name to verify against the peer's cert; a client defaults to the dialled host
    public SslProtocols MinVersion { get; set; }// default TLS 1.2, the floor IEC 62351 sets
}
```

**Mutual authentication is not optional.** DNP3 carries controls that operate
plant, and a channel that authenticates only the server lets anyone who can reach
the port issue them. IEC 62351-3 requires both sides to present certificates, and
these channels refuse to build a configuration that does not.

## SerialConfig

```csharp
public sealed class SerialConfig
{
    public string Device { get; set; }        // /dev/ttyUSB0, COM3, …
    public int Baud { get; set; }             // zero uses 9600, the DNP3 convention
    public int DataBits { get; set; }         // zero uses 8
    public Parity Parity { get; set; }        // System.IO.Ports.Parity; defaults to None
    public StopBits StopBits { get; set; }    // System.IO.Ports.StopBits; defaults to One
    public TimeSpan ReadTimeout { get; set; } // zero uses one second
}
```

`ReadTimeout` bounds a blocking read so a session's cancellation can be noticed.
It is not a protocol timeout: an idle line legitimately produces nothing for
minutes at a time, and a read returning empty is not an error.

## UdpConfig

```csharp
public sealed class UdpConfig
{
    public string LocalAddr { get; set; }  // empty binds an ephemeral port on all interfaces — what a master wants
    public string RemoteAddr { get; set; } // empty replies to whoever writes first — what an outstation wants
}
```

---

# Namespace SharpDnp3.Master

The station that polls outstations, receives their events, and issues commands.

## MasterSession

```csharp
public sealed partial class MasterSession
{
    public MasterSession(MasterConfig config, IMasterHandler? handler = null); // null handler becomes NopHandler

    public Task RunAsync(IChannel channel, CancellationToken cancellationToken = default);

    public bool Connected { get; }
    public MasterStats Stats { get; }
    public Iin LastIin { get; }
}
```

`RunAsync` connects and polls until the token is cancelled. It is the call that
starts the session loop; start it without awaiting and cancel the token to stop
it. There is no `Close`, `Stop` or `Dispose` on the session — **cancellation is
the shutdown path**.

All protocol state lives in that loop. **Every request method below is safe to
call from any thread**: each hands a task to the session loop and awaits its
completion, so no caller ever touches protocol state directly. They all complete
when the outstation answers, or throw when it does not.

## MasterConfig

```csharp
public sealed class MasterConfig
{
    public ushort LocalAddr { get; set; }  // this master's link address
    public ushort RemoteAddr { get; set; } // the outstation's

    public TimeSpan ResponseTimeout { get; set; } // default 5s
    public TimeSpan TaskRetryPeriod { get; set; } // default 5s

    public bool IntegrityOnStartup { get; set; }    // class 0+1+2+3 poll at startup and on every reported restart
    public bool DisableUnsolOnStartup { get; set; } // send disable-unsolicited first, the standard's startup sequence
    public Class UnsolClassMask { get; set; }       // classes to enable after the integrity poll; zero enables none

    public int MaxTxFragment { get; set; } // default 2048
    public int MaxRxFragment { get; set; } // default 2048

    public bool UseLinkConfirms { get; set; } // link-layer confirmation; normally off over TCP
    public int LinkRetries { get; set; }      // retransmissions of a confirmed frame
    public TimeSpan LinkTimeout { get; set; } // default 1s; matters only with UseLinkConfirms

    public TimeSpan KeepAlive { get; set; }   // probe an idle link this often; zero disables

    public IDnp3Logger? Log { get; set; }         // null discards
    public TimeProvider? TimeProvider { get; set; } // null uses TimeProvider.System
}
```

`KeepAlive` exists because an idle TCP connection is indistinguishable from a
peer that has gone away: both are silent. Without a probe a master notices only
when its next poll times out, which on a slow schedule can be minutes.

`TimeProvider` is what makes a session testable with a virtual clock —
`Microsoft.Extensions.TimeProvider.Testing`'s `FakeTimeProvider` drops straight
in.

## Reads

```csharp
public Task IntegrityPollAsync(CancellationToken cancellationToken = default);
public Task ScanClassesAsync(Class mask, CancellationToken cancellationToken = default);
public Task ScanRangeAsync(byte group, byte variation, ushort start, ushort stop,
                           CancellationToken cancellationToken = default);
public Task AddPeriodicScanAsync(TimeSpan period, Class mask,
                                 CancellationToken cancellationToken = default);
```

`IntegrityPollAsync` reads every class, re-baselining the master's picture.
`ScanClassesAsync` reads the given classes once; an empty mask is
`BadConfigException`.

`ScanRangeAsync` reads a contiguous index range of one group and variation.
**Pass variation zero to let the outstation choose its default**, which is what a
master normally wants: the outstation knows which encoding carries its points
without loss. `start > stop` is `BadConfigException`.

`AddPeriodicScanAsync` completes as soon as the scan is queued, not when it first
runs; the poll then runs for the life of the session. Failures do not stop it — a
poll that fails because the link dropped must keep trying once the link returns.

## Commands

```csharp
public Task<CommandResult> DirectOperateAsync(IReadOnlyList<Command> commands,
                                              CancellationToken cancellationToken = default);
public Task<CommandResult> DirectOperateAsync(params Command[] commands);

public Task<CommandResult> SelectAndOperateAsync(IReadOnlyList<Command> commands,
                                                 CancellationToken cancellationToken = default);
public Task<CommandResult> SelectAndOperateAsync(params Command[] commands);

public Task DirectOperateNoReplyAsync(IReadOnlyList<Command> commands,
                                      CancellationToken cancellationToken = default);
```

`DirectOperateAsync` executes immediately and is the right choice for an
automated action. `SelectAndOperateAsync` runs the two-pass sequence, and an
operator-initiated control on plant that matters should use it: the select is the
outstation's opportunity to say "not that point, not right now" before anything
in the substation moves, and a failed select is never followed by an operate — it
throws instead.

The two requests of a select-and-operate are chained internally so nothing can be
scheduled between them. The standard requires the OPERATE to carry the sequence
number one above the SELECT, so a periodic poll landing in the middle would make
the outstation reject the operate with `NO_SELECT`.

`DirectOperateNoReplyAsync` completes as soon as the request is on the wire.
Nothing comes back, so nothing can be checked. Use it only where the outcome
genuinely does not need confirming.

All of them throw `BadConfigException` when given no commands. A command the
outstation *refused* is not an exception: the returned `CommandResult` carries
the per-point statuses.

### Building commands

```csharp
public readonly record struct Command
{
    public ushort Index { get; init; }
    public override string ToString();

    public static Command Crob(ushort index, ControlRelayOutputBlock c);
    public static Command Trip(ushort index, uint pulseMillis);   // pulse the trip coil
    public static Command Close(ushort index, uint pulseMillis);  // pulse the close coil
    public static Command LatchOn(ushort index);
    public static Command LatchOff(ushort index);

    public static Command AnalogOutputInt16(ushort index, short v);    // g41v2
    public static Command AnalogOutputInt32(ushort index, int v);      // g41v1
    public static Command AnalogOutputFloat32(ushort index, float v);  // g41v3
    public static Command AnalogOutputFloat64(ushort index, double v); // g41v4
}
```

Build commands only through these factories. The encoding differs per variation,
and the status octet must be zero on the way out so that the outstation's echo is
what fills it in.

Commands sharing a group and variation are packed into one object header with
per-object index prefixes, so a multi-point control is one request.

### CommandResult

```csharp
public sealed class CommandResult
{
    public List<CommandStatus> Statuses { get; }        // one per command, in the order sent
    public IReadOnlyList<Command> Commands { get; init; } // echo of what was sent

    public bool OK();                  // every command succeeded; false when Statuses is empty
    public Dnp3Exception? Error();     // describes the failures, or null
    public void ThrowIfFailed();
    public override string ToString();
}
```

A multi-command request can partially succeed. `OK()` is false unless every
status is `CommandStatus.Success`, because treating a partial success as success
would tell an operator a breaker operated when it did not.

## Time and configuration

```csharp
public Task SyncTimeAsync(CancellationToken cancellationToken = default);
public Task SyncTimeWithDelayAsync(CancellationToken cancellationToken = default);
public Task WriteTimeAsync(DateTimeOffset t, CancellationToken cancellationToken = default);
public Task WriteDeadbandAsync(IReadOnlyDictionary<ushort, float> deadbands,
                               CancellationToken cancellationToken = default);
public Task EnableUnsolicitedAsync(Class mask, CancellationToken cancellationToken = default);
public Task DisableUnsolicitedAsync(Class mask, CancellationToken cancellationToken = default);
public Task RestartAsync(RestartMode mode, CancellationToken cancellationToken = default);
```

`SyncTimeAsync` uses the LAN procedure, writing the time directly. It assumes the
transit delay is negligible against the outstation's timestamp resolution — true
over Ethernet, not over a slow serial link.

`SyncTimeWithDelayAsync` is the serial procedure: measure the turnaround with
DELAY_MEASURE, then write a time already corrected by the one-way transit (the
round trip less the outstation's reported processing delay, halved). Without the
correction the outstation's clock lands late by that amount, which over a 1200
baud link is easily tens of milliseconds and puts every event it stamps into the
past.

`WriteDeadbandAsync` takes at most 255 entries — the limit of the one-octet count
— and rejects an empty dictionary. A deadband is how a master tells an outstation
how much a point must move before it is worth an event.

`RestartAsync` returns when the request was *accepted*, not when the device is
back: the outstation answers with how long it expects to be unavailable and then
restarts.

## IMasterHandler

```csharp
public interface IMasterHandler
{
    void BeginFragment(ResponseInfo info);
    void EndFragment(ResponseInfo info);

    void HandleBinary(HeaderInfo info, IReadOnlyList<Indexed<Binary>> values);
    void HandleDoubleBit(HeaderInfo info, IReadOnlyList<Indexed<DoubleBitBinary>> values);
    void HandleCounter(HeaderInfo info, IReadOnlyList<Indexed<Counter>> values);
    void HandleFrozenCounter(HeaderInfo info, IReadOnlyList<Indexed<FrozenCounter>> values);
    void HandleAnalog(HeaderInfo info, IReadOnlyList<Indexed<Analog>> values);
    void HandleBinaryOutputStatus(HeaderInfo info, IReadOnlyList<Indexed<BinaryOutputStatus>> values);
    void HandleAnalogOutputStatus(HeaderInfo info, IReadOnlyList<Indexed<AnalogOutputStatus>> values);
    void HandleOctetString(HeaderInfo info, IReadOnlyList<Indexed<byte[]>> values);
}

public class NopHandler : IMasterHandler; // every method virtual and empty
```

`BeginFragment` and `EndFragment` bracket every fragment, so a consumer that
needs a consistent set — a database transaction, a UI repaint — has somewhere to
open and close it.

**Handler methods are called from the session loop.** A slow handler delays the
session's polling, so anything expensive belongs behind a queue;
[`ChannelHandler`](#channelhandler) is that queue. Derive from `NopHandler` and
override only the methods you care about.

`HandleOctetString` receives groups 110 and 111: the point names, firmware
versions and serial numbers a device reports as text rather than as
measurements.

```csharp
public readonly record struct ResponseInfo
{
    public Iin Iin { get; init; }              // internal indications the outstation reported
    public bool Unsolicited { get; init; }     // arrived unprompted rather than in answer to a poll
    public byte Sequence { get; init; }        // application sequence number
    public DateTimeOffset Received { get; init; } // when the fragment was decoded
}

public readonly record struct HeaderInfo
{
    public GroupVar GV { get; init; }  // the group and variation it arrived under
    public Kind Kind { get; init; }    // static or event
    public Class Class { get; init; }  // the event class, when it came from a class poll
    public bool IsEvent { get; }
}
```

Consumers need `HeaderInfo` more often than it looks: the same analog point read
as a static value and received as an event mean different things to a historian,
and only the group tells them apart.

## ChannelHandler

```csharp
public sealed class ChannelHandler : NopHandler
{
    public ChannelHandler(int buffer = 256);   // buffer <= 0 uses 256
    public ChannelReader<Update> Updates { get; }
    public ulong Dropped { get; }
}

public readonly record struct Update
{
    public HeaderInfo Info { get; init; }
    public ResponseInfo Fragment { get; init; }

    public PointType Type { get; init; } // selects which measurement property below is meaningful
    public ushort Index { get; init; }

    public Binary Binary { get; init; }
    public DoubleBitBinary DoubleBit { get; init; }
    public Counter Counter { get; init; }
    public FrozenCounter FrozenCounter { get; init; }
    public Analog Analog { get; init; }
    public BinaryOutputStatus BinaryOutput { get; init; }
    public AnalogOutputStatus AnalogOutput { get; init; }
    public byte[]? OctetString { get; init; }
}
```

This is what a terminal UI or a recorder consumes: the session loop stays
responsive because it only ever does a non-blocking write, and the consumer reads
at its own pace with `await foreach (var u in handler.Updates.ReadAllAsync(ct))`.

**Updates are dropped rather than blocking the session when the consumer falls
behind**, and the drop is counted — a stalled UI must not stall the protocol.
Check `Dropped` if you care whether you have a complete picture.

## MasterStats

```csharp
public record struct MasterStats
{
    public ulong TasksRun;
    public ulong TasksSucceeded;
    public ulong TasksFailed;
    public ulong ResponseTimeouts;
    public ulong FragmentsRx;
    public ulong Unsolicited;
    public ulong Connections;
    public ulong RestartsSeen;
}
```

---

# Namespace SharpDnp3.Outstation

The device that holds measurements, answers a master's polls, and executes its
commands.

## OutstationSession

```csharp
public sealed partial class OutstationSession
{
    public OutstationSession(OutstationConfig config,
                             IOutstationApplication? application = null,
                             ICommandHandler? commandHandler = null);

    public Task RunAsync(IChannel channel, CancellationToken cancellationToken = default);

    public void Update(Action<Database> action);
    public Database Database { get; }
    public EventBuffer? Events { get; }
    public int MastersAttached { get; }
    public void Restart();
    public OutstationStats Stats { get; }
}
```

A null `IOutstationApplication` uses `NopApplication`. **A null `ICommandHandler`
uses `RejectingCommandHandler`, which refuses every control** — an outstation
whose controls are not wired up must say so rather than silently report success.

`Update` applies the action holding the database's lock and is safe to call from
anywhere. An update queued before a request arrives is applied before that
request is answered, so a master that polls after your application reports a
change is told about the change. Batching related changes in one call is what makes a breaker opening
and its alarm asserting produce one consistent set of events rather than a torn
read:

```csharp
outstation.Update(db =>
{
    db.UpdateBinary(0, new Binary(true, Flags.Online, Timestamp.Now(DateTimeOffset.UtcNow)));
    db.UpdateAnalog(3, new Analog(11.2, Flags.Online, Timestamp.Now(DateTimeOffset.UtcNow)));
});
```

`Database` returns the database directly. It is **not safe for concurrent use** —
prefer `Update` for modifications once the session is running. Reading or
configuring it before `RunAsync` starts is fine, and is where point configuration
belongs.

`Restart()` makes the outstation report a restart to its masters. It is what a
device calls when it has genuinely restarted and what a simulator calls to
produce the condition on demand. The restart indication is the only signal that
tells a master its whole picture is stale: the event history is gone, so no
incremental poll can recover it and only a full re-baseline will do. Every
attached master is told, and so is any that attaches before one of them clears
it.

### Serving several masters

`MaxMasters` above one makes one session serve that many masters at once over a
listening channel, running a conversation per connection against one database:

```csharp
var outstation = new OutstationSession(new OutstationConfig
{
    LocalAddr = 10,
    RemoteAddr = 1,
    MaxMasters = 4,
    Events = new EventBufferConfig { MaxEvents = 1000 },
});

await outstation.RunAsync(new TcpServerChannel(":20000"), token);
```

Shared between the masters: the database, the clock, the command handler, the
application, and the counters in `Stats`. Private to each: the event queue,
application sequence numbers, the select-before-operate reservation, which
classes it has enabled for unsolicited reporting, and the internal indications
it is owed. None of those mean anything across a connection — a select from the
control centre must not be operable from the engineering workstation, and events
one master acknowledges must still reach the other.

Four consequences worth knowing before you turn it on:

- **Each master costs an event queue of `Events.MaxEvents` entries.** Size the
  two together.
- **`Events` returns the sole master's queue** when one is attached, and the
  queue events accumulate in while none is. With several attached there is no
  single answer and it returns the empty unattached queue; read
  `MastersAttached` before drawing conclusions from it.
- **Events raised while nothing is attached go to the next master to attach**,
  which is what makes a reconnecting master see what it missed. A master joining
  an outstation that already has one starts with an empty queue and re-baselines
  from its integrity poll.
- **A master arriving past the limit is accepted and immediately disconnected**,
  logged, and counted in `Stats.MastersRefused`. Leaving the connection open and
  unserved would look to both sides like an outstation that had gone mute.

Requests are processed one at a time across all masters, so `ICommandHandler`
and `IOutstationApplication` are called the same way they are with one master
and need no locking of their own. The same fact is the cost: a handler that
blocks stalls every master, not just the one that called it.

## OutstationConfig

```csharp
public sealed class OutstationConfig
{
    public ushort LocalAddr { get; set; }  // this outstation's link address
    public ushort RemoteAddr { get; set; } // the master's

    public DatabaseConfig Database { get; set; }
    public EventBufferConfig Events { get; set; } // sizes the queue each attached master holds

    public int MaxMasters { get; set; }    // default 1; above one needs a listening channel

    public int MaxTxFragment { get; set; } // default 2048
    public int MaxRxFragment { get; set; } // default 2048

    public TimeSpan ConfirmTimeout { get; set; } // default 5s; wait for an application confirm before requeueing events
    public TimeSpan SelectTimeout { get; set; }  // default 5s; how long a select reservation stays valid

    public UnsolicitedConfig Unsolicited { get; set; }

    public bool UseLinkConfirms { get; set; }
    public int LinkRetries { get; set; }
    public TimeSpan LinkTimeout { get; set; } // default 1s

    public IDnp3Logger? Log { get; set; }     // null discards
}

public sealed class UnsolicitedConfig
{
    public bool Enabled { get; set; }             // the device-level switch: the outstation is capable of it at all
    public TimeSpan HoldTime { get; set; }        // wait this long after an event so a burst becomes one response
    public int MaxEvents { get; set; }            // transmit at this many queued events regardless of hold time; zero means no threshold
    public TimeSpan ConfirmTimeout { get; set; }  // default 5s
    public int MaxRetries { get; set; }           // default 3
}
```

`Enabled` alone does not start unsolicited reporting: the master still has to
enable the individual classes with ENABLE_UNSOLICITED. After `MaxRetries`
unconfirmed re-sends the outstation gives up and waits for the master to poll
instead.

## Database

```csharp
public sealed class Database
{
    public Database(DatabaseConfig config, EventBuffer? events = null);
    public EventBuffer? Events { get; }
}

public sealed class DatabaseConfig
{
    public int Binary { get; set; }
    public int DoubleBitBinary { get; set; }
    public int Counter { get; set; }
    public int FrozenCounter { get; set; }
    public int Analog { get; set; }
    public int BinaryOutputStatus { get; set; }
    public int AnalogOutputStatus { get; set; }
    public int OctetString { get; set; }

    public Class DefaultClass { get; set; } // applied to every point at construction
}
```

Each count is the number of points of that type, indexed `0..n-1`.

```csharp
public void UpdateBinary(ushort index, Binary v);
public void UpdateDoubleBit(ushort index, DoubleBitBinary v);
public void UpdateCounter(ushort index, Counter v);
public void UpdateFrozenCounter(ushort index, FrozenCounter v);
public void UpdateAnalog(ushort index, Analog v);
public void UpdateBinaryOutputStatus(ushort index, BinaryOutputStatus v);
public void UpdateAnalogOutputStatus(ushort index, AnalogOutputStatus v);
public void UpdateOctetString(ushort index, ReadOnlySpan<byte> v);
```

An update generates an event when the value or its quality changed and the point
is assigned to an event class. For analogs and counters the deadband applies —
**and the comparison is against the value last *reported*, not the value last
stored.** Comparing against the stored value lets a point drift indefinitely in
deadband-sized steps without ever reporting, which is the classic implementation
bug and one that hides a slow ramp toward a limit.

Octet strings are unusual: the variation number *is* the length, so changing the
string's length changes the variation the outstation reports it in. That is
legal, and masters must cope with it. `UpdateOctetString` takes a span, so
`db.UpdateOctetString(0, "SHARPDNP3 RTU"u8)` works without allocating.

```csharp
public bool TryGetBinary(ushort index, out Binary value, out PointConfig config);
public bool TryGetDoubleBit(ushort index, out DoubleBitBinary value, out PointConfig config);
public bool TryGetCounter(ushort index, out Counter value, out PointConfig config);
public bool TryGetFrozenCounter(ushort index, out FrozenCounter value, out PointConfig config);
public bool TryGetAnalog(ushort index, out Analog value, out PointConfig config);
public bool TryGetBinaryOutputStatus(ushort index, out BinaryOutputStatus value, out PointConfig config);
public bool TryGetAnalogOutputStatus(ushort index, out AnalogOutputStatus value, out PointConfig config);
public bool TryGetOctetString(ushort index, out byte[] value, out PointConfig config);
```

The return value reports whether the index exists.

```csharp
public bool Configure(PointType pt, ushort index, PointConfig config);
public void AssignClass(PointType pt, Class cls);
public void FreezeCounters();
public DatabaseConfig Counts();
```

`Configure` is a no-op returning false for an index the database does not have,
so a configuration file listing a removed point does not bring the outstation
down at startup. `AssignClass` sets the class of every point of a type, which is
what the ASSIGN_CLASS function code does. `FreezeCounters` copies every counter
into its frozen counterpart.

### PointConfig

```csharp
public record struct PointConfig
{
    public Class Class { get; set; }          // Class.None suppresses the point's events entirely
    public byte StaticVariation { get; set; } // used when reported in a class 0 or range read
    public byte EventVariation { get; set; }  // used when the point's events are reported
    public double Deadband { get; set; }      // ignored for binaries, which event on any change
}
```

**`Configure` replaces the whole `PointConfig`.** A zero `StaticVariation` or
`EventVariation` falls back to the point's existing value, but a zero `Class` is
`Class.None` and a zero `Deadband` is zero — so setting one field by passing a
fresh struct silently switches the point's events off. Read the current config
back first:

```csharp
if (db.TryGetAnalog(0, out _, out var cfg))
{
    cfg.StaticVariation = 5; // g30v5, single precision with flags
    cfg.Deadband = 0.5;
    db.Configure(PointType.Analog, 0, cfg);
}
```

Defaults, chosen as the widest lossless encoding for each type:

| Type | Static | Event |
| --- | --- | --- |
| Binary | g1v2 (with flags) | g2v2 (absolute time) |
| DoubleBitBinary | g3v2 | g4v2 |
| Counter | g20v1 (32-bit, flags) | g22v5 (with time) |
| FrozenCounter | g21v1 | g23v5 |
| Analog | g30v1 (32-bit, flags) | g32v3 (32-bit, time) |
| BinaryOutputStatus | g10v2 | g11v2 |
| AnalogOutputStatus | g40v1 | g42v3 |

The analog defaults are 32-bit **integer** variations. A point that needs
fractions must be configured for a float variation — see
[Variations and precision](user-guide.md#variations-and-precision).

## IOutstationApplication

```csharp
public interface IOutstationApplication
{
    DateTimeOffset Now();                       // the outstation's idea of now; tests inject a virtual clock here
    bool WriteAbsoluteTime(DateTimeOffset t);   // a master set the clock; false rejects
    TimeSpan ColdRestart();                     // how long the device expects to be unavailable
    TimeSpan WarmRestart();
    bool SupportsWriteTime();                   // whether clock writes are accepted at all
}

public class NopApplication : IOutstationApplication; // usable defaults; every method virtual
```

The returned restart duration is reported back to the master in a group 52 time
delay.

## ICommandHandler

```csharp
public interface ICommandHandler
{
    CommandStatus SelectCrob(ushort index, ControlRelayOutputBlock c);
    CommandStatus OperateCrob(ushort index, ControlRelayOutputBlock c, OperateType op);

    CommandStatus SelectAnalog(ushort index, AnalogOutputCommand v);
    CommandStatus OperateAnalog(ushort index, AnalogOutputCommand v, OperateType op);
}

public sealed class RejectingCommandHandler : ICommandHandler; // refuses everything with NOT_SUPPORTED
```

**Select must not operate anything.** It reports whether the outstation *would*
accept the command. Operate is called for OPERATE, DIRECT_OPERATE and
DIRECT_OPERATE_NR, and is the call that actually moves the plant.

Both are called from the session loop, so a slow handler stalls the protocol.
Anything slow belongs behind a queue — but note that returning success before the
operation completes is a claim the outstation cannot take back.

```csharp
public enum OperateType : byte
{
    Direct = 0,   // DIRECT_OPERATE: no prior select, answer with the outcome
    DirectNoAck,  // DIRECT_OPERATE_NR: no response at all
    Selected,     // OPERATE following a successful SELECT
}

public readonly record struct AnalogOutputCommand(double Value, byte Variation);
// Variation 1: int32, 2: int16, 3: float32, 4: float64
```

The distinction matters to an implementation that logs or authorises controls: a
direct operate arrived with nothing reserved, and a no-reply operate will get no
acknowledgement whatever the outcome. A handler that does not care can read
`Value` and ignore `Variation`.

## EventBuffer

```csharp
public sealed class EventBuffer
{
    public const int DefaultMaxEvents = 1000;

    public EventBuffer(EventBufferConfig config);

    public void Add(Event e);
    public List<Event> Select(Class mask, int limit);
    public int Confirm();
    public int Unselect();
    public int Count(Class mask);
    public int Total { get; }
    public int SelectedCount { get; }
    public Class Classes();
    public bool Overflowed { get; }
    public void ClearOverflow();
    public void Reset();
}

public sealed class EventBufferConfig
{
    public int MaxEvents { get; set; } // total capacity across all classes; zero uses DefaultMaxEvents
}
```

The lifecycle is the part that matters and the part implementations get wrong: an
event is queued, then **selected** when it goes into a response, and only
**removed** when the master confirms that response. An outstation that drops
events at transmission loses exactly the data a sequence-of-events record exists
to preserve — and loses it silently, because the master has no way to know an
event it never saw was sent. `Unselect` is what happens when a confirmation does
not arrive: the events go back on the queue and are re-sent.

On overflow the **oldest** event is discarded, not the newest: after an overflow
the master's picture is already incomplete, and the recent past is what an
operator needs. The overflow is latched so it can be reported in the internal
indications, which is the only way the master learns there is a hole in its
record.

The session drives all of this. You normally only read the counters.

Each attached master holds its own buffer, because selection and confirmation
are per-master: one shared between them would let the first master to poll take
the events and leave the second never knowing they happened.

```csharp
public record struct Event
{
    public PointType Type { get; set; } // selects which measurement property is meaningful
    public ushort Index { get; set; }
    public Class Class { get; set; }
    public byte Variation { get; set; }
    public Timestamp Time { get; set; }

    public Binary Binary { get; set; }
    public DoubleBitBinary DoubleBit { get; set; }
    public Counter Counter { get; set; }
    public FrozenCounter FrozenCounter { get; set; }
    public Analog Analog { get; set; }
    public BinaryOutputStatus BinaryOutput { get; set; }
    public AnalogOutputStatus AnalogOutput { get; set; }
    public byte[]? OctetString { get; set; }
}
```

A tagged struct rather than a class hierarchy keeps events off the heap in the
buffer, which matters when a storm queues thousands per second.

## OutstationStats

```csharp
public record struct OutstationStats
{
    public ulong RequestsReceived;
    public ulong ResponsesSent;
    public ulong FragmentsSent;
    public ulong ConfirmsReceived;
    public ulong ConfirmTimeouts;
    public ulong UnknownFunction;
    public ulong MalformedRequests;
    public ulong Connections;

    public ulong CommandsExecuted;
    public ulong CommandsRejected;
    public ulong UnsolicitedSent;
    public ulong UnsolicitedTimeouts;

    public int MastersAttached;      // right now
    public int PeakMastersAttached;  // the most at once
    public ulong MastersRefused;     // turned away at the MaxMasters limit
}
```

The counters are the device's, not one master's: with several attached they are
the totals across all of them.

---

# Namespace SharpDnp3.Decoding

Turns DNP3 octets into a structured trace. It produces a tree, not log strings:
one consumer renders it to a log, one to a terminal UI, one to text for the
command-line decoder, and none of them re-implement any parsing.

```csharp
public sealed class Dnp3Decoder
{
    public Dnp3Decoder(Direction direction, IObjectSizer? sizer = null); // null uses the default sizer

    public void Feed(ReadOnlySpan<byte> data, Action<Trace> onTrace);
    public void Reset();
    public void SetSynchronized(bool value);
    public (LinkStats Link, TransportStats Transport) Stats { get; }

    public static bool TryDecodeFrame(IObjectSizer? sizer, ReadOnlySpan<byte> data,
                                      out Trace trace, out int consumed);
}
```

`Feed` invokes the callback for each frame found; octets that do not yet form a
complete frame are buffered until they do.

**One `Dnp3Decoder` belongs to one direction of one connection.** It holds link
and transport state, and feeding both directions into a single decoder would
interleave two independent transport sequences and produce nonsense. `Reset`
clears that state, as when a connection is re-established.

`SetSynchronized` records whether the outstation's clock is set, which decides
the quality stamped on decoded timestamps. Timestamps are treated as synchronized
until told otherwise; call this from a session that has seen NEED_TIME. An
offline tool has no way to know, and marking every timestamp in a capture as
unsynchronized would be a claim the octets do not support either way.

`TryDecodeFrame` is the one-shot form for offline tools: a frame pasted from a
capture is assumed to carry a complete fragment, which is true for the
single-frame messages that make up most DNP3 traffic. Multi-frame fragments need
a `Dnp3Decoder`.

```csharp
public enum Direction : byte { Unknown = 0, Tx, Rx }

public sealed class Trace
{
    public Direction Direction { get; init; }
    public LinkInfo Link { get; init; }
    public TransportInfo? Transport { get; init; }
    public AppInfo? App { get; init; }

    public ReadOnlyMemory<byte> Raw { get; init; } // the frame's octets as they appeared on the wire
    public string? Error { get; init; }            // set when the frame itself could not be decoded

    public void Render(StringBuilder b, bool showHex);
    public static string HexDump(ReadOnlySpan<byte> data);
}
```

A frame always yields link and transport information. It yields application
information only when it completed a fragment, since a fragment can span nine
frames and only the last one finishes it — so **check `trace.App is not null`**.

`Render` writes the layer tree, indented, so an operator can see at a glance
which layer a problem lives in.

```csharp
public readonly record struct LinkInfo
{
    public Control Control { get; init; }
    public ushort Dest { get; init; }
    public ushort Src { get; init; }
    public byte Length { get; init; }
    public int PayloadLen { get; init; } // user data the frame carried
    public int FrameSize { get; init; }  // total octets on the wire
}

public readonly record struct TransportInfo
{
    public TransportHeader Header { get; init; }
    public bool Complete { get; init; }
    public DiscardReason Discarded { get; init; }
}

public sealed class AppInfo
{
    public AppHeader Header { get; init; }
    public IReadOnlyList<ObjectHeader> Objects { get; init; }
    public IReadOnlyList<IReadOnlyList<Value>> Values { get; init; } // indexed to match Objects
    public string? Error { get; init; } // the fragment header parsed but the object headers did not
}
```

When `AppInfo.Error` is set, the headers decoded before the failure are still in
`Objects`: showing an operator what was understood before the corruption beats
showing nothing.

```csharp
public readonly record struct Value
{
    public ushort Index { get; init; }
    public PointType Type { get; init; }
    public string Text { get; init; }   // formatted, not typed
    public Flags Flags { get; init; }
    public Timestamp Time { get; init; }
}

public static class ValueDecoder
{
    public static bool TryDecodeValues(ObjectHeader h, Context ctx, out List<Value> values);
}
```

The value is held as formatted text because every consumer of this namespace — a
log line, a terminal table, a text dump — wants text. **Callers that need typed
measurements should use the object codecs directly**, or run a real
`MasterSession`.

---

# Namespace SharpDnp3.Objects

The group and variation codecs. Most of this namespace is generated from
`src/SharpDnp3/Objects/Spec/dnp3_objects.yaml`, the single source of truth for
every group, variation, size and field layout. The generated files are committed,
so consumers never run the generator.

Hand-written code covers what the table cannot express: bit-packed objects, whose
objects share octets, and commands, whose fields map onto purpose-built structs.

## Identifying an object

```csharp
public readonly record struct GroupVar(byte Group, byte Variation)
{
    public static GroupVar GV(byte group, byte variation);
    public ushort Key { get; }          // packed form used as a dictionary key
    public override string ToString();  // "g30v5"
}

public readonly record struct Descriptor
{
    public GroupVar GV { get; init; }
    public string Name { get; init; }
    public int Level { get; init; }
    public Kind Kind { get; init; }
    public PointType Measurement { get; init; }

    public int SizeBits { get; init; }  // under eight means bit-packed: objects share octets across a range
    public bool Packed { get; init; }

    public bool HasFlags { get; init; }
    public bool HasTime { get; init; }
    public bool RelativeTime { get; init; } // timestamp is an offset from a preceding g51 CTO object

    public int ValueBits { get; init; }
    public bool FloatValue { get; init; }   // IEEE 754 rather than an integer

    public bool TrySizeOctets(out int octets);
}

public static class ObjectRegistry
{
    public static IReadOnlyDictionary<GroupVar, Descriptor> All { get; }
    public static bool TryLookup(GroupVar gv, out Descriptor descriptor);
}
```

`ValueBits` and `FloatValue` are recorded rather than inferred from the variation
number, because the mapping is not consistent across groups: variation 3 is a
32-bit integer in group 30 and a single-precision float in group 40. An
outstation choosing which variation can carry a reading needs the real answer,
not a rule that happens to hold for one group.

`TrySizeOctets` returns false for packed objects: they are measured per range,
not per object.

```csharp
public enum Kind : byte
{
    Unknown = 0, Static, Event, Command, CommandEvent, Time,
    Class, Indication, Deadband, String, File, Attribute,
}
```

`Kind` is what lets a master decide whether a header names data, a command, or a
class to poll.

## Codecs

```csharp
public delegate T ParseObject<out T>(ReadOnlySpan<byte> buf, Context ctx);
public delegate void WriteObject<in T>(List<byte> dst, T value, Context ctx);

public readonly record struct Codec<T>(ParseObject<T> Parse, WriteObject<T> Write);

public static bool TryBinaryCodec(GroupVar gv, out Codec<Binary> codec);
public static bool TryDoubleBitCodec(GroupVar gv, out Codec<DoubleBitBinary> codec);
public static bool TryCounterCodec(GroupVar gv, out Codec<Counter> codec);
public static bool TryFrozenCounterCodec(GroupVar gv, out Codec<FrozenCounter> codec);
public static bool TryAnalogCodec(GroupVar gv, out Codec<Analog> codec);
public static bool TryBinaryOutputCodec(GroupVar gv, out Codec<BinaryOutputStatus> codec);
public static bool TryAnalogOutputCodec(GroupVar gv, out Codec<AnalogOutputStatus> codec);
```

`Parse` assumes the buffer holds at least the object's size. Callers get that
guarantee from the framing layer, which has already validated the header's length
arithmetic against the fragment — if you call a codec on bytes of your own, you
must check the length yourself.

```csharp
public readonly record struct Context
{
    public bool Synchronized { get; init; } // the outstation's clock was synchronised
    public DateTimeOffset Cto { get; init; }// common time of occurrence from the most recent g51 object
    public bool HasCto { get; init; }

    public Context WithCto(DateTimeOffset t);
    public Timestamp RelativeTime(ushort offsetMillis);
    public ushort RelativeOffset(Timestamp t);
    public TimestampQuality TimeQuality();
}
```

`Context` carries what a decoder needs that is not in the object itself. Both
things in it are properties of the session rather than of the octets.

## Commands and times

```csharp
public static class CommandObjects
{
    public const int CrobSize = 11;
    public const int AnalogOutput32Size = 5;    // g41v1
    public const int AnalogOutput16Size = 3;    // g41v2
    public const int AnalogOutputFloatSize = 5; // g41v3
    public const int AnalogOutputDoubleSize = 9;// g41v4
    public const int Time48Size = 6;

    public static void AppendCrob(List<byte> dst, ControlRelayOutputBlock c);
    public static ControlRelayOutputBlock ParseCrob(ReadOnlySpan<byte> buf);

    public static void AppendAnalogOutputInt16(List<byte> dst, AnalogOutputInt16 v);
    public static void AppendAnalogOutputInt32(List<byte> dst, AnalogOutputInt32 v);
    public static void AppendAnalogOutputFloat32(List<byte> dst, AnalogOutputFloat32 v);
    public static void AppendAnalogOutputFloat64(List<byte> dst, AnalogOutputFloat64 v);
    public static AnalogOutputInt16 ParseAnalogOutputInt16(ReadOnlySpan<byte> buf);
    public static AnalogOutputInt32 ParseAnalogOutputInt32(ReadOnlySpan<byte> buf);
    public static AnalogOutputFloat32 ParseAnalogOutputFloat32(ReadOnlySpan<byte> buf);
    public static AnalogOutputFloat64 ParseAnalogOutputFloat64(ReadOnlySpan<byte> buf);

    public static void AppendTime48(List<byte> dst, Timestamp t);
    public static Timestamp ParseTime48(ReadOnlySpan<byte> buf);
    public static uint ParseTimeDelay(byte variation, ReadOnlySpan<byte> buf); // always milliseconds
}
```

`ParseTimeDelay` handles group 52: variation 1 counts seconds and variation 2
counts milliseconds, and both are returned in milliseconds so callers need not
care — which is the whole reason the two variations exist separately on the wire.

## Packed objects

```csharp
public static class PackedObjects
{
    public static void AppendPackedBinary(List<byte> dst, IReadOnlyList<bool> values);
    public static void AppendPackedDoubleBit(List<byte> dst, IReadOnlyList<DoubleBit> values);
    public static void ParsePackedBinary(ReadOnlySpan<byte> buf, int count, List<Binary> output);
    public static void ParsePackedBinaryOutput(ReadOnlySpan<byte> buf, int count, List<BinaryOutputStatus> output);
    public static void ParsePackedDoubleBit(ReadOnlySpan<byte> buf, int count, List<DoubleBitBinary> output);
    public static int PackedOctets(int count, int bitsPerObject);
}
```

`ParsePackedBinary` serves group 1 variation 1, group 10 variation 1 and group 80
variation 1, which share an encoding. **Packed variations carry no quality
information, so every value comes back online** — the encoding has nowhere to say
otherwise.

`ObjectConvert` holds the little-endian append/parse helpers the codecs are built
from, including `ClampInt16`/`ClampInt32`, which is how an outstation narrows a
`double` into an integer variation without wrapping.

---

# Namespaces SharpDnp3.App, SharpDnp3.Link, SharpDnp3.Transport

The layer primitives. They are public because the decoder's trace exposes them
and because a conformance test needs to build a fragment by hand — not because an
application is expected to use them. If you are naming these types in application
code, check first that a session method does not already do what you want.

```csharp
// SharpDnp3.App
public enum FuncCode : byte { Confirm = 0, Read = 1, Write = 2, Select = 3, Operate = 4,
                              DirectOperate = 5, DirectOperateNR = 6, /* … */ Response = 129,
                              UnsolicitedResponse = 130, AuthResponse = 131 }
public readonly record struct AppControl(bool Fir, bool Fin, bool Con, bool Uns, byte Seq);
public readonly record struct AppHeader(AppControl Control, FuncCode Func, Iin Iin);
public readonly record struct ObjectHeader;   // group, variation, qualifier, range, payload slice
public readonly record struct ObjectRange;    // start/stop or count
public readonly record struct Qualifier;      // index prefix + range spec
public enum IndexPrefix : byte { None, Index1, Index2, Index4, Size1, Size2, Size4, Reserved }
public enum RangeSpec : byte { StartStop8, StartStop16, StartStop32, Virtual8, Virtual16,
                               Virtual32, AllObjects, Count8, Count16, Count32, ReservedA, Variable }
public enum AppParseStatus { Ok, ShortFragment, Truncated, BadQualifier, BadRange, UnknownObject, FragmentTooLarge }
public interface IObjectSizer { bool TrySizeBits(byte group, byte variation, out int bits); }
public static class AppConstants { public const int DefaultMaxFragment = 2048; /* … */ }

// SharpDnp3.Link
public readonly record struct Control(bool Dir, bool Prm, bool Fcb, bool Fcv, LinkFunction Func);
public readonly record struct LinkHeader(Control Control, ushort Dest, ushort Src, byte Length);
public enum LinkFunction : byte { ResetLinkStates = 0, Nack = 1, TestLinkStates = 2,
                                  ConfirmedUserData = 3, UnconfirmedUserData = 4,
                                  RequestLinkStatus = 9, LinkStatus = 11, NotSupported = 15 }
public enum LinkDecodeStatus { Ok, ShortFrame, BadStart, BadLength, HeaderCrc, BodyCrc, PayloadTooLong }
public record struct LinkStats { public ulong FramesDecoded, HeaderCrcErrors, BodyCrcErrors,
                                 BadLength, BytesDiscarded, Resyncs; }
public static class LinkConstants { public const int MaxFrameSize = 292; public const int MaxPayload = 250;
                                    public const ushort SelfAddress = 0xFFFC;
                                    public static bool IsBroadcast(ushort address); /* … */ }

// SharpDnp3.Transport
public readonly record struct TransportHeader(bool Fir, bool Fin, byte Seq);
public enum DiscardReason { None, EmptySegment, NoFir, UnexpectedFir, BadSequence, Overflow }
public record struct TransportStats { public ulong SegmentsReceived, SegmentsDiscarded, FragmentsCompleted;
                                      public ulong Discarded(DiscardReason reason); }
public static class TransportConstants { public const int MaxSegmentPayload = 249; /* … */ }
```

Each of these enums has a `ToDisplayString()` extension that renders the
protocol's own spelling, which is what the decoder prints.

---

## See also

- [User guide](user-guide.md) — how to build things with this
- [`SKILL.md`](../SKILL.md) — the same ground, condensed for AI coding agents
- [The example tools](../src/README.md) — four working programs built on this API
