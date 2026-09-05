// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// One session's view of a shared line: the channel it connects to, and the
// stream that channel hands back.

using System.Buffers.Binary;
using System.Globalization;
using System.Threading.Channels;
using SharpDnp3.App;
using SharpDnp3.Channels;
using SharpDnp3.Link;
using SharpDnp3.Transport;

namespace SharpDnp3.Multidrop;

/// <summary>Identifies one session's place on the bus.</summary>
/// <remarks>
/// The addresses must match the session's own configuration: they are what the
/// bus routes on, and a station listening on an address its session does not
/// use would collect frames the session then discards.
/// </remarks>
public readonly record struct Station
{
    /// <summary>The session's own link address.</summary>
    public ushort LocalAddr { get; init; }

    /// <summary>The station it talks to.</summary>
    public ushort RemoteAddr { get; init; }

    /// <summary>Says the session is a master.</summary>
    /// <remarks>
    /// It decides two things. Masters sharing a line normally share one link
    /// address, so a master is routed by source address — whose reply this is —
    /// while an outstation is routed by destination and answers whichever
    /// master addressed it. And only a master takes the line: it is the one
    /// that starts an exchange, so it is the one that has to wait its turn.
    /// </remarks>
    public bool IsMaster { get; init; }

    /// <inheritdoc/>
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "{0} {1}↔{2}", IsMaster ? "master" : "outstation", LocalAddr, RemoteAddr);

    /// <summary>
    /// Reports whether a frame could match both stations, which makes the pair
    /// unroutable.
    /// </summary>
    internal bool Conflicts(Station other)
    {
        if (LocalAddr != other.LocalAddr)
        {
            return false;
        }

        // An outstation accepts every source, so it is indistinguishable from
        // anything else sharing its address. Two masters are told apart by
        // whose outstation answered.
        if (!IsMaster || !other.IsMaster)
        {
            return true;
        }

        return RemoteAddr == other.RemoteAddr;
    }

    /// <summary>Reports whether a frame belongs to this station.</summary>
    internal bool Matches(LinkHeader h)
    {
        if (IsMaster)
        {
            // Masters on one line normally share a link address — the line is
            // one master station with several sessions on it — so the
            // destination says almost nothing and the source is what separates
            // one outstation's reply from another's. Broadcasts are
            // deliberately not matched: masters send them, nobody sends them to
            // a master.
            return h.Dest == LocalAddr && h.Src == RemoteAddr;
        }

        // An outstation answers whichever master addressed it, so the
        // destination decides on its own. Its RemoteAddr says where unsolicited
        // responses go; it is not a filter on what it will accept, and making
        // it one would leave a second master on the line unable to poll.
        return h.Dest == LocalAddr || LinkConstants.IsBroadcast(h.Dest);
    }
}

/// <summary>
/// A station's connection has ended because the line under it has gone.
/// </summary>
/// <remarks>
/// It ends the session's connection the same way a socket error would, and the
/// session reconnects — onto whatever connection the bus has by then.
/// </remarks>
internal sealed class BusDisconnectedException : Dnp3Exception
{
    public BusDisconnectedException()
        : base("multidrop: the bus connection dropped") { }
}

/// <summary>
/// One session's view of the bus, satisfying <see cref="IChannel"/> so a session
/// cannot tell it from a socket.
/// </summary>
internal sealed class StationChannel : IChannel
{
    private readonly Bus _bus;

    public StationChannel(Bus bus, Station address)
    {
        _bus = bus;
        Address = address;
    }

    /// <summary>Where this station sits on the line.</summary>
    public Station Address { get; }

    /// <summary>
    /// The station's live connection, guarded by the bus lock — which is also
    /// what makes routing and reconnection agree on which connection is
    /// current.
    /// </summary>
    public StationConnection? Current { get; set; }

    /// <summary>Marks a station taken off the bus.</summary>
    public bool Removed { get; set; }

    /// <inheritdoc/>
    public Task<Stream> ConnectAsync(CancellationToken cancellationToken) =>
        _bus.ConnectStationAsync(this, cancellationToken);

    /// <summary>Takes the station off the bus.</summary>
    /// <remarks>
    /// It does not close the underlying channel — the bus owns that, and other
    /// stations are still using it.
    /// </remarks>
    public void Close() => _bus.RemoveStation(this);

    /// <inheritdoc/>
    public void Dispose() => Close();

    /// <inheritdoc/>
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture, "multidrop {0} [{1}]", _bus.ChannelName, Address);
}

/// <summary>
/// One station's connection: frames routed to it on the way in, the shared line
/// on the way out.
/// </summary>
internal sealed class StationConnection : Stream
{
    private readonly Bus _bus;
    private readonly Stream _line;
    private readonly Channel<byte[]> _rx;
    private readonly CancellationTokenSource _done = new();

    private ReadOnlyMemory<byte> _remainder;
    private int _torndown;

