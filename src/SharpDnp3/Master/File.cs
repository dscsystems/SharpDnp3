// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// A file transfer is a conversation, not a request: open, then a block at a
// time, then close. Every step depends on what the last one said — the handle
// comes back from the open, the block size may be smaller than what was asked
// for, and only the outstation knows which block is the last.
//
// The steps are chained rather than queued, the way select-and-operate is. A
// poll landing between two blocks would not corrupt the file, but it would put
// the outstation's inactivity timer at risk on a slow link, and the outstation
// is holding a handle for the duration.
//
// That is also the cost: a transfer owns the session until it finishes, so
// polls queued behind a large file wait for it. On a serial link a firmware
// image is minutes of the line, whoever asks for it.

using System.Globalization;
using SharpDnp3.App;
using SharpDnp3.Objects;

namespace SharpDnp3.Master;

/// <summary>The state one file exchange carries between its steps.</summary>
internal sealed class FileTransferState
{
    /// <summary>The path being transferred.</summary>
    public required string Name { get; init; }

    /// <summary>Ties each response to the request that caused it.</summary>
    public required ushort RequestId { get; init; }

    /// <summary>
    /// The handle the open issued. Zero means nothing was opened, so nothing
    /// needs closing.
    /// </summary>
    public uint Handle { get; set; }

    /// <summary>The file's length, as the outstation reported it.</summary>
    public uint Size { get; set; }

    /// <summary>The block size both ends settled on.</summary>
    public ushort BlockSize { get; set; }

    /// <summary>The next block number to ask for or to send.</summary>
    public uint Block { get; set; }

    /// <summary>Set once the block marked final has been handled.</summary>
    public bool Last { get; set; }

    /// <summary>Where a read puts its octets.</summary>
    public Stream? Destination { get; init; }

    /// <summary>Where a write takes its octets from.</summary>
    public Stream? Source { get; init; }

    /// <summary>
    /// Counts the octets moved, which is what a caller sees when the file turns
    /// out to be shorter than the outstation said.
    /// </summary>
    public long Transferred { get; set; }

    /// <summary>The protocol-level outcome.</summary>
    /// <remarks>
    /// The task machinery reports transport failures — a timeout, a dropped
    /// link — through its own exception; a file the outstation refused is a
    /// successful exchange with a status code in it, and this is where that
    /// lands.
    /// </remarks>
    public Exception? Error { get; private set; }

    /// <summary>
    /// Records that the close step has run, so a caller does not send a second
    /// one after a failure.
    /// </summary>
    public bool Closed { get; set; }

    /// <summary>Records the first protocol-level failure and stops the chain.</summary>
    public void Fail(Exception e) => Error ??= e;

    /// <summary>
    /// Replaces whatever was recorded, for the one diagnosis that is better
    /// than anything read out of the response body.
    /// </summary>
    public void Supersede(Exception e) => Error = e;

    /// <summary>
    /// Turns "I do not implement this" into an error the caller can classify,
    /// rather than leaving it as a response with nothing in it.
    /// </summary>
    /// <remarks>
    /// It overrides whatever the response body produced. A device that does not
    /// implement the function still sends a response, and reading its empty
    /// body yields something like "the response carried no descriptor" — true,
    /// useless, and the wrong diagnosis. The indication is the device's own
    /// statement about the request as a whole, so it wins.
    /// </remarks>
    public void CheckSupported(Iin iin, string step)
    {
        if (iin.Has(Iin.NoFuncCodeSupport))
        {
            Supersede(new NotSupportedByPeerException(string.Format(
                CultureInfo.InvariantCulture,
                "master: file {0}: dnp3: not supported by peer", step)));
        }
    }
}

