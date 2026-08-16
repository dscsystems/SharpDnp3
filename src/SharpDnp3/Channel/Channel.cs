// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The physical layer beneath a DNP3 session: the thing that produces a byte
// stream and reproduces it after a failure.
//
// Every transport DNP3 runs over — TCP, TLS, UDP, serial, or an in-process
// pipe — reduces to the same contract: hand me a connection, and hand me
// another one when this breaks. Keeping that behind one small interface is
// what lets the session layer be written once.

using System.Globalization;
using System.Threading.Channels;

namespace SharpDnp3.Channels;

/// <summary>Produces connections for a session.</summary>
/// <remarks>
/// <see cref="ConnectAsync"/> blocks until a connection is available or the
/// token is cancelled. A session calls it again after every disconnection, so
/// implementations own their own reconnect timing.
/// </remarks>
public interface IChannel : IDisposable
{
    /// <summary>Produces the next connection.</summary>
    Task<Stream> ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Shuts the channel down; it will produce no more connections.
    /// </summary>
    void Close();

    /// <summary>
    /// Reports whether <see cref="ConnectAsync"/> may be called again while an
    /// earlier connection is still in use.
    /// </summary>
    /// <remarks>
    /// A listener says yes: each call accepts a different peer, so an outstation
    /// can serve several masters at once. A channel that dials — TCP client,
    /// serial, UDP — says no, because asking it for another connection does not
    /// produce another peer, it produces a second connection to the same one.
    /// An outstation configured for several masters refuses to run over a
    /// channel that says no rather than quietly serving one.
    /// </remarks>
    bool SupportsConcurrentConnections => false;
}

/// <summary>Names the far end of a connection, for logging.</summary>
/// <remarks>
/// A session serving several masters at once needs something to tell them apart
/// in the log beyond a counter, and the socket address is the only thing it
/// knows about a peer before the first request arrives.
/// </remarks>
internal interface IPeerEndpoint
{
    /// <summary>A short description of the peer, or null when there is none.</summary>
    string? Peer { get; }
}

/// <summary>
/// The channel has been shut down and will produce no more connections.
/// </summary>
public sealed class ChannelClosedException : Dnp3Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public ChannelClosedException(string message = "channel: closed") : base(message) { }
}

