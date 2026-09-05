// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// File transfer — group 70 — is how a master reads and writes the files on an
// outstation: configuration, firmware images, event logs, and the directory
// listings that say what is there.
//
// It is unlike the rest of DNP3. Everything else in the protocol names a point
// by index and carries a measurement; file transfer names a path, opens a
// handle, and moves opaque octets in numbered blocks. The types here are the
// vocabulary of that exchange; the wire encodings live in SharpDnp3.Objects,
// and the sequencing in SharpDnp3.Master and SharpDnp3.Outstation.

using System.Globalization;
using System.Text;

namespace SharpDnp3;

/// <summary>Says whether a directory entry is a file or another directory.</summary>
public enum FileType : ushort
{
    /// <summary>A directory, whose contents are a run of descriptors.</summary>
    Directory = 0,

    /// <summary>An ordinary file.</summary>
    Simple = 1,
}

/// <summary>What a file is opened for.</summary>
public enum FileOpenMode : ushort
{
    /// <summary>
    /// Opens nothing. It is what a command that names a file without
    /// transferring it — a delete — carries.
    /// </summary>
    Null = 0,

    /// <summary>Opens an existing file for reading.</summary>
    Read = 1,

    /// <summary>Creates or truncates a file for writing.</summary>
    Write = 2,

    /// <summary>Opens a file for writing at its end.</summary>
    Append = 3,
}

/// <summary>The outcome an outstation reports for a file operation.</summary>
/// <remarks>
/// The set is the standard's, including the gap between 9 and 16: the codes are
/// fixed by IEEE 1815 table 4-8 and are not renumbered here, because a capture
/// has to be readable against the specification.
/// </remarks>
public enum FileStatus : byte
{
    /// <summary>The operation succeeded.</summary>
    Success = 0,

    /// <summary>The outstation refused on permissions.</summary>
    PermissionDenied = 1,

    /// <summary>
    /// The requested mode is not one the outstation allows for that file —
    /// writing a read-only configuration, say.
    /// </summary>
    InvalidMode = 2,

    /// <summary>No such file.</summary>
    NotFound = 3,

    /// <summary>Another master has the file open.</summary>
    Locked = 4,

    /// <summary>The outstation has no free handle.</summary>
    TooManyOpen = 5,

    /// <summary>
    /// The handle is unknown, which is what a master sees after the outstation
    /// has timed the transfer out.
    /// </summary>
    InvalidHandle = 6,

    /// <summary>The block size is not one the outstation accepts.</summary>
    WriteBlockSize = 7,

    /// <summary>The link dropped mid-transfer.</summary>
    CommLost = 8,

    /// <summary>The transfer cannot be abandoned at this point.</summary>
    CannotAbort = 9,

    /// <summary>The handle names no open file.</summary>
    NotOpen = 16,

    /// <summary>
    /// The outstation closed the file itself after the master went quiet for
    /// longer than its inactivity timeout.
    /// </summary>
    HandleExpired = 17,

    /// <summary>The outstation ran out of room.</summary>
    BufferOverrun = 18,

    /// <summary>
    /// The transfer failed for a reason the outstation cannot describe more
    /// precisely; the file is not usable.
    /// </summary>
    Fatal = 19,

    /// <summary>
    /// A block arrived out of order, which invalidates everything written so
    /// far.
    /// </summary>
    BlockSequence = 20,

    /// <summary>An error the outstation could not classify.</summary>
    Undefined = 255,
}

/// <summary>Naming and error mapping for <see cref="FileStatus"/>.</summary>
public static class FileStatusExtensions
{
    private static readonly Dictionary<FileStatus, string> Names = new()
    {
        [FileStatus.Success] = "success",
        [FileStatus.PermissionDenied] = "permission denied",
        [FileStatus.InvalidMode] = "invalid mode",
        [FileStatus.NotFound] = "file not found",
        [FileStatus.Locked] = "file locked",
        [FileStatus.TooManyOpen] = "too many files open",
        [FileStatus.InvalidHandle] = "invalid handle",
        [FileStatus.WriteBlockSize] = "invalid block size",
        [FileStatus.CommLost] = "communications lost",
        [FileStatus.CannotAbort] = "cannot abort",
        [FileStatus.NotOpen] = "file not open",
        [FileStatus.HandleExpired] = "handle expired",
        [FileStatus.BufferOverrun] = "buffer overrun",
        [FileStatus.Fatal] = "fatal error",
        [FileStatus.BlockSequence] = "block sequence error",
        [FileStatus.Undefined] = "undefined error",
    };

    /// <summary>Renders the status using the standard's wording.</summary>
    public static string ToDisplayString(this FileStatus s) =>
        Names.TryGetValue(s, out var name)
            ? name
            : string.Format(CultureInfo.InvariantCulture, "FileStatus({0})", (byte)s);

