// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// These tests attack the authentication rather than use it: each one tries to
// get a command executed without a valid, fresh, matching authentication, and
// fails if it succeeds. They speak the wire protocol directly, so a shared
// misreading between the library's master and outstation cannot hide a hole.

using System.Buffers.Binary;
using SharpDnp3.App;
using SharpDnp3.Objects;
using SharpDnp3.Outstation;
using SharpDnp3.Security;

namespace SharpDnp3.Conformance.Tests;

public class SecurityAttackTests
{
    private static readonly byte[] UpdateKey = Enumerable.Repeat((byte)0x5A, 16).ToArray();

    private static (Harness H, RecordingCommandHandler Rec) SecureHarness(SecureAuthenticationConfig cfg)
    {
        cfg.Users[1] = UpdateKey;
        var db = Requests.SmallDatabase();
        db.Counter = 3;
        db.FrozenCounter = 3;
        var rec = new RecordingCommandHandler();
        var h = new Harness(new OutstationConfig { Database = db, SecureAuthentication = cfg }, rec);
        return (h, rec);
    }

    /// <summary>Sends one request exactly as built and returns it with the reply.</summary>
    private static async Task<(byte[] Request, Fragment Reply)> RawExchangeAsync(
        Harness h, FuncCode fc, params ObjectHeader[] objs)
    {
        var before = h.Count;
        await h.SendAsync(fc, objs);
        var frag = FragmentFactory.BuildRequest(new AppControl(true, true, false, false, h.Seq), fc, objs);
        return (frag, await h.AwaitAsync(before));
    }

    private static ReadOnlyMemory<byte> AuthObject(Fragment f, byte variation)
    {
        Assert.True(
            f.Objects.Count == 1 && f.Objects[0].Group == 120 && f.Objects[0].Variation == variation,
            $"want one g120v{variation}, got {f.Objects.Count} objects (function {f.Header.Func})");
        return FreeFormat.FirstObject(f.Objects[0]);
    }

    private static async Task<byte[]> EstablishSessionKeysAsync(Harness h)
    {
        var (_, st) = await RawExchangeAsync(h, FuncCode.AuthRequest, new ObjectHeader
        {
            Group = 120,
            Variation = 4,
            Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
            Data = new byte[] { 1, 0 },
        });
        var status = AuthenticationCodec.ParseKeyStatus(AuthObject(st, 5).Span);

        var control = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var monitor = Enumerable.Repeat((byte)0x22, 32).ToArray();
        var plain = new List<byte> { 32, 0 };
        plain.AddRange(control);
        plain.AddRange(monitor);
        AuthenticationCodec.Append(plain, status with { Mac = [] });
        while (plain.Count % 8 != 0)
        {
            plain.Add(0);
        }

        var wrapped = SaCrypto.Wrap(UpdateKey, plain.ToArray());
        var value = new List<byte>();
        AuthenticationCodec.Append(value, new AuthKeyChange(status.Sequence, 1, wrapped));
        var (change, resp) = await RawExchangeAsync(h, FuncCode.AuthRequest, FreeFormat.Build(120, 6, value.ToArray()));
        var ks = AuthenticationCodec.ParseKeyStatus(AuthObject(resp, 5).Span);
        Assert.True(ks.Status == 1 && SaCrypto.Equal(ks.Mac, SaCrypto.Mac(monitor, change)), "key change not accepted");
        return control;
    }

    private static async Task<(byte[] Request, Fragment Challenge, AuthChallenge C)> ChallengeForAsync(
        Harness h, FuncCode fc, params ObjectHeader[] objs)
    {
        var (req, resp) = await RawExchangeAsync(h, fc, objs);
        Assert.True(resp.Header.Func == FuncCode.AuthResponse, $"{fc} was answered with {resp.Header.Func}, want a challenge");
        var c = AuthenticationCodec.ParseChallenge(AuthObject(resp, 1).Span);
        return (req, resp, c);
    }

    private static async Task<Fragment> SendReplyAsync(Harness h, uint seq, byte[] mac)
    {
        var value = new List<byte>();
        AuthenticationCodec.Append(value, new AuthReply(seq, 1, mac));
        var (_, r) = await RawExchangeAsync(h, FuncCode.AuthRequest, FreeFormat.Build(120, 2, value.ToArray()));
        return r;
    }

