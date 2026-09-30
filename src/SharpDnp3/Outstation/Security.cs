// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// A symmetric subset of DNP3 Secure Authentication v5, independent of TLS:
// locally provisioned AES update keys, RFC 3394 session-key wrapping, SHA-256
// HMAC with 16-octet MACs, and challenge/reply for critical requests.
// Aggressive mode, remote user and update-key management and other MAC
// algorithms are not implemented, and this is not a certified SAv5 profile.

using System.Buffers.Binary;
using System.Security.Cryptography;
using SharpDnp3.App;
using SharpDnp3.Objects;
using SharpDnp3.Security;
using SharpDnp3.Stack;

namespace SharpDnp3.Outstation;

/// <summary>
/// Enables the symmetric SAv5 challenge/reply path. Update keys are provisioned
/// locally.
/// </summary>
public sealed class SecureAuthenticationConfig
{
    /// <summary>User number to 16- or 32-octet AES update key.</summary>
    public IDictionary<ushort, byte[]> Users { get; } = new Dictionary<ushort, byte[]>();

    /// <summary>How long a challenge waits for its reply. Zero uses five seconds.</summary>
    public TimeSpan ReplyTimeout { get; set; }

    /// <summary>How long a session key lives. Zero uses fifteen minutes.</summary>
    public TimeSpan KeyTimeout { get; set; }

    /// <summary>How many authenticated requests a session key covers. Zero uses 1000.</summary>
    public uint MaxMessages { get; set; }

    /// <summary>The event class of security statistic events. None uses class 1.</summary>
    public Class StatisticsClass { get; set; }

    /// <summary>A statistic raises an event each time it is a multiple of this. Zero uses 10.</summary>
    public uint StatisticsThreshold { get; set; }

    /// <summary>
    /// Grants a user the requested function. Null grants configured users all
    /// functions; applications can enforce roles here.
    /// </summary>
    public Func<ushort, byte, bool>? Authorize { get; set; }
}

internal sealed class SecurityUser
{
    public byte[] Update = [];
    public byte[] Control = [];
    public byte[] Monitor = [];
    public byte[]? Status;
    public byte[] KeyChange = [];
    public ushort StatusSource;
    public DateTimeOffset StatusUntil;
    public ushort Source;
    public DateTimeOffset Until;
    public uint Messages;
}

internal sealed class AuthenticationPending
{
    public required Received Received { get; init; }

    public required byte[] Challenge { get; init; }

    public required uint Sequence { get; init; }

    public required DateTimeOffset Until { get; init; }
}

/// <summary>One connection's authentication state.</summary>
internal sealed class SecurityState
{
    public Dictionary<ushort, SecurityUser> Users { get; } = [];

    public AuthenticationPending? Pending { get; set; }
}

public sealed partial class OutstationSession
{
    private const int SecurityStatisticCount = 18;

    private readonly Lock _securityGate = new();
    private uint _securitySequence;
    private uint _keySequence;
    private readonly uint[] _securityStats = new uint[SecurityStatisticCount];

    private SecureAuthenticationConfig? Sa => _cfg.SecureAuthentication;

    private SecurityState NewSecurityState()
    {
        var state = new SecurityState();
        if (Sa is { } cfg)
        {
            foreach (var (id, key) in cfg.Users)
            {
                if (id != 0 && key.Length is 16 or 32)
                {
                    state.Users[id] = new SecurityUser { Update = (byte[])key.Clone() };
                }
            }
        }

        return state;
    }

    private uint SecurityMaxMessages => Sa is { MaxMessages: > 0 } c ? c.MaxMessages : 1000;

    private TimeSpan SecurityReplyTimeout => Sa is { } c && c.ReplyTimeout > TimeSpan.Zero
        ? c.ReplyTimeout
        : TimeSpan.FromSeconds(5);

    private TimeSpan SecurityKeyTimeout => Sa is { } c && c.KeyTimeout > TimeSpan.Zero
        ? c.KeyTimeout
        : TimeSpan.FromMinutes(15);

    private bool SecurityKeyValid(SecurityUser? u, ushort source) =>
        u is not null && u.Control.Length >= 16 && u.Source == source &&
        _appl.Now() < u.Until && u.Messages < SecurityMaxMessages;

