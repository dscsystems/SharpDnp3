// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Group 70 is hand-written for a different reason from the commands: its
// objects are variable length. Every other object in the table has a size the
// generator can state, so a parser knows how many octets to take before it
// looks at them. A file command carries a name, a transport object carries a
// block of file data, and both are as long as they are — which is why the
// qualifier that carries them puts an explicit size in front of each object.
//
// These are the only codecs in this namespace that can fail. The rest are total
// functions over a buffer the framing layer has already measured; here the
// object measures itself, and a device that declares a name longer than the
// object holding it has to be refused rather than sliced.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SharpDnp3.Objects;

/// <summary>A group 70 object could not be decoded.</summary>
public sealed class FileObjectException : MalformedException
{
    /// <summary>Creates the exception with a message.</summary>
    public FileObjectException(string message) : base("objects: file object: " + message) { }
}

/// <summary>A group 70 variation 2 authentication object.</summary>
/// <remarks>
/// A master sends it to exchange credentials for the authentication key that a
/// later open command carries.
/// </remarks>
public readonly record struct FileAuth
{
    /// <summary>The user name offered.</summary>
    public string User { get; init; }

    /// <summary>The password offered.</summary>
    public string Password { get; init; }

    /// <summary>
    /// The authentication key: zero in the request, and the value the
    /// outstation issues in its reply.
    /// </summary>
    public uint Key { get; init; }
}

/// <summary>
/// A group 70 variation 3 object: the request that opens or deletes a file.
/// </summary>
public readonly record struct FileCommand
{
    /// <summary>The path the file is known by.</summary>
    public string Name { get; init; }

    /// <summary>
    /// The file's creation time, which a master sets when writing a file and
    /// leaves at <see langword="default"/> otherwise.
    /// </summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>The access bits to create the file with.</summary>
    public FilePermissions Permissions { get; init; }

    /// <summary>
    /// The authentication key from a prior authentication exchange. Zero where
    /// the outstation does not require one.
    /// </summary>
    public uint Key { get; init; }

    /// <summary>
    /// The length of a file being written. It is zero when reading, where the
    /// outstation is the one that knows.
    /// </summary>
    public uint Size { get; init; }

    /// <summary>What the file is being opened for.</summary>
    public FileOpenMode Mode { get; init; }

    /// <summary>
    /// The largest block the master will accept. The outstation answers with
    /// the size it will actually use, which may be smaller.
    /// </summary>
    public ushort MaxBlockSize { get; init; }

    /// <summary>Ties a response to the request that caused it.</summary>
    public ushort RequestId { get; init; }
}

/// <summary>
/// A group 70 variation 4 object: what an outstation answers an open, close or
/// delete with, and what a master sends to close a file it has open.
/// </summary>
public readonly record struct FileCommandStatus
{
    /// <summary>Identifies the open file for the rest of the transfer.</summary>
    /// <remarks>
    /// It is the outstation's to choose, and a master must send back exactly
    /// what it was given.
    /// </remarks>
    public uint Handle { get; init; }

    /// <summary>
    /// The file's length, which is how a master reading a file learns how much
    /// to expect.
    /// </summary>
    public uint Size { get; init; }

    /// <summary>The block size the outstation has settled on.</summary>
    public ushort MaxBlockSize { get; init; }

    /// <summary>Echoes the request this answers.</summary>
    public ushort RequestId { get; init; }

    /// <summary>The outcome.</summary>
    public FileStatus Status { get; init; }

    /// <summary>An optional human-readable explanation.</summary>
    /// <remarks>Devices use it to say what a status code could not.</remarks>
    public string? Text { get; init; }
}

/// <summary>A group 70 variation 5 object: one block of a file.</summary>
public readonly record struct FileTransport
{
    /// <summary>The open file's handle.</summary>
    public uint Handle { get; init; }

    /// <summary>The block number, counting from zero and rising by one.</summary>
    public uint Block { get; init; }

    /// <summary>
    /// Marks the final block of the transfer, which is what tells the receiver
    /// the file is complete rather than merely paused.
    /// </summary>
    public bool Last { get; init; }

    /// <summary>The block's octets.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }
}

