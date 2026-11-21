# Connection hardening and security events

SharpDnp3 exposes the connection hardening and security reporting introduced
in go-dnp3 v0.9.0 through the `SharpDnp3.Security` namespace. These settings
are opt-in. A master, outstation, and multidrop bus can each have its own
policy or share one dispatcher.

```csharp
using System.Net;
using SharpDnp3.Channels;
using SharpDnp3.Outstation;
using SharpDnp3.Security;

await using var events = new SecurityDispatcher(
    SecurityEventSinks.JsonLines(Console.Error));

var outstation = new OutstationSession(new OutstationConfig
{
    LocalAddr = 10,
    RemoteAddr = 1,
    IdleTimeout = TimeSpan.FromMinutes(5),
    FrameAssemblyTimeout = TimeSpan.FromSeconds(5),
    WriteTimeout = TimeSpan.FromSeconds(5),
    Security = new SecurityPolicy
    {
        Dispatcher = events,
        AllowedPeers = [IPNetwork.Parse("192.0.2.0/24")],
        RejectUnlisted = true,
        AllowedMasters = new HashSet<ushort> { 1 },
        RejectUnexpectedMaster = true,
        Functions = new FunctionPolicy { ReadOnly = true, AllowTimeWrite = true },
    },
});

using var channel = new TcpServerChannel(":20000") { Busy = BusyPolicy.Reject };
await outstation.RunAsync(channel, cancellationToken);
```

Replace the example subnet and DNP3 addresses with those of your installation.
A policy with a sink and no rejection or throttling settings reports
observations while normal protocol processing continues.

## Connections

| Setting | Behavior |
| --- | --- |
| `OutstationConfig.IdleTimeout` | Sends a link-status probe after a period without an admitted frame. An unanswered probe ends the association. |
| `OutstationConfig.FrameAssemblyTimeout` | Ends an association that leaves a link frame incomplete past the configured duration. |
| Master and outstation `WriteTimeout` | Bounds a write and flush on the channel. A timeout ends the association so the session can reconnect. Custom streams must honor write cancellation. |
| TCP and TLS server `BusyPolicy.Reject` | Immediately closes extra connections while a connection is active. Use this on a single-master listener. The default keeps connections in the listener backlog. |
| `UdpConfig.PinPeer` | Accepts datagrams only from the configured endpoint or the first learned endpoint. `UdpChannel.Stats` counts foreign and dropped sources. |
| `TlsServerChannel.HandshakeTimeout` | Bounds an accepted peer's TLS handshake. |
| `Dnp3TlsConfig.VerifyPeer` | Applies an application check after the existing certificate verification. A false return refuses the peer. |

The session timeouts above are disabled at zero. Policy-based IP admission
runs before TCP and TLS connections enter a session and before TLS authentication.
For UDP it runs before a datagram can establish the peer. Link-address
admission is applied to outstation primary frames before they reach the link
state machine. `FunctionPolicy` and `CommandGuard` can refuse an application
operation before its handler runs.

Set `SecurityPolicy.FloodAction` to change the response to a configured
threshold. The default reports it. Throttling returns a command status for
individual controls and a request error for other throttleable requests.
Link status, link resets, and authentication are reported and keep flowing.

## Events and peer identity

Event kinds use the dotted names shared with go-dnp3. A
`SecurityDispatcher` queues observations on a bounded channel and delivers
them on a separate task. It aggregates repeated observations within its
configured time window, reports drops, and isolates sink exceptions. A
session disposes a dispatcher it created from its own `Sink`. If you pass a
shared `Dispatcher`, dispose it after its last session ends to flush its queue.

A sample is bounded by `SecurityPolicy.MaxRaw`. Use a negative value to
disable samples. SharpDnp3 omits samples that may contain Secure
Authentication material or file credentials, and reports link anomalies with
frame metadata and counters. Certificate metadata can include the
fingerprint, issuer, expiry, and negotiated TLS parameters.

MAC lookup reads the local neighbor table on Linux and Windows. On macOS
and FreeBSD it uses the system `arp` and `ndp` utilities. Applications can
provide `SecurityPolicy.Resolver` for other platforms or for an authoritative
inventory. `SecurityPeerCache` can retain peer history across connections.
Use `Save` and `LoadAsync` to persist it. `PeerBindings` can enforce known
IP-to-MAC mappings, and `MacRecheckInterval` can detect a change during a
connection. `DisconnectOnMacChange` opts into ending a connection on a
detected change. For off-link IPv4 peers, an unambiguous default gateway may
appear as `mac_source: gateway`. Pinning uses the peer's own MAC.

TLS failure detail varies with the operating system's TLS backend. All
rejected or timed-out handshakes are reported when a sink is configured.
