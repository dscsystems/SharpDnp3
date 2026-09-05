// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The sequencing of a file transfer: the handle, the block numbering, and the
// rules about what a master may ask for next.

using SharpDnp3.App;
using SharpDnp3.Objects;
using SharpDnp3.Stack;

namespace SharpDnp3.Outstation;

/// <summary>The one file a session has open.</summary>
/// <remarks>
/// One at a time is deliberate. A device with a handle table has to expire
/// entries, refuse the master that opened too many, and answer for handles it
/// has forgotten; a device with a single slot answers "too many files open" and
/// is done. Masters open one file at a time in practice.
/// </remarks>
internal sealed class Transfer : IDisposable
{
    /// <summary>The handle the master addresses this transfer by.</summary>
    public uint Handle { get; set; }

    /// <summary>The path the transfer names.</summary>
    public required string Name { get; init; }

    /// <summary>What the file was opened for.</summary>
    public required FileOpenMode Mode { get; init; }

    /// <summary>The stream a read takes its octets from.</summary>
    public Stream? Reader { get; init; }

    /// <summary>The stream a write puts its octets into.</summary>
    public Stream? Writer { get; init; }

    /// <summary>The next block number expected in, or to be sent out.</summary>
    public uint Block { get; set; }

    /// <summary>The block size both ends settled on.</summary>
    public required ushort BlockSize { get; init; }

    /// <summary>The file's length, where it is known.</summary>
    public uint Size { get; set; }

    /// <summary>When an idle transfer is abandoned.</summary>
    public DateTimeOffset Deadline { get; set; }

    /// <summary>
    /// Set once the last block has passed, so a master that keeps asking is
    /// told the file is finished rather than being served past its end.
    /// </summary>
    public bool Done { get; set; }

    /// <summary>
    /// One octet read ahead of the block just served, held back so the next
    /// block can start with it.
    /// </summary>
    /// <remarks>
    /// The look-ahead is what makes "last" honest. A block that comes back
    /// exactly full is indistinguishable from the end of the file until
    /// something says otherwise, and a master that never sees the last-block
    /// flag waits for a block that is not coming.
    /// </remarks>
    public int PeekedByte { get; set; } = -1;

    /// <inheritdoc/>
    public void Dispose()
    {
        Reader?.Dispose();
        Writer?.Dispose();
    }
}

public sealed partial class OutstationSession
{
    /// <summary>The transfer in flight, or <see langword="null"/>.</summary>
    private Transfer? _file;

    /// <summary>Issues the handles.</summary>
    /// <remarks>
    /// A handle is never reused within a session, so a master holding a stale
    /// one is told so rather than being handed somebody else's file.
    /// </remarks>
    private uint _handleSeq;

    /// <summary>Reports whether file transfer is configured.</summary>
    /// <remarks>
    /// When it is not, the outstation answers the file function codes the way a
    /// device that does not implement them does.
    /// </remarks>
    private bool FileEnabled => _cfg.Files.Handler is not null;

    /// <summary>Returns the group 70 object header a request carries, if any.</summary>
    private static bool TryFileObject(Fragment frag, out ObjectHeader header)
    {
        foreach (var h in frag.Objects)
        {
            if (h.Group == 70)
            {
                header = h;
                return true;
            }
        }

        header = default;
        return false;
    }

    /// <summary>Answers a file request on an outstation without a handler.</summary>
    private void UnsupportedFile(Association a, Received r, AppHeader req)
    {
        lock (_gate)
        {
            _stats.UnknownFunction++;
        }

        a.Iin = a.Iin.Set(Iin.NoFuncCodeSupport);
        if (r.Broadcast)
        {
            return;
        }

        Respond(a, r, req, []);
    }

    /// <summary>Answers with a single group 70 object.</summary>
    private void RespondFile(
        Association a, Received r, AppHeader req, byte variation, List<byte> obj)
    {
        ObjectHeader h;
        try
        {
            h = FreeFormat.Build(70, variation, System.Runtime.InteropServices
                .CollectionsMarshal.AsSpan(obj));
        }
        catch (Dnp3Exception)
        {
            a.Iin = a.Iin.Set(Iin.ParameterError);
            Respond(a, r, req, []);
            return;
        }

        var body = new List<byte>(h.Size);
        ObjectHeaderCodec.AppendObjectHeader(body, h);
        Respond(a, r, req, body.ToArray());
    }

