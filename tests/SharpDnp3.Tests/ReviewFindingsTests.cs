// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Regressions found reviewing go-dnp3, the reference implementation, and fixed
// in both: each test pins one of them.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SharpDnp3.App;
using SharpDnp3.Channels;
using SharpDnp3.Link;
using SharpDnp3.Master;
using SharpDnp3.Multidrop;
using SharpDnp3.Objects;
using SharpDnp3.Outstation;

namespace SharpDnp3.Tests;

public class ReviewFindingsTests
{
    // ---------- Parsing ----------

    /// <summary>
    /// A size-prefixed header with variation zero has no objects for its count
    /// to describe. It used to parse, leaving a 32-bit count unchecked against
    /// the data.
    /// </summary>
    [Fact]
    public void SizePrefixedVariationZeroRejected()
    {
        byte[] frag = [0xC0, 0x81, 0x00, 0x00, 0x57, 0x00, 0x59, 0xFF, 0xFF, 0xFF, 0xFF];
        var status = FragmentParser.ParseFragment(null, frag, out _, out _);
        Assert.NotEqual(AppParseStatus.Ok, status);
    }

    // ---------- Link ----------

    /// <summary>
    /// A link that failed while the peer had DFC raised kept it latched. The
    /// link was then idle, nothing idle ever clears DFC, and every later send
    /// was refused instead of starting a fresh reset handshake.
    /// </summary>
    [Fact]
    public void PrimaryFailureClearsDfc()
    {
        var p = new Primary { LocalAddr = 1, RemoteAddr = 10, UseConfirms = true, MaxRetries = 3 };

        p.Send(new byte[] { 0xC0 });
        p.OnFrame(Secondary(LinkFunction.Ack, dfc: false)); // link reset, data goes out

        var (_, action) = p.OnFrame(Secondary(LinkFunction.NotSupported, dfc: true));
        Assert.Equal(LinkAction.Failed, action);
        Assert.False(p.DataFlowControl, "DFC stayed latched after the link failed");

        p.Send(new byte[] { 0xC1 });
    }

    private static LinkFrame Secondary(LinkFunction fn, bool dfc) => new()
    {
        Header = new LinkHeader(
            Control: new Control(Dir: false, Prm: false, Fcb: false, Fcv: dfc, Func: fn),
            Dest: 1,
            Src: 10,
            Length: LinkConstants.MinLength),
        Payload = ReadOnlyMemory<byte>.Empty,
    };

    // ---------- Events ----------

    /// <summary>
    /// An event with no encoding used to be skipped yet stay selected, so the
    /// response's confirmation deleted it as though the master had received
    /// it. It now comes back as unsent, is discarded, and counts as a loss the
    /// overflow indication reports; the encodable event beside it is still the
    /// only one the confirmation removes.
    /// </summary>
    [Fact]
    public void UnencodableEventsAreNotConfirmedAway()
    {
        var events = new EventBuffer();
        var db = new Database(new DatabaseConfig { Binary = 2 }, events);
        events.Add(new Event { Type = PointType.Binary, Index = 0, Class = Class.Class1, Variation = 99 });
        events.Add(new Event { Type = PointType.Binary, Index = 1, Class = Class.Class1, Variation = 1 });

        var selected = events.Select(Class.Class1, 10);
        var unsent = new ResponseWriter(db).BuildEvents(new ResponseBuilder(0, new Context()), selected);

        var only = Assert.Single(unsent);
        Assert.Equal(0, only.Index);
        Assert.Equal(1, events.Discard(unsent));
        Assert.True(events.Overflowed, "discarding an unreportable event did not set the overflow indication");
        Assert.Equal(1, events.Confirm());
    }

    [Fact]
    public void ConfigureRejectsUnencodableEventVariation()
    {
        var db = new Database(new DatabaseConfig { Binary = 1 });
        Assert.False(db.Configure(PointType.Binary, 0, new PointConfig { EventVariation = 99 }));
        Assert.True(db.Configure(PointType.Binary, 0, new PointConfig { EventVariation = 2 }));
    }

    // ---------- The outstation session ----------

