// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace SharpDnp3.Channels;

/// <summary>Parses "host:port" into an endpoint.</summary>
internal static class EndpointParser
{
    /// <summary>
    /// Splits an address of the form <c>host:port</c> or <c>:port</c>, where an
    /// empty host means "every interface".
    /// </summary>
    public static (string Host, int Port) Split(string address)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);

        var idx = address.LastIndexOf(':');
        if (idx < 0)
        {
            throw new BadConfigException(string.Format(
                CultureInfo.InvariantCulture,
                "channel: address {0} has no port", address));
        }

        var host = address[..idx];
        var portText = address[(idx + 1)..];

        // Strip the brackets an IPv6 literal carries.
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }

        if (!int.TryParse(portText, CultureInfo.InvariantCulture, out var port) ||
            port is < 0 or > 65535)
        {
            throw new BadConfigException(string.Format(
                CultureInfo.InvariantCulture,
                "channel: address {0} has an invalid port", address));
        }

        return (host, port);
    }
}

/// <summary>A channel that dials an address, retrying with backoff.</summary>
public sealed class TcpClientChannel : IChannel
{
    private readonly string _address;
    private readonly Retry _retry;
    private int _attempt;
    private volatile bool _closed;

    /// <summary>How long a single dial may take before it is abandoned.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Creates a channel that dials <paramref name="address"/>.</summary>
    public TcpClientChannel(string address, Retry retry)
    {
        _address = address;
        _retry = retry;
    }

    /// <inheritdoc/>
    public async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        var (host, port) = EndpointParser.Split(_address);

        while (true)
        {
            if (_closed)
            {
                throw new ChannelClosedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var client = new TcpClient();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ConnectTimeout);

                await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);

                // Nagle would coalesce a poll with whatever follows it, adding
                // latency to every request on an otherwise idle link.
                client.NoDelay = true;
                _attempt = 0;
                return new TcpConnectionStream(client);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                client.Dispose();

                cancellationToken.ThrowIfCancellationRequested();

                if (_retry.Min <= TimeSpan.Zero)
                {
                    throw new NoConnectionException(string.Format(
                        CultureInfo.InvariantCulture,
                        "channel: dial {0} failed: {1}", _address, ex.Message));
                }

                await _retry.SleepAsync(_attempt, cancellationToken).ConfigureAwait(false);
                _attempt++;
            }
        }
    }

    /// <inheritdoc/>
    public void Close() => _closed = true;

    /// <inheritdoc/>
    public void Dispose() => Close();

    /// <inheritdoc/>
    public override string ToString() => "tcp-client " + _address;
}

/// <summary>A channel that accepts inbound connections on an address.</summary>
/// <remarks>
/// Each <see cref="ConnectAsync"/> accepts one master. A session that serves
/// one master at a time calls it again after each disconnection; a session
/// configured for several calls it again straight away, so the listener hands
/// out as many peers as the session is willing to hold.
/// </remarks>
public sealed class TcpServerChannel : IChannel
{
    private readonly string _address;
    private readonly Lock _gate = new();
    private TcpListener? _listener;
    private bool _closed;

    /// <summary>Creates a channel that listens on <paramref name="address"/>.</summary>
    public TcpServerChannel(string address) => _address = address;

    /// <summary>
    /// The address the listener bound to, which is how a caller that asked for
    /// port 0 finds out what it got. Null until it has bound.
    /// </summary>
    public IPEndPoint? BoundAddress
    {
        get
        {
            lock (_gate)
            {
                return _listener?.LocalEndpoint as IPEndPoint;
            }
        }
    }

    private TcpListener Listen()
    {
        lock (_gate)
        {
            if (_closed)
            {
                throw new ChannelClosedException();
            }

            if (_listener is not null)
            {
                return _listener;
            }

            var (host, port) = EndpointParser.Split(_address);
            var ip = host.Length == 0 ? IPAddress.IPv6Any : IPAddress.Parse(host);

            var listener = new TcpListener(ip, port);
            if (ip.Equals(IPAddress.IPv6Any))
            {
                // Dual-stack, so ":20000" accepts both families the way the Go
                // reference implementation's wildcard listener does.
                listener.Server.SetSocketOption(
                    SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
            }

            listener.Start();
            _listener = listener;
            return listener;
        }
    }

    /// <inheritdoc/>
    public bool SupportsConcurrentConnections => true;

    /// <inheritdoc/>
    public async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        var listener = Listen();
        var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        client.NoDelay = true;
        return new TcpConnectionStream(client);
    }

    /// <inheritdoc/>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _listener?.Stop();
            _listener = null;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Close();

    /// <inheritdoc/>
    public override string ToString() => "tcp-server " + _address;
}

/// <summary>
/// A <see cref="NetworkStream"/> that disposes the socket it was built from.
/// </summary>
/// <remarks>
/// The session closes the stream when a connection drops; without this the
/// <see cref="TcpClient"/> wrapper would leak until finalization.
/// </remarks>
internal sealed class TcpConnectionStream : Stream, IPeerEndpoint
{
    private readonly TcpClient _client;
    private readonly NetworkStream _inner;

    public TcpConnectionStream(TcpClient client)
    {
        _client = client;
        _inner = client.GetStream();

        // Read it once, here: a dropped socket no longer knows who it was
        // connected to, and the log line that matters most is the one written
        // when the connection ends.
        try
        {
            Peer = client.Client.RemoteEndPoint?.ToString();
        }
        catch (SocketException)
        {
            Peer = null;
        }
        catch (ObjectDisposedException)
        {
            Peer = null;
        }
    }

    /// <inheritdoc/>
    public string? Peer { get; }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        _inner.Write(buffer, offset, count);

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _client.Dispose();
        }

        base.Dispose(disposing);
    }
}