    public StationConnection(Bus bus, StationChannel station, Stream line, int queue)
    {
        _bus = bus;
        Station = station;
        _line = line;
        _rx = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(queue)
        {
            // A full queue means the session has stopped reading, so the frame
            // is dropped and counted. Blocking instead would stall every other
            // station on the line behind one wedged session, which is the
            // failure this whole namespace exists to avoid.
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
    }

    /// <summary>Which station this connection belongs to.</summary>
    public StationChannel Station { get; }

    /// <summary>Reports whether the connection has been torn down.</summary>
    public bool Gone => Volatile.Read(ref _torndown) != 0;

    /// <summary>Queues a frame for the session, reporting whether it fit.</summary>
    public bool Push(byte[] frame) => _rx.Writer.TryWrite(frame);

    /// <summary>
    /// Makes the connection dead: reads end, writes fail, and any hold on the
    /// line is given up.
    /// </summary>
    public void Teardown()
    {
        if (Interlocked.Exchange(ref _torndown, 1) != 0)
        {
            return;
        }

        _rx.Writer.TryComplete();
        _done.Cancel();
        _bus.Arbiter.ReleaseFor(this);
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_remainder.IsEmpty)
        {
            if (Gone)
            {
                return 0;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _done.Token);

            try
            {
                if (!await _rx.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
                {
                    return 0;
                }
            }
            catch (OperationCanceledException) when (_done.IsCancellationRequested)
            {
                // The line under this connection has gone; end of stream.
                return 0;
            }

            if (_rx.Reader.TryRead(out var frame))
            {
                _remainder = frame;
            }
        }

        var n = Math.Min(buffer.Length, _remainder.Length);
        _remainder[..n].CopyTo(buffer);
        _remainder = _remainder[n..];
        return n;
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Puts one frame on the line, waiting for its turn if another master is
    /// mid-exchange.
    /// </summary>
    /// <remarks>
    /// A stack writes one complete frame per call, which is what makes the
    /// write lock enough to keep two stations' frames from interleaving.
    /// </remarks>
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Gone)
        {
            throw new BusDisconnectedException();
        }

        // Only a master waits its turn: it is the one that starts an exchange.
        var takesLine = Station.Address.IsMaster;
        if (takesLine)
        {
            await _bus.Arbiter.AcquireAsync(this, cancellationToken).ConfigureAwait(false);
        }

        var failed = false;
        await _bus.WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Gone)
            {
                // The line dropped while we waited for our turn. Writing now
                // would put a frame on a connection this session no longer
                // owns.
                failed = true;
                throw new BusDisconnectedException();
            }

            await _line.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await _line.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (MarkFailed(ref failed))
        {
            throw;
        }
        finally
        {
            _bus.WriteLock.Release();

            if (takesLine && (failed || !ExpectsReply(buffer.Span)))
            {
                // Nothing is coming back — an application confirm, a no-reply
                // control, a broadcast, or a frame that never made it onto the
                // line — so the line is free at once rather than idling for the
                // whole turnaround.
                _bus.Arbiter.ReleaseFor(this);
            }
        }
    }

    private static bool MarkFailed(ref bool failed)
    {
        failed = true;

        // Never handles the exception; it only records that the write did not
        // land, so the line is released rather than held for a reply that
        // cannot come.
        return false;
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <summary>Ends this session's connection. The bus keeps the line.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bus.DetachConnection(this);
            Teardown();
            _done.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// Reports whether a transmitted frame leaves the line owed an answer, and
    /// so whether the sender should keep it.
    /// </summary>
    /// <remarks>
    /// It peeks rather than decodes. The transport and application headers sit
    /// at a fixed offset after the ten-octet link header, ahead of the first
    /// block CRC, so they are readable without re-verifying CRCs the stack has
    /// just computed.
    /// </remarks>
    internal static bool ExpectsReply(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < LinkConstants.HeaderSize)
        {
            return false;
        }

        if (LinkConstants.IsBroadcast(BinaryPrimitives.ReadUInt16LittleEndian(frame[4..6])))
        {
            // Nothing answers a broadcast. Every outstation replying at once is
            // precisely the collision arbitration exists to prevent, and the
            // standard has them stay silent for the same reason.
            return false;
        }

        if (!Control.Parse(frame[3]).Prm)
        {
            // A secondary frame is itself a reply — an acknowledgement, a link
            // status — and nothing comes back for it. Reading it as a request
            // would leave a master holding the line for a whole turnaround
            // every time it acknowledged an outstation, which is most frames on
            // a confirmed link.
            return false;
        }

        // A primary frame with no user data is a link-layer request — a reset,
        // a status request, a test — and every one of them is answered.
        if (frame.Length < LinkConstants.HeaderSize + 3)
        {
            return true;
        }

        if (!TransportHeader.Parse(frame[LinkConstants.HeaderSize]).Fir)
        {
            // A continuation segment: the decision was made on the first one,
            // and this station is holding the line either way.
            return true;
        }

        var fn = (FuncCode)frame[LinkConstants.HeaderSize + 2];
        return fn != FuncCode.Confirm && !fn.NoReply();
    }
}
