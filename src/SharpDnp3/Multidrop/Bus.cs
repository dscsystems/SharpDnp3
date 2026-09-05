// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Shares one channel between several DNP3 sessions.
//
// A session owns its channel: MasterSession.RunAsync connects, polls, and
// reconnects on that one stream. Over TCP that is exactly right — every
// outstation gets its own socket — and on a multi-drop serial line it is
// impossible. One RS-485 pair carries every station on the line, and a serial
// port cannot be opened twice: the second session to try gets "device or
// resource busy", or worse, on the platforms that allow it, two sessions
// interleaving frames into the same UART.
//
// A Bus sits between them. It opens the shared channel once and hands each
// session an IChannel of its own, so nothing above changes: a session still
// connects, still reconnects, still owns its stack. The bus routes inbound link
// frames to the session they are addressed to, serialises what goes out so two
// stations' frames cannot interleave, and — because the line is half duplex —
// keeps one master's exchange from starting while another's is still in flight.
//
// The same arrangement covers a terminal server: several RTUs behind one TCP
// connection to a serial gateway is the same line with a longer wire.
//
//     using var port = new SerialChannel(cfg, Retry.Default);
//     using var bus = new Bus(port, new BusConfig());
//
//     foreach (ushort addr in new ushort[] { 10, 11, 12 })
//     {
//         var ch = bus.Add(new Station
//         {
//             LocalAddr = 1, RemoteAddr = addr, IsMaster = true,
//         });
//
//         var m = new MasterSession(new MasterConfig
//         {
//             LocalAddr = 1, RemoteAddr = addr, UseLinkConfirms = true,
//         }, handler);
//
//         _ = m.RunAsync(ch, ct);
//     }
//
// What the bus does not do is schedule the sessions against each other. Each
// master still polls on its own clock; the bus only stops their exchanges from
// overlapping. Three masters polling a slow line every second will spend their
// time waiting for each other — pace the polls, and give the line the time it
// needs.

using System.Globalization;
using SharpDnp3.Channels;
using SharpDnp3.Link;
using SharpDnp3.Transport;

namespace SharpDnp3.Multidrop;

/// <summary>Parameterises a bus.</summary>
public sealed class BusConfig
{
    /// <summary>
    /// How long a master keeps the line after transmitting while it waits for
    /// the outstation to answer.
    /// </summary>
    /// <remarks>
    /// It bounds the damage a silent outstation does: the line is idle for this
    /// long before another master may transmit. Too short and a slow device's
    /// reply collides with the next master's request; too long and one dead
    /// station paces the whole line. Two seconds is a compromise for 9600 baud,
    /// where a maximum-length response alone takes a third of a second.
    /// </remarks>
    public static readonly TimeSpan DefaultTurnaround = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How many frames may be waiting for one station before further frames are
    /// dropped.
    /// </summary>
    public const int DefaultQueue = 16;

    /// <summary>
    /// How long a master holds the line after transmitting a request, waiting
    /// for the reply.
    /// </summary>
    /// <remarks>
    /// The hold ends early as soon as the reply arrives complete, so this only
    /// governs stations that do not answer.
    /// <para>
    /// Zero uses <see cref="DefaultTurnaround"/>. A negative value disables
    /// arbitration altogether, which is right only when the far end is not a
    /// shared medium — a gateway that queues and turns the line around itself.
    /// </para>
    /// </remarks>
    public TimeSpan Turnaround { get; set; }

    /// <summary>
    /// How many frames may be waiting for one station. Zero uses
    /// <see cref="DefaultQueue"/>.
    /// </summary>
    /// <remarks>
    /// A queue only fills when a session has stopped reading, which means it is
    /// wedged or shutting down; frames beyond it are dropped and counted rather
    /// than stalling the bus for every other station.
    /// </remarks>
    public int Queue { get; set; }

    /// <summary>Receives bus events.</summary>
    public IDnp3Logger? Log { get; set; }

    internal void ApplyDefaults()
    {
        if (Turnaround == TimeSpan.Zero)
        {
            Turnaround = DefaultTurnaround;
        }
        else if (Turnaround < TimeSpan.Zero)
        {
            Turnaround = TimeSpan.Zero; // arbitration disabled
        }

        if (Queue <= 0)
        {
            Queue = DefaultQueue;
        }

        Log ??= NullDnp3Logger.Instance;
    }
}

/// <summary>Counts what a bus has carried.</summary>
public record struct BusStats
{
    /// <summary>How many times the underlying channel connected.</summary>
    public ulong Connections;

    /// <summary>Frames delivered to at least one station.</summary>
    public ulong FramesRouted;

