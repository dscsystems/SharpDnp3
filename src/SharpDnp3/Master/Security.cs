// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The master half of the symmetric Secure Authentication exchange: it fetches
// a fresh session key pair through the outstation's locally provisioned update
// key, then answers the challenge each critical request draws.

using System.Security.Cryptography;
using SharpDnp3.App;
using SharpDnp3.Objects;
using SharpDnp3.Security;

namespace SharpDnp3.Master;

/// <summary>
/// Enables the symmetric SAv5 master exchange. <see cref="UpdateKey"/> must
/// match the outstation's locally provisioned user key. Remote update-key
/// management and aggressive mode are not supported.
/// </summary>
public sealed class SecureAuthenticationConfig
{
    /// <summary>The user number this master authenticates as.</summary>
    public ushort User { get; set; }

    /// <summary>The 16- or 32-octet AES update key.</summary>
    public byte[] UpdateKey { get; set; } = [];

    /// <summary>How long a session key is used before a new one is fetched. Zero uses ten minutes.</summary>
    public TimeSpan KeyTimeout { get; set; }

    /// <summary>How many messages a session key covers. Zero uses 500.</summary>
    public uint MaxMessages { get; set; }
}

internal sealed class MasterSecurity
{
    public byte[] Control = [];
    public byte[] Monitor = [];
    public DateTimeOffset Until;
    public uint Messages;
    public byte[] LastRequest = [];

    public void Clear()
    {
        CryptographicOperations.ZeroMemory(Control);
        CryptographicOperations.ZeroMemory(Monitor);
        Control = [];
        Monitor = [];
        Until = default;
        Messages = 0;
        LastRequest = [];
    }
}

public sealed partial class MasterSession
{
    private readonly MasterSecurity _security = new();

    private uint SecurityMessageLimit =>
        _cfg.SecureAuthentication is { MaxMessages: > 0 } c ? c.MaxMessages : 500;

    private bool SecurityReady() =>
        _security.Control.Length >= 16 && _time.GetUtcNow() < _security.Until &&
        _security.Messages < SecurityMessageLimit;

    private static bool SingleAuthObject(Fragment f, byte variation, out ObjectHeader h)
    {
        h = default;
        if (f.Objects.Count != 1)
        {
            return false;
        }

        h = f.Objects[0];
        return h.Group == 120 && h.Variation == variation && h.Count == 1 && h.Qualifier == FreeFormat.Qualifier;
    }

