// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Device attributes and file transfer, against a second implementation.
//
// These two features are the ones where a bug is hardest to catch alone: both
// have encodings the framing layer cannot check — group 0 carries its own type
// and length, group 70 its own size — so a mistake produces octets that this
// library happily reads back and nothing else understands. Testing them against
// go-dnp3 is what turns "our parser agrees with our encoder" into evidence.

using System.Globalization;
using SharpDnp3.Channels;
using SharpDnp3.Master;

namespace SharpDnp3.Interop.Tests;

public class GoDnp3FeatureTests
{
    private static async Task<(MasterSession Master, Task Run, IChannel Channel)> ConnectAsync(
        int port, CancellationToken cancellationToken)
    {
        var master = new MasterSession(new MasterConfig
        {
            LocalAddr = 1,
            RemoteAddr = 10,
            ResponseTimeout = TimeSpan.FromSeconds(10),
        });

        var channel = new TcpClientChannel(
            string.Format(CultureInfo.InvariantCulture, "127.0.0.1:{0}", port), Retry.Default);

        var run = master.RunAsync(channel, cancellationToken);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !master.Connected)
        {
            await Task.Delay(20, cancellationToken);
        }

        Assert.True(master.Connected, "the master did not connect to the go-dnp3 outstation");
        return (master, run, channel);
    }

    /// <summary>
    /// Our master reads the group 0 attributes go-dnp3's outstation reports.
    /// </summary>
    /// <remarks>
    /// The variation is the attribute's identity rather than an encoding, so
    /// this exercises the one place in the protocol where the object header
    /// means something different — and where a parser that reads it the
    /// ordinary way rejects the whole fragment.
    /// </remarks>
    [Fact]
    public async Task OurMasterReadsGoAttributes()
    {
        var outstationBin = Peers.GoDnp3("dnp3-outstation");
        Assert.SkipUnless(
            outstationBin is not null,
            "go-dnp3 not available; set GO_DNP3_BIN to a directory holding dnp3-outstation");

        var port = Peers.FreePort();
        await using var peer = new PeerProcess(
            outstationBin!,
            "-listen", string.Format(CultureInfo.InvariantCulture, "127.0.0.1:{0}", port));

        await PeerProcess.WaitForPortAsync(port, TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (master, run, channel) = await ConnectAsync(port, cts.Token);

        try
        {
            var attrs = await master.ReadAttributesAsync(cts.Token);

            Assert.NotEmpty(attrs);

            // The counts the peer derives from its own database, which is what
            // proves the numbers came out as numbers rather than as octets.
            const byte binaryInputCount = SharpDnp3.Outstation.DerivedAttributeNumbers
                .BinaryInputCount;
            var binaries = attrs.SingleOrDefault(a => a.Variation == binaryInputCount);
            Assert.True(
                binaries.Variation == binaryInputCount,
                "the peer should report its binary input count");
            Assert.Equal(AttributeType.UnsignedInt, binaries.Type);
            Assert.True(binaries.Number > 0);

            // And the strings it is configured with.
            Assert.Contains(
                attrs,
                a => a.Type == AttributeType.VisibleString &&
                     !string.IsNullOrEmpty(a.ValueText()));

            // Reading one by name must agree with reading them all.
            var one = await master.ReadAttributeAsync(
                AttributeNumbers.StandardSet, binaryInputCount, cts.Token);
            Assert.Equal(binaries.Number, one.Number);
        }
        finally
        {
            await cts.CancelAsync();
            channel.Dispose();
            await Task.WhenAny(run, Task.Delay(3000));
        }
    }

    /// <summary>
    /// Our master reads a directory and a file from go-dnp3's outstation, which
    /// serves an in-memory filesystem by default.
    /// </summary>
    [Fact]
    public async Task OurMasterReadsGoFiles()
    {
        var outstationBin = Peers.GoDnp3("dnp3-outstation");
        Assert.SkipUnless(
            outstationBin is not null,
            "go-dnp3 not available; set GO_DNP3_BIN to a directory holding dnp3-outstation");

        var port = Peers.FreePort();
        await using var peer = new PeerProcess(
            outstationBin!,
            "-listen", string.Format(CultureInfo.InvariantCulture, "127.0.0.1:{0}", port));

        await PeerProcess.WaitForPortAsync(port, TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (master, run, channel) = await ConnectAsync(port, cts.Token);

        try
        {
            // A directory is read as a file whose contents are a run of
            // descriptors, so this proves the descriptor walk agrees with the
            // one that wrote them.
            var entries = await master.ReadDirectoryAsync("/", cts.Token);
            Assert.NotEmpty(entries);

            var file = entries.First(e => !e.IsDirectory && e.Size > 0);

            // The whole file, block by block, with the last-block flag ending
            // it. A device.txt from the peer's simulated RTU is a few hundred
            // octets, so this is one or two blocks.
            var content = await master.ReadFileBytesAsync("/" + file.Name, cts.Token);
            Assert.Equal((int)file.Size, content.Length);

            // And a file that is not there must come back classified, not as a
            // generic failure.
            var ex = await Assert.ThrowsAsync<FileTransferException>(
                () => master.ReadFileBytesAsync("/no-such-file", cts.Token));
            Assert.Equal(FileStatus.NotFound, ex.Status);
        }
        finally
        {
            await cts.CancelAsync();
            channel.Dispose();
            await Task.WhenAny(run, Task.Delay(3000));
        }
    }

    /// <summary>
    /// Our master writes a file to go-dnp3's outstation and reads back exactly
    /// what it sent.
    /// </summary>
    /// <remarks>
    /// The round trip is the point: a block size negotiated wrongly, or a
    /// last-block flag set on the wrong block, produces a file that is short or
    /// a transfer that never ends, and only reading it back catches either.
    /// </remarks>
    [Fact]
    public async Task OurMasterWritesAFileToGo()
    {
        var outstationBin = Peers.GoDnp3("dnp3-outstation");
        Assert.SkipUnless(
            outstationBin is not null,
            "go-dnp3 not available; set GO_DNP3_BIN to a directory holding dnp3-outstation");

        var port = Peers.FreePort();
        await using var peer = new PeerProcess(
            outstationBin!,
            "-listen", string.Format(CultureInfo.InvariantCulture, "127.0.0.1:{0}", port));

        await PeerProcess.WaitForPortAsync(port, TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (master, run, channel) = await ConnectAsync(port, cts.Token);

        try
        {
            // Long enough to span several blocks at the peer's default size.
            var content = new byte[5000];
            for (var i = 0; i < content.Length; i++)
            {
                content[i] = (byte)(i % 251);
            }

            await master.WriteFileBytesAsync("/from-sharp.bin", content, cts.Token);

            var got = await master.ReadFileBytesAsync("/from-sharp.bin", cts.Token);
            Assert.Equal(content, got);

            await master.DeleteFileAsync("/from-sharp.bin", cts.Token);
        }
        finally
        {
            await cts.CancelAsync();
            channel.Dispose();
            await Task.WhenAny(run, Task.Delay(3000));
        }
    }

    /// <summary>
    /// go-dnp3's master reads the attributes and files our outstation serves.
    /// </summary>
    /// <remarks>
    /// The other direction matters as much: this is what proves our encoder
    /// produces octets a second implementation accepts, rather than only ones
    /// our own parser is willing to read back.
    /// </remarks>
    [Fact]
    public async Task GoMasterReadsOurAttributesAndFiles()
    {
        // go-dnp3's own master CLI has no subcommand for either feature, so the
        // peer here is a small probe built against its master library. See
        // testdata/interop/goprobe.
        var probe = Peers.GoDnp3("goprobe");
        Assert.SkipUnless(
            probe is not null,
            "goprobe not available; build testdata/interop/goprobe into GO_DNP3_BIN");

        var dir = Path.Combine(
            Path.GetTempPath(), "sharpdnp3-interop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var content = "served to a go master over group 70\n"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(dir, "hello.txt"), content);

            var port = Peers.FreePort();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            var outstationConfig = new SharpDnp3.Outstation.OutstationConfig
            {
                LocalAddr = 10,
                RemoteAddr = 1,
                Database = new SharpDnp3.Outstation.DatabaseConfig
                {
                    Binary = 4, Analog = 2, DefaultClass = Class.Class1,
                },
                Files = new SharpDnp3.Outstation.FileConfig
                {
                    Handler = new SharpDnp3.Outstation.DirectoryFileHandler(dir),
                },
            };

            // The attributes are read once at construction, so they go in
            // before the session is built.
            outstationConfig.Attributes.Add(DeviceAttribute.String(252, "DSC Systems"));
            outstationConfig.Attributes.Add(DeviceAttribute.String(250, "SharpDnp3 test rig"));

            var outstation = new SharpDnp3.Outstation.OutstationSession(outstationConfig);

            using var channel = new TcpServerChannel(
                string.Format(CultureInfo.InvariantCulture, "127.0.0.1:{0}", port));

            var run = outstation.RunAsync(channel, cts.Token);
            await PeerProcess.WaitForPortAsync(port, TimeSpan.FromSeconds(10));

            try
            {
                var target = string.Format(CultureInfo.InvariantCulture, "127.0.0.1:{0}", port);

                // Group 0: the attributes we configured, and a count we derived.
                var attrs = await PeerProcess.RunToCompletionAsync(
                    probe!, TimeSpan.FromSeconds(40),
                    "-host", target, "-do", "attributes");

                Assert.Contains("DSC Systems", attrs, StringComparison.Ordinal);
                Assert.Contains("SharpDnp3 test rig", attrs, StringComparison.Ordinal);
                Assert.Contains("number of binary inputs", attrs, StringComparison.Ordinal);

                // Group 70: a directory listing, which is a run of descriptors
                // this outstation encoded.
                var listing = await PeerProcess.RunToCompletionAsync(
                    probe!, TimeSpan.FromSeconds(40),
                    "-host", target, "-do", "ls", "-path", "/");

                Assert.Contains("hello.txt", listing, StringComparison.Ordinal);

                // And the file itself, block by block.
                var file = await PeerProcess.RunToCompletionAsync(
                    probe!, TimeSpan.FromSeconds(40),
                    "-host", target, "-do", "get", "-path", "/hello.txt");

                Assert.Contains(
                    "served to a go master over group 70", file, StringComparison.Ordinal);
            }
            finally
            {
                await cts.CancelAsync();
                channel.Close();
                await Task.WhenAny(run, Task.Delay(3000));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Leaving a temporary directory behind must not fail the run.
            }
        }
    }
}
