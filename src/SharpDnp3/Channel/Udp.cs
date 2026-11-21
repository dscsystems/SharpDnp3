// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// UDP is a legal DNP3 transport, and a genuinely awkward one.
//
// The stack above expects a stream: the link layer resynchronises on a
// delimiter, and the transport function reassembles fragments across frames.
// UDP delivers datagrams that may be dropped, duplicated or reordered, and none
// of those layers were designed to repair that — the link layer's frame count
// bit assumes an ordered channel.
//
// So this presents a datagram socket as a stream, which works because DNP3 over
// UDP puts whole link frames in single datagrams. What it cannot do is hide
// loss: a dropped datagram is a dropped frame, and a fragment spanning several
// frames simply fails to reassemble. Use UDP where the network is reliable and
// the messages are small, and prefer TCP everywhere else.

using System.Net;
using System.Net.Sockets;
using SharpDnp3.Security;

namespace SharpDnp3.Channels;

/// <summary>Describes a UDP endpoint.</summary>
public sealed class UdpConfig
{
    /// <summary>
    /// The address to bind. Empty binds an ephemeral port on all interfaces,
    /// which is what a master normally wants.
    /// </summary>
    public string LocalAddr { get; set; } = "";

    /// <summary>
    /// Where to send. Empty means reply to whoever writes first, which is what
    /// an outstation normally wants.
    /// </summary>
    public string RemoteAddr { get; set; } = "";

    /// <summary>Discard datagrams whose endpoint differs from the configured or learned peer.</summary>
    public bool PinPeer { get; set; }
}

/// <summary>Counts UDP datagrams from an endpoint other than the channel's peer.</summary>
public readonly record struct UdpStats(long ForeignSources, long DroppedSources);

/// <summary>A channel over a UDP socket.</summary>
public sealed class UdpChannel : IChannel, IObservedChannel
{
    private readonly UdpConfig _cfg;
    private readonly Lock _gate = new();
    private UdpClient? _client;
    private IPEndPoint? _remote;
    private bool _closed;
    private long _foreignSources;
    private long _droppedSources;

    /// <summary>Returns the cumulative UDP source counters.</summary>
    public UdpStats Stats => new(Interlocked.Read(ref _foreignSources), Interlocked.Read(ref _droppedSources));

    /// <summary>Creates a channel over a UDP socket.</summary>
    public UdpChannel(UdpConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _cfg = config;
    }

    /// <summary>
    /// The address the socket bound to, or null before it has bound.
    /// </summary>
    public IPEndPoint? BoundAddress
    {
        get
        {
            lock (_gate)
            {
                return _client?.Client.LocalEndPoint as IPEndPoint;
            }
        }
    }

    /// <inheritdoc/>
    public Task<Stream> ConnectAsync(CancellationToken cancellationToken) =>
        ConnectAsync(null, cancellationToken);

    Task<Stream> IObservedChannel.ConnectAsync(SecurityRuntime? security, CancellationToken cancellationToken) =>
        ConnectAsync(security, cancellationToken);

