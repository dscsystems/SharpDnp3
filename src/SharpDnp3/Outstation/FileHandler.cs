// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// File transfer is the one part of an outstation that reaches outside its own
// point database. Everything else answers from memory the application already
// owns; this hands a master a path and lets it read, write or delete what is
// behind it. That is a large amount of authority to grant over a protocol with
// no authentication of its own, which is why it is off unless a handler is
// configured, and why the handler this library ships cannot be talked out of
// its directory.

using System.Globalization;

namespace SharpDnp3.Outstation;

/// <summary>Parameterises file transfer.</summary>
public sealed class FileConfig
{
    /// <summary>
    /// The block size an outstation offers when a master asks for more than it
    /// wants to send at once.
    /// </summary>
    /// <remarks>
    /// It is well inside a 2048-octet fragment, leaving room for the headers
    /// around it.
    /// </remarks>
    public const ushort DefaultBlockSize = 1024;

    /// <summary>
    /// How long a transfer the master has stopped talking about is kept open.
    /// </summary>
    /// <remarks>
    /// Without it a master that goes away mid-file leaves the outstation
    /// holding a handle — and, on a device that allows one transfer at a time,
    /// refusing every later one.
    /// </remarks>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Serves the requests.</summary>
    /// <remarks>
    /// <see langword="null"/> disables file transfer entirely: the outstation
    /// answers the file function codes the way a device without them does, with
    /// NO_FUNC_CODE_SUPPORT.
    /// </remarks>
    public IFileHandler? Handler { get; set; }

    /// <summary>
    /// Caps the block size, whatever the master asks for. Zero uses
    /// <see cref="DefaultBlockSize"/>.
    /// </summary>
    public ushort MaxBlockSize { get; set; }

    /// <summary>
    /// Closes a transfer that has gone quiet. Zero uses
    /// <see cref="DefaultTimeout"/>.
    /// </summary>
    public TimeSpan Timeout { get; set; }

    internal void ApplyDefaults()
    {
        if (MaxBlockSize == 0)
        {
            MaxBlockSize = DefaultBlockSize;
        }

        if (Timeout <= TimeSpan.Zero)
        {
            Timeout = DefaultTimeout;
        }
    }
}

/// <summary>Serves the file transfer requests a master makes.</summary>
/// <remarks>
/// <para>
/// Every method reports a <see cref="FileStatus"/> rather than throwing,
/// because the status is what goes on the wire: a master distinguishes a
/// missing file from a denied one, and flattening both into "failed" would
/// leave it unable to tell whether retrying could ever work.
/// </para>
/// <para>
/// The session calls these from its own loop, one at a time, so an
/// implementation needs no locking of its own.
/// </para>
/// </remarks>
public interface IFileHandler
{
    /// <summary>Describes a file without opening it.</summary>
    /// <remarks>
    /// It is what decides whether a read is served as a file or as a directory
    /// listing.
    /// </remarks>
    FileStatus Info(string name, out FileEntry info);

    /// <summary>Returns the entries of a directory.</summary>
    /// <remarks>
    /// The session encodes them; an implementation never sees the wire format.
    /// </remarks>
    FileStatus List(string name, out IReadOnlyList<FileEntry> entries);

    /// <summary>Opens a file for reading.</summary>
    /// <remarks>
    /// The session disposes the stream when the transfer ends, times out, or
    /// the connection drops.
    /// </remarks>
    FileStatus OpenRead(string name, out Stream? stream, out FileEntry info);

    /// <summary>Opens a file for writing.</summary>
    /// <param name="name">The path to write.</param>
    /// <param name="mode">
    /// <see cref="FileOpenMode.Write"/> to replace the file or
    /// <see cref="FileOpenMode.Append"/> to add to it.
    /// </param>
    /// <param name="size">
    /// The length the master says it will send, which may be zero when it does
    /// not know.
    /// </param>
    /// <param name="stream">Receives the opened stream.</param>
    FileStatus OpenWrite(string name, FileOpenMode mode, uint size, out Stream? stream);

    /// <summary>Removes a file.</summary>
    FileStatus Delete(string name);
}

/// <summary>Refuses every request.</summary>
/// <remarks>
/// It is what a handler that has not been wired up should be: an outstation
/// that answers a file request with anything other than a refusal is claiming
/// to have served it.
/// </remarks>
public sealed class RejectingFileHandler : IFileHandler
{
    /// <inheritdoc/>
    public FileStatus Info(string name, out FileEntry info)
    {
        info = default;
        return FileStatus.PermissionDenied;
    }

    /// <inheritdoc/>
    public FileStatus List(string name, out IReadOnlyList<FileEntry> entries)
    {
        entries = [];
        return FileStatus.PermissionDenied;
    }