    private static Task SettleAsync() => Task.Delay(100);

    private static ObjectHeader Crob(byte index) => Requests.CrobHeader(index, ControlCode.LatchOn);

    [Fact]
    public async Task AuthenticatedCriticalRequestExecutesExactlyOnce()
    {
        var (h, rec) = SecureHarness(new SecureAuthenticationConfig());
        await using var harness = h;
        var control = await EstablishSessionKeysAsync(h);

        var (req, chal, c) = await ChallengeForAsync(h, FuncCode.DirectOperate, Crob(1));
        Assert.Equal(0, rec.Operates);
        await SendReplyAsync(h, c.Sequence, SaCrypto.Mac(control, chal.Raw, req));
        await Harness.WaitForAsync(() => rec.Operates == 1, "the operate");
    }

    [Fact]
    public async Task UnauthenticatedCriticalRequestDoesNotExecute()
    {
        var (h, rec) = SecureHarness(new SecureAuthenticationConfig());
        await using var harness = h;
        var (_, resp) = await RawExchangeAsync(h, FuncCode.DirectOperate, Crob(1));
        Assert.False(resp.Header.Func == FuncCode.Response && resp.Objects.Count > 0, "answered with data");
        await SettleAsync();
        Assert.Equal(0, rec.Operates);
    }

    [Fact]
    public async Task WrongMacDoesNotExecute()
    {
        var (h, rec) = SecureHarness(new SecureAuthenticationConfig());
        await using var harness = h;
        var control = await EstablishSessionKeysAsync(h);
        var (req, chal, c) = await ChallengeForAsync(h, FuncCode.DirectOperate, Crob(1));
        var bad = SaCrypto.Mac(control, chal.Raw, req);
        bad[0] ^= 1;
        await SendReplyAsync(h, c.Sequence, bad);
        await SettleAsync();
        Assert.Equal(0, rec.Operates);
    }

    /// <summary>A MAC covers the challenge and the request together, so one command cannot authorise another.</summary>
    [Fact]
    public async Task MacOverADifferentRequestDoesNotExecute()
    {
        var (h, rec) = SecureHarness(new SecureAuthenticationConfig());
        await using var harness = h;
        var control = await EstablishSessionKeysAsync(h);
        var (_, chal, c) = await ChallengeForAsync(h, FuncCode.DirectOperate, Crob(1));
        var other = FragmentFactory.BuildRequest(new AppControl(true, true, false, false, 9), FuncCode.DirectOperate, Crob(2));
        await SendReplyAsync(h, c.Sequence, SaCrypto.Mac(control, chal.Raw, other));
        await SettleAsync();
        Assert.Equal(0, rec.Operates);
    }

    [Fact]
    public async Task ReplayedReplyAndRequestDoNotExecuteAgain()
    {
        var (h, rec) = SecureHarness(new SecureAuthenticationConfig());
        await using var harness = h;
        var control = await EstablishSessionKeysAsync(h);
        var (req, chal, c) = await ChallengeForAsync(h, FuncCode.DirectOperate, Crob(1));
        var mac = SaCrypto.Mac(control, chal.Raw, req);
        await SendReplyAsync(h, c.Sequence, mac);
        await Harness.WaitForAsync(() => rec.Operates == 1, "the operate");

        await SendReplyAsync(h, c.Sequence, mac);
        await SettleAsync();
        Assert.Equal(1, rec.Operates);

        await h.SendRawAsync(req);
        await SettleAsync();
        Assert.Equal(1, rec.Operates);
    }

    [Fact]
    public async Task LateReplyDoesNotExecute()
    {
        var (h, rec) = SecureHarness(new SecureAuthenticationConfig { ReplyTimeout = TimeSpan.FromMilliseconds(100) });
        await using var harness = h;
        var control = await EstablishSessionKeysAsync(h);
        var (req, chal, c) = await ChallengeForAsync(h, FuncCode.DirectOperate, Crob(1));
        await Task.Delay(250);
        await SendReplyAsync(h, c.Sequence, SaCrypto.Mac(control, chal.Raw, req));
        await SettleAsync();
        Assert.Equal(0, rec.Operates);
    }