public sealed partial class MasterSession
{
    /// <summary>Returns the group 70 object a response carries.</summary>
    private static bool TryFileObject(
        Fragment frag, byte variation, out ReadOnlyMemory<byte> obj)
    {
        foreach (var h in frag.Objects)
        {
            if (h.Group != 70 || h.Variation != variation)
            {
                continue;
            }

            try
            {
                obj = FreeFormat.FirstObject(h);
                return true;
            }
            catch (MalformedException)
            {
                obj = default;
                return false;
            }
        }

        obj = default;
        return false;
    }

    /// <summary>
    /// Pulls the g70v6 an outstation reports a failed block with.
    /// </summary>
    private static bool TryStatusObject(Fragment frag, out FileTransportStatus status)
    {
        status = default;
        if (!TryFileObject(frag, 6, out var obj))
        {
            return false;
        }

        try
        {
            status = FileObjects.ParseTransportStatus(obj.Span);
            return true;
        }
        catch (MalformedException)
        {
            return false;
        }
    }

    /// <summary>Renders an outstation's explanation, when it gave one.</summary>
    private static string StatusText(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty : " (" + s + ")";

    private static FileTransferException Refused(
        FileStatus status, string what, string? text) =>
        new(status, what + StatusText(text));

    /// <summary>Opens a file and records the handle the outstation issues.</summary>
    private static MasterTask FileOpenTask(
        FileTransferState t, FileOpenMode mode, ushort blockSize, Func<MasterTask?> next) => new()
        {
            Name = "file-open",
            FuncCode = FuncCode.OpenFile,
            Priority = TaskPriority.Command,
            Build = b =>
            {
                var obj = new List<byte>(FileObjects.FileCommandSize + t.Name.Length);
                FileObjects.AppendCommand(obj, new FileCommand
                {
                    Name = t.Name,
                    Mode = mode,
                    Size = t.Size,
                    MaxBlockSize = blockSize,
                    RequestId = t.RequestId,
                });

                b.TryAddObject(FreeFormat.Build(70, 3, System.Runtime.InteropServices
                    .CollectionsMarshal.AsSpan(obj)));
            },
            OnFragment = frag =>
            {
                if (!TryFileObject(frag, 4, out var obj))
                {
                    return;
                }

                FileCommandStatus st;
                try
                {
                    st = FileObjects.ParseCommandStatus(obj.Span);
                }
                catch (MalformedException ex)
                {
                    t.Fail(new Dnp3Exception("master: file open: " + ex.Message));
                    return;
                }

                if (!st.Status.OK())
                {
                    t.Fail(Refused(st.Status, "master: opening " + t.Name, st.Text));
                    return;
                }

                t.Handle = st.Handle;
                t.Size = st.Size;

                // The outstation may come back with a smaller block than was
                // asked for, and its answer is the one that counts.
                t.BlockSize = st.MaxBlockSize > 0 && st.MaxBlockSize < blockSize
                    ? st.MaxBlockSize
                    : blockSize;
            },
            OnDone = iin =>
            {
                t.CheckSupported(iin, "open");
                if (t.Error is null && t.Handle == 0)
                {
                    t.Fail(new Dnp3Exception(string.Format(
                        CultureInfo.InvariantCulture,
                        "master: opening {0}: the outstation returned no handle", t.Name)));
                }
            },
            Next = () => t.Error is null ? next() : null,
        };

    /// <summary>Asks for one block and writes it out.</summary>
    private static MasterTask FileReadTask(FileTransferState t, Func<MasterTask?> next) => new()
    {
        Name = "file-read",
        FuncCode = FuncCode.Read,
        Priority = TaskPriority.Command,
        Build = b =>
        {
            var obj = new List<byte>(FileObjects.FileTransportSize);
            FileObjects.AppendTransport(obj, new FileTransport
            {
                Handle = t.Handle,
                Block = t.Block,
            });

            b.TryAddObject(FreeFormat.Build(70, 5, System.Runtime.InteropServices
                .CollectionsMarshal.AsSpan(obj)));
        },
        OnFragment = frag =>
        {
            if (TryStatusObject(frag, out var st))
            {
                t.Fail(Refused(
                    st.Status,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "master: reading {0} block {1}", t.Name, t.Block),
                    st.Text));
                return;
            }

            if (!TryFileObject(frag, 5, out var obj))
            {
                t.Fail(new Dnp3Exception(string.Format(
                    CultureInfo.InvariantCulture,
                    "master: reading {0} block {1}: the response carried no file data",
                    t.Name, t.Block)));
                return;
            }

            FileTransport block;
            try
            {
                block = FileObjects.ParseTransport(obj);
            }
            catch (MalformedException ex)
            {
                t.Fail(new Dnp3Exception(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "master: reading {0}: {1}", t.Name, ex.Message)));
                return;
            }

            if (block.Block != t.Block)
            {
                // Writing it anyway would put the file together in the wrong
                // order, which no checksum here would catch.
                t.Fail(new Dnp3Exception(string.Format(
                    CultureInfo.InvariantCulture,
                    "master: reading {0}: block {1} arrived where {2} was expected",
                    t.Name, block.Block, t.Block)));
                return;
            }

            if (!block.Data.IsEmpty)
            {
                try
                {
                    t.Destination!.Write(block.Data.Span);
                }
                catch (IOException ex)
                {
                    t.Fail(new Dnp3Exception(string.Format(
                        CultureInfo.InvariantCulture,
                        "master: writing {0} out: {1}", t.Name, ex.Message)));
                    return;
                }

                t.Transferred += block.Data.Length;
            }

            t.Block++;
            t.Last = block.Last;
        },
        OnDone = iin => t.CheckSupported(iin, "read"),
        Next = () => t.Error is null ? next() : null,
    };

    /// <summary>Sends one block.</summary>
    private static MasterTask FileWriteTask(
        FileTransferState t, byte[] data, bool last, Func<MasterTask?> next) => new()
        {
            Name = "file-write",
            FuncCode = FuncCode.Write,
            Priority = TaskPriority.Command,
            Build = b =>
            {
                var obj = new List<byte>(FileObjects.FileTransportSize + data.Length);
                FileObjects.AppendTransport(obj, new FileTransport
                {
                    Handle = t.Handle,
                    Block = t.Block,
                    Last = last,
                    Data = data,
                });

                b.TryAddObject(FreeFormat.Build(70, 5, System.Runtime.InteropServices
                    .CollectionsMarshal.AsSpan(obj)));
            },
            OnFragment = frag =>
            {
                if (!TryStatusObject(frag, out var st))
                {
                    // Some outstations acknowledge a block with an empty
                    // response. Taking silence for success is the only reading
                    // that lets them work, and a real failure still surfaces at
                    // the close.
                    return;
                }

                if (!st.Status.OK())
                {
                    t.Fail(Refused(
                        st.Status,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "master: writing {0} block {1}", t.Name, t.Block),
                        st.Text));
                }
            },
            OnDone = iin =>
            {
                t.CheckSupported(iin, "write");
                if (t.Error is null)
                {
                    t.Block++;
                    t.Transferred += data.Length;
                    t.Last = last;
                }
            },
            Next = () => t.Error is null ? next() : null,
        };

    /// <summary>Ends a transfer.</summary>
    private static MasterTask FileCloseTask(FileTransferState t) => new()
    {
        Name = "file-close",
        FuncCode = FuncCode.CloseFile,
        Priority = TaskPriority.Command,
        Build = b =>
        {
            var obj = new List<byte>(FileObjects.FileCommandStatusSize);
            FileObjects.AppendCommandStatus(obj, new FileCommandStatus
            {
                Handle = t.Handle,
                RequestId = t.RequestId,
            });

            b.TryAddObject(FreeFormat.Build(70, 4, System.Runtime.InteropServices
                .CollectionsMarshal.AsSpan(obj)));
        },
        OnFragment = frag =>
        {
            if (!TryFileObject(frag, 4, out var obj))
            {
                return;
            }

            FileCommandStatus st;
            try
            {
                st = FileObjects.ParseCommandStatus(obj.Span);
            }
            catch (MalformedException ex)
            {
                t.Fail(new Dnp3Exception(string.Format(
                    CultureInfo.InvariantCulture,
                    "master: closing {0}: {1}", t.Name, ex.Message)));
                return;
            }

            if (!st.Status.OK())
            {
                // A close that fails on a write means the file did not land,
                // however well the blocks went.
                t.Fail(Refused(st.Status, "master: closing " + t.Name, st.Text));
            }
        },
        OnDone = _ => t.Closed = true,
    };

    /// <summary>Removes a file.</summary>
    private static MasterTask FileDeleteTask(FileTransferState t) => new()
    {
        Name = "file-delete",
        FuncCode = FuncCode.DeleteFile,
        Priority = TaskPriority.Command,
        Build = b =>
        {
            var obj = new List<byte>(FileObjects.FileCommandSize + t.Name.Length);
            FileObjects.AppendCommand(obj, new FileCommand
            {
                Name = t.Name,
                Mode = FileOpenMode.Null,
                RequestId = t.RequestId,
            });

            b.TryAddObject(FreeFormat.Build(70, 3, System.Runtime.InteropServices
                .CollectionsMarshal.AsSpan(obj)));
        },
        OnFragment = frag =>
        {
            if (!TryFileObject(frag, 4, out var obj))
            {
                return;
            }

            FileCommandStatus st;
            try
            {
                st = FileObjects.ParseCommandStatus(obj.Span);
            }
            catch (MalformedException ex)
            {
                t.Fail(new Dnp3Exception(string.Format(
                    CultureInfo.InvariantCulture,
                    "master: deleting {0}: {1}", t.Name, ex.Message)));
                return;
            }

            if (!st.Status.OK())
            {
                t.Fail(Refused(st.Status, "master: deleting " + t.Name, st.Text));
            }
        },
        OnDone = iin => t.CheckSupported(iin, "delete"),
    };

    /// <summary>Describes a file without transferring it.</summary>
    private static MasterTask FileInfoTask(FileTransferState t, Action<FileEntry> onInfo) => new()
    {
        Name = "file-info",
        FuncCode = FuncCode.GetFileInfo,
        Priority = TaskPriority.Command,
        Build = b =>
        {
            // The request is the answer's own object with only the name filled
            // in, which is how the standard asks the question.
            var obj = new List<byte>(FileObjects.FileDescriptorSize + t.Name.Length);
            FileObjects.AppendDescriptor(obj, new FileDescriptor
            {
                Name = t.Name,
                RequestId = t.RequestId,
            });

            b.TryAddObject(FreeFormat.Build(70, 7, System.Runtime.InteropServices
                .CollectionsMarshal.AsSpan(obj)));
        },
        OnFragment = frag =>
        {
            if (TryFileObject(frag, 4, out var statusObj))
            {
                // The outstation answered with a status instead of a
                // descriptor, which is how it says the file is not there.
                try
                {
                    var st = FileObjects.ParseCommandStatus(statusObj.Span);
                    t.Fail(Refused(st.Status, "master: file info for " + t.Name, st.Text));
                    return;
                }
                catch (MalformedException)
                {
                    // Fall through to the descriptor path.
                }
            }

            if (!TryFileObject(frag, 7, out var obj))
            {
                t.Fail(new Dnp3Exception(string.Format(
                    CultureInfo.InvariantCulture,
                    "master: file info for {0}: the response carried no descriptor", t.Name)));
                return;
            }

            try
            {
                onInfo(FileObjects.ParseDescriptor(obj.Span).ToInfo());
            }
            catch (MalformedException ex)
            {
                t.Fail(new Dnp3Exception(string.Format(
                    CultureInfo.InvariantCulture,
                    "master: file info for {0}: {1}", t.Name, ex.Message)));
            }
        },
        OnDone = iin => t.CheckSupported(iin, "info"),
    };
}
