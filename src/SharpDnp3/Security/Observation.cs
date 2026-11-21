// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using SharpDnp3.App;
using SharpDnp3.Stack;
using SharpDnp3.Channels;
using SharpDnp3.Link;

namespace SharpDnp3.Security;

internal sealed class SecurityRuntime : IAsyncDisposable
{
    private static long _nextConnection;
    private readonly SecurityDispatcher? _owned;
    private readonly SecurityDispatcher? _dispatch;
    private readonly Lock _gate = new();
    private readonly Dictionary<IPAddress, Bucket> _accept = new();
    private readonly ConditionalWeakTable<Stream, SecurityObservation> _connections = new();
    private readonly Bucket _globalAccept = new();
    private readonly Dictionary<string, (long Time, long? Connection)> _duplicates = new();

    public SecurityRuntime(SecurityPolicy policy, string role)
    {
        policy.Validate();
        Policy = policy;
        Role = role;
        _owned = policy.Dispatcher is null && policy.Sink is { } sink
            ? new SecurityDispatcher(sink, policy.DispatcherOptions)
            : null;
        _dispatch = policy.Dispatcher ?? _owned;
    }

    public SecurityPolicy Policy { get; }
    public bool HasSink => _dispatch is not null;
    public string Role { get; }

    public SecurityObservation ForConnection(IChannel channel, Stream stream) =>
        _connections.GetValue(stream, connection => new(this, TransportOf(channel),
            Interlocked.Increment(ref _nextConnection),
            () => connection is IPeerEndpoint peer ? peer.SecurityPeer : null));

    public void Bind(Stream stream, SecurityObservation? observation)
    {
        if (observation is not null)
        {
            _connections.Add(stream, observation);
        }
    }

    public void Transfer(Stream stream, Stream wrapper)
    {
        if (_connections.TryGetValue(stream, out var observation))
        {
            Bind(wrapper, observation);
        }
    }

    public static Task<Stream> ConnectAsync(IChannel channel, SecurityRuntime? security, CancellationToken token) =>
        channel is IObservedChannel observed ? observed.ConnectAsync(security, token) : channel.ConnectAsync(token);

    public SecurityObservation ForPeer(string transport, IPEndPoint? peer) =>
        new(this, transport, Interlocked.Increment(ref _nextConnection),
            () => peer is null ? null : new SecurityPeer { Network = transport, Remote = peer.ToString() });