    /// <summary>
    /// Reports whether a request changes the device's state or configuration
    /// and so needs authenticating.
    /// </summary>
    /// <remarks>
    /// It is deliberately the whole set of state-changing functions, not only
    /// the ones a reading of the standard makes mandatory: a request that
    /// freezes or clears counters, or moves points between event classes, is as
    /// much a change as a control, and leaving it open leaves an
    /// unauthenticated peer able to disturb what an authenticated operator
    /// sees. Only reads, confirms and the delay measurement stay open.
    /// </remarks>
    private static bool CriticalFunction(FuncCode f) => f is
        FuncCode.ImmedFreeze or FuncCode.ImmedFreezeNR or FuncCode.FreezeClear or FuncCode.FreezeClearNR or
        FuncCode.FreezeAtTime or FuncCode.FreezeAtTimeNR or FuncCode.AssignClass or FuncCode.InitializeData or
        FuncCode.Write or FuncCode.Select or FuncCode.Operate or FuncCode.DirectOperate or
        FuncCode.DirectOperateNR or FuncCode.ColdRestart or FuncCode.WarmRestart or
        FuncCode.InitializeAppl or FuncCode.StartAppl or FuncCode.StopAppl or FuncCode.SaveConfig or
        FuncCode.EnableUnsolicited or FuncCode.DisableUnsolicited or FuncCode.RecordCurrentTime or
        FuncCode.OpenFile or FuncCode.CloseFile or FuncCode.DeleteFile or FuncCode.GetFileInfo or
        FuncCode.AuthenticateFile or FuncCode.AbortFile or FuncCode.ActivateConfig;

    /// <summary>
    /// Prevents group 120 objects from bypassing validation when carried under
    /// a normal READ or WRITE function code.
    /// </summary>
    private static bool HasAuthenticationObject(Fragment f)
    {
        foreach (var h in f.Objects)
        {
            if (h.Group == 120)
            {
                return true;
            }
        }

        return false;
    }

    private byte[]? SendAuthentication(Association a, Received r, byte seq, ObjectHeader h)
    {
        if (r.Broadcast)
        {
            return null;
        }

        var data = new List<byte>();
        HeaderCodec.AppendHeader(data, new AppHeader(
            new AppControl(Fir: true, Fin: true, Con: false, Uns: false, Seq: seq),
            FuncCode.AuthResponse,
            CurrentIin(a)));
        ObjectHeaderCodec.AppendObjectHeader(data, h);
        if (data.Count > _cfg.MaxTxFragment)
        {
            throw AppParseStatus.FragmentTooLarge.ToException();
        }

        CountSecurity(5);
        var bytes = data.ToArray();
        try
        {
            a.Stack.SendTo(a.Sink, r.Source, bytes);
        }
        catch (Dnp3Exception ex)
        {
            a.Log.Log(Dnp3LogLevel.Warn, "authentication response failed", ("err", ex.Message));
        }

        return bytes;
    }

    private void AuthenticationError(Association a, Received r, byte seq, ushort user, byte code)
    {
        uint sequence;
        lock (_securityGate)
        {
            sequence = _securitySequence;
        }

        var data = new List<byte>();
        ObjectConvert.AppendUInt32(data, sequence);
        ObjectConvert.AppendUInt16(data, user);
        ObjectConvert.AppendUInt16(data, 0); // this association
        data.Add(code);
        CommandObjects.AppendTime48(data, Timestamp.Now(_appl.Now()));
        CountSecurity(10);
        SendAuthentication(a, r, seq, FreeFormat.Build(120, 7, data.ToArray()));
    }

    private void ChallengeRequest(Association a, Received r, Fragment frag)
    {
        if (r.Broadcast)
        {
            CountSecurity(9);
            return;
        }

        var ready = false;
        foreach (var u in a.Security.Users.Values)
        {
            ready = ready || SecurityKeyValid(u, r.Source);
        }

        if (!ready)
        {
            AuthenticationError(a, r, frag.Header.Control.Seq, 0, 1);
            return;
        }

        uint sequence;
        lock (_securityGate)
        {
            sequence = ++_securitySequence;
        }

        var value = new List<byte>();
        AuthenticationCodec.Append(value, new AuthChallenge(
            sequence, 0, 4, 1, RandomNumberGenerator.GetBytes(32)));
        var data = SendAuthentication(a, r, frag.Header.Control.Seq, FreeFormat.Build(120, 1, value.ToArray()));
        if (data is null)
        {
            return;
        }

        a.Security.Pending = new AuthenticationPending
        {
            Received = new Received
            {
                Fragment = r.Fragment.ToArray(),
                Source = r.Source,
                Dest = r.Dest,
                Broadcast = r.Broadcast,
            },
            Challenge = data,
            Sequence = sequence,
            Until = _appl.Now() + SecurityReplyTimeout,
        };
        CountSecurity(8);
    }

