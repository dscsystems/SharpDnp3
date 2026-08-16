// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Serial is where DNP3 came from, and where its link layer earns its keep.
//
// A serial line has no framing of its own, no delivery guarantee and no
// ordering beyond what arrives: the 0x0564 delimiter, the per-block CRCs and
// the link-layer confirmation exist precisely because of it. Enable
// UseLinkConfirms on a session using this channel — without it a corrupted
// frame is simply lost, with nothing to notice or repair it.

using System.Globalization;
using System.IO.Ports;

namespace SharpDnp3.Channels;

/// <summary>Describes a serial port.</summary>
public sealed class SerialConfig
{
    /// <summary>The port name: /dev/ttyUSB0, COM3, and so on.</summary>
    public string Device { get; set; } = "";

    /// <summary>The line rate. Zero uses 9600, the DNP3 convention.</summary>
    public int Baud { get; set; }

    /// <summary>The character size. Zero uses 8.</summary>
    public int DataBits { get; set; }

    /// <summary>Defaults to none.</summary>
    public Parity Parity { get; set; } = Parity.None;

    /// <summary>Defaults to one.</summary>
    public StopBits StopBits { get; set; } = StopBits.One;

    /// <summary>
    /// Bounds a blocking read so a session's cancellation can be noticed. Zero
    /// uses one second.
    /// </summary>
    /// <remarks>
    /// It is not a protocol timeout: an idle line legitimately produces nothing
    /// for minutes at a time, and a read returning empty is not an error.
    /// </remarks>
    public TimeSpan ReadTimeout { get; set; }

    internal void ApplyDefaults()
    {
        if (Baud == 0)
        {
            Baud = 9600;
        }

        if (DataBits == 0)
        {
            DataBits = 8;
        }

        if (StopBits == StopBits.None)
        {
            StopBits = StopBits.One;
        }

        if (ReadTimeout <= TimeSpan.Zero)
        {
            ReadTimeout = TimeSpan.FromSeconds(1);
        }
    }
}

/// <summary>A channel over a serial port.</summary>
public sealed class SerialChannel : IChannel
{
    private readonly SerialConfig _cfg;
    private readonly Lock _gate = new();
    private SerialPort? _port;
    private bool _closed;

    /// <summary>Creates a channel over a serial port.</summary>
    public SerialChannel(SerialConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.ApplyDefaults();
        _cfg = config;
    }

    /// <inheritdoc/>
    public Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_closed)
            {
                throw new ChannelClosedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            // A port that is already open is handed back: reopening a serial
            // device on every reconnect drops DTR and can reset the attached
            // radio or modem.
            if (_port is { IsOpen: true })
            {
                return Task.FromResult<Stream>(new SerialStream(_port));
            }

            _port?.Dispose();

            var port = new SerialPort(_cfg.Device, _cfg.Baud, _cfg.Parity, _cfg.DataBits, _cfg.StopBits)
            {
                ReadTimeout = (int)_cfg.ReadTimeout.TotalMilliseconds,
                WriteTimeout = (int)_cfg.ReadTimeout.TotalMilliseconds,
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = true,
            };

            try
            {
                port.Open();
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                port.Dispose();
                throw new NoConnectionException(string.Format(
                    CultureInfo.InvariantCulture,
                    "channel: opening {0}: {1}", _cfg.Device, ex.Message));
            }

            _port = port;
            return Task.FromResult<Stream>(new SerialStream(port));
        }
    }

    /// <inheritdoc/>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _port?.Dispose();
            _port = null;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Close();

    /// <inheritdoc/>
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture, "serial {0}@{1}", _cfg.Device, _cfg.Baud);

    /// <summary>
    /// Presents the port as a stream whose reads return empty rather than
    /// throwing when the line is idle.
    /// </summary>
    private sealed class SerialStream : Stream
    {
        private readonly SerialPort _port;

        public SerialStream(SerialPort port) => _port = port;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _port.BaseStream.Flush();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var n = await _port.BaseStream
                        .ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (n > 0)
                    {
                        return n;
                    }
                }
                catch (TimeoutException)
                {
                    // An idle line is not an error; wait for the next octet.
                    continue;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    return 0;
                }
            }
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            _port.BaseStream.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _port.BaseStream.WriteAsync(buffer, cancellationToken);

        /// <summary>
        /// Disposing does not close the port: the channel owns it, and a
        /// session reconnecting must find it still there.
        /// </summary>
        protected override void Dispose(bool disposing) => base.Dispose(disposing);
    }
}