    private Task<Stream> ConnectAsync(SecurityRuntime? security, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_closed)
            {
                throw new ChannelClosedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (_client is not null)
            {
                // A datagram socket has no connection to re-establish, so a
                // reconnecting session gets the same socket back.
                return Task.FromResult<Stream>(new UdpStream(this, security));
            }

            var local = string.IsNullOrEmpty(_cfg.LocalAddr) ? ":0" : _cfg.LocalAddr;
            var (host, port) = EndpointParser.Split(local);
            var bindAddress = host.Length == 0 ? IPAddress.Any : IPAddress.Parse(host);

            if (!string.IsNullOrEmpty(_cfg.RemoteAddr))
            {
                var (rhost, rport) = EndpointParser.Split(_cfg.RemoteAddr);
                _remote = new IPEndPoint(IPAddress.Parse(rhost), rport);
            }

            _client = new UdpClient(new IPEndPoint(bindAddress, port));
            return Task.FromResult<Stream>(new UdpStream(this, security));
        }
    }

    /// <inheritdoc/>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _client?.Dispose();
            _client = null;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Close();

    /// <inheritdoc/>
    public override string ToString() => string.IsNullOrEmpty(_cfg.RemoteAddr)
        ? "udp " + _cfg.LocalAddr
        : "udp " + _cfg.LocalAddr + "→" + _cfg.RemoteAddr;

    /// <summary>Presents the datagram socket as a stream.</summary>
    private sealed class UdpStream : Stream, IPeerEndpoint
    {
        private readonly UdpChannel _channel;
        private readonly SecurityRuntime? _security;
        private readonly HashSet<IPEndPoint> _refused = [];
        private ReadOnlyMemory<byte> _remainder;
        private IPEndPoint? _identified;

        public UdpStream(UdpChannel channel, SecurityRuntime? security)
        {
            _channel = channel;
            _security = security;
        }

        public string? Peer
        {
            get
            {
                lock (_channel._gate)
                {
                    return _channel._remote?.ToString();
                }
            }
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            while (_remainder.IsEmpty)
            {
                UdpClient? client;
                lock (_channel._gate)
                {
                    client = _channel._client;
                }

                if (client is null)
                {
                    return 0;
                }

                UdpReceiveResult result;
                try
                {
                    result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    return 0;
                }

                // Learn the peer from the first datagram, so an outstation can
                // answer a master it was not configured with.
                IPEndPoint? remote;
                lock (_channel._gate)
                {
                    remote = _channel._remote;
                }

                var sender = result.RemoteEndPoint;
                if (_security is { } security && !sender.Equals(_identified))
                {
                    var foreign = remote is not null && !remote.Equals(sender);
                    var observation = foreign
                        ? security.ForPeer("udp", sender)
                        : security.ForConnection(_channel, this);
                    if (_refused.Contains(sender) || !security.Admit(observation, sender, countAccept: false) ||
                        !await observation.IdentifyAsync(cancellationToken, sender).ConfigureAwait(false))
                    {
                        if (_refused.Count < 256)
                        {
                            _refused.Add(sender);
                        }

                        Interlocked.Increment(ref _channel._droppedSources);
                        continue;
                    }

                    if (!foreign && result.Buffer.Length != 0)
                    {
                        _identified = sender;
                    }
                }

                if (remote is null && result.Buffer.Length != 0)
                {
                    lock (_channel._gate)
                    {
                        remote = _channel._remote ??= result.RemoteEndPoint;
                    }
                }

                if (remote is not null && !remote.Equals(result.RemoteEndPoint))
                {
                    Interlocked.Increment(ref _channel._foreignSources);
                    _security?.ForConnection(_channel, this).Emit(new(SecurityEventKind.ConnUDPForeignSource)
                    {
                        Action = _channel._cfg.PinPeer ? SecurityAction.Ignored : SecurityAction.Observed,
                        Peer = new SecurityPeer { Network = "udp", Remote = result.RemoteEndPoint.ToString() },
                    });
                    if (_channel._cfg.PinPeer)
                    {
                        Interlocked.Increment(ref _channel._droppedSources);
                        continue;
                    }
                }

                _remainder = result.Buffer;
            }

            var n = Math.Min(buffer.Length, _remainder.Length);
            _remainder[..n].CopyTo(buffer);
            _remainder = _remainder[n..];
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            UdpClient? client;
            IPEndPoint? remote;
            lock (_channel._gate)
            {
                client = _channel._client;
                remote = _channel._remote;
            }

            if (client is null)
            {
                throw new ChannelClosedException();
            }

            if (remote is null)
            {
                // Nothing has been heard from yet, so there is nowhere to send.
                // This is normal for an outstation that has not been polled.
                throw new NoConnectionException("channel: no UDP peer known yet");
            }

            await client.SendAsync(buffer, remote, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Disposing does not close the socket: the channel owns it, and a
        /// session reconnecting must find it still there.
        /// </summary>
        protected override void Dispose(bool disposing) => base.Dispose(disposing);
    }
}