/// <summary>
/// A group 70 variation 6 object: the acknowledgement of one written block.
/// </summary>
public readonly record struct FileTransportStatus
{
    /// <summary>The open file's handle.</summary>
    public uint Handle { get; init; }

    /// <summary>The block being acknowledged.</summary>
    public uint Block { get; init; }

    /// <summary>Whether that block was the last of the transfer.</summary>
    public bool Last { get; init; }

    /// <summary>The outcome.</summary>
    public FileStatus Status { get; init; }

    /// <summary>An optional human-readable explanation.</summary>
    public string? Text { get; init; }
}

/// <summary>
/// A group 70 variation 7 object: what a file is, rather than what it contains.
/// </summary>
/// <remarks>
/// It answers a file-info request, and a directory's contents are nothing but a
/// run of these.
/// </remarks>
public readonly record struct FileDescriptor
{
    /// <summary>The entry's name.</summary>
    public string Name { get; init; }

    /// <summary>Whether it is a file or a directory.</summary>
    public FileType Type { get; init; }

    /// <summary>The file's length in octets.</summary>
    public uint Size { get; init; }

    /// <summary>The file's creation time, or <see langword="default"/>.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>The access bits.</summary>
    public FilePermissions Permissions { get; init; }

    /// <summary>Echoes the request this answers.</summary>
    public ushort RequestId { get; init; }

    /// <summary>Converts the descriptor to the value a caller of the master API sees.</summary>
    public FileEntry ToInfo() => new()
    {
        Name = Name,
        Type = Type,
        Size = Size,
        Created = Created,
        Permissions = Permissions,
    };

    /// <summary>Builds the descriptor that reports <paramref name="info"/>.</summary>
    public static FileDescriptor For(FileEntry info, ushort requestId) => new()
    {
        Name = info.Name,
        Type = info.Type,
        Size = info.Size,
        Created = info.Created,
        Permissions = info.Permissions,
        RequestId = requestId,
    };
}

/// <summary>Encodes and decodes the group 70 file transfer objects.</summary>
public static class FileObjects
{
    /// <summary>The fixed portion of a g70v2 authentication object.</summary>
    public const int FileAuthSize = 12;

    /// <summary>The fixed portion of a g70v3 file command.</summary>
    public const int FileCommandSize = 26;

    /// <summary>The fixed portion of a g70v4 command status.</summary>
    public const int FileCommandStatusSize = 13;

    /// <summary>The fixed portion of a g70v5 transport object.</summary>
    public const int FileTransportSize = 8;

    /// <summary>The fixed portion of a g70v6 transport status.</summary>
    public const int FileTransportStatusSize = 9;

    /// <summary>The fixed portion of a g70v7 file descriptor.</summary>
    public const int FileDescriptorSize = 20;

    /// <summary>
    /// The top bit of a block number, set on the final block of a transfer.
    /// </summary>
    /// <remarks>
    /// Without it neither end could tell a short last block from a block that
    /// merely happened to be short.
    /// </remarks>
    private const uint FileLastBlock = 1u << 31;

    // ---------- g70v2: file authentication ----------

    /// <summary>Decodes a group 70 variation 2 object.</summary>
    public static FileAuth ParseAuth(ReadOnlySpan<byte> buf)
    {
        Require(buf.Length, FileAuthSize, "g70v2");

        return new FileAuth
        {
            User = SliceString(
                buf, "user name",
                BinaryPrimitives.ReadUInt16LittleEndian(buf[0..2]),
                BinaryPrimitives.ReadUInt16LittleEndian(buf[2..4]),
                FileAuthSize),
            Password = SliceString(
                buf, "password",
                BinaryPrimitives.ReadUInt16LittleEndian(buf[4..6]),
                BinaryPrimitives.ReadUInt16LittleEndian(buf[6..8]),
                FileAuthSize),
            Key = BinaryPrimitives.ReadUInt32LittleEndian(buf[8..12]),
        };
    }