    /// <summary>Frames addressed to nobody on this bus.</summary>
    /// <remarks>
    /// No station has that address, or the one that does has no session
    /// connected, which to the line looks the same as a device that is not
    /// powered up. Neither is an error on a line shared with equipment this
    /// program does not own, but a steady climb with no traffic reaching a
    /// session means an address is wrong.
    /// </remarks>
    public ulong FramesUnrouted;

    /// <summary>
    /// Frames discarded because a station's queue was full — a session that has
    /// stopped reading.
    /// </summary>
    public ulong FramesDropped;

    /// <summary>Frames the link parser decoded, across all connections.</summary>
    /// <remarks>
    /// A per-session stats block cannot report these: the bus decodes the
    /// stream, so its sessions never see the octets that failed to make a
    /// frame.
    /// </remarks>
    public ulong FramesDecoded;

    /// <summary>Octets thrown away while resynchronising.</summary>
    public ulong BytesDiscarded;

    /// <summary>Frames rejected because the header CRC failed.</summary>
    public ulong HeaderCrcErrors;

    /// <summary>Frames rejected because a body block CRC failed.</summary>
    public ulong BodyCrcErrors;

    /// <summary>Frames rejected because the LEN octet was out of range.</summary>
    public ulong BadLength;

    /// <summary>Times the parser hunted forward for a delimiter.</summary>
    public ulong Resyncs;
}

/// <summary>Shares one channel between several sessions.</summary>
public sealed class Bus : IDisposable
{
    private readonly IChannel _channel;
    private readonly BusConfig _cfg;
    private readonly IDnp3Logger _log;

    private readonly Lock _gate = new();
    private readonly List<StationChannel> _stations = [];
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Serialises transmission. A stack writes one complete frame per call, so
    /// holding this for the duration of one write is what keeps two stations'
    /// frames from interleaving on the wire.
    /// </summary>
    internal SemaphoreSlim WriteLock { get; } = new(1, 1);

    /// <summary>Gives the line to one master at a time.</summary>
    internal Arbiter Arbiter { get; }

    private Stream? _conn;
    private Task? _pump;
    private BusStats _stats;
    private bool _closed;
    private bool _stopped;
    private Exception? _error;

    /// <summary>
    /// Completed when a connection becomes available, and replaced when one
    /// drops, which is how a station's connect waits for the line without
    /// polling.
    /// </summary>
    private TaskCompletionSource _up =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completed once the bus stops for good.</summary>
    private readonly TaskCompletionSource _dead =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Returns a bus over <paramref name="channel"/>.</summary>
    /// <remarks>
    /// The bus takes ownership of the channel: <see cref="Dispose"/> closes it,
    /// and nothing else should connect it.
    /// </remarks>
    public Bus(IChannel channel, BusConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(channel);

        config ??= new BusConfig();
        config.ApplyDefaults();

        _channel = channel;
        _cfg = config;
        _log = new ScopedLogger(config.Log!, ("bus", channel.ToString() ?? "channel"));
        Arbiter = new Arbiter(config.Turnaround);
    }

    /// <summary>How the underlying channel describes itself.</summary>
    internal string ChannelName => _channel.ToString() ?? "channel";

