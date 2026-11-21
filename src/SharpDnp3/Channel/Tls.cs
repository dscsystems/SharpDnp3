// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using SharpDnp3.Security;
using AuthenticationException = System.Security.Authentication.AuthenticationException;

namespace SharpDnp3.Channels;

/// <summary>Describes a mutually authenticated TLS channel.</summary>
/// <remarks>
/// Mutual authentication is not optional here. DNP3 carries controls that
/// operate plant, and a channel that authenticates only the server lets anyone
/// who can reach the port issue them. IEC 62351-3 requires both sides to
/// present certificates, and this refuses to build a configuration that does
/// not.
/// </remarks>
public sealed class Dnp3TlsConfig
{
    /// <summary>This end's certificate.</summary>
    public string CertFile { get; set; } = "";

    /// <summary>This end's private key.</summary>
    public string KeyFile { get; set; } = "";

    /// <summary>The authority that signs the peer's certificate.</summary>
    public string CaFile { get; set; } = "";

    /// <summary>
    /// The name to verify against the peer's certificate. For a client it
    /// defaults to the dialled host.
    /// </summary>
    public string ServerName { get; set; } = "";

    /// <summary>
    /// The lowest TLS version to accept. <see cref="SslProtocols.None"/> uses
    /// TLS 1.2, the floor IEC 62351 sets.
    /// </summary>
    public SslProtocols MinVersion { get; set; } = SslProtocols.None;

    /// <summary>Additional verification after the configured certificate and name checks pass.</summary>
    public Func<SecurityTlsPeer, bool>? VerifyPeer { get; set; }

    internal bool Verify(
        X509Certificate2Collection authority, X509Certificate? peer, X509Chain? chain,
        SslPolicyErrors errors, SecurityObservation? observation, bool server)
    {
        var trusted = VerifyAgainst(authority, peer, chain, errors);
        var metadata = peer is not null && (observation is not null || VerifyPeer is not null)
            ? DescribeCertificate(peer) with { Verified = trusted }
            : null;
        if (trusted && metadata is { } certificate && VerifyPeer is { } verify)
        {
            try
            {
                trusted = verify(certificate);
            }
            catch (Exception)
            {
                trusted = false;
            }
        }

        if (!trusted)
        {
            observation?.Emit(new(server && peer is null
                ? SecurityEventKind.TLSNoClientCert : SecurityEventKind.TLSCertRejected)
            {
                Action = SecurityAction.Rejected,
                Peer = observation.Peer is { } endpoint ? endpoint with { Tls = metadata } : null,
            });
        }

        return trusted;
    }

    internal static SecurityTlsPeer DescribeCertificate(X509Certificate certificate)
    {
        using var cert = new X509Certificate2(certificate);
        return new SecurityTlsPeer(cert.Subject, cert.Issuer, cert.SerialNumber,
            cert.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant(), cert.NotAfter.ToUniversalTime());
    }

