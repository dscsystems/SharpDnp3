// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Threading.Channels;
using SharpDnp3.Stack;
using SharpDnp3.Security;

namespace SharpDnp3.Channels;

internal static class ConnectionIo
{
    public static async Task WriteAsync(
        Stream stream, ReadOnlyMemory<byte> data, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = timeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        deadline?.CancelAfter(timeout);
        var token = deadline?.Token ?? cancellationToken;
        try
        {
            await stream.WriteAsync(data, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            deadline?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            throw new Dnp3TimeoutException("channel: write timed out");
        }
    }
}


// Both sessions use the same bounded receive queue and backpressure behavior.
internal static class ReceiveQueue
{
    public const int Capacity = 8;

    public static Channel<byte[]> Create() => Channel.CreateBounded<byte[]>(new BoundedChannelOptions(Capacity)
    {
        SingleWriter = true,
        SingleReader = true,
    });

    public static async Task ReadAsync(
        Stream stream, Channel<byte[]> queue, SecurityObservation? security, CancellationToken token)
    {
        var buffer = new byte[ProtocolStack.ReadChunk];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                var chunk = buffer.AsSpan(0, read).ToArray();
                if (security?.HasSink == true)
                {
                    await security.EnqueueAsync(queue, chunk, token).ConfigureAwait(false);
                }
                else
                {
                    await queue.Writer.WriteAsync(chunk, token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            queue.Writer.TryComplete();
        }
    }
}