    /// <inheritdoc/>
    public FileStatus OpenRead(string name, out Stream? stream, out FileEntry info)
    {
        stream = null;
        info = default;
        return FileStatus.PermissionDenied;
    }

    /// <inheritdoc/>
    public FileStatus OpenWrite(string name, FileOpenMode mode, uint size, out Stream? stream)
    {
        stream = null;
        return FileStatus.PermissionDenied;
    }

    /// <inheritdoc/>
    public FileStatus Delete(string name) => FileStatus.PermissionDenied;
}

/// <summary>Serves one directory and nothing above it.</summary>
/// <remarks>
/// <para>
/// Path traversal is the obvious attack on file transfer. The defence here is
/// not to sanitise the name and hope: every resolved path is compared against
/// the root's fully-resolved real path, symbolic links included, and anything
/// that lands outside is refused. A name is first cleaned as though the served
/// directory were the whole filesystem — which is what a device does and what a
/// master expects, since DNP3 names are POSIX-shaped and usually absolute — so
/// "/../../etc/passwd" collapses to "etc/passwd" inside the served directory
/// before the check even runs.
/// </para>
/// </remarks>
public sealed class DirectoryFileHandler : IFileHandler
{
    private readonly string _root;

    /// <summary>
    /// Refuses writes and deletes. A device that exposes its configuration for
    /// inspection but not for modification sets it.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>Returns a handler rooted at <paramref name="directory"/>.</summary>
    /// <exception cref="DirectoryNotFoundException">
    /// The directory does not exist.
    /// </exception>
    public DirectoryFileHandler(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(string.Format(
                CultureInfo.InvariantCulture, "outstation: no such directory: {0}", directory));
        }