    internal static string HandshakeFailureKind(Exception error, TlsHandshakePrefix? prefix = null)
    {
        if (error is OperationCanceledException)
        {
            return SecurityEventKind.TLSHandshakeTimeout;
        }

        if (prefix?.NonTlsKind is { } nonTls)
        {
            return nonTls;
        }

        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
        {
            var message = cause.Message;
            if (message.Contains("alert unknown ca", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("alert bad certificate", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("alert certificate required", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("alert certificate expired", StringComparison.OrdinalIgnoreCase))
            {
                return SecurityEventKind.TLSLocalCertRejected;
            }
        }

        return SecurityEventKind.TLSHandshakeFailed;
    }

    internal static void ReportExpiry(SecurityObservation? observation, SecurityTlsPeer? certificate)
    {
        if (observation is not null && certificate is not null &&
            observation.Policy.CertExpiryWarning >= TimeSpan.Zero &&
            certificate.NotAfter - DateTimeOffset.UtcNow <= observation.Policy.CertExpiryWarning)
        {
            observation.Emit(new(SecurityEventKind.TLSCertExpiring));
        }
    }

    internal SslProtocols EffectiveProtocols => MinVersion == SslProtocols.None
        ? SslProtocols.Tls12 | SslProtocols.Tls13
        : MinVersion;

    /// <summary>Loads this end's certificate and the authority to verify with.</summary>
    internal (X509Certificate2 Certificate, X509Certificate2Collection Authority) Load()
    {
        if (string.IsNullOrEmpty(CertFile) || string.IsNullOrEmpty(KeyFile))
        {
            throw new BadConfigException("channel: TLS requires a certificate and key");
        }

        if (string.IsNullOrEmpty(CaFile))
        {
            throw new BadConfigException(
                "channel: TLS requires a CA certificate to verify the peer; a DNP3 channel " +
                "that does not authenticate its peer lets anyone who can reach the port operate plant");
        }

        X509Certificate2 cert;
        try
        {
            cert = X509Certificate2.CreateFromPemFile(CertFile, KeyFile);

            // A key read from PEM is ephemeral, and SChannel cannot use an
            // ephemeral key to authenticate: on Windows every handshake would
            // end with the peer seeing the connection close. A round trip
            // through PKCS#12 gives it a key it can use.
            if (OperatingSystem.IsWindows())
            {
                using var pem = cert;
                cert = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
            }
        }
        catch (Exception ex) when (ex is IOException or CryptographicException)
        {
            throw new BadConfigException("channel: loading the TLS key pair: " + ex.Message);
        }

        var authority = new X509Certificate2Collection();
        try
        {
            authority.ImportFromPemFile(CaFile);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException)
        {
            throw new BadConfigException("channel: reading the CA certificate: " + ex.Message);
        }

        if (authority.Count == 0)
        {
            throw new BadConfigException(string.Format(
                CultureInfo.InvariantCulture,
                "channel: {0} contains no usable certificates", CaFile));
        }

        return (cert, authority);
    }

    /// <summary>
    /// Verifies the peer's chain against the configured authority alone, rather
    /// than the machine's trust store.
    /// </summary>
    /// <remarks>
    /// A substation's certificates are issued by the utility, not by a public
    /// CA, and trusting the machine store as well would let any public issuer
    /// vouch for a device that operates plant.
    /// </remarks>
    internal static bool VerifyAgainst(
        X509Certificate2Collection authority,
        X509Certificate? peer,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (peer is null)
        {
            return false;
        }

        // A name mismatch is still a failure; only the chain's trust anchor is
        // being replaced here.
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        using var verifier = new X509Chain();
        verifier.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        verifier.ChainPolicy.CustomTrustStore.AddRange(authority);
        verifier.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        using var cert = new X509Certificate2(peer);
        return verifier.Build(cert);
    }
}

/// <summary>
/// A channel that dials an address over TLS, retrying with backoff.
/// </summary>
public sealed class TlsClientChannel : IChannel, IObservedChannel
{
    private readonly string _address;
    private readonly Dnp3TlsConfig _tls;
    private readonly Retry _retry;
    private readonly string _serverName;
    private readonly X509Certificate2 _certificate;
    private readonly X509Certificate2Collection _authority;
    private int _attempt;
    private volatile bool _closed;

    /// <summary>How long a single dial may take before it is abandoned.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Creates a channel that dials <paramref name="address"/> over TLS.</summary>
    public TlsClientChannel(string address, Dnp3TlsConfig tls, Retry retry)
    {
        ArgumentNullException.ThrowIfNull(tls);

        _address = address;
        _tls = tls;
        _retry = retry;

        var (host, _) = EndpointParser.Split(address);
        _serverName = string.IsNullOrEmpty(tls.ServerName) ? host : tls.ServerName;
        (_certificate, _authority) = tls.Load();
    }

    /// <inheritdoc/>
    public Task<Stream> ConnectAsync(CancellationToken cancellationToken) =>
        ConnectAsync(null, cancellationToken);

    Task<Stream> IObservedChannel.ConnectAsync(SecurityRuntime? security, CancellationToken cancellationToken) =>
        ConnectAsync(security, cancellationToken);

    private async Task<Stream> ConnectAsync(SecurityRuntime? security, CancellationToken cancellationToken)
    {
        var (host, port) = EndpointParser.Split(_address);

        while (true)
        {
            if (_closed)
            {
                throw new ChannelClosedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var client = new TcpClient();
            SslStream? ssl = null;
            SecurityObservation? observation = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ConnectTimeout);

                await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
                client.NoDelay = true;
                observation = security?.ForPeer("tls", client.Client.RemoteEndPoint as IPEndPoint);

                ssl = new SslStream(
                    client.GetStream(),
                    leaveInnerStreamOpen: false,
                    (_, cert, chain, errors) =>
                        _tls.Verify(_authority, cert, chain, errors, observation, server: false));

                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = _serverName,
                        ClientCertificates = [_certificate],
                        EnabledSslProtocols = _tls.EffectiveProtocols,
                    },
                    timeout.Token).ConfigureAwait(false);

                _attempt = 0;

                // A dial can take seconds, and a connection made for a channel
                // that was closed meanwhile has nobody to close it.
                if (_closed)
                {
                    await ssl.DisposeAsync().ConfigureAwait(false);
                    client.Dispose();
                    throw new ChannelClosedException();
                }

                var stream = new TlsConnectionStream(client, ssl);
                if (observation is not null)
                {
                    observation.PeerSource = () => stream.SecurityPeer;
                    security!.Bind(stream, observation);
                    stream.ReportNegotiatedSecurity(observation);
                    Dnp3TlsConfig.ReportExpiry(observation, Dnp3TlsConfig.DescribeCertificate(_certificate));
                }

                return stream;
            }
            catch (Exception ex) when (
                ex is SocketException or AuthenticationException or IOException or OperationCanceledException)
            {
                ssl?.Dispose();
                client.Dispose();

                cancellationToken.ThrowIfCancellationRequested();

                (observation ?? security?.ForPeer("tls", null))?.Emit(new(observation is null
                    ? SecurityEventKind.ConnDialFailed
                    : Dnp3TlsConfig.HandshakeFailureKind(ex))
                {
                    Action = SecurityAction.Rejected,
                    Detail = ex.GetType().Name,
                });
                if (_retry.Min <= TimeSpan.Zero)
                {
                    throw new NoConnectionException(string.Format(
                        CultureInfo.InvariantCulture,
                        "channel: TLS dial {0} failed: {1}", _address, ex.Message));
                }

                await _retry.SleepAsync(_attempt, cancellationToken).ConfigureAwait(false);
                _attempt++;
            }
        }
    }