    private void OnAuthentication(Association a, Received r, Fragment frag)
    {
        var seq = frag.Header.Control.Seq;
        if (Sa is null)
        {
            a.Iin = a.Iin.Set(Iin.NoFuncCodeSupport);
            if (frag.Header.Func.NoReply())
            {
                return;
            }

            Respond(a, r, frag.Header, []);
            return;
        }

        if (frag.Header.Func == FuncCode.AuthRequestNoAck)
        {
            // This code carries errors and must never execute an operation.
            a.Security.Pending = null;
            CountSecurity(11);
            return;
        }

        if (r.Broadcast || frag.Objects.Count != 1)
        {
            AuthenticationError(a, r, seq, 0, 1);
            return;
        }

        var h = frag.Objects[0];
        if (h.Group != 120 || h.Count != 1)
        {
            AuthenticationError(a, r, seq, 0, 1);
            return;
        }

        if (h.Variation == 4)
        {
            if (h.Qualifier != Qualifier.Make(IndexPrefix.None, RangeSpec.Count8) || h.Data.Length != 2)
            {
                AuthenticationError(a, r, seq, 0, 1);
                return;
            }

            var user = BinaryPrimitives.ReadUInt16LittleEndian(h.Data.Span);
            if (!a.Security.Users.TryGetValue(user, out var known))
            {
                AuthenticationError(a, r, seq, user, 11);
                return;
            }

            SendKeyStatus(a, r, seq, user, known);
            return;
        }

        if (h.Qualifier != FreeFormat.Qualifier)
        {
            AuthenticationError(a, r, seq, 0, 1);
            return;
        }

        ReadOnlyMemory<byte> data;
        try
        {
            data = FreeFormat.FirstObject(h);
        }
        catch (MalformedException)
        {
            AuthenticationError(a, r, seq, 0, 1);
            return;
        }

        switch (h.Variation)
        {
            case 6:
                OnKeyChange(a, r, frag, seq, data.Span);
                return;

            case 2:
                OnAuthenticationReply(a, r, seq, data.Span);
                return;

            default:
                AuthenticationError(a, r, seq, 0, 4);
                return;
        }
    }