    /// <summary>Reports whether the operation succeeded.</summary>
    public static bool OK(this FileStatus s) => s == FileStatus.Success;

    /// <summary>
    /// Returns <see langword="null"/> for <see cref="FileStatus.Success"/> and a
    /// <see cref="FileTransferException"/> otherwise.
    /// </summary>
    /// <remarks>
    /// So a caller can test the outcome by type and still print what the
    /// outstation actually said.
    /// </remarks>
    public static FileTransferException? ToException(this FileStatus s) =>
        s == FileStatus.Success ? null : new FileTransferException(s);
}

/// <summary>An outstation refused or failed a file operation.</summary>
public sealed class FileTransferException : Dnp3Exception
{
    /// <summary>Creates the exception from the status the outstation reported.</summary>
    public FileTransferException(FileStatus status)
        : base("dnp3: file transfer: " + status.ToDisplayString()) => Status = status;

    /// <summary>Creates the exception from a status and an explanation.</summary>
    public FileTransferException(FileStatus status, string detail)
        : base(string.Format(
            CultureInfo.InvariantCulture,
            "dnp3: file transfer: {0}: {1}", status.ToDisplayString(), detail)) => Status = status;

    /// <summary>What the outstation reported.</summary>
    public FileStatus Status { get; }
}

/// <summary>
/// The access bits of a file, laid out as POSIX lays them out: three groups of
/// read, write and execute.
/// </summary>
/// <remarks>Bit values are from IEEE 1815 table 4-7.</remarks>
[Flags]
public enum FilePermissions : ushort
{
    /// <summary>No permissions at all.</summary>
    None = 0,

    /// <summary>World execute.</summary>
    WorldExecute = 0x0001,

    /// <summary>World write.</summary>
    WorldWrite = 0x0002,

    /// <summary>World read.</summary>
    WorldRead = 0x0004,

    /// <summary>Group execute.</summary>
    GroupExecute = 0x0008,

    /// <summary>Group write.</summary>
    GroupWrite = 0x0010,

    /// <summary>Group read.</summary>
    GroupRead = 0x0020,

    /// <summary>Owner execute.</summary>
    OwnerExecute = 0x0040,

    /// <summary>Owner write.</summary>
    OwnerWrite = 0x0080,

    /// <summary>Owner read.</summary>
    OwnerRead = 0x0100,
}

/// <summary>Rendering helpers for <see cref="FilePermissions"/>.</summary>
public static class FilePermissionsExtensions
{
    private static readonly (FilePermissions Mask, char Char)[] Bits =
    [
        (FilePermissions.OwnerRead, 'r'),
        (FilePermissions.OwnerWrite, 'w'),
        (FilePermissions.OwnerExecute, 'x'),
        (FilePermissions.GroupRead, 'r'),
        (FilePermissions.GroupWrite, 'w'),
        (FilePermissions.GroupExecute, 'x'),
        (FilePermissions.WorldRead, 'r'),
        (FilePermissions.WorldWrite, 'w'),
        (FilePermissions.WorldExecute, 'x'),
    ];

    /// <summary>Renders the bits the way <c>ls</c> does.</summary>
    /// <remarks>
    /// So a directory listing from a device reads like one from a filesystem.
    /// </remarks>
    public static string ToDisplayString(this FilePermissions p)
    {
        var sb = new StringBuilder(Bits.Length);
        foreach (var (mask, ch) in Bits)
        {
            sb.Append((p & mask) != 0 ? ch : '-');
        }

        return sb.ToString();
    }
}

/// <summary>Describes one file or directory on an outstation.</summary>
public readonly record struct FileEntry
{
    /// <summary>The path the outstation knows the file by.</summary>
    /// <remarks>
    /// In a directory listing it is the entry's own name, not the full path.
    /// </remarks>
    public string Name { get; init; }

    /// <summary>Whether the entry is a file or a directory.</summary>
    public FileType Type { get; init; }

    /// <summary>The file's length in octets.</summary>
    public uint Size { get; init; }

    /// <summary>The file's creation time.</summary>
    /// <remarks>
    /// It is <see langword="default"/> when the outstation does not keep one,
    /// which many embedded devices do not.
    /// </remarks>
    public DateTimeOffset Created { get; init; }

    /// <summary>The access bits.</summary>
    public FilePermissions Permissions { get; init; }

    /// <summary>Reports whether the entry is a directory.</summary>
    public bool IsDirectory => Type == FileType.Directory;

    /// <inheritdoc/>
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "{0}{1} {2,8} {3}",
        IsDirectory ? "d" : "-",
        Permissions.ToDisplayString(),
        Size,
        Name);
}