    /// <inheritdoc/>
    public void Close() => _closed = true;

    /// <inheritdoc/>
    public void Dispose()
    {
        Close();
        _certificate.Dispose();
    }

    /// <inheritdoc/>
    public override string ToString() => "tls-client " + _address;
}

/// <summary>
/// A channel that accepts mutually authenticated TLS connections, serving one
/// at a time.
/// </summary>
public sealed class TlsServerChannel : IChannel, IObservedChannel
{
    private readonly string _address;
    private readonly Dnp3TlsConfig _tls;
    private readonly X509Certificate2 _certificate;
    private readonly X509Certificate2Collection _authority;
    private readonly Lock _gate = new();
    private readonly ServerAdmission _admission = new();
    private TcpListener? _listener;
    private bool _closed;

    /// <summary>Creates a TLS listener on <paramref name="address"/>.</summary>
    public TlsServerChannel(string address, Dnp3TlsConfig tls)
    {
        ArgumentNullException.ThrowIfNull(tls);

        _address = address;
        _tls = tls;
        (_certificate, _authority) = tls.Load();
    }

    /// <summary>The address the listener bound to, or null before it has bound.</summary>
    public IPEndPoint? BoundAddress
    {
        get
        {
            lock (_gate)
            {
                return _listener?.LocalEndpoint as IPEndPoint;
            }
        }
    }

