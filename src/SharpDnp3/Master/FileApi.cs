// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The file methods below follow the same rule as the rest of the request API:
// safe to call from any thread, and each one waits for the exchange to finish.
// A transfer is many requests rather than one, so it holds the session for its
// whole duration — see the note in File.cs.

using System.Globalization;
using SharpDnp3.Objects;

namespace SharpDnp3.Master;

public sealed partial class MasterSession
{
    /// <summary>
    /// Caps what <see cref="ReadFileBytesAsync"/> and
    /// <see cref="ReadDirectoryAsync"/> will accumulate in memory.
    /// </summary>
    /// <remarks>
    /// So a device that reports an implausible size — or never stops sending
    /// blocks — cannot exhaust the master. Use <see cref="ReadFileAsync"/> with
    /// a file on disk for anything larger.
    /// </remarks>
    public const int MaxFileSize = 16 << 20;

    private int _fileSeq;

    /// <summary>
    /// Issues the identifier that ties a response to its request.
    /// </summary>
    /// <remarks>
    /// It wraps at sixteen bits, as the field does. Reuse is harmless: it
    /// disambiguates two exchanges in flight, and a session has one.
    /// </remarks>
    private ushort NextRequestId() => (ushort)Interlocked.Increment(ref _fileSeq);

    /// <summary>
    /// The largest file block this master will ask for, derived from the
    /// fragment caps when not configured.
    /// </summary>
    private ushort BlockSizeForTransfer()
    {
        if (_cfg.FileBlockSize > 0)
        {
            return _cfg.FileBlockSize;
        }

        // Room for the application header, the object header and the transport
        // object's fixed part, with margin.
        var room = Math.Min(_cfg.MaxRxFragment, _cfg.MaxTxFragment) - 32;
        room = Math.Max(room, 64);
        room = Math.Min(room, 0xFFFF);
        return (ushort)room;
    }

    /// <summary>Reads a file from the outstation into <paramref name="destination"/>.</summary>
    /// <remarks>
    /// <para>
    /// The transfer is open, read, close. A failure part way through still
    /// closes the file: an outstation left holding a handle refuses the next
    /// transfer until its own timeout expires, which on a device that allows
    /// one at a time means the master has locked itself out.
    /// </para>
    /// <para>
    /// The octet count is reported even when the transfer fails — through
    /// <see cref="FileTransferIncompleteException.Transferred"/> — so a caller
    /// can tell a file that arrived short from one that never started.
    /// </para>
    /// </remarks>
    public async Task<long> ReadFileAsync(
        string name,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(destination);

        var t = new FileTransferState
        {
            Name = name,
            RequestId = NextRequestId(),
            Destination = destination,
        };

        // The read step chains to itself until the outstation marks a block
        // final, then to the close. Building the next task in the closure is
        // what lets a transfer of unknown length be expressed without knowing
        // how many steps it will take.
        MasterTask? ReadStep() =>
            FileReadTask(t, () => t.Last ? FileCloseTask(t) : ReadStep());

        try
        {
            await RunTransferAsync(
                    t,
                    FileOpenTask(t, FileOpenMode.Read, BlockSizeForTransfer(), ReadStep),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Dnp3Exception && t.Transferred > 0)
        {
            // The file arrived short. The count is worth as much as the reason:
            // a caller can tell a transfer that stopped half way from one that
            // never started.
            throw new FileTransferIncompleteException(t.Transferred, ex);
        }

        return t.Transferred;
    }

    /// <summary>Reads a file into memory, up to <see cref="MaxFileSize"/>.</summary>
    public async Task<byte[]> ReadFileBytesAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        using var buf = new MemoryStream();
        using var limited = new LimitedWriteStream(buf, MaxFileSize, name);

        await ReadFileAsync(name, limited, cancellationToken).ConfigureAwait(false);
        return buf.ToArray();
    }