    /// <summary>Answers an open, close, delete or abort with a g70v4 status.</summary>
    private void RespondCommandStatus(Association a, Received r, AppHeader req, FileCommandStatus st)
    {
        if (!st.Status.OK())
        {
            lock (_gate)
            {
                _stats.FileErrors++;
            }
        }

        var obj = new List<byte>(FileObjects.FileCommandStatusSize);
        FileObjects.AppendCommandStatus(obj, st);
        RespondFile(a, r, req, 4, obj);
    }

    /// <summary>Answers a written block.</summary>
    private void RespondTransportStatus(Association a, Received r, AppHeader req, FileTransportStatus st)
    {
        if (!st.Status.OK())
        {
            lock (_gate)
            {
                _stats.FileErrors++;
            }
        }

        var obj = new List<byte>(FileObjects.FileTransportStatusSize);
        FileObjects.AppendTransportStatus(obj, st);
        RespondFile(a, r, req, 6, obj);
    }

    /// <summary>Pulls a g70v3 out of a request.</summary>
    private static bool TryParseFileCommand(
        Fragment frag, out FileCommand cmd, out string? error)
    {
        cmd = default;
        error = null;

        if (!TryFileObject(frag, out var h))
        {
            error = "outstation: the request carries no group 70 object";
            return false;
        }

        try
        {
            cmd = FileObjects.ParseCommand(FreeFormat.FirstObject(h).Span);
            return true;
        }
        catch (MalformedException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Opens a file and hands back a handle.</summary>
    private void OnOpenFile(Association a, Received r, Fragment frag)
    {
        if (!FileEnabled)
        {
            UnsupportedFile(a, r, frag.Header);
            return;
        }

        if (!TryParseFileCommand(frag, out var cmd, out var error))
        {
            a.Log.Log(Dnp3LogLevel.Warn, "malformed file open", ("err", error));
            a.Iin = a.Iin.Set(Iin.ParameterError);
            Respond(a, r, frag.Header, []);
            return;
        }

        var reply = new FileCommandStatus { RequestId = cmd.RequestId };

        if (_file is not null)
        {
            // One transfer at a time. Answering with the handle of the transfer
            // already running would have two masters writing the same file.
            a.Log.Log(
                Dnp3LogLevel.Warn,
                "file open refused; another transfer is in flight",
                ("requested", cmd.Name), ("open", _file.Name));
            RespondCommandStatus(a, r, frag.Header, reply with { Status = FileStatus.TooManyOpen });
            return;
        }

        var status = OpenTransfer(cmd, out var t);
        if (!status.OK() || t is null)
        {
            RespondCommandStatus(a, r, frag.Header, reply with { Status = status });
            return;
        }

        _handleSeq++;
        t.Handle = _handleSeq;
        t.Deadline = _appl.Now() + _cfg.Files.Timeout;
        _file = t;

        lock (_gate)
        {
            _stats.FilesOpened++;
        }

        a.Log.Log(
            Dnp3LogLevel.Info,
            "file opened",
            ("name", cmd.Name), ("mode", cmd.Mode), ("handle", t.Handle),
            ("size", t.Size), ("block_size", t.BlockSize));

        RespondCommandStatus(a, r, frag.Header, reply with
        {
            Handle = t.Handle,
            Size = t.Size,
            MaxBlockSize = t.BlockSize,
        });
    }

    /// <summary>Asks the handler for the file and prepares the transfer.</summary>
    private FileStatus OpenTransfer(FileCommand cmd, out Transfer? transfer)
    {
        transfer = null;

        var h = _cfg.Files.Handler!;
        var blockSize = NegotiateBlockSize(cmd.MaxBlockSize);

        switch (cmd.Mode)
        {
            case FileOpenMode.Read:
            {
                var status = h.Info(cmd.Name, out var info);
                if (!status.OK())
                {
                    return status;
                }

                Stream reader;
                uint size;

                if (info.IsDirectory)
                {
                    // A directory is read as a file whose contents are its
                    // entries. Building them here rather than in the handler
                    // keeps the wire format out of every implementation.
                    status = DirectoryContents(cmd, out var content);
                    if (!status.OK())
                    {
                        return status;
                    }

                    reader = new MemoryStream(content, writable: false);
                    size = (uint)content.Length;
                }
                else
                {
                    status = h.OpenRead(cmd.Name, out var stream, out _);
                    if (!status.OK() || stream is null)
                    {
                        return status.OK() ? FileStatus.Fatal : status;
                    }

                    reader = stream;
                    size = info.Size;
                }

                transfer = new Transfer
                {
                    Name = cmd.Name,
                    Mode = cmd.Mode,
                    BlockSize = blockSize,
                    Reader = reader,
                    Size = size,
                };
                return FileStatus.Success;
            }

            case FileOpenMode.Write:
            case FileOpenMode.Append:
            {
                var status = h.OpenWrite(cmd.Name, cmd.Mode, cmd.Size, out var stream);
                if (!status.OK() || stream is null)
                {
                    return status.OK() ? FileStatus.Fatal : status;
                }

                transfer = new Transfer
                {
                    Name = cmd.Name,
                    Mode = cmd.Mode,
                    BlockSize = blockSize,
                    Writer = stream,
                    Size = cmd.Size,
                };
                return FileStatus.Success;
            }

            default:
                return FileStatus.InvalidMode;
        }
    }

    /// <summary>Encodes a directory listing as the file a master reads.</summary>
    private FileStatus DirectoryContents(FileCommand cmd, out byte[] content)
    {
        content = [];

        var status = _cfg.Files.Handler!.List(cmd.Name, out var entries);
        if (!status.OK())
        {
            return status;
        }

        var buf = new List<byte>(entries.Count * 32);
        foreach (var e in entries)
        {
            FileObjects.AppendDescriptor(buf, FileDescriptor.For(e, cmd.RequestId));
        }

        content = buf.ToArray();
        return FileStatus.Success;
    }

    /// <summary>Settles on a block size both ends can carry.</summary>
    /// <remarks>
    /// The master states the largest it will accept and the outstation the
    /// largest it will send; the smaller wins. The fragment cap is the third
    /// constraint and the one a device forgets: a block that does not fit in a
    /// response fragment cannot be sent at all.
    /// </remarks>
    private ushort NegotiateBlockSize(ushort requested)
    {
        var size = _cfg.Files.MaxBlockSize;
        if (requested > 0 && requested < size)
        {
            size = requested;
        }

        // Room for the application header, the object header and the transport
        // object's own fixed part.
        var room = _cfg.MaxTxFragment - 32;
        if (room > 0 && size > room)
        {
            size = (ushort)room;
        }

        return size;
    }

    /// <summary>Ends a transfer.</summary>
    private void OnCloseFile(Association a, Received r, Fragment frag)
    {
        if (!FileEnabled)
        {
            UnsupportedFile(a, r, frag.Header);
            return;
        }

        FileCommandStatus req;
        try
        {
            if (!TryFileObject(frag, out var h))
            {
                a.Iin = a.Iin.Set(Iin.ParameterError);
                Respond(a, r, frag.Header, []);
                return;
            }

            req = FileObjects.ParseCommandStatus(FreeFormat.FirstObject(h).Span);
        }
        catch (MalformedException ex)
        {
            a.Log.Log(Dnp3LogLevel.Warn, "malformed file close", ("err", ex.Message));
            a.Iin = a.Iin.Set(Iin.ParameterError);
            Respond(a, r, frag.Header, []);
            return;
        }

        var reply = new FileCommandStatus { Handle = req.Handle, RequestId = req.RequestId };

        if (_file is null || _file.Handle != req.Handle)
        {
            RespondCommandStatus(a, r, frag.Header, reply with { Status = FileStatus.InvalidHandle });
            return;
        }

        var name = _file.Name;
        try
        {
            CloseFile();
        }
        catch (IOException ex)
        {
            a.Log.Log(
                Dnp3LogLevel.Warn,
                "closing a transferred file failed",
                ("name", name), ("err", ex.Message));

            // The master needs to know: a write whose close failed has not
            // landed, whatever the individual blocks reported.
            RespondCommandStatus(a, r, frag.Header, reply with { Status = FileStatus.Fatal });
            return;
        }

        a.Log.Log(Dnp3LogLevel.Info, "file closed", ("name", name), ("handle", req.Handle));
        RespondCommandStatus(a, r, frag.Header, reply);
    }

    /// <summary>Removes a file.</summary>
    private void OnDeleteFile(Association a, Received r, Fragment frag)
    {
        if (!FileEnabled)
        {
            UnsupportedFile(a, r, frag.Header);
            return;
        }

        if (!TryParseFileCommand(frag, out var cmd, out var error))
        {
            a.Log.Log(Dnp3LogLevel.Warn, "malformed file delete", ("err", error));
            a.Iin = a.Iin.Set(Iin.ParameterError);
            Respond(a, r, frag.Header, []);
            return;
        }

        var reply = new FileCommandStatus { RequestId = cmd.RequestId };

        if (_file is not null && _file.Name == cmd.Name)
        {
            // Deleting the file being transferred would leave the transfer
            // writing to something with no name.
            RespondCommandStatus(a, r, frag.Header, reply with { Status = FileStatus.Locked });
            return;
        }

        var status = _cfg.Files.Handler!.Delete(cmd.Name);
        if (status.OK())
        {
            a.Log.Log(Dnp3LogLevel.Info, "file deleted", ("name", cmd.Name));
        }

        RespondCommandStatus(a, r, frag.Header, reply with { Status = status });
    }

    /// <summary>Abandons a transfer without finishing it.</summary>
    /// <remarks>
    /// Abort differs from close in what it means for a write: a closed file is
    /// complete and an aborted one is not. Both release the handle, which is
    /// what the master is really asking for.
    /// </remarks>
    private void OnAbortFile(Association a, Received r, Fragment frag)
    {
        if (!FileEnabled)
        {
            UnsupportedFile(a, r, frag.Header);
            return;
        }

        var reply = default(FileCommandStatus);
        if (TryFileObject(frag, out var h))
        {
            try
            {
                // An abort is addressed by handle, in the same object a close
                // uses. A master that sends the open command instead is
                // answered on the transfer in flight.
                var st = FileObjects.ParseCommandStatus(FreeFormat.FirstObject(h).Span);
                reply = reply with { Handle = st.Handle, RequestId = st.RequestId };
            }
            catch (MalformedException)
            {
                // An abort with an unreadable object still means "let go of
                // whatever you are holding".
            }
        }

        if (_file is null)
        {
            RespondCommandStatus(a, r, frag.Header, reply with { Status = FileStatus.NotOpen });
            return;
        }

        if (reply.Handle != 0 && reply.Handle != _file.Handle)
        {
            RespondCommandStatus(a, r, frag.Header, reply with { Status = FileStatus.InvalidHandle });
            return;
        }

        var name = _file.Name;
        TryCloseFile();

        lock (_gate)
        {
            _stats.FilesAborted++;
        }

        a.Log.Log(Dnp3LogLevel.Info, "file transfer aborted", ("name", name));
        RespondCommandStatus(a, r, frag.Header, reply);
    }

    /// <summary>Describes a file without transferring it.</summary>
    private void OnGetFileInfo(Association a, Received r, Fragment frag)
    {
        if (!FileEnabled)
        {
            UnsupportedFile(a, r, frag.Header);
            return;
        }

        FileDescriptor req;
        try
        {
            if (!TryFileObject(frag, out var h))
            {
                a.Iin = a.Iin.Set(Iin.ParameterError);
                Respond(a, r, frag.Header, []);
                return;
            }

            // The request names the file in the same descriptor object the
            // answer comes back in, with everything but the name left empty.
            req = FileObjects.ParseDescriptor(FreeFormat.FirstObject(h).Span);
        }
        catch (MalformedException ex)
        {
            a.Log.Log(Dnp3LogLevel.Warn, "malformed file info request", ("err", ex.Message));
            a.Iin = a.Iin.Set(Iin.ParameterError);
            Respond(a, r, frag.Header, []);
            return;
        }

        var status = _cfg.Files.Handler!.Info(req.Name, out var info);
        if (!status.OK())
        {
            lock (_gate)
            {
                _stats.FileErrors++;
            }

            RespondCommandStatus(a, r, frag.Header, new FileCommandStatus
            {
                RequestId = req.RequestId,
                Status = status,
            });
            return;
        }

        // The name reported is the handler's, not the path that was asked for:
        // a descriptor names the file the way a directory listing does, and the
        // master already knows what it requested.
        var obj = new List<byte>(FileObjects.FileDescriptorSize + 32);
        FileObjects.AppendDescriptor(obj, FileDescriptor.For(info, req.RequestId));
        RespondFile(a, r, frag.Header, 7, obj);
    }

    /// <summary>Serves the next block of a file.</summary>
    private void OnFileRead(Association a, Received r, Fragment frag, ObjectHeader h)
    {
        if (!FileEnabled)
        {
            UnsupportedFile(a, r, frag.Header);
            return;
        }

        FileTransport req;
        try
        {
            req = FileObjects.ParseTransport(FreeFormat.FirstObject(h));
        }
        catch (MalformedException ex)
        {
            a.Log.Log(Dnp3LogLevel.Warn, "malformed file read", ("err", ex.Message));
            a.Iin = a.Iin.Set(Iin.ParameterError);
            Respond(a, r, frag.Header, []);
            return;
        }

        void Fail(FileStatus status) => RespondTransportStatus(a, r, frag.Header, new FileTransportStatus
        {
            Handle = req.Handle,
            Block = req.Block,
            Status = status,
        });

        var t = _file;
        if (t is null)
        {
            Fail(FileStatus.NotOpen);
            return;
        }

        if (t.Handle != req.Handle)
        {
            Fail(FileStatus.InvalidHandle);
            return;
        }

        if (t.Mode != FileOpenMode.Read)
        {
            Fail(FileStatus.InvalidMode);
            return;
        }

        if (t.Done)
        {
            // The last block has already gone. Serving another would be
            // inventing a file end that the master would then append to what it
            // has.
            Fail(FileStatus.NotOpen);
            return;
        }

        if (req.Block != t.Block)
        {
            // The stream cannot rewind, so a master asking for anything but the
            // next block is asking for something that cannot be given.
            a.Log.Log(
                Dnp3LogLevel.Warn,
                "file read out of sequence",
                ("want", t.Block), ("got", req.Block));
            Fail(FileStatus.BlockSequence);
            return;
        }

        t.Deadline = _appl.Now() + _cfg.Files.Timeout;

        byte[] data;
        bool last;
        try
        {
            data = ReadBlock(t, out last);
        }
        catch (IOException ex)
        {
            a.Log.Log(
                Dnp3LogLevel.Warn,
                "reading a file block failed",
                ("name", t.Name), ("err", ex.Message));
            TryCloseFile();
            Fail(FileStatus.Fatal);
            return;
        }

        var block = new FileTransport
        {
            Handle = t.Handle,
            Block = t.Block,
            Last = last,
            Data = data,
        };

        t.Block++;
        t.Done = last;

        lock (_gate)
        {
            _stats.FileBlocksSent++;
        }

        var obj = new List<byte>(FileObjects.FileTransportSize + data.Length);
        FileObjects.AppendTransport(obj, block);
        RespondFile(a, r, frag.Header, 5, obj);
    }

    /// <summary>
    /// Takes the next block off a transfer, reporting whether it is the last.
    /// </summary>
    /// <remarks>
    /// The look-ahead is what makes "last" honest. A block that comes back
    /// exactly full is indistinguishable from the end of the file until
    /// something says otherwise, and a master that never sees the last-block
    /// flag waits for a block that is not coming.
    /// </remarks>
    private static byte[] ReadBlock(Transfer t, out bool last)
    {
        var buf = new byte[t.BlockSize];
        var n = 0;

        // Whatever the previous call read ahead starts this block.
        if (t.PeekedByte >= 0)
        {
            buf[n++] = (byte)t.PeekedByte;
            t.PeekedByte = -1;
        }

        var stream = t.Reader!;
        while (n < buf.Length)
        {
            var read = stream.Read(buf, n, buf.Length - n);
            if (read == 0)
            {
                break;
            }

            n += read;
        }

        if (n < buf.Length)
        {
            // A short block, or none at all: an empty file still owes the
            // master one last block, or it would never learn the transfer had
            // finished.
            last = true;
            return buf[..n];
        }

        // A full block. Read one octet ahead to find out whether anything
        // follows it, and keep that octet for the next block.
        var peek = stream.ReadByte();
        if (peek < 0)
        {
            last = true;
            return buf;
        }

        t.PeekedByte = peek;
        last = false;
        return buf;
    }

    /// <summary>Accepts one block of a file being written.</summary>
    private void OnFileWrite(Association a, Received r, Fragment frag, ObjectHeader h)
    {
        if (!FileEnabled)
        {
            UnsupportedFile(a, r, frag.Header);
            return;
        }

        FileTransport block;
        try
        {
            block = FileObjects.ParseTransport(FreeFormat.FirstObject(h));
        }
        catch (MalformedException ex)
        {
            a.Log.Log(Dnp3LogLevel.Warn, "malformed file write", ("err", ex.Message));
            a.Iin = a.Iin.Set(Iin.ParameterError);
            Respond(a, r, frag.Header, []);
            return;
        }

        var reply = new FileTransportStatus
        {
            Handle = block.Handle,
            Block = block.Block,
            Last = block.Last,
        };

        var t = _file;
        FileStatus status;

        if (t is null)
        {
            status = FileStatus.NotOpen;
        }
        else if (t.Handle != block.Handle)
        {
            status = FileStatus.InvalidHandle;
        }
        else if (t.Mode != FileOpenMode.Write && t.Mode != FileOpenMode.Append)
        {
            status = FileStatus.InvalidMode;
        }
        else if (block.Block != t.Block)
        {
            // Blocks must arrive in order: the writer appends, so a gap would
            // be silently filled with whatever came next.
            a.Log.Log(
                Dnp3LogLevel.Warn,
                "file write out of sequence",
                ("want", t.Block), ("got", block.Block));
            status = FileStatus.BlockSequence;
        }
        else if (block.Data.Length > t.BlockSize)
        {
            status = FileStatus.WriteBlockSize;
        }
        else
        {
            t.Deadline = _appl.Now() + _cfg.Files.Timeout;
            try
            {
                t.Writer!.Write(block.Data.Span);
                t.Block++;
                t.Done = block.Last;
                status = FileStatus.Success;

                lock (_gate)
                {
                    _stats.FileBlocksReceived++;
                }
            }
            catch (IOException ex)
            {
                a.Log.Log(
                    Dnp3LogLevel.Warn,
                    "writing a file block failed",
                    ("name", t.Name), ("err", ex.Message));
                TryCloseFile();
                status = FileStatus.Fatal;
            }
        }

        RespondTransportStatus(a, r, frag.Header, reply with { Status = status });
    }

    /// <summary>Ends the transfer in flight, propagating a close failure.</summary>
    private void CloseFile()
    {
        var t = _file;
        if (t is null)
        {
            return;
        }

        _file = null;
        t.Dispose();
    }

    /// <summary>Ends the transfer in flight, swallowing a close failure.</summary>
    /// <remarks>
    /// Used where the outcome is already decided — an abort, a failed block, a
    /// dropped connection — and the close is only releasing the handle.
    /// </remarks>
    private void TryCloseFile()
    {
        try
        {
            CloseFile();
        }
        catch (IOException ex)
        {
            _log.Log(Dnp3LogLevel.Warn, "closing a transfer failed", ("err", ex.Message));
        }
    }

    /// <summary>Abandons a transfer the master has stopped talking about.</summary>
    private void CheckFileTimeout(DateTimeOffset now)
    {
        var t = _file;
        if (t is null || now < t.Deadline)
        {
            return;
        }

        _log.Log(
            Dnp3LogLevel.Warn,
            "file transfer timed out; the handle is being released",
            ("name", t.Name), ("handle", t.Handle));

        TryCloseFile();

        lock (_gate)
        {
            _stats.FileTimeouts++;
        }
    }
}