    /// <summary>
    /// Update used to queue 64 batches and run the 65th on the caller's thread,
    /// where it overtook the queued ones. Batches now apply in submission order
    /// however many are waiting.
    /// </summary>
    [Fact]
    public async Task UpdateKeepsSubmissionOrder()
    {
        var session = new OutstationSession(new OutstationConfig { Database = new DatabaseConfig { Binary = 1 } });
        var order = new List<int>();

        session.Update(_ => order.Add(-1));
        Assert.True(order.Count == 1, "with the session not running, Update did not apply the batch before returning");

        var (_, outstationSide) = Pipe.Create();
        using var cts = new CancellationTokenSource();

        // Held up so the batches queue behind one another rather than being
        // applied as they arrive.
        using var gate = new ManualResetEventSlim();
        var run = session.RunAsync(outstationSide, cts.Token);
        // Update must only queue while the session serves: one that waited for
        // the running batch would never get to open the gate.
        var stalled = false;
        session.Update(_ => stalled = !gate.Wait(TimeSpan.FromSeconds(5)));
        for (var i = 0; i < 200; i++)
        {
            var n = i;
            session.Update(_ => order.Add(n));
        }

        gate.Set();
        await cts.CancelAsync();
        outstationSide.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        // Applied by the session, or handed over in order when it stopped.
        session.Update(_ => order.Add(200));

        Assert.False(stalled, "Update waited for a running batch instead of queuing behind it");
        Assert.Equal(202, order.Count);
        for (var i = 1; i < order.Count; i++)
        {
            Assert.Equal(i - 1, order[i]);
        }
    }

    /// <summary>
    /// A null application accepted a master's clock write and discarded it, so
    /// the outstation cleared NEED_TIME and reported synchronised timestamps
    /// from a clock nobody had set. The write now takes effect.
    /// </summary>
    [Fact]
    public void NullApplicationAppliesClockWrites()
    {
        var appl = new OffsetClockApplication();
        var want = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        Assert.True(appl.WriteAbsoluteTime(want));
        Assert.True((appl.Now() - want).Duration() < TimeSpan.FromSeconds(1), $"Now() = {appl.Now()} after setting {want}");
    }

    /// <summary>
    /// A class 0 poll reported octet strings while an explicit read of group
    /// 110 was refused as an unknown object.
    /// </summary>
    [Fact]
    public async Task OctetStringsReadByGroup()
    {
        await using var pair = new TestPair(configureOutstation: c => c.Database.OctetString = 3);
        await pair.WaitConnectedAsync();

        pair.Outstation.Update(db =>
        {
            db.UpdateOctetString(1, "Feeder 2"u8);
            db.UpdateOctetString(2, "Bus tie"u8);
        });

        await pair.Master.ScanRangeAsync(110, 0, 1, 2);
        await TestPair.WaitForAsync(() => pair.Handler.OctetStrings.Count >= 2, "the strings");
        Assert.Equal("Feeder 2"u8.ToArray(), pair.Handler.OctetStrings[1]);
        Assert.Equal("Bus tie"u8.ToArray(), pair.Handler.OctetStrings[2]);
    }

    /// <summary>
    /// DisableAttributes is a device without group 0. The derived point counts
    /// used to be reported regardless.
    /// </summary>
    [Fact]
    public async Task DisabledAttributesAreNotSupported()
    {
        await using var pair = new TestPair(configureOutstation: c =>
        {
            c.Attributes.Add(DeviceAttribute.String(250, "RTU"));
            c.DisableAttributes = true;
        });
        await pair.WaitConnectedAsync();

        await Assert.ThrowsAnyAsync<NotSupportedByPeerException>(() => pair.Master.ReadAttributesAsync());
        await Assert.ThrowsAnyAsync<NotSupportedByPeerException>(() =>
            pair.Master.ReadAttributeAsync(AttributeNumbers.StandardSet, DerivedAttributeNumbers.BinaryInputCount));
    }

    // ---------- The master ----------