    public static async ValueTask<bool> IdentifyAsync(SecurityObservation? observation, Stream stream, CancellationToken token)
    {
        if (observation is null)
        {
            return true;
        }

        try
        {
            if (await observation.IdentifyAsync(token).ConfigureAwait(false))
            {
                return true;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        stream.Dispose();
        return false;
    }

    public bool Admit(SecurityObservation observation, IPEndPoint endpoint, bool countAccept)
    {
        var address = endpoint.Address;
        var allowed = Policy.Allows(address);
        if (!allowed)
        {
            observation.Emit(new(SecurityEventKind.ConnUnexpected)
            {
                Action = Policy.RejectUnlisted ? SecurityAction.Rejected : SecurityAction.Observed,
                Peer = observation.Peer ?? new SecurityPeer { Remote = endpoint.ToString() },
            });

            if (Policy.RejectUnlisted)
            {
                return false;
            }
        }

        if (!countAccept)
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        bool over;
        lock (_gate)
        {
            if (!_accept.TryGetValue(address, out var bucket))
            {
                if (_accept.Count >= 1024)
                {
                    _accept.Remove(_accept.MinBy(pair => pair.Value.Last).Key);
                }

                _accept.Add(address, bucket = new Bucket());
            }

            over = bucket.Over(Policy.AcceptRate, 1) | _globalAccept.Over(Policy.GlobalAcceptRate, 1);
        }

        if (over)
        {
            observation.Emit(new(SecurityEventKind.ConnRate)
            {
                Action = Policy.RejectOverRate ? SecurityAction.Rejected : SecurityAction.Observed,
            });
        }

        return !over || !Policy.RejectOverRate;
    }

    public bool Duplicate(ReadOnlySpan<byte> data, long? connection)
    {
        if (_dispatch is null || Policy.DuplicateWindow <= TimeSpan.Zero)
        {
            return false;
        }

        var key = Convert.ToHexString(SHA256.HashData(data));
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var duplicate = _duplicates.TryGetValue(key, out var prior) &&
                prior.Connection != connection && Stopwatch.GetElapsedTime(prior.Time, now) <= Policy.DuplicateWindow;

            if (_duplicates.Count >= 1024 && !_duplicates.ContainsKey(key))
            {
                _duplicates.Remove(_duplicates.MinBy(pair => pair.Value.Time).Key);
            }

            _duplicates[key] = (now, connection);
            return duplicate;
        }
    }

    public void Submit(SecurityEvent observation) => _dispatch?.Submit(observation);

    public ValueTask DisposeAsync() => _owned?.DisposeAsync() ?? ValueTask.CompletedTask;

    internal static string TransportOf(IChannel channel) => channel switch
    {
        TlsClientChannel or TlsServerChannel => "tls",
        TcpClientChannel or TcpServerChannel => "tcp",
        UdpChannel => "udp",
        SerialChannel => "serial",
        _ when channel.ToString()?.StartsWith("pipe", StringComparison.Ordinal) == true => "pipe",
        _ => channel.ToString()?.Split(' ', 2)[0] ?? "unknown",
    };
}

internal sealed class SecurityObservation(
    SecurityRuntime runtime, string transport, long? id, Func<SecurityPeer?> peer)
{
    private readonly Dictionary<string, Bucket> _buckets = new();
    private readonly HashSet<string> _scan = new();
    private SecurityNeighbor? _neighbor;
    private string? _interface;
    private IPAddress? _address;
    private int _link = -1;
    private readonly DateTimeOffset _connectedAt = DateTimeOffset.UtcNow;
    private long _scanStart = Stopwatch.GetTimestamp();
    private long? _queueFullSince;
    private bool _queueFullReported;

    internal Func<SecurityPeer?> PeerSource { get; set; } = peer;

    public SecurityPolicy Policy => runtime.Policy;
    public bool HasSink => runtime.HasSink;
    public SecurityPeer? Peer
    {
        get
        {
            if (PeerSource() is not { } remote)
            {
                return null;
            }

            var neighbor = Volatile.Read(ref _neighbor);
            var link = Volatile.Read(ref _link);
            return remote with
            {
                Mac = neighbor?.Address.ToString(),
                MacSource = neighbor?.Source,
                Interface = neighbor?.Interface ?? _interface,
                OnLink = _interface is not null ? true : null,
                LinkAddr = link >= 0 ? (ushort)link : null,
                ConnectedAt = _connectedAt,
            };
        }
    }

    public bool Disconnect { get; private set; }
    public bool SawData { get; set; }
    public int ConfirmTimeouts { get; set; }

    public void Emit(SecurityEvent observation, ReadOnlySpan<byte> fragment = default, string? transportOverride = null)
    {
        if (!runtime.HasSink)
        {
            return;
        }

        var maximum = Policy.MaxRaw == 0 ? 64 : Policy.MaxRaw;
        var raw = maximum > 0 && !fragment.IsEmpty && !SecretBearing(fragment)
            ? fragment[..Math.Min(maximum, fragment.Length)].ToArray()
            : null;
        runtime.Submit(observation with
        {
            Role = runtime.Role,
            Transport = transportOverride ?? transport,
            ConnId = id,
            Peer = (observation.Peer ?? Peer) is { } current
                ? current with { Network = current.Network ?? transport } : null,
            Raw = raw,
        });
    }

    public bool Duplicate(ReadOnlySpan<byte> data) => runtime.Duplicate(data, id);

    public async ValueTask<bool> IdentifyAsync(CancellationToken token, IPEndPoint? supplied = null)
    {
        var endpoint = supplied;
        if (endpoint is null && !IPEndPoint.TryParse(PeerSource()?.Remote ?? "", out endpoint))
        {
            return true;
        }

        var address = endpoint.Address;
        address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (!Policy.DisableMacLookup)
        {
            _interface = SystemNeighbors.OnLink(address);
            _neighbor = await ResolveAsync(address, token).ConfigureAwait(false);
            if (_interface is not null && _neighbor is null)
            {
                Emit(new(SecurityEventKind.PeerMACUnavailable));
            }
        }

        Volatile.Write(ref _address, address);
        RememberPeer();
        if (Policy.PeerBindings is not { } bindings || !bindings.TryGetValue(address, out var pinned) ||
            _neighbor is { Source: not "gateway" } accepted && pinned.Equals(accepted.Address))
        {
            return true;
        }

        var reject = Policy.RejectBindingMismatch;
        Emit(new(SecurityEventKind.PeerMACPinningViolation)
        {
            Action = reject ? SecurityAction.Rejected : SecurityAction.Observed,
        });
        if (reject)
        {
            Emit(new(SecurityEventKind.ConnUnexpectedMAC) { Action = SecurityAction.Rejected });
        }

        return !reject;
    }

    public async Task RecheckAsync(CancellationTokenSource connection)
    {
        if (Policy.DisableMacLookup || Policy.MacRecheckInterval <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            using var timer = new PeriodicTimer(Policy.MacRecheckInterval);
            while (await timer.WaitForNextTickAsync(connection.Token).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _address) is not { } address)
                {
                    continue;
                }

                var neighbor = await ResolveAsync(address, connection.Token).ConfigureAwait(false);
                if (neighbor is null)
                {
                    continue;
                }

                var previous = Interlocked.Exchange(ref _neighbor, neighbor);
                if (previous is { Source: not "gateway" } && neighbor.Source != "gateway" &&
                    !previous.Address.Equals(neighbor.Address))
                {
                    var disconnect = Policy.DisconnectOnMacChange;
                    Emit(new(SecurityEventKind.PeerMACChangedMidsession)
                    {
                        Action = disconnect ? SecurityAction.Disconnected : SecurityAction.Observed,
                    });
                    if (disconnect)
                    {
                        connection.Cancel();
                        return;
                    }
                }

                RememberPeer();
            }
        }
        catch (OperationCanceledException) when (connection.IsCancellationRequested)
        {
        }
    }

    public void RememberLink(ushort link)
    {
        if (Interlocked.Exchange(ref _link, link) != link)
        {
            RememberPeer();
        }
    }

    private void RememberPeer()
    {
        if (Volatile.Read(ref _address) is not { } address)
        {
            return;
        }

        var cache = Policy.MacCache ?? SecurityPeerCache.Default;
        var link = Volatile.Read(ref _link);
        var neighbor = Volatile.Read(ref _neighbor);
        var mac = neighbor is { Source: not "gateway" } ? neighbor.Address.ToString() : null;
        foreach (var kind in cache.Observe(address, mac, PeerSource()?.Tls?.Fingerprint,
                     link >= 0 ? (ushort)link : null))
        {
            Emit(new(kind));
        }
    }

    private async ValueTask<SecurityNeighbor?> ResolveAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var gateway = Policy.Resolver is null && _interface is null;
        var destination = gateway ? SystemNeighbors.DefaultGateway(address) : address;
        if (destination is null)
        {
            return null;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (Policy.MacLookupTimeout > TimeSpan.Zero)
        {
            deadline.CancelAfter(Policy.MacLookupTimeout);
        }

        try
        {
            var resolve = Policy.Resolver ?? SystemNeighbors.ResolveAsync;
            var neighbor = await resolve(destination, deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
            if (neighbor?.Address.GetAddressBytes().Any(value => value != 0) != true)
            {
                return null;
            }

            return gateway ? neighbor with { Source = "gateway" } : neighbor;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Scan(string key)
    {
        if (Policy.ScanThreshold <= 0 || Policy.ScanWindow <= TimeSpan.Zero)
        {
            return;
        }

        if (Stopwatch.GetElapsedTime(_scanStart) >= Policy.ScanWindow)
        {
            _scanStart = Stopwatch.GetTimestamp();
            _scan.Clear();
        }

        if (_scan.Count < Policy.ScanThreshold && _scan.Add(key) && _scan.Count == Policy.ScanThreshold)
        {
            Emit(new(SecurityEventKind.AccessScan) { Count = _scan.Count });
        }
    }

    public bool SharedLink => Policy.LinkTopology == SecurityLinkTopology.SharedBus ||
        Policy.LinkTopology == SecurityLinkTopology.Auto && transport is "serial" or "multidrop";

    public void Link(string kind, LinkFrame frame, SecurityAction action = SecurityAction.Ignored,
        string? transportOverride = null) =>
        Emit(new(kind)
        {
            Action = action,
            LinkSrc = frame.Header.Src,
            LinkDest = frame.Header.Dest,
            LinkFunc = (byte)frame.Header.Control.Func,
        }, transportOverride: transportOverride);

    // Called by the read task at each enqueue. It also observes a wait for
    // capacity, so a slow session can be reported while its queue is blocked.
    public async ValueTask EnqueueAsync(
        System.Threading.Channels.Channel<byte[]> queue, byte[] chunk, CancellationToken token)
    {
        if (queue.Reader.Count < ReceiveQueue.Capacity / 2)
        {
            _queueFullSince = null;
            _queueFullReported = false;
        }

        var pending = queue.Writer.WriteAsync(chunk, token);
        if (!pending.IsCompleted)
        {
            var since = _queueFullSince ??= Stopwatch.GetTimestamp();
            if (!_queueFullReported)
            {
                var task = pending.AsTask();
                var remaining = TimeSpan.FromSeconds(1) - Stopwatch.GetElapsedTime(since);
                if (remaining > TimeSpan.Zero)
                {
                    try
                    {
                        await task.WaitAsync(remaining, token).ConfigureAwait(false);
                        return;
                    }
                    catch (TimeoutException)
                    {
                        // The underlying write remains queued.
                    }
                }

                if (!token.IsCancellationRequested)
                {
                    _queueFullReported = true;
                    Emit(new(SecurityEventKind.DoSRxBacklog));
                }

                await task.ConfigureAwait(false);
                return;
            }
        }

        await pending.ConfigureAwait(false);
    }

    public void ParserStats(LinkStats before, LinkStats after, string? transportOverride = null)
    {
        Report(SecurityEventKind.LinkCRCError,
            after.HeaderCrcErrors - before.HeaderCrcErrors + after.BodyCrcErrors - before.BodyCrcErrors);
        Report(SecurityEventKind.LinkBadLength, after.BadLength - before.BadLength);
        Report(SecurityEventKind.LinkGarbage, after.BytesDiscarded - before.BytesDiscarded);

        void Report(string kind, ulong count)
        {
            if (count != 0)
            {
                Emit(new(kind) { Action = SecurityAction.Ignored, Count = (int)Math.Min(count, int.MaxValue) },
                    transportOverride: transportOverride);
            }
        }
    }

    public void Application(
        string kind, Received received, AppHeader header, SecurityAction action = SecurityAction.Observed,
        ObjectHeader? obj = null, string? detail = null) =>
        Emit(new(kind)
        {
            Action = action,
            LinkSrc = received.Source,
            LinkDest = received.Dest,
            AppFunc = header.Func.ToDisplayString(),
            AppSeq = header.Control.Seq,
            Iin = header.IsResponse ? header.Iin.Value : null,
            Group = obj?.Group,
            Variation = obj?.Variation,
            Qualifier = obj?.Qualifier.Value,
            Detail = detail,
        }, received.Fragment.Span);

    public bool Over(string kind, SecurityRate rate, int cost = 1)
    {
        if (!_buckets.TryGetValue(kind, out var bucket))
        {
            _buckets.Add(kind, bucket = new Bucket());
        }

        return bucket.Over(rate, cost);
    }

    public SecurityAction Flood(string kind, SecurityRate rate, bool mayThrottle = false, bool mayDisconnect = true, int cost = 1, string? bucket = null)
    {
        if (!Over(bucket ?? kind, rate, cost))
        {
            return SecurityAction.Observed;
        }

        var action = Policy.FloodAction switch
        {
            SecurityAction.Throttled when mayThrottle => SecurityAction.Throttled,
            SecurityAction.Disconnected when mayDisconnect => SecurityAction.Disconnected,
            _ => SecurityAction.Observed,
        };
        Disconnect |= action == SecurityAction.Disconnected;
        Emit(new(kind) { Action = action, Value = bucket ?? (kind == SecurityEventKind.FloodFrames ? "frames" : null) });
        return action;
    }

    private static bool SecretBearing(ReadOnlySpan<byte> fragment)
    {
        if (fragment.Length < 2 || (FuncCode)fragment[1] is
            FuncCode.OpenFile or FuncCode.DeleteFile or FuncCode.AuthenticateFile or
            FuncCode.AuthRequest or FuncCode.AuthRequestNoAck or FuncCode.AuthResponse)
        {
            return true;
        }

        // Treat malformed input conservatively because it may contain bytes the
        // object parser could not reach. Scan before truncating the sample.
        for (var i = 2; i + 1 < fragment.Length; i++)
        {
            if (fragment[i] == 120 || fragment[i] == 70 && fragment[i + 1] is >= 2 and <= 4)
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class Bucket
{
    public long Last { get; private set; }
    private double _tokens;

    public bool Over(SecurityRate rate, int cost)
    {
        if (rate.Count <= 0 || rate.Period <= TimeSpan.Zero)
        {
            return false;
        }

        var now = Stopwatch.GetTimestamp();
        _tokens = Last == 0 ? rate.Count : Math.Min(rate.Count,
            _tokens + Stopwatch.GetElapsedTime(Last, now).TotalSeconds / rate.Period.TotalSeconds * rate.Count);
        Last = now;
        if (_tokens < cost)
        {
            _tokens = 0;
            return true;
        }

        _tokens -= cost;
        return false;
    }
}
