// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Net;
using System.Net.Sockets;
using SharpDnp3.Security;

namespace SharpDnp3.Channels;

/// <summary>How a single-connection server treats additional connections.</summary>
public enum BusyPolicy
{
    /// <summary>Leave additional peers in the listener backlog.</summary>
    Backlog,
    /// <summary>Accept and immediately close additional connections.</summary>
    Reject,
}

internal sealed class ServerAdmission : IDisposable
{
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly CancellationTokenSource _closed = new();

    public async Task<Stream> AcceptAsync(
        Func<CancellationToken, Task<Stream>> accept,
        Func<TcpListener> listen,
        BusyPolicy busy,
        SecurityRuntime? security,
        string transport,
        CancellationToken cancellationToken)
    {
        if (busy != BusyPolicy.Reject)
        {
            return await accept(cancellationToken).ConfigureAwait(false);
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        try
        {
            await _slot.WaitAsync(wait.Token).ConfigureAwait(false);
            try
            {
                var stream = await accept(wait.Token).ConfigureAwait(false);
                var reject = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
                var wrapper = new BusyConnection(stream, reject, RejectAsync(listen, security, transport, reject.Token), _slot);
                security?.Transfer(stream, wrapper);
                return wrapper;
            }
            catch
            {
                _slot.Release();
                throw;
            }
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ChannelClosedException();
        }
    }

    private static async Task RejectAsync(
        Func<TcpListener> listen, SecurityRuntime? security, string transport, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var peer = await TcpAccept.AcceptAsync(listen, token).ConfigureAwait(false);
                security?.ForPeer(transport, peer.Client.RemoteEndPoint as IPEndPoint)
                    .Emit(new(SecurityEventKind.ConnRejectedBusy) { Action = SecurityAction.Rejected });
                peer.Client.LingerState = new LingerOption(true, 0);
            }
        }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException or SocketException)
        {
            // Closing the active stream or listener stops the reject loop.
        }
    }

    public void Dispose() => _closed.Cancel();

    private sealed class BusyConnection(
        Stream inner, CancellationTokenSource reject, Task rejecting, SemaphoreSlim slot) : Stream, IPeerEndpoint
    {
        private int _disposed;
        public string? Peer => (inner as IPeerEndpoint)?.Peer;
        public SecurityPeer? SecurityPeer => (inner as IPeerEndpoint)?.SecurityPeer;

        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    reject.Cancel();
                    inner.Dispose();
                    rejecting.GetAwaiter().GetResult();
                }
                finally
                {
                    reject.Dispose();
                    slot.Release();
                }
            }

            base.Dispose(disposing);
        }
    }
}