    /// <summary>
    /// The master sent a link-confirmed frame without arming the
    /// retransmission timer, and while a request was in flight it slept until
    /// the response deadline regardless of the link. Against a peer that never
    /// acknowledges, the first retransmission has to come one link timeout
    /// after the frame.
    /// </summary>
    [Fact]
    public async Task ConfirmedLinkRetransmitsAtTheLinkTimeout()
    {
        var linkTimeout = TimeSpan.FromMilliseconds(200);
        var (raw, outstationSide) = Pipe.Create();
        var recording = new RecordingChannel(raw);

        var master = new MasterSession(new MasterConfig
        {
            LocalAddr = 1,
            RemoteAddr = 10,
            ResponseTimeout = TimeSpan.FromSeconds(10),
            UseLinkConfirms = true,
            LinkTimeout = linkTimeout,
            LinkRetries = 3,
        }, new NopHandler());

        using var cts = new CancellationTokenSource();
        var run = master.RunAsync(recording, cts.Token);
        var peer = Task.Run(async () =>
        {
            // A peer that reads everything and answers nothing.
            var conn = await outstationSide.ConnectAsync(cts.Token);
            await conn.CopyToAsync(Stream.Null, cts.Token);
        });

        try
        {
            _ = master.IntegrityPollAsync(cts.Token);

            DateTime first = default, second = default;
            var start = DateTime.UtcNow;
            await TestPair.WaitForAsync(() =>
            {
                var n = recording.Writes;
                if (n >= 1 && first == default)
                {
                    first = DateTime.UtcNow;
                }

                if (n >= 2)
                {
                    second = DateTime.UtcNow;
                    return true;
                }

                return false;
            }, "a retransmission", TimeSpan.FromSeconds(3));

            Assert.True(second - first >= linkTimeout / 2, $"retransmitted {second - first} after the first frame, before the {linkTimeout} link timeout");
            Assert.True(second - start < TimeSpan.FromSeconds(2), $"the first retransmission took {second - start}; the link timeout is {linkTimeout}");
        }
        finally
        {
            await cts.CancelAsync();
            recording.Close();
            outstationSide.Close();
            await Task.WhenAny(Task.WhenAll(run, peer), Task.Delay(2000));
        }
    }

    /// <summary>The whole point of the no-reply variant: nothing comes back.</summary>
    [Fact]
    public async Task DirectOperateNoReplyIsNotAnswered()
    {
        await using var pair = new TestPair();
        await pair.WaitConnectedAsync();

        // The master's startup clears the restart indication, and the answer
        // to that must be counted before the snapshot, not mistaken for an
        // answer to the operate.
        await TestPair.WaitForAsync(() => pair.Outstation.Stats.ResponsesSent >= 1, "the startup write's answer");
        var before = pair.Outstation.Stats.ResponsesSent;

        await pair.Master.DirectOperateNoReplyAsync([Command.LatchOn(1)]);
        await TestPair.WaitForAsync(() => pair.Commands.Read(c => c.Operated.Count) >= 1, "the operate");

        await Task.Delay(100);
        Assert.Equal(before, pair.Outstation.Stats.ResponsesSent);
        Assert.Equal(OperateType.DirectNoAck, pair.Commands.Read(c => c.Operated[0].Op));
    }

    /// <summary>Counts the writes made through the connections it hands out.</summary>
    private sealed class RecordingChannel(IChannel inner) : IChannel
    {
        private int _writes;

        public int Writes => Volatile.Read(ref _writes);

        public async Task<Stream> ConnectAsync(CancellationToken cancellationToken) =>
            new CountingStream(await inner.ConnectAsync(cancellationToken), this);

        public void Close() => inner.Close();

        public void Dispose() => inner.Dispose();