    [Fact]
    public async Task WrongChallengeSequenceDoesNotExecute()
    {
        var (h, rec) = SecureHarness(new SecureAuthenticationConfig());
        await using var harness = h;
        var control = await EstablishSessionKeysAsync(h);
        var (req, chal, c) = await ChallengeForAsync(h, FuncCode.DirectOperate, Crob(1));
        await SendReplyAsync(h, c.Sequence + 1, SaCrypto.Mac(control, chal.Raw, req));
        await SettleAsync();
        Assert.Equal(0, rec.Operates);
    }

    [Fact]
    public async Task BroadcastCriticalRequestDoesNotExecute()
    {
        var (h, rec) = SecureHarness(new SecureAuthenticationConfig());
        await using var harness = h;
        await EstablishSessionKeysAsync(h);
        var before = h.Count;
        await h.SendToAsync(0xFFFF, FuncCode.DirectOperate, Crob(1));
        await Task.Delay(150);
        Assert.True(rec.Operates == 0 && h.Count == before, $"{rec.Operates} operates, {h.Count - before} unexpected fragment(s)");
    }

    /// <summary>AUTH_REQUEST_NO_ACK carries errors, and must never execute what it carries.</summary>
    [Fact]
    public async Task AuthRequestNoAckNeverExecutes()
    {
        var (h, rec) = SecureHarness(new SecureAuthenticationConfig());
        await using var harness = h;
        await EstablishSessionKeysAsync(h);
        await h.SendRawAsync(FragmentFactory.BuildRequest(
            new AppControl(true, true, false, false, 5), FuncCode.AuthRequestNoAck, Crob(1)));
        await SettleAsync();
        Assert.Equal(0, rec.Operates);
    }

    [Fact]
    public async Task KeyMessageBudgetIsEnforced()
    {
        var (h, rec) = SecureHarness(new SecureAuthenticationConfig { MaxMessages = 1 });
        await using var harness = h;
        var control = await EstablishSessionKeysAsync(h);
        var (req, chal, c) = await ChallengeForAsync(h, FuncCode.DirectOperate, Crob(1));
        await SendReplyAsync(h, c.Sequence, SaCrypto.Mac(control, chal.Raw, req));
        await Harness.WaitForAsync(() => rec.Operates == 1, "the operate");

        var (_, resp) = await RawExchangeAsync(h, FuncCode.DirectOperate, Crob(1));
        Assert.False(
            resp.Header.Func == FuncCode.AuthResponse && resp.Objects.Count == 1 && resp.Objects[0].Variation == 1,
            "a challenge was issued after the key's message budget was spent");
        await SettleAsync();
        Assert.Equal(1, rec.Operates);
    }

    /// <summary>A READ that carries a g120 object is challenged like any critical request.</summary>
    [Fact]
    public async Task AuthenticationObjectUnderAReadIsNotAnswered()
    {
        var (h, _) = SecureHarness(new SecureAuthenticationConfig());
        await using var harness = h;
        var value = new List<byte>();
        AuthenticationCodec.Append(value, new AuthReply(1, 1, new byte[16]));
        var (_, resp) = await RawExchangeAsync(h, FuncCode.Read, FreeFormat.Build(120, 2, value.ToArray()));
        Assert.False(resp.Header.Func == FuncCode.Response && resp.Objects.Count > 0, "answered with data");
    }

    /// <summary>Every request that changes state is critical.</summary>
    [Theory]
    [InlineData(FuncCode.FreezeClear)]
    [InlineData(FuncCode.ImmedFreeze)]
    [InlineData(FuncCode.FreezeAtTime)]
    [InlineData(FuncCode.AssignClass)]
    [InlineData(FuncCode.FreezeClearNR)]
    [InlineData(FuncCode.ImmedFreezeNR)]
    [InlineData(FuncCode.FreezeAtTimeNR)]
    public async Task EveryStateChangingRequestNeedsAuthentication(FuncCode fc)
    {
        var (h, _) = SecureHarness(new SecureAuthenticationConfig());
        await using var harness = h;
        h.Outstation.Update(d => d.UpdateCounter(0, new Counter(100, Flags.Online, default)));
        await Harness.WaitForAsync(
            () => h.Outstation.Database.TryGetCounter(0, out var c, out _) && c.Value == 100, "the counter");

        ObjectHeader[] objs = [FragmentFactory.ReadAllObjects(20, 0)];
        if (fc is FuncCode.FreezeAtTime or FuncCode.FreezeAtTimeNR)
        {
            var data = new List<byte>();
            CommandObjects.AppendTime48(data, Timestamp.Now(DateTimeOffset.UtcNow.AddMilliseconds(50)));
            data.AddRange(new byte[4]);
            objs =
            [
                new ObjectHeader
                {
                    Group = 50,
                    Variation = 2,
                    Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.Count8),
                    Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
                    Data = data.ToArray(),
                },
                FragmentFactory.ReadAllObjects(20, 0),
            ];
        }
        else if (fc == FuncCode.AssignClass)
        {
            objs = [FragmentFactory.ReadAllObjects(60, 2), FragmentFactory.ReadAllObjects(20, 0)];
        }