    /// <summary>
    /// Puts a station on the bus and returns the channel its session runs on.
    /// </summary>
    /// <remarks>
    /// It fails if the station cannot be told apart from one already added: two
    /// masters polling the same outstation, or two outstations at one address,
    /// would both match the same frames and there is no answer to which of them
    /// should have it.
    /// </remarks>
    public IChannel Add(Station station)
    {
        if (LinkConstants.IsBroadcast(station.LocalAddr) ||
            LinkConstants.IsReserved(station.LocalAddr))
        {
            throw new BadConfigException(string.Format(
                CultureInfo.InvariantCulture,
                "multidrop: {0} is not a usable local address", station.LocalAddr));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);

            foreach (var existing in _stations)
            {
                if (existing.Address.Conflicts(station))
                {
                    throw new BadConfigException(string.Format(
                        CultureInfo.InvariantCulture,
                        "multidrop: {0} cannot be told apart from {1}",
                        station, existing.Address));
                }
            }

            var st = new StationChannel(this, station);
            _stations.Add(st);
            return st;
        }
    }

    /// <summary>Returns a snapshot of the bus counters.</summary>
    public BusStats Stats
    {
        get
        {
            lock (_gate)
            {
                return _stats;
            }
        }
    }

    /// <summary>Shuts the bus down, closing the underlying channel.</summary>
    /// <remarks>
    /// Every session on it sees its connection drop and its next connect report
    /// <see cref="ChannelClosedException"/>, which both sessions treat as a
    /// clean shutdown.
    /// </remarks>
    public void Dispose()
    {
        Stream? conn;
        List<StationConnection> live;

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            conn = _conn;
            _conn = null;
            live = DetachAllLocked();
        }

        Die(null);
        _cts.Cancel();
        Arbiter.Close();

        foreach (var c in live)
        {
            c.Teardown();
        }

        // Closing the connection as well as the channel is what unblocks the
        // pump's read: a TCP channel does not own the connections it dialled,
        // so closing it alone would leave the pump parked in a read until the
        // peer noticed.
        conn?.Dispose();
        _channel.Close();
        _channel.Dispose();

        Arbiter.Dispose();
        WriteLock.Dispose();
        _cts.Dispose();
    }

    /// <inheritdoc/>
    public override string ToString() => "multidrop " + ChannelName;

    /// <summary>Waits for the bus to have a line and returns a station's view of it.</summary>
    internal async Task<Stream> ConnectStationAsync(
        StationChannel station, CancellationToken cancellationToken)
    {
        Start();

        while (true)
        {
            Task up;

            lock (_gate)
            {
                if (_error is not null)
                {
                    throw _error;
                }

                if (_closed || _stopped || station.Removed)
                {
                    throw new ChannelClosedException();
                }

                if (_conn is not null)
                {
                    var c = new StationConnection(this, station, _conn, _cfg.Queue);
                    var previous = station.Current;
                    station.Current = c;

                    // A session that reconnects while the line is still up
                    // abandons its previous connection, which must not be left
                    // holding the line.
                    previous?.Teardown();
                    return c;
                }

                up = _up.Task;
            }

            await Task.WhenAny(up, _dead.Task, Task.Delay(Timeout.Infinite, cancellationToken))
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Takes a station off the bus.</summary>
    internal void RemoveStation(StationChannel station)
    {
        StationConnection? current;

        lock (_gate)
        {
            station.Removed = true;
            current = station.Current;
            station.Current = null;
            _stations.Remove(station);
        }

        current?.Teardown();
    }

    /// <summary>Clears a station's record of a connection it is disposing.</summary>
    internal void DetachConnection(StationConnection c)
    {
        lock (_gate)
        {
            if (ReferenceEquals(c.Station.Current, c))
            {
                c.Station.Current = null;
            }
        }
    }

    /// <summary>Launches the pump on the first connect.</summary>
    private void Start()
    {
        lock (_gate)
        {
            _pump ??= Task.Run(() => PumpAsync(_cts.Token));
        }
    }

    /// <summary>Records why the bus stopped and wakes everyone waiting on it.</summary>
    private void Die(Exception? error)
    {
        lock (_gate)
        {
            _stopped = true;
            _error ??= error;
        }

        _dead.TrySetResult();
    }

    /// <summary>
    /// Owns the underlying connection: it connects, reads, routes, and
    /// reconnects, for the life of the bus.
    /// </summary>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var buf = new byte[LinkConstants.MaxFrameSize];

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Die(null);
                return;
            }

            Stream conn;
            try
            {
                conn = await _channel.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Cancellation and a closed channel are a shutdown, not a
                // failure; anything else stops the bus and is reported to every
                // station, so a misconfigured port surfaces as a session error
                // rather than as sessions that never connect.
                Die(ex is OperationCanceledException or ChannelClosedException
                    ? null
                    : new Dnp3Exception("multidrop: connect: " + ex.Message, ex));
                return;
            }

            if (!Attach(conn))
            {
                // The bus was closed while this connection was being made.
                // Nobody is going to use it, and leaving it open would leave
                // the pump reading a line the bus has given up.
                conn.Dispose();
                Die(null);
                return;
            }

            _log.Log(Dnp3LogLevel.Info, "bus connected");

            // A fresh parser per connection: buffered octets from a line that
            // has just dropped are half a frame that will never be completed.
            var parser = new FrameParser();
            await ReadAsync(conn, parser, buf, cancellationToken).ConfigureAwait(false);

            Detach(conn);
            _log.Log(Dnp3LogLevel.Info, "bus disconnected");
        }
    }

    /// <summary>
    /// Publishes a new connection and wakes the stations waiting for one.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> if the bus closed while the connection was being
    /// made.
    /// </returns>
    private bool Attach(Stream conn)
    {
        TaskCompletionSource up;

        lock (_gate)
        {
            if (_closed)
            {
                return false;
            }

            _conn = conn;
            _stats.Connections++;
            up = _up;
        }

        up.TrySetResult();
        return true;
    }

    /// <summary>
    /// Retires a connection, dropping every station's connection with it.
    /// </summary>
    private void Detach(Stream conn)
    {
        List<StationConnection> live;

        lock (_gate)
        {
            _conn = null;
            _up = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            live = DetachAllLocked();
        }

        foreach (var c in live)
        {
            c.Teardown();
        }

        conn.Dispose();
    }

    /// <summary>
    /// Clears every station's current connection and returns them for the
    /// caller to tear down outside the lock.
    /// </summary>
    private List<StationConnection> DetachAllLocked()
    {
        var live = new List<StationConnection>(_stations.Count);
        foreach (var s in _stations)
        {
            if (s.Current is { } c)
            {
                live.Add(c);
                s.Current = null;
            }
        }

        return live;
    }

    /// <summary>Pumps one connection until it fails.</summary>
    private async Task ReadAsync(
        Stream conn, FrameParser parser, byte[] buf, CancellationToken cancellationToken)
    {
        var previous = default(LinkStats);

        while (true)
        {
            int n;
            try
            {
                n = await conn.ReadAsync(buf, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _log.Log(Dnp3LogLevel.Warn, "bus read failed", ("err", ex.Message));
                }

                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (n == 0)
            {
                return;
            }

            Feed(parser, buf.AsSpan(0, n));
            SyncParserStats(parser, ref previous);
        }
    }

    /// <summary>
    /// Pushes received octets through the parser, routing every frame they
    /// complete.
    /// </summary>
    /// <remarks>
    /// The read buffer holds one maximum frame and every complete frame is
    /// drained before the next read, so the parser — which holds two — always
    /// has room. The loop and the refusal check stay anyway: octets dropped
    /// here would surface as frames that vanished with nothing in the log to
    /// say why.
    /// </remarks>
    private void Feed(FrameParser parser, ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var n = parser.Write(data);
            data = data[n..];

            while (parser.TryNext(out var frame))
            {
                Route(frame);
            }

            if (n == 0)
            {
                // The parser took nothing and draining freed nothing, so the
                // rest of this read has nowhere to go. Giving up beats
                // spinning.
                _log.Log(
                    Dnp3LogLevel.Warn,
                    "bus parser is full; octets dropped",
                    ("dropped", data.Length));
                return;
            }
        }
    }

    /// <summary>Folds the parser's counters into the bus totals.</summary>
    /// <remarks>
    /// They are added as deltas because the parser is replaced on every
    /// reconnect, and a counter that goes backwards when a cable is re-seated
    /// is worse than no counter at all.
    /// </remarks>
    private void SyncParserStats(FrameParser parser, ref LinkStats previous)
    {
        var current = parser.Stats;

        lock (_gate)
        {
            _stats.FramesDecoded += current.FramesDecoded - previous.FramesDecoded;
            _stats.BytesDiscarded += current.BytesDiscarded - previous.BytesDiscarded;
            _stats.HeaderCrcErrors += current.HeaderCrcErrors - previous.HeaderCrcErrors;
            _stats.BodyCrcErrors += current.BodyCrcErrors - previous.BodyCrcErrors;
            _stats.BadLength += current.BadLength - previous.BadLength;
            _stats.Resyncs += current.Resyncs - previous.Resyncs;
        }

        previous = current;
    }

    /// <summary>Delivers one frame to the stations it belongs to.</summary>
    private void Route(LinkFrame f)
    {
        List<StationConnection> targets;

        lock (_gate)
        {
            targets = new List<StationConnection>(_stations.Count);
            foreach (var s in _stations)
            {
                if (s.Address.Matches(f.Header) && s.Current is { } c)
                {
                    targets.Add(c);
                }
            }

            if (targets.Count == 0)
            {
                _stats.FramesUnrouted++;
            }
            else
            {
                _stats.FramesRouted++;
            }
        }

        if (targets.Count == 0)
        {
            _log.Log(
                Dnp3LogLevel.Debug,
                "frame for nobody on the bus",
                ("src", f.Header.Src), ("dest", f.Header.Dest));
            return;
        }

        // The frame is re-encoded rather than forwarded verbatim: the parser
        // hands back a decoded frame, not the octets it consumed. Every CRC was
        // just verified and the payload is unchanged, so what goes to the
        // session is the same frame it would have read off the wire.
        byte[] outgoing;
        try
        {
            outgoing = FrameCodec.Encode(f.Header, f.Payload.Span);
        }
        catch (Dnp3Exception ex)
        {
            _log.Log(Dnp3LogLevel.Warn, "bus re-encode failed", ("err", ex.Message));
            return;
        }

        // A fragment is complete when the transport header says so. That is
        // what ends a master's turn: everything before it is more of the same
        // reply, and releasing the line early would let another master transmit
        // into the middle of it.
        var complete = !f.Payload.IsEmpty &&
                       TransportHeader.Parse(f.Payload.Span[0]).Fin;

        foreach (var c in targets)
        {
            if (!c.Push(outgoing))
            {
                lock (_gate)
                {
                    _stats.FramesDropped++;
                }

                _log.Log(
                    Dnp3LogLevel.Warn,
                    "station queue full; frame dropped",
                    ("station", c.Station.Address));
            }

            Arbiter.Observe(c, complete);
        }
    }
}