        private sealed class CountingStream(Stream inner, RecordingChannel owner) : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() => inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                inner.ReadAsync(buffer, cancellationToken);

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                Interlocked.Increment(ref owner._writes);
                inner.Write(buffer, offset, count);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner._writes);
                return inner.WriteAsync(buffer, cancellationToken);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }

    // ---------- Channels ----------

    /// <summary>
    /// A zero Factor used to multiply the delay to nothing, so a channel that
    /// could not connect retried in a hot spin. A zero Max left the delay
    /// uncapped.
    /// </summary>
    [Fact]
    public void RetryZeroFieldsStaySane()
    {
        var noFactor = new Retry { Min = TimeSpan.FromMilliseconds(100), Max = TimeSpan.FromSeconds(1) };
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(100), noFactor.Delay(i));
        }

        var noMax = new Retry { Min = TimeSpan.FromSeconds(1), Factor = 2 };
        foreach (var n in new[] { 10, 100, 10000 })
        {
            Assert.Equal(Retry.Default.Max, noMax.Delay(n));
        }
    }

    /// <summary>
    /// Closing a server while it waits in accept is a shutdown, and has to say
    /// so: the sessions return cleanly on a closed channel and treat any other
    /// connect error as a failure.
    /// </summary>
    [Fact]
    public async Task TcpServerCloseDuringAcceptIsChannelClosed()
    {
        var server = new TcpServerChannel("127.0.0.1:0");
        var connect = server.ConnectAsync(CancellationToken.None);
        await TestPair.WaitForAsync(() => server.BoundAddress is not null, "the listener");
        await Task.Delay(20);
        server.Close();

        var done = await Task.WhenAny(connect, Task.Delay(5000));
        Assert.Same(connect, done);
        await Assert.ThrowsAsync<ChannelClosedException>(() => connect);
    }

    /// <summary>
    /// The server's connect completes the TLS handshake, bounded in time, and a
    /// peer that fails it is dropped while the server goes on to the next one
    /// rather than giving up.
    /// </summary>
    [Fact]
    public async Task TlsServerSurvivesAPeerThatFailsTheHandshake()
    {
        using var dir = new TempDir();
        var (ca, serverCert, serverKey, clientCert, clientKey) = Certificates.Write(dir.Path);

        using var server = new TlsServerChannel("127.0.0.1:0", new Dnp3TlsConfig
        {
            CertFile = serverCert,
            KeyFile = serverKey,
            CaFile = ca,
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepted = server.ConnectAsync(cts.Token);
        await TestPair.WaitForAsync(() => server.BoundAddress is not null, "the listener");
        var addr = server.BoundAddress!;

        // A peer that speaks something other than TLS.
        using (var junk = new TcpClient())
        {
            await junk.ConnectAsync(addr.Address, addr.Port);
            await junk.GetStream().WriteAsync("GET / HTTP/1.0\r\n\r\n"u8.ToArray());
        }

        using var client = new TlsClientChannel(
            $"127.0.0.1:{addr.Port}",
            new Dnp3TlsConfig { CertFile = clientCert, KeyFile = clientKey, CaFile = ca, ServerName = "localhost" },
            Retry.None);
        await using var clientConn = await client.ConnectAsync(cts.Token);

        var done = await Task.WhenAny(accepted, Task.Delay(5000));
        Assert.Same(accepted, done);
        await using var serverConn = await accepted;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("sharpdnp3-tls-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>A throwaway CA and the server and client certificates it signs.</summary>
    private static class Certificates
    {
        public static (string Ca, string ServerCert, string ServerKey, string ClientCert, string ClientKey) Write(string dir)
        {
            using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var caReq = new CertificateRequest("CN=test-ca", caKey, HashAlgorithmName.SHA256);
            caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            using var caCert = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            var ca = System.IO.Path.Combine(dir, "ca.pem");
            File.WriteAllText(ca, caCert.ExportCertificatePem());

            var (serverCert, serverKey) = Leaf(dir, "server", caCert, serverAuth: true);
            var (clientCert, clientKey) = Leaf(dir, "client", caCert, serverAuth: false);
            return (ca, serverCert, serverKey, clientCert, clientKey);
        }

        private static (string Cert, string Key) Leaf(string dir, string name, X509Certificate2 ca, bool serverAuth)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            req.CertificateExtensions.Add(san.Build());
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid(serverAuth ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], false));

            var serial = RandomNumberGenerator.GetBytes(8);
            using var cert = req.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(12), serial);

            var certPath = System.IO.Path.Combine(dir, name + ".pem");
            var keyPath = System.IO.Path.Combine(dir, name + "-key.pem");
            File.WriteAllText(certPath, cert.ExportCertificatePem());
            File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
            return (certPath, keyPath);
        }
    }

    // ---------- Multidrop ----------

    /// <summary>
    /// A bus closed directly, against the advice to release it, stayed
    /// registered and was handed to the next caller already dead.
    /// </summary>
    [Fact]
    public void RegistryReplacesADirectlyClosedBus()
    {
        var registry = new BusRegistry();

        var first = registry.Open(new NamedChannel("serial /dev/ttyUSB0 9600/8N1"));
        first.Dispose();

        var channel = new NamedChannel("serial /dev/ttyUSB0 9600/8N1");
        var second = registry.Open(channel);
        try
        {
            Assert.NotSame(first, second);
            Assert.False(second.IsClosed, "Open returned the bus that had already been closed");
            Assert.False(channel.Closed, "the channel the new bus needs was closed as though redundant");
            Assert.Equal(1, registry.Count);
        }
        finally
        {
            registry.Release(second);
        }
    }

    private sealed class NamedChannel(string name) : IChannel
    {
        public bool Closed { get; private set; }

        public Task<Stream> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromException<Stream>(new ChannelClosedException());

        public void Close() => Closed = true;

        public void Dispose() => Close();

        public override string ToString() => name;
    }
}