    private TcpListener Listen()
    {
        lock (_gate)
        {
            if (_closed)
            {
                throw new ChannelClosedException();
            }

            if (_listener is not null)
            {
                return _listener;
            }

            var (host, port) = EndpointParser.Split(_address);
            var ip = host.Length == 0 ? IPAddress.IPv6Any : IPAddress.Parse(host);

            var listener = new TcpListener(ip, port);
            if (ip.Equals(IPAddress.IPv6Any))
            {
                listener.Server.SetSocketOption(
                    SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
            }

            listener.Start();
            _listener = listener;
            return listener;
        }
    }

    /// <inheritdoc/>
    public bool SupportsConcurrentConnections => Busy != BusyPolicy.Reject;

    /// <summary>How additional connections are handled while a peer is being served.</summary>
    public BusyPolicy Busy { get; init; }

    /// <inheritdoc/>
    /// <remarks>
    /// The handshake completes before the connection is handed over, bounded by
    /// <see cref="HandshakeTimeout"/>: a peer that connected and then said
    /// nothing would otherwise park the session for good. A failed handshake is
    /// the peer's problem, not the listener's, so that peer is dropped and the
    /// next one waited for.
    /// </remarks>
    public Task<Stream> ConnectAsync(CancellationToken cancellationToken) =>
        ConnectAsync(null, cancellationToken);

    Task<Stream> IObservedChannel.ConnectAsync(SecurityRuntime? security, CancellationToken cancellationToken) =>
        ConnectAsync(security, cancellationToken);

    private Task<Stream> ConnectAsync(SecurityRuntime? security, CancellationToken cancellationToken) =>
        _admission.AcceptAsync(token => AcceptConnectionAsync(security, token), Listen, Busy, security, "tls", cancellationToken);

    private async Task<Stream> AcceptConnectionAsync(SecurityRuntime? security, CancellationToken cancellationToken)
    {
        while (true)
        {
            var client = await TcpAccept.AcceptAsync(Listen, cancellationToken, security, "tls").ConfigureAwait(false);
            var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
            var observation = security?.ForPeer("tls", remote);
            if (observation is not null && !security!.Admit(observation, remote, countAccept: true))
            {
                client.Dispose();
                continue;
            }

            client.NoDelay = true;

            // Capture only the record prefix the TLS stack already reads, to
            // distinguish cleartext DNP3 and unrelated traffic on a TLS port.
            var prefix = observation?.HasSink == true ? new TlsHandshakePrefix(client.Client) : null;
            var ssl = new SslStream(
                prefix ?? client.GetStream(),
                leaveInnerStreamOpen: false,
                (_, cert, chain, errors) =>
                    _tls.Verify(_authority, cert, chain, errors, observation, server: true));

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(HandshakeTimeout);
                await ssl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        ClientCertificateRequired = true,
                        EnabledSslProtocols = _tls.EffectiveProtocols,

                        // The client is verified by the callback given to the
                        // stream above. Setting it here as well is refused, and
                        // every handshake failed.
                    },
                    timeout.Token).ConfigureAwait(false);
                var stream = new TlsConnectionStream(client, ssl);
                if (observation is not null)
                {
                    observation.PeerSource = () => stream.SecurityPeer;
                    security!.Bind(stream, observation);
                    stream.ReportNegotiatedSecurity(observation);
                    Dnp3TlsConfig.ReportExpiry(observation, Dnp3TlsConfig.DescribeCertificate(_certificate));
                }

                return stream;
            }
            catch (Exception ex) when (
                ex is AuthenticationException or IOException or OperationCanceledException or Win32Exception)
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                client.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                var kind = Dnp3TlsConfig.HandshakeFailureKind(ex, prefix);
                observation?.Emit(new(kind)
                {
                    Action = SecurityAction.Rejected,
                    Detail = ex.GetType().Name,
                });
            }
        }
    }

    /// <summary>
    /// How long an accepted peer has to complete the handshake, matching the
    /// client's connect timeout.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <inheritdoc/>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _listener?.Stop();
            _listener = null;
        }

        _admission.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Close();
        _certificate.Dispose();
    }

    /// <inheritdoc/>
    public override string ToString() => "tls-server " + _address;
}

/// <summary>An <see cref="SslStream"/> that disposes the socket beneath it.</summary>
internal sealed class TlsConnectionStream : Stream, IPeerEndpoint
{
    private readonly TcpClient _client;
    private readonly SslStream _inner;

    public TlsConnectionStream(TcpClient client, SslStream inner)
    {
        _client = client;
        _inner = inner;

        try
        {
            Peer = client.Client.RemoteEndPoint?.ToString();
            SecurityPeer = new SecurityPeer
            {
                Network = "tls",
                Remote = Peer,
                Local = client.Client.LocalEndPoint?.ToString(),
                Tls = inner.RemoteCertificate is { } certificate ? Dnp3TlsConfig.DescribeCertificate(certificate) with
                {
                    Version = inner.SslProtocol switch
                    {
                        SslProtocols.Tls12 => 0x0303,
                        SslProtocols.Tls13 => 0x0304,
                        _ => null,
                    },
                    CipherSuite = (ushort)inner.NegotiatedCipherSuite,
                    ServerName = string.IsNullOrEmpty(inner.TargetHostName) ? null : inner.TargetHostName,
                    Verified = true,
                } : null,
            };
        }
        catch (SocketException)
        {
            Peer = null;
        }
        catch (ObjectDisposedException)
        {
            Peer = null;
        }
    }

    internal void ReportNegotiatedSecurity(SecurityObservation observation)
    {
        var cipher = _inner.NegotiatedCipherSuite.ToString();
        if (_inner.SslProtocol is not (SslProtocols.Tls12 or SslProtocols.Tls13) ||
            cipher.Contains("_RC4_", StringComparison.Ordinal) ||
            cipher.Contains("_3DES_", StringComparison.Ordinal) ||
            cipher.Contains("_CBC_", StringComparison.Ordinal) ||
            cipher.Contains("_NULL_", StringComparison.Ordinal))
        {
            observation.Emit(new(SecurityEventKind.TLSWeakParams) { Detail = cipher });
        }

        Dnp3TlsConfig.ReportExpiry(observation, SecurityPeer?.Tls);
    }

    /// <inheritdoc/>
    public string? Peer { get; }

    public SecurityPeer? SecurityPeer { get; }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        _inner.Write(buffer, offset, count);

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _client.Dispose();
        }

        base.Dispose(disposing);
    }
}
