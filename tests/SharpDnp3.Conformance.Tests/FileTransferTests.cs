// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Group 70, where a master reads and writes the files on an outstation.
//
// These run a real master against a real outstation over a pipe, because a file
// transfer is a conversation rather than a request: the handle comes back from
// the open, the block size is negotiated, and only the outstation knows which
// block is the last. Nothing short of both ends talking proves that sequencing.

using SharpDnp3.Channels;
using SharpDnp3.Master;
using SharpDnp3.Outstation;

namespace SharpDnp3.Conformance.Tests;

public class FileTransferTests : IDisposable
{
    private readonly string _dir;

    public FileTransferTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sharpdnp3-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A test that left a handle open should not fail the run over it.
        }

        GC.SuppressFinalize(this);
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>
    /// Runs a master and an outstation over a pipe with file transfer wired up.
    /// </summary>
    private async Task WithPairAsync(
        Func<MasterSession, OutstationSession, Task> body,
        bool readOnly = false,
        ushort blockSize = 0)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var handler = new DirectoryFileHandler(_dir) { ReadOnly = readOnly };

        var outstation = new OutstationSession(new OutstationConfig
        {
            LocalAddr = Addresses.Outstation,
            RemoteAddr = Addresses.Master,
            Database = Requests.SmallDatabase(),
            Files = new FileConfig { Handler = handler, MaxBlockSize = blockSize },
        });

        var master = new MasterSession(new MasterConfig
        {
            LocalAddr = Addresses.Master,
            RemoteAddr = Addresses.Outstation,
        });

        var (masterChannel, outstationChannel) = Pipe.Create();
        using var mc = masterChannel;
        using var oc = outstationChannel;

        var outstationTask = outstation.RunAsync(oc, cts.Token);
        var masterTask = master.RunAsync(mc, cts.Token);

        try
        {
            await Harness.WaitForAsync(() => master.Connected, "the master to connect");
            await body(master, outstation).ConfigureAwait(false);
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            mc.Close();
            oc.Close();
            await Task.WhenAny(
                    Task.WhenAll(masterTask, outstationTask), Task.Delay(2000))
                .ConfigureAwait(false);
        }
    }

    /// <summary>A file read end to end returns exactly what is on disk.</summary>
    [Fact]
    public async Task ReadFileReturnsItsContents()
    {
        var content = "the quick brown fox jumps over the lazy dog"u8.ToArray();
        Write("readme.txt", content);

        await WithPairAsync(async (master, _) =>
        {
            var got = await master.ReadFileBytesAsync("/readme.txt");
            Assert.Equal(content, got);
        });
    }

    /// <summary>
    /// A file longer than one block is reassembled in order, and the last-block
    /// flag is what ends it.
    /// </summary>
    [Fact]
    public async Task ReadFileSpansSeveralBlocks()
    {
        var content = new byte[5000];
        for (var i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i % 251);
        }

        Write("big.bin", content);

        await WithPairAsync(
            async (master, _) =>
            {
                var got = await master.ReadFileBytesAsync("/big.bin");
                Assert.Equal(content, got);
            },
            blockSize: 256);
    }

    /// <summary>
    /// A file whose length is an exact multiple of the block size still ends
    /// cleanly. Without a look-ahead the outstation cannot tell the last full
    /// block from a middle one, and the master waits for a block that is never
    /// coming.
    /// </summary>
    [Fact]
    public async Task ReadFileEndingOnABlockBoundaryTerminates()
    {
        var content = new byte[512];
        Array.Fill(content, (byte)0x5A);
        Write("aligned.bin", content);

        await WithPairAsync(
            async (master, _) =>
            {
                var got = await master.ReadFileBytesAsync("/aligned.bin");
                Assert.Equal(content, got);
            },
            blockSize: 256);
    }

    /// <summary>An empty file still owes the master one last block.</summary>
    [Fact]
    public async Task ReadEmptyFileSucceeds()
    {
        Write("empty.txt", []);

        await WithPairAsync(async (master, _) =>
        {
            var got = await master.ReadFileBytesAsync("/empty.txt");
            Assert.Empty(got);
        });
    }

    /// <summary>A write lands on disk with exactly the octets sent.</summary>
    [Fact]
    public async Task WriteFileLandsOnDisk()
    {
        var content = "configuration written over DNP3"u8.ToArray();

        await WithPairAsync(async (master, _) =>
        {
            await master.WriteFileBytesAsync("/written.txt", content);
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(_dir, "written.txt")));
        });
    }

    /// <summary>And so does one that spans several blocks.</summary>
    [Fact]
    public async Task WriteFileSpansSeveralBlocks()
    {
        var content = new byte[3000];
        for (var i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i % 97);
        }

        await WithPairAsync(
            async (master, _) =>
            {
                await master.WriteFileBytesAsync("/big-write.bin", content);
                Assert.Equal(
                    content, File.ReadAllBytes(Path.Combine(_dir, "big-write.bin")));
            },
            blockSize: 256);
    }

    /// <summary>
    /// A directory is read exactly as a file is; what comes back is a run of
    /// descriptors.
    /// </summary>
    [Fact]
    public async Task ReadDirectoryListsItsEntries()
    {
        Write("one.txt", "1"u8.ToArray());
        Write("two.txt", "22"u8.ToArray());
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));

        await WithPairAsync(async (master, _) =>
        {
            var entries = await master.ReadDirectoryAsync("/");

            Assert.Equal(3, entries.Count);
            Assert.Contains(entries, e => e.Name == "one.txt" && e.Size == 1);
            Assert.Contains(entries, e => e.Name == "two.txt" && e.Size == 2);
            Assert.Contains(entries, e => e.Name == "sub" && e.IsDirectory);
        });
    }

    /// <summary>A file-info request describes a file without transferring it.</summary>
    [Fact]
    public async Task FileInfoDescribesTheFile()
    {
        Write("info.txt", new byte[42]);

        await WithPairAsync(async (master, _) =>
        {
            var info = await master.FileInfoAsync("/info.txt");

            Assert.Equal("info.txt", info.Name);
            Assert.Equal(42u, info.Size);
            Assert.False(info.IsDirectory);
        });
    }

    /// <summary>A delete removes the file.</summary>
    [Fact]
    public async Task DeleteRemovesTheFile()
    {
        var path = Write("doomed.txt", "x"u8.ToArray());

        await WithPairAsync(async (master, _) =>
        {
            await master.DeleteFileAsync("/doomed.txt");
            Assert.False(File.Exists(path));
        });
    }

    /// <summary>A missing file is reported as such, not as a generic failure.</summary>
    [Fact]
    public async Task MissingFileIsReportedAsNotFound()
    {
        await WithPairAsync(async (master, _) =>
        {
            var ex = await Assert.ThrowsAsync<FileTransferException>(
                () => master.ReadFileBytesAsync("/nope.txt"));

            Assert.Equal(FileStatus.NotFound, ex.Status);
        });
    }

    /// <summary>A read-only handler refuses a write and says why.</summary>
    [Fact]
    public async Task ReadOnlyHandlerRefusesWrites()
    {
        await WithPairAsync(
            async (master, _) =>
            {
                var ex = await Assert.ThrowsAsync<FileTransferException>(
                    () => master.WriteFileBytesAsync("/nope.txt", "x"u8.ToArray()));

                Assert.Equal(FileStatus.PermissionDenied, ex.Status);
            },
            readOnly: true);
    }

    /// <summary>And a delete.</summary>
    [Fact]
    public async Task ReadOnlyHandlerRefusesDeletes()
    {
        Write("safe.txt", "x"u8.ToArray());

        await WithPairAsync(
            async (master, _) =>
            {
                await Assert.ThrowsAsync<FileTransferException>(
                    () => master.DeleteFileAsync("/safe.txt"));

                Assert.True(File.Exists(Path.Combine(_dir, "safe.txt")));
            },
            readOnly: true);
    }

    /// <summary>
    /// A failed transfer still releases the handle, so the next one is not
    /// refused with "too many files open".
    /// </summary>
    [Fact]
    public async Task AFailedTransferDoesNotLockTheOutstationOut()
    {
        Write("good.txt", "content"u8.ToArray());

        await WithPairAsync(async (master, _) =>
        {
            await Assert.ThrowsAsync<FileTransferException>(
                () => master.ReadFileBytesAsync("/missing.txt"));

            // The next transfer must work, which it cannot if the outstation is
            // still holding a handle from the one that failed.
            var got = await master.ReadFileBytesAsync("/good.txt");
            Assert.Equal("content"u8.ToArray(), got);
        });
    }

    /// <summary>
    /// An outstation with no file handler answers the file function codes the
    /// way a device that does not implement them does.
    /// </summary>
    [Fact]
    public async Task FileFunctionCodesAreUnsupportedWithoutAHandler()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var outstation = new OutstationSession(new OutstationConfig
        {
            LocalAddr = Addresses.Outstation,
            RemoteAddr = Addresses.Master,
            Database = Requests.SmallDatabase(),
        });

        var master = new MasterSession(new MasterConfig
        {
            LocalAddr = Addresses.Master,
            RemoteAddr = Addresses.Outstation,
        });

        var (mc, oc) = Pipe.Create();
        using var masterChannel = mc;
        using var outstationChannel = oc;

        var outstationTask = outstation.RunAsync(outstationChannel, cts.Token);
        var masterTask = master.RunAsync(masterChannel, cts.Token);

        try
        {
            await Harness.WaitForAsync(() => master.Connected, "the master to connect");

            await Assert.ThrowsAsync<NotSupportedByPeerException>(
                () => master.ReadFileBytesAsync("/anything"));
        }
        finally
        {
            await cts.CancelAsync();
            masterChannel.Close();
            outstationChannel.Close();
            await Task.WhenAny(Task.WhenAll(masterTask, outstationTask), Task.Delay(2000));
        }
    }

    /// <summary>
    /// Path traversal is the obvious attack on file transfer. A name that
    /// climbs out of the served directory is collapsed against its root rather
    /// than followed.
    /// </summary>
    [Theory]
    [InlineData("/../escape.txt")]
    [InlineData("../../escape.txt")]
    [InlineData("/sub/../../escape.txt")]
    [InlineData(@"..\..\escape.txt")]
    public async Task PathTraversalCannotLeaveTheServedDirectory(string name)
    {
        var outside = Path.Combine(Path.GetDirectoryName(_dir)!, "escape.txt");
        await File.WriteAllTextAsync(outside, "secret");

        try
        {
            await WithPairAsync(async (master, _) =>
            {
                // Either it is refused outright, or it resolves to a file of
                // that name inside the served directory — which does not exist.
                // What it must never do is return what is outside.
                var ex = await Assert.ThrowsAsync<FileTransferException>(
                    () => master.ReadFileBytesAsync(name));

                Assert.True(
                    ex.Status is FileStatus.NotFound or FileStatus.PermissionDenied,
                    $"unexpected status {ex.Status.ToDisplayString()} for {name}");
            });
        }
        finally
        {
            File.Delete(outside);
        }
    }
}