/// <summary>Describes how long to wait between connection attempts.</summary>
/// <remarks>
/// The jitter matters more than it looks. A substation that loses a switch
/// brings every master's connection down at the same instant; without jitter
/// they all retry in lockstep and keep colliding, turning one outage into a
/// self-sustaining thundering herd.
/// </remarks>
public readonly record struct Retry
{
    /// <summary>The shortest delay, used before the first retry.</summary>
    public TimeSpan Min { get; init; }

    /// <summary>The cap on the delay. Zero means uncapped.</summary>
    public TimeSpan Max { get; init; }

    /// <summary>The multiplier applied per attempt.</summary>
    public double Factor { get; init; }

    /// <summary>
    /// The fraction of the delay to randomise, from 0 to 1.
    /// </summary>
    public double Jitter { get; init; }

    /// <summary>Backs off from half a second to a minute.</summary>
    public static Retry Default => new()
    {
        Min = TimeSpan.FromMilliseconds(500),
        Max = TimeSpan.FromSeconds(60),
        Factor = 2,
        Jitter = 0.2,
    };

    /// <summary>
    /// Connects once and gives up, which is what tests and one-shot tools want.
    /// </summary>
    public static Retry None => new() { Min = TimeSpan.Zero, Max = TimeSpan.Zero, Factor = 1 };

    /// <summary>
    /// Returns the wait before attempt <paramref name="n"/>, counting from
    /// zero.
    /// </summary>
    public TimeSpan Delay(int n)
    {
        if (Min <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var d = Min.TotalMilliseconds;
        for (var i = 0; i < n; i++)
        {
            d *= Factor;
            if (Max > TimeSpan.Zero && d >= Max.TotalMilliseconds)
            {
                d = Max.TotalMilliseconds;
                break;
            }
        }

        if (Jitter > 0)
        {
            // Symmetric jitter: the delay is scaled by 1±Jitter.
            d *= 1 + (Jitter * ((2 * Random.Shared.NextDouble()) - 1));
        }

        return TimeSpan.FromMilliseconds(d);
    }

    /// <summary>
    /// Waits for the backoff delay or the cancellation, whichever comes first.
    /// </summary>
    public async Task SleepAsync(int attempt, CancellationToken cancellationToken)
    {
        var d = Delay(attempt);
        if (d <= TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        await Task.Delay(d, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// One end of an in-memory full-duplex byte stream.
/// </summary>
/// <remarks>
/// Reads drain a queue the peer writes into, so the two ends behave like a
/// socket pair without one existing.
/// </remarks>
internal sealed class DuplexStream : Stream
{
    private readonly Channel<byte[]> _inbound;
    private Channel<byte[]>? _outbound;
    private ReadOnlyMemory<byte> _remainder;
    private bool _disposed;

    private DuplexStream(Channel<byte[]> inbound) => _inbound = inbound;

    /// <summary>Creates two streams connected to each other.</summary>
    public static (DuplexStream A, DuplexStream B) CreatePair()
    {
        var toA = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
        var toB = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

        var a = new DuplexStream(toA) { _outbound = toB };
        var b = new DuplexStream(toB) { _outbound = toA };
        return (a, b);
    }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => true;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Flush() { }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (_remainder.IsEmpty)
        {
            // WaitToReadAsync reports completion by returning false rather than
            // throwing, so a closed peer surfaces as end of stream here.
            if (!await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            if (!_inbound.Reader.TryRead(out var chunk))
            {
                return 0;
            }

            _remainder = chunk;
        }

        var n = Math.Min(buffer.Length, _remainder.Length);
        _remainder[..n].CopyTo(buffer);
        _remainder = _remainder[n..];
        return n;
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
        {
            return;
        }

        // A write to a peer that has gone away is dropped rather than thrown:
        // that is what a socket whose far end has closed looks like until the
        // next read reports end of stream.
        _outbound?.Writer.TryWrite(buffer.ToArray());
    }

    /// <inheritdoc/>
    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;

            // Completing the peer's inbound queue is what turns its next read
            // into an end-of-stream rather than a hang.
            _outbound?.Writer.TryComplete();
            _inbound.Writer.TryComplete();
            _outbound = null;
        }

        base.Dispose(disposing);
    }
}

/// <summary>Creates two channels connected to each other in memory.</summary>
/// <remarks>
/// This is what every integration test runs over, and what the terminal
/// explorer's demo mode uses: a full master and outstation talking to each
/// other through the real link, transport and application layers, with no
/// socket and no hardware.
/// </remarks>
public static class Pipe
{
    /// <summary>Returns the two connected ends.</summary>
    public static (IChannel A, IChannel B) Create()
    {
        var pair = new PipePair();
        return (new PipeEnd(pair, 0), new PipeEnd(pair, 1));
    }

    private sealed class PipePair
    {
        private readonly Lock _gate = new();
        private readonly Stream?[] _pending = new Stream?[2];
        private readonly bool[] _claimed = new bool[2];
        private bool _done;

        /// <summary>Hands out one end of a connected pair.</summary>
        /// <remarks>
        /// The first side to ask creates the pair and parks the other end for
        /// its peer; the second side collects it, and the slot resets for the
        /// next reconnect. A side that asks twice without its peer having
        /// collected — which is what a reconnect looks like when only one end
        /// dropped — gets a fresh pair, and the stale one is closed so the peer
        /// sees the disconnection rather than talking into an abandoned pipe.
        /// </remarks>
        public Stream Claim(int side)
        {
            lock (_gate)
            {
                if (_done)
                {
                    throw new ChannelClosedException();
                }

                if (_pending[0] is null || _claimed[side])
                {
                    DiscardLocked();
                    var (x, y) = DuplexStream.CreatePair();
                    _pending[0] = x;
                    _pending[1] = y;
                    _claimed[0] = false;
                    _claimed[1] = false;
                }

                var c = _pending[side]!;
                _claimed[side] = true;
                if (_claimed[0] && _claimed[1])
                {
                    _pending[0] = null;
                    _pending[1] = null;
                    _claimed[0] = false;
                    _claimed[1] = false;
                }

                return c;
            }
        }

        public void CloseAll()
        {
            lock (_gate)
            {
                _done = true;
                DiscardLocked();
            }
        }

        /// <summary>Closes any unclaimed ends. The caller holds the lock.</summary>
        private void DiscardLocked()
        {
            for (var i = 0; i < _pending.Length; i++)
            {
                _pending[i]?.Dispose();
                _pending[i] = null;
            }
        }
    }

    private sealed class PipeEnd(PipePair pair, int side) : IChannel
    {
        public Task<Stream> ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(pair.Claim(side));
        }

        public void Close() => pair.CloseAll();

        public void Dispose() => Close();

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "pipe-{0}", side);
    }
}

/// <summary>
/// An in-memory listener that accepts any number of connections at once.
/// </summary>
/// <remarks>
/// <see cref="Pipe"/> connects exactly two ends, which is all a single
/// master-outstation pair needs. An outstation serving several masters needs a
/// channel that behaves like a listening socket — one that hands out a
/// different peer on every accept — and this is that, without a socket.
/// </remarks>
public sealed class PipeListener : IDisposable
{
    // Not SingleReader: the accept loop reads it, and so does the shutdown that
    // disposes whatever is still queued.
    private readonly Channel<Stream> _accepted = Channel.CreateUnbounded<Stream>();

    private readonly Lock _gate = new();
    private bool _closed;
    private int _peers;

    /// <summary>The listening end, which an outstation session runs over.</summary>
    public IChannel Server { get; }

    /// <summary>Creates a listener with nothing connected to it.</summary>
    public PipeListener() => Server = new ServerEnd(this);

    /// <summary>
    /// Returns a channel for one more peer. Every connection it makes is a
    /// separate connection to the listener, so a master running over it can
    /// reconnect the way it would over a socket.
    /// </summary>
    public IChannel Connect() => new ClientEnd(this, Interlocked.Increment(ref _peers));

    /// <inheritdoc/>
    public void Dispose() => Close();

    /// <remarks>
    /// Only the ends nobody has accepted yet are disposed, the way a listening
    /// socket being shut down closes its backlog and leaves the connections
    /// already handed out to their owners. A session holding one ends it by
    /// being cancelled.
    /// </remarks>
    private void Close()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
        }

        _accepted.Writer.TryComplete();
        while (_accepted.Reader.TryRead(out var pending))
        {
            pending.Dispose();
        }
    }

    /// <summary>Creates a connected pair and queues the listener's end.</summary>
    private Stream Dial()
    {
        lock (_gate)
        {
            if (_closed)
            {
                throw new ChannelClosedException();
            }

            var (server, client) = DuplexStream.CreatePair();
            if (!_accepted.Writer.TryWrite(server))
            {
                server.Dispose();
                client.Dispose();
                throw new ChannelClosedException();
            }

            return client;
        }
    }

    private async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        // WaitToReadAsync reports a closed listener by returning false rather
        // than throwing, which is the same shape as a listening socket being
        // shut down under an accept.
        if (!await _accepted.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false) ||
            !_accepted.Reader.TryRead(out var conn))
        {
            throw new ChannelClosedException();
        }

        return conn;
    }

    private sealed class ServerEnd(PipeListener owner) : IChannel
    {
        public bool SupportsConcurrentConnections => true;

        public Task<Stream> ConnectAsync(CancellationToken cancellationToken) =>
            owner.AcceptAsync(cancellationToken);

        public void Close() => owner.Close();

        public void Dispose() => Close();

        public override string ToString() => "pipe-listener";
    }

    private sealed class ClientEnd(PipeListener owner, int id) : IChannel
    {
        private volatile bool _closed;

        public Task<Stream> ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_closed)
            {
                throw new ChannelClosedException();
            }

            return Task.FromResult(owner.Dial());
        }

        public void Close() => _closed = true;

        public void Dispose() => Close();

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "pipe-peer-{0}", id);
    }
}