        var before = h.Count;
        await h.SendAsync(fc, objs);
        var resp = await h.AwaitAsync(before);
        Assert.Equal(FuncCode.AuthResponse, resp.Header.Func);
        await Task.Delay(150);
        h.Outstation.Database.TryGetFrozenCounter(0, out var f, out _);
        Assert.Equal(0u, f.Value);
        h.Outstation.Database.TryGetCounter(0, out var run, out _);
        Assert.Equal(100u, run.Value);
    }

    /// <summary>Reading and measuring delay change nothing, so they stay open.</summary>
    [Fact]
    public async Task ReadOnlyRequestsNeedNoAuthentication()
    {
        var (h, _) = SecureHarness(new SecureAuthenticationConfig());
        await using var harness = h;
        var (_, read) = await RawExchangeAsync(h, FuncCode.Read, FragmentFactory.ReadAllObjects(60, 1));
        Assert.Equal(FuncCode.Response, read.Header.Func);
        var (_, delay) = await RawExchangeAsync(h, FuncCode.DelayMeasure);
        Assert.Equal(FuncCode.Response, delay.Header.Func);
    }

    [Fact]
    public async Task AuthenticatedFreezeClearRuns()
    {
        var (h, _) = SecureHarness(new SecureAuthenticationConfig());
        await using var harness = h;
        var control = await EstablishSessionKeysAsync(h);
        h.Outstation.Update(d => d.UpdateCounter(0, new Counter(100, Flags.Online, default)));
        await Harness.WaitForAsync(
            () => h.Outstation.Database.TryGetCounter(0, out var c, out _) && c.Value == 100, "the counter");

        var (req, chal, c) = await ChallengeForAsync(h, FuncCode.FreezeClear, FragmentFactory.ReadAllObjects(20, 0));
        await SendReplyAsync(h, c.Sequence, SaCrypto.Mac(control, chal.Raw, req));
        await Harness.WaitForAsync(
            () => h.Outstation.Database.TryGetFrozenCounter(0, out var f, out _) && f.Value == 100, "the freeze");
        h.Outstation.Database.TryGetCounter(0, out var run, out _);
        Assert.Equal(0u, run.Value);
    }

    /// <summary>A rejected authentication shows up in the security statistics.</summary>
    [Fact]
    public async Task SecurityStatisticsCountRejectedAuthentications()
    {
        var (h, _) = SecureHarness(new SecureAuthenticationConfig());
        await using var harness = h;
        var control = await EstablishSessionKeysAsync(h);

        async Task<Dictionary<ushort, uint>> ReadStatsAsync()
        {
            var r = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(121, 1));
            var output = new Dictionary<ushort, uint>();
            foreach (var o in r.Objects)
            {
                Assert.True(o.Group == 121 && o.Data.Length == 7, $"unexpected object g{o.Group}v{o.Variation}");
                output[(ushort)o.Range.Start] = BinaryPrimitives.ReadUInt32LittleEndian(o.Data.Span[3..]);
            }

            return output;
        }

        var before = await ReadStatsAsync();
        Assert.Equal(18, before.Count);

        var (req, chal, c) = await ChallengeForAsync(h, FuncCode.DirectOperate, Crob(1));
        var bad = SaCrypto.Mac(control, chal.Raw, req);
        bad[0] ^= 1;
        await SendReplyAsync(h, c.Sequence, bad);

        var after = await ReadStatsAsync();
        Assert.Contains(after, kv => kv.Value > before[kv.Key]);
        Assert.Equal(before[2] + 1, after[2]);
    }
}