    /// <summary>
    /// Builds the exchange that fetches a session key pair — the key status
    /// request, then the key change — and ends by running <paramref name="next"/>.
    /// </summary>
    private MasterTask NewSessionKeysTask(MasterTask next)
    {
        var cfg = _cfg.SecureAuthentication!;
        var status = default(AuthKeyStatus);
        byte[] control = [];
        byte[] monitor = [];
        byte[] changeAsdu = [];

        var change = new MasterTask
        {
            Name = "auth-key-change",
            FuncCode = FuncCode.AuthRequest,
            Priority = TaskPriority.Startup,
            Startup = next.Startup,
            Origin = next,
        };
        change.Build = b =>
        {
            control = RandomNumberGenerator.GetBytes(32);
            monitor = RandomNumberGenerator.GetBytes(32);
            var plain = new List<byte> { 32, 0 };
            plain.AddRange(control);
            plain.AddRange(monitor);
            AuthenticationCodec.Append(plain, status with { Mac = [] });
            while (plain.Count % 8 != 0)
            {
                plain.Add(0);
            }

            var plainBytes = plain.ToArray();
            byte[] wrapped;
            try
            {
                wrapped = SaCrypto.Wrap(cfg.UpdateKey, plainBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }

            var value = new List<byte>();
            AuthenticationCodec.Append(value, new AuthKeyChange(status.Sequence, cfg.User, wrapped));
            AddExt(b, FreeFormat.Build(120, 6, value.ToArray()));
            changeAsdu = b.ToArray();
        };
        change.OnFragment = f =>
        {
            if (!SingleAuthObject(f, 5, out var h))
            {
                change.Failure = new AuthenticationException();
                return;
            }

            AuthKeyStatus v;
            try
            {
                v = AuthenticationCodec.ParseKeyStatus(FreeFormat.FirstObject(h).Span);
            }
            catch (MalformedException ex)
            {
                change.Failure = ex;
                return;
            }

            if (v.User != cfg.User || v.Status != 1 || v.Algorithm != 4 ||
                !SaCrypto.Equal(v.Mac, SaCrypto.Mac(monitor, changeAsdu)))
            {
                change.Failure = new AuthenticationException();
                return;
            }

            _security.Clear();
            _security.Control = control;
            _security.Monitor = monitor;
            _security.Messages = 0;
            var ttl = cfg.KeyTimeout > TimeSpan.Zero ? cfg.KeyTimeout : TimeSpan.FromMinutes(10);
            _security.Until = _time.GetUtcNow() + ttl;
        };
        change.Next = () => next;

        var request = new MasterTask
        {
            Name = "auth-key-status",
            FuncCode = FuncCode.AuthRequest,
            Priority = TaskPriority.Startup,
            Startup = next.Startup,
            Origin = next,
        };
        request.Build = b =>
        {
            if (cfg.User == 0 || cfg.UpdateKey.Length is not (16 or 32))
            {
                throw new AuthenticationException();
            }

            AddExt(b, new ObjectHeader
            {
                Group = 120,
                Variation = 4,
                Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.Count8),
                Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
                Data = new[] { (byte)cfg.User, (byte)(cfg.User >> 8) },
            });
        };
        request.OnFragment = f =>
        {
            if (!SingleAuthObject(f, 5, out var h))
            {
                request.Failure = new AuthenticationException();
                return;
            }

            try
            {
                status = AuthenticationCodec.ParseKeyStatus(FreeFormat.FirstObject(h).Span);
            }
            catch (MalformedException ex)
            {
                request.Failure = ex;
                return;
            }

            if (status.User != cfg.User || (status.WrapAlgorithm != 1 && status.WrapAlgorithm != 2) ||
                (status.WrapAlgorithm == 1 && cfg.UpdateKey.Length != 16) ||
                (status.WrapAlgorithm == 2 && cfg.UpdateKey.Length != 32))
            {
                request.Failure = new AuthenticationException();
            }
        };
        request.Next = () => change;

        // Whoever is waiting on the original task is waiting on the whole
        // chain, which ends when that task has run.
        request.Done = next.Done;
        next.Done = null;
        return request;
    }

    /// <summary>
    /// Answers the challenge an outstation sent in place of a response to a
    /// critical request.
    /// </summary>
    private void OnAuthenticationResponse(Fragment f)
    {
        var t = _inflight;
        if (_cfg.SecureAuthentication is not { } cfg || t is null || f.Header.Control.Uns ||
            !(f.Header.Control.Fir && f.Header.Control.Fin) || f.Header.Control.Con ||
            f.Header.Control.Seq != t.Seq)
        {
            return;
        }

        if (t.FuncCode == FuncCode.AuthRequest)
        {
            OnSolicited(f);
            return;
        }

        if (!SingleAuthObject(f, 1, out var h))
        {
            _inflight = null;
            CompleteTask(t, new AuthenticationException());
            return;
        }

        AuthChallenge c;
        try
        {
            c = AuthenticationCodec.ParseChallenge(FreeFormat.FirstObject(h).Span);
        }
        catch (MalformedException ex)
        {
            _inflight = null;
            CompleteTask(t, ex);
            return;
        }

        if (!SecurityReady() || c.Algorithm != 4 || c.Reason != 1 || (c.User != 0 && c.User != cfg.User) ||
            c.Data.Length != 32)
        {
            _inflight = null;
            CompleteTask(t, new AuthenticationException());
            return;
        }

        var mac = SaCrypto.Mac(_security.Control, f.Raw, _security.LastRequest);
        var value = new List<byte>();
        AuthenticationCodec.Append(value, new AuthReply(c.Sequence, cfg.User, mac));
        var fragment = FragmentFactory.BuildRequest(
            new AppControl(Fir: true, Fin: true, Con: false, Uns: false, Seq: t.Seq),
            FuncCode.AuthRequest,
            FreeFormat.Build(120, 2, value.ToArray()));
        try
        {
            _stack!.Send(_sink, fragment);
        }
        catch (Dnp3Exception ex)
        {
            _inflight = null;
            CompleteTask(t, ex);
            return;
        }

        _security.Messages++;
        if (t.NoResponse)
        {
            // The command still needs its authentication challenge/reply
            // exchange, even though its application function has no normal
            // response.
            _inflight = null;
            CompleteTask(t, null);
            return;
        }

        t.Deadline = _time.GetUtcNow() + _cfg.ResponseTimeout;
    }
}