        // Resolved once, links and all, so every later comparison is against
        // the real location rather than the path the caller happened to type.
        _root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(new DirectoryInfo(directory).ResolveLinkTarget(true)?.FullName
                             ?? directory));
    }

    /// <summary>
    /// Turns a DNP3 file name into a full path inside the root, or reports why
    /// it cannot.
    /// </summary>
    /// <remarks>
    /// The two stages matter separately. Cleaning collapses every "\.\." against
    /// the root so a relative escape cannot be expressed at all; the containment
    /// check afterwards catches what cleaning cannot see — a symbolic link
    /// inside the directory pointing out of it.
    /// </remarks>
    private bool TryResolve(string name, out string path)
    {
        path = string.Empty;

        var cleaned = (name ?? string.Empty).Replace('\\', '/').Trim();
        var parts = new List<string>();
        foreach (var segment in cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                // Collapsed against the root rather than escaping it, which is
                // what treating the served directory as the whole filesystem
                // means.
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(segment);
        }

        var candidate = parts.Count == 0
            ? _root
            : Path.GetFullPath(Path.Combine(_root, Path.Combine([.. parts])));

        // Resolve links on whatever part of the path exists, then confirm the
        // result is still inside the root.
        var real = RealPath(candidate);
        if (!IsInsideRoot(real))
        {
            return false;
        }

        path = candidate;
        return true;
    }

    /// <summary>
    /// Resolves symbolic links on the longest existing prefix of a path.
    /// </summary>
    /// <remarks>
    /// A file being created does not exist yet, so the check has to be made
    /// against the deepest directory that does — which is the one a link would
    /// have to be planted in.
    /// </remarks>
    private static string RealPath(string candidate)
    {
        var current = candidate;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if (File.Exists(current))
                {
                    var target = new System.IO.FileInfo(current).ResolveLinkTarget(true);
                    return Path.GetFullPath(target?.FullName ?? current);
                }

                if (Directory.Exists(current))
                {
                    var target = new DirectoryInfo(current).ResolveLinkTarget(true);
                    return Path.GetFullPath(target?.FullName ?? current);
                }
            }
            catch (IOException)
            {
                return current;
            }
            catch (UnauthorizedAccessException)
            {
                return current;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
            {
                return candidate;
            }

            // The tail below the deepest existing directory cannot contain a
            // link, because nothing there exists to be one.
            var resolvedParent = RealPath(parent);
            return Path.GetFullPath(
                Path.Combine(resolvedParent, Path.GetFileName(current)));
        }

        return candidate;
    }

    private bool IsInsideRoot(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(full, _root, PathComparison))
        {
            return true;
        }

        return full.StartsWith(_root + Path.DirectorySeparatorChar, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>Maps a filesystem exception to what the master should be told.</summary>
    private static FileStatus StatusFor(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => FileStatus.NotFound,
        UnauthorizedAccessException => FileStatus.PermissionDenied,

        // Anything else. FATAL rather than a guess: the master learns the
        // operation will not work, which is the actionable part.
        _ => FileStatus.Fatal,
    };

    /// <inheritdoc/>
    public FileStatus Info(string name, out FileEntry info)
    {
        info = default;
        if (!TryResolve(name, out var path))
        {
            return FileStatus.PermissionDenied;
        }

        try
        {
            if (Directory.Exists(path))
            {
                info = InfoFor(new DirectoryInfo(path));
                return FileStatus.Success;
            }

            var f = new System.IO.FileInfo(path);
            if (!f.Exists)
            {
                return FileStatus.NotFound;
            }

            info = InfoFor(f);
            return FileStatus.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusFor(ex);
        }
    }

    /// <inheritdoc/>
    public FileStatus List(string name, out IReadOnlyList<FileEntry> entries)
    {
        entries = [];
        if (!TryResolve(name, out var path))
        {
            return FileStatus.PermissionDenied;
        }

        try
        {
            if (!Directory.Exists(path))
            {
                return FileStatus.NotFound;
            }

            var output = new List<FileEntry>();
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                try
                {
                    output.Add(InfoFor(entry));
                }
                catch (IOException)
                {
                    // A file that vanished between the listing and the stat.
                    // Reporting the rest is better than failing the whole
                    // directory.
                }
            }

            entries = output;
            return FileStatus.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusFor(ex);
        }
    }

    /// <inheritdoc/>
    public FileStatus OpenRead(string name, out Stream? stream, out FileEntry info)
    {
        stream = null;
        info = default;

        if (!TryResolve(name, out var path))
        {
            return FileStatus.PermissionDenied;
        }

        try
        {
            if (Directory.Exists(path))
            {
                // A directory is read as a listing, which the session builds.
                // Opening one as a stream would hand back raw directory octets.
                return FileStatus.InvalidMode;
            }

            var f = new System.IO.FileInfo(path);
            if (!f.Exists)
            {
                return FileStatus.NotFound;
            }

            info = InfoFor(f);
            stream = f.OpenRead();
            return FileStatus.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusFor(ex);
        }
    }

    /// <inheritdoc/>
    public FileStatus OpenWrite(string name, FileOpenMode mode, uint size, out Stream? stream)
    {
        stream = null;
        if (ReadOnly)
        {
            return FileStatus.PermissionDenied;
        }

        if (!TryResolve(name, out var path))
        {
            return FileStatus.PermissionDenied;
        }

        try
        {
            stream = mode == FileOpenMode.Append
                ? new FileStream(path, System.IO.FileMode.Append, FileAccess.Write)
                : new FileStream(path, System.IO.FileMode.Create, FileAccess.Write);
            return FileStatus.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusFor(ex);
        }
    }

    /// <inheritdoc/>
    public FileStatus Delete(string name)
    {
        if (ReadOnly)
        {
            return FileStatus.PermissionDenied;
        }

        if (!TryResolve(name, out var path))
        {
            return FileStatus.PermissionDenied;
        }

        try
        {
            if (!File.Exists(path))
            {
                return FileStatus.NotFound;
            }

            File.Delete(path);
            return FileStatus.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusFor(ex);
        }
    }

    /// <summary>Converts a filesystem entry to what the protocol reports.</summary>
    /// <remarks>
    /// The modification time stands in for the creation time: a master
    /// comparing a file it wrote against what came back cares about when the
    /// content last changed, and many filesystems keep only that.
    /// </remarks>
    private static FileEntry InfoFor(FileSystemInfo entry)
    {
        var isDir = entry is DirectoryInfo;
        uint size = 0;
        if (entry is System.IO.FileInfo f)
        {
            // The protocol's size field is 32 bits. A file larger than that
            // cannot be described, and reporting a truncated length would have
            // the master stop reading early and believe it had the whole thing.
            size = (uint)Math.Min(f.Length, uint.MaxValue);
        }

        return new FileEntry
        {
            Name = entry.Name,
            Type = isDir ? FileType.Directory : FileType.Simple,
            Size = size,
            Created = entry.LastWriteTimeUtc,
            Permissions = PermissionsFor(entry),
        };
    }

    /// <summary>Maps what the platform will say onto the protocol's nine bits.</summary>
    /// <remarks>
    /// On Unix the mode bits map straight across, being the same nine bits in
    /// the same order. Windows has no such bits, so read is assumed and write is
    /// reported from the read-only attribute — which is the only part of the
    /// answer it can actually give.
    /// </remarks>
    private static FilePermissions PermissionsFor(FileSystemInfo entry)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (FilePermissions)((ushort)entry.UnixFileMode & 0x01FF);
        }

        var p = FilePermissions.OwnerRead | FilePermissions.GroupRead | FilePermissions.WorldRead;
        if ((entry.Attributes & FileAttributes.ReadOnly) == 0)
        {
            p |= FilePermissions.OwnerWrite;
        }

        if (entry is DirectoryInfo)
        {
            p |= FilePermissions.OwnerExecute |
                 FilePermissions.GroupExecute |
                 FilePermissions.WorldExecute;
        }

        return p;
    }
}