    /// <summary>
    /// Writes <paramref name="size"/> octets read from <paramref name="source"/>
    /// to the named file on the outstation.
    /// </summary>
    /// <remarks>
    /// The size has to be declared up front because the open command carries
    /// it: an outstation deciding whether it has room for a firmware image asks
    /// before the first block, not after the last.
    /// </remarks>
    public async Task WriteFileAsync(
        string name,
        Stream source,
        uint size,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(source);

        var t = new FileTransferState
        {
            Name = name,
            RequestId = NextRequestId(),
            Source = source,
            Size = size,
        };

        // Each block is read from the source as its task is built, so a large
        // file is never held in memory whole.
        MasterTask? WriteStep()
        {
            var buf = new byte[t.BlockSize];
            var n = 0;
            try
            {
                while (n < buf.Length)
                {
                    var read = source.Read(buf, n, buf.Length - n);
                    if (read == 0)
                    {
                        break;
                    }

                    n += read;
                }
            }
            catch (IOException ex)
            {
                t.Fail(new Dnp3Exception(string.Format(
                    CultureInfo.InvariantCulture,
                    "master: reading {0} to send: {1}", name, ex.Message)));
                return null;
            }

            // Short means the source is exhausted, so this is the final block.
            // An empty file still sends one: the last-block flag is what tells
            // the outstation the transfer is complete.
            var last = n < buf.Length;
            return FileWriteTask(
                t, buf[..n], last, () => t.Last ? FileCloseTask(t) : WriteStep());
        }

        await RunTransferAsync(
                t,
                FileOpenTask(t, FileOpenMode.Write, BlockSizeForTransfer(), WriteStep),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Writes <paramref name="data"/> to the named file.</summary>
    public Task WriteFileBytesAsync(
        string name,
        byte[] data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        return WriteFileAsync(
            name, new MemoryStream(data, writable: false), (uint)data.Length, cancellationToken);
    }

    /// <summary>Lists a directory on the outstation.</summary>
    /// <remarks>
    /// A directory is read exactly as a file is; what comes back is a run of
    /// file descriptors rather than arbitrary octets. Pass the path the
    /// outstation knows its root by, which is conventionally "/".
    /// </remarks>
    public async Task<IReadOnlyList<FileEntry>> ReadDirectoryAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var content = await ReadFileBytesAsync(name, cancellationToken).ConfigureAwait(false);

        try
        {
            return FileObjects.ParseDirectory(content);
        }
        catch (MalformedException ex)
        {
            throw new MalformedException(string.Format(
                CultureInfo.InvariantCulture, "master: listing {0}: {1}", name, ex.Message));
        }
    }

    /// <summary>Removes a file from the outstation.</summary>
    public async Task DeleteFileAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var t = new FileTransferState { Name = name, RequestId = NextRequestId() };
        await RunTaskAsync(FileDeleteTask(t), cancellationToken).ConfigureAwait(false);

        if (t.Error is not null)
        {
            throw t.Error;
        }
    }

    /// <summary>
    /// Describes a file on the outstation without transferring it.
    /// </summary>
    /// <remarks>
    /// Not every device implements the request. One that does not answers with
    /// NO_FUNC_CODE_SUPPORT, which comes back as
    /// <see cref="NotSupportedByPeerException"/>; reading the parent directory
    /// is the fallback that works everywhere.
    /// </remarks>
    public async Task<FileEntry> FileInfoAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var info = default(FileEntry);
        var t = new FileTransferState { Name = name, RequestId = NextRequestId() };

        await RunTaskAsync(FileInfoTask(t, i => info = i), cancellationToken)
            .ConfigureAwait(false);

        return t.Error is null ? info : throw t.Error;
    }

    /// <summary>Runs a chained transfer and makes sure the file is closed.</summary>
    private async Task RunTransferAsync(
        FileTransferState t,
        MasterTask first,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await RunTaskAsync(first, cancellationToken).ConfigureAwait(false);
            failure = t.Error;
        }
        catch (Exception ex) when (ex is Dnp3Exception or OperationCanceledException)
        {
            failure = ex;
        }

        if (failure is null)
        {
            return;
        }

        // The chain stopped early. If a handle was issued, the outstation is
        // holding the file open and will keep refusing transfers until its own
        // timeout expires — so the close is attempted even when the caller's
        // token is already cancelled, and bounded so a dead link cannot hang
        // here.
        if (t.Handle != 0 && !t.Closed)
        {
            using var closeCts = new CancellationTokenSource(_cfg.ResponseTimeout);
            try
            {
                await RunTaskAsync(FileCloseTask(t), closeCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Dnp3Exception or OperationCanceledException)
            {
                _log.Log(
                    Dnp3LogLevel.Warn,
                    "could not close a failed transfer",
                    ("file", t.Name), ("err", ex.Message));
            }
        }

        throw failure is Dnp3Exception or OperationCanceledException
            ? failure
            : new Dnp3Exception(failure.Message, failure);
    }
}

/// <summary>
/// A transfer that moved some octets before it failed, so a caller can tell a
/// file that arrived short from one that never started.
/// </summary>
public sealed class FileTransferIncompleteException : Dnp3Exception
{
    /// <summary>Creates the exception from what was moved and why it stopped.</summary>
    public FileTransferIncompleteException(long transferred, Exception cause)
        : base(cause.Message, cause) => Transferred = transferred;

    /// <summary>How many octets reached the destination.</summary>
    public long Transferred { get; }
}

/// <summary>
/// Stops an outstation from filling memory with a file whose declared size bore
/// no relation to what it sent.
/// </summary>
internal sealed class LimitedWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly long _max;
    private readonly string _name;
    private long _written;

    public LimitedWriteStream(Stream inner, long max, string name)
    {
        _inner = inner;
        _max = max;
        _name = name;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _written;

    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_written + buffer.Length > _max)
        {
            throw new IOException(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "master: {0} exceeds the {1} octet limit for reading a file into memory",
                _name, _max));
        }

        _inner.Write(buffer);
        _written += buffer.Length;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