    /// <summary>Encodes a group 70 variation 2 object.</summary>
    public static void AppendAuth(List<byte> dst, FileAuth a)
    {
        ArgumentNullException.ThrowIfNull(dst);

        var user = Ascii(a.User);
        var password = Ascii(a.Password);

        AppendUInt16(dst, FileAuthSize);
        AppendUInt16(dst, (ushort)user.Length);
        AppendUInt16(dst, (ushort)(FileAuthSize + user.Length));
        AppendUInt16(dst, (ushort)password.Length);
        AppendUInt32(dst, a.Key);
        dst.AddRange(user);
        dst.AddRange(password);
    }

    // ---------- g70v3: file command ----------

    /// <summary>Decodes a group 70 variation 3 object.</summary>
    public static FileCommand ParseCommand(ReadOnlySpan<byte> buf)
    {
        Require(buf.Length, FileCommandSize, "g70v3");

        return new FileCommand
        {
            Name = SliceString(
                buf, "file name",
                BinaryPrimitives.ReadUInt16LittleEndian(buf[0..2]),
                BinaryPrimitives.ReadUInt16LittleEndian(buf[2..4]),
                FileCommandSize),
            Created = ParseFileTime(buf[4..10]),
            Permissions = (FilePermissions)BinaryPrimitives.ReadUInt16LittleEndian(buf[10..12]),
            Key = BinaryPrimitives.ReadUInt32LittleEndian(buf[12..16]),
            Size = BinaryPrimitives.ReadUInt32LittleEndian(buf[16..20]),
            Mode = (FileOpenMode)BinaryPrimitives.ReadUInt16LittleEndian(buf[20..22]),
            MaxBlockSize = BinaryPrimitives.ReadUInt16LittleEndian(buf[22..24]),
            RequestId = BinaryPrimitives.ReadUInt16LittleEndian(buf[24..26]),
        };
    }

    /// <summary>Encodes a group 70 variation 3 object.</summary>
    public static void AppendCommand(List<byte> dst, FileCommand c)
    {
        ArgumentNullException.ThrowIfNull(dst);

        var name = Ascii(c.Name);

        AppendUInt16(dst, FileCommandSize);
        AppendUInt16(dst, (ushort)name.Length);
        AppendFileTime(dst, c.Created);
        AppendUInt16(dst, (ushort)c.Permissions);
        AppendUInt32(dst, c.Key);
        AppendUInt32(dst, c.Size);
        AppendUInt16(dst, (ushort)c.Mode);
        AppendUInt16(dst, c.MaxBlockSize);
        AppendUInt16(dst, c.RequestId);
        dst.AddRange(name);
    }

    // ---------- g70v4: file command status ----------