    private void OnKeyChange(Association a, Received r, Fragment frag, byte seq, ReadOnlySpan<byte> data)
    {
        AuthKeyChange v;
        try
        {
            v = AuthenticationCodec.ParseKeyChange(data);
        }
        catch (MalformedException)
        {
            AuthenticationError(a, r, seq, 0, 1);
            return;
        }

        if (!a.Security.Users.TryGetValue(v.User, out var u))
        {
            AuthenticationError(a, r, seq, v.User, 11);
            return;
        }

        var status = u.Status;
        u.Status = null; // status challenges are single-use, including failures
        if (status is null || status.Length < 11 || v.Sequence != BinaryPrimitives.ReadUInt32LittleEndian(status) ||
            u.StatusSource != r.Source || _appl.Now() >= u.StatusUntil)
        {
            AuthenticationError(a, r, seq, v.User, 1);
            return;
        }

        byte[] plain;
        try
        {
            plain = SaCrypto.Unwrap(u.Update, v.Wrapped);
        }
        catch (AuthenticationException)
        {
            CountSecurity(14);
            AuthenticationError(a, r, seq, v.User, 1);
            return;
        }

        try
        {
            if (plain.Length < 2)
            {
                AuthenticationError(a, r, seq, v.User, 1);
                return;
            }

            int n = BinaryPrimitives.ReadUInt16LittleEndian(plain);
            var end = 2 + (2 * n);
            if (n < 16 || n > 64 || end + status.Length > plain.Length || plain.Length - end - status.Length > 7 ||
                !SaCrypto.Equal(plain.AsSpan(end, status.Length), status))
            {
                CountSecurity(14);
                AuthenticationError(a, r, seq, v.User, 1);
                return;
            }

            CryptographicOperations.ZeroMemory(u.Control);
            CryptographicOperations.ZeroMemory(u.Monitor);
            u.Control = plain.AsSpan(2, n).ToArray();
            u.Monitor = plain.AsSpan(2 + n, n).ToArray();
            u.KeyChange = frag.Raw.ToArray();
            u.Source = r.Source;
            u.Until = _appl.Now() + SecurityKeyTimeout;
            u.Messages = 0;
            a.Security.Pending = null;
            CountSecurity(13);
            SendKeyStatus(a, r, seq, v.User, u);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private void OnAuthenticationReply(Association a, Received r, byte seq, ReadOnlySpan<byte> data)
    {
        AuthReply v;
        try
        {
            v = AuthenticationCodec.ParseReply(data);
        }
        catch (MalformedException)
        {
            AuthenticationError(a, r, seq, 0, 1);
            return;
        }

        var p = a.Security.Pending;
        if (p is null)
        {
            AuthenticationError(a, r, seq, 0, 1);
            return;
        }

        a.Security.Pending = null;
        a.Security.Users.TryGetValue(v.User, out var u);
        if (v.Sequence != p.Sequence || r.Source != p.Received.Source || _appl.Now() >= p.Until ||
            !SecurityKeyValid(u, r.Source) ||
            !SaCrypto.Equal(v.Mac, SaCrypto.Mac(u!.Control, p.Challenge, p.Received.Fragment)))
        {
            CountSecurity(2);
            AuthenticationError(a, r, seq, v.User, 1);
            return;
        }

        FragmentParser.ParseFragment(null, p.Received.Fragment, out var original, out _);
        if (Sa!.Authorize is { } authorize && !authorize(v.User, (byte)original.Header.Func))
        {
            CountSecurity(1);
            AuthenticationError(a, r, seq, v.User, 7);
            return;
        }

        u.Messages++;
        CountSecurity(12);
        Handle(a, p.Received, authenticated: true);
    }

    private void SendKeyStatus(Association a, Received r, byte seq, ushort user, SecurityUser u)
    {
        uint keySequence;
        lock (_securityGate)
        {
            keySequence = ++_keySequence;
        }

        var status = new AuthKeyStatus(keySequence, user, (byte)(u.Update.Length == 32 ? 2 : 1), 2, 0, RandomNumberGenerator.GetBytes(32), []);
        if (SecurityKeyValid(u, r.Source))
        {
            status = status with { Status = 1 };
        }

        if (u.Monitor.Length > 0)
        {
            status = status with { Algorithm = 4, Mac = SaCrypto.Mac(u.Monitor, u.KeyChange) };
        }

        var encoded = new List<byte>();
        AuthenticationCodec.Append(encoded, status);
        u.Status = encoded.GetRange(0, encoded.Count - status.Mac.Length).ToArray();
        u.StatusSource = r.Source;
        u.StatusUntil = _appl.Now() + SecurityReplyTimeout;
        SendAuthentication(a, r, seq, FreeFormat.Build(120, 5, encoded.ToArray()));
    }

    private void ReadSecurityStatistics(Association a, ResponseBuilder b, ObjectHeader h)
    {
        if (Sa is null || h.Variation != 1)
        {
            a.Iin = a.Iin.Set(Iin.ObjectUnknown);
            return;
        }

        if (!TryForEachPointRun(h, (start, stop) =>
        {
            for (var i = (int)start; i <= stop && i < SecurityStatisticCount; i++)
            {
                uint value;
                lock (_securityGate)
                {
                    value = _securityStats[i];
                }

                var data = new List<byte> { Flags.Online.Value, 0, 0 };
                ObjectConvert.AppendUInt32(data, value);
                b.Add(ResponseWriter.RangeObjectHeader(GroupVar.GV(121, 1), (ushort)i, (ushort)i, data.ToArray()));
            }
        }))
        {
            a.Iin = a.Iin.Set(Iin.ParameterError);
        }
    }

    private void CountSecurity(int index)
    {
        if (Sa is not { } cfg)
        {
            return;
        }

        uint value;
        lock (_securityGate)
        {
            value = ++_securityStats[index];
        }

        var threshold = cfg.StatisticsThreshold == 0 ? 10 : cfg.StatisticsThreshold;
        if (value % threshold != 0)
        {
            return;
        }

        var cls = cfg.StatisticsClass & Class.Class123;
        if (cls == Class.None)
        {
            cls = Class.Class1;
        }

        _db.RaiseSecurityStatistic((ushort)index, cls, value, Timestamp.Now(_appl.Now()));
    }

    private void ExpireSecurity(Association a)
    {
        if (Sa is null)
        {
            return;
        }

        var now = _appl.Now();
        if (a.Security.Pending is { } p && now >= p.Until)
        {
            a.Security.Pending = null;
            CountSecurity(3);
            CountSecurity(9);
        }

        foreach (var u in a.Security.Users.Values)
        {
            if (now >= u.Until || u.Messages >= SecurityMaxMessages)
            {
                CryptographicOperations.ZeroMemory(u.Control);
                u.Control = [];
            }
        }
    }
}