    /// <summary>Decodes a group 70 variation 4 object.</summary>
    public static FileCommandStatus ParseCommandStatus(ReadOnlySpan<byte> buf)
    {
        Require(buf.Length, FileCommandStatusSize, "g70v4");

        return new FileCommandStatus
        {
            Handle = BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]),
            Size = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..8]),
            MaxBlockSize = BinaryPrimitives.ReadUInt16LittleEndian(buf[8..10]),
            RequestId = BinaryPrimitives.ReadUInt16LittleEndian(buf[10..12]),
            Status = (FileStatus)buf[12],
            Text = Encoding.ASCII.GetString(buf[FileCommandStatusSize..]),
        };
    }

    /// <summary>Encodes a group 70 variation 4 object.</summary>
    public static void AppendCommandStatus(List<byte> dst, FileCommandStatus s)
    {
        ArgumentNullException.ThrowIfNull(dst);

        AppendUInt32(dst, s.Handle);
        AppendUInt32(dst, s.Size);
        AppendUInt16(dst, s.MaxBlockSize);
        AppendUInt16(dst, s.RequestId);
        dst.Add((byte)s.Status);
        dst.AddRange(Ascii(s.Text));
    }

    // ---------- g70v5: file transport ----------

    /// <summary>Decodes a group 70 variation 5 object.</summary>
    /// <remarks>The returned data aliases <paramref name="buf"/>.</remarks>
    public static FileTransport ParseTransport(ReadOnlyMemory<byte> buf)
    {
        Require(buf.Length, FileTransportSize, "g70v5");

        var span = buf.Span;
        var block = BinaryPrimitives.ReadUInt32LittleEndian(span[4..8]);

        return new FileTransport
        {
            Handle = BinaryPrimitives.ReadUInt32LittleEndian(span[0..4]),
            Block = block & ~FileLastBlock,
            Last = (block & FileLastBlock) != 0,
            Data = buf[FileTransportSize..],
        };
    }

    /// <summary>Encodes a group 70 variation 5 object.</summary>
    public static void AppendTransport(List<byte> dst, FileTransport t)
    {
        ArgumentNullException.ThrowIfNull(dst);

        var block = t.Block & ~FileLastBlock;
        if (t.Last)
        {
            block |= FileLastBlock;
        }

        AppendUInt32(dst, t.Handle);
        AppendUInt32(dst, block);
        dst.AddRange(t.Data.Span);
    }

    // ---------- g70v6: file transport status ----------

    /// <summary>Decodes a group 70 variation 6 object.</summary>
    public static FileTransportStatus ParseTransportStatus(ReadOnlySpan<byte> buf)
    {
        Require(buf.Length, FileTransportStatusSize, "g70v6");

        var block = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..8]);

        return new FileTransportStatus
        {
            Handle = BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]),
            Block = block & ~FileLastBlock,
            Last = (block & FileLastBlock) != 0,
            Status = (FileStatus)buf[8],
            Text = Encoding.ASCII.GetString(buf[FileTransportStatusSize..]),
        };
    }

    /// <summary>Encodes a group 70 variation 6 object.</summary>
    public static void AppendTransportStatus(List<byte> dst, FileTransportStatus s)
    {
        ArgumentNullException.ThrowIfNull(dst);

        var block = s.Block & ~FileLastBlock;
        if (s.Last)
        {
            block |= FileLastBlock;
        }

        AppendUInt32(dst, s.Handle);
        AppendUInt32(dst, block);
        dst.Add((byte)s.Status);
        dst.AddRange(Ascii(s.Text));
    }

    // ---------- g70v7: file descriptor ----------

    /// <summary>Decodes a group 70 variation 7 object.</summary>
    public static FileDescriptor ParseDescriptor(ReadOnlySpan<byte> buf) =>
        ParseDescriptor(buf, out _);

    /// <summary>
    /// Decodes one descriptor and reports how many octets it consumed, which is
    /// what lets directory contents be walked.
    /// </summary>
    public static FileDescriptor ParseDescriptor(ReadOnlySpan<byte> buf, out int consumed)
    {
        Require(buf.Length, FileDescriptorSize, "g70v7");

        var offset = BinaryPrimitives.ReadUInt16LittleEndian(buf[0..2]);
        var size = BinaryPrimitives.ReadUInt16LittleEndian(buf[2..4]);

        var d = new FileDescriptor
        {
            Name = SliceString(buf, "file name", offset, size, FileDescriptorSize),
            Type = (FileType)BinaryPrimitives.ReadUInt16LittleEndian(buf[4..6]),
            Size = BinaryPrimitives.ReadUInt32LittleEndian(buf[6..10]),
            Created = ParseFileTime(buf[10..16]),
            Permissions = (FilePermissions)BinaryPrimitives.ReadUInt16LittleEndian(buf[16..18]),
            RequestId = BinaryPrimitives.ReadUInt16LittleEndian(buf[18..20]),
        };

        // The entry is as long as its name reaches, but never shorter than the
        // fixed part: an entry with no name declares an offset of its own
        // choosing, and a length below the fixed size would walk backwards.
        consumed = Math.Max(offset + size, FileDescriptorSize);
        return d;
    }

    /// <summary>Encodes a group 70 variation 7 object.</summary>
    public static void AppendDescriptor(List<byte> dst, FileDescriptor d)
    {
        ArgumentNullException.ThrowIfNull(dst);

        var name = Ascii(d.Name);

        AppendUInt16(dst, FileDescriptorSize);
        AppendUInt16(dst, (ushort)name.Length);
        AppendUInt16(dst, (ushort)d.Type);
        AppendUInt32(dst, d.Size);
        AppendFileTime(dst, d.Created);
        AppendUInt16(dst, (ushort)d.Permissions);
        AppendUInt16(dst, d.RequestId);
        dst.AddRange(name);
    }

    /// <summary>Decodes the contents of a directory file.</summary>
    /// <remarks>
    /// Reading a directory is reading a file: the octets that come back are a
    /// run of file descriptors laid end to end, each one saying how long it is.
    /// That is why the descriptor carries a name offset at all — it is what
    /// makes the run walkable.
    /// </remarks>
    public static List<FileEntry> ParseDirectory(ReadOnlySpan<byte> data)
    {
        var output = new List<FileEntry>();

        var off = 0;
        while (off < data.Length)
        {
            FileDescriptor d;
            int n;
            try
            {
                d = ParseDescriptor(data[off..], out n);
            }
            catch (FileObjectException ex)
            {
                throw new FileObjectException(string.Format(
                    CultureInfo.InvariantCulture,
                    "directory entry {0} at offset {1}: {2}", output.Count, off, ex.Message));
            }

            output.Add(d.ToInfo());
            off += n;
        }

        return output;
    }

    // ---------- shared helpers ----------

    private static void Require(int have, int need, string what)
    {
        if (have < need)
        {
            throw new FileObjectException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} is {1} octets, needs {2}", what, have, need));
        }
    }

    /// <summary>Extracts a length-prefixed string an object points at.</summary>
    /// <remarks>
    /// The offset is honoured rather than assumed: the standard puts one in the
    /// object precisely so a device may lay out its variable part however it
    /// likes, and a parser that took the fixed size on trust would misread
    /// anything that did.
    /// </remarks>
    private static string SliceString(
        ReadOnlySpan<byte> buf, string what, ushort offset, ushort size, int fixedSize)
    {
        if (size == 0)
        {
            return string.Empty;
        }

        if (offset < fixedSize)
        {
            throw new FileObjectException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} starts at {1}, inside the object's fixed {2} octets",
                what, offset, fixedSize));
        }

        var end = offset + size;
        if (end > buf.Length)
        {
            throw new FileObjectException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} runs to {1} in a {2} octet object", what, end, buf.Length));
        }

        return Encoding.ASCII.GetString(buf[offset..end]);
    }

    /// <summary>Decodes a creation time.</summary>
    /// <remarks>
    /// The zero value maps to the default rather than to 1970 — a device that
    /// keeps no creation time sends zero, and reporting that as a date is worse
    /// than reporting nothing.
    /// </remarks>
    private static DateTimeOffset ParseFileTime(ReadOnlySpan<byte> buf)
    {
        var ms = ObjectConvert.ReadTime48(buf);
        return ms == 0 ? default : Dnp3Time.FromDnp3(ms);
    }

    /// <summary>Encodes a creation time, mapping the default back to zero.</summary>
    private static void AppendFileTime(List<byte> dst, DateTimeOffset t) =>
        ObjectConvert.AppendTime48(dst, t == default ? 0 : Dnp3Time.ToDnp3(t));

    private static byte[] Ascii(string? s) =>
        string.IsNullOrEmpty(s) ? [] : Encoding.ASCII.GetBytes(s);

    private static void AppendUInt16(List<byte> dst, ushort v)
    {
        dst.Add((byte)v);
        dst.Add((byte)(v >> 8));
    }

    private static void AppendUInt32(List<byte> dst, uint v)
    {
        dst.Add((byte)v);
        dst.Add((byte)(v >> 8));
        dst.Add((byte)(v >> 16));
        dst.Add((byte)(v >> 24));
    }
}
