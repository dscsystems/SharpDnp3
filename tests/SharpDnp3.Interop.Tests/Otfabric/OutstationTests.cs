// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Our outstation, serving the baseline fixture of otfabric/dnp3-interop,
// against that repository's reference masters (opendnp3, DSC go-dnp3 and Step
// Function I/O dnp3, each in a container). The cases follow its own
// tests/cases.tsv: each master runs one operation, and the result document and
// what reached our command handler and application are what is asserted.

using System.Text.Json;

namespace SharpDnp3.Interop.Tests.Otfabric;

public class OutstationTests
{
    public static TheoryData<string> Adapters => Reference.AdapterData();

    private const string Baseline =
        "g10v2x2,g110v5x1,g1v1x1,g1v2x4,g20v1x1,g20v2x1,g20v5x1,g21v1x1,g21v2x1," +
        "g30v1x3,g30v2x1,g30v5x1,g3v2x2,g40v1x1,g40v3x1";

    /// <summary>
    /// The IIN of a result without DEVICE_RESTART, which depends on what ran
    /// against the outstation before, as the reference cases leave it out.
    /// </summary>
    private static string Iin(ClientResult r) =>
        string.Join(',', r.Iin.Where(i => i != "DEVICE_RESTART"));

    private static async Task<JsonElement> FeaturesAsync(string adapter) =>
        await ClientResult.CapabilitiesAsync(adapter);

    private static bool Has(JsonElement features, string name) =>
        features.TryGetProperty(name, out var f) && f.GetBoolean();

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task IntegrityPoll_ReadsTheBaselineFixture(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var outstation = new BaselineOutstation();
        var r = await ClientResult.RunAsync(adapter, outstation.Port, "integrity-poll");

        Assert.True(r.Ok, r.Raw);
        Assert.Equal("NEED_TIME", Iin(r));
        Assert.Equal(Baseline, r.ObjectSummary);

        Assert.Equal(230.5, r.Object(30, 0).GetProperty("value").GetDouble());
        Assert.Equal(-1234, r.Object(30, 1).GetProperty("value").GetDouble());
        Assert.Equal(4294967295, r.Object(20, 2).GetProperty("value").GetInt64());
        Assert.Equal("48656c6c6f", r.Object(110, 0).GetProperty("value").GetString());
        Assert.True(r.Object(1, 2).GetProperty("value").GetBoolean());
        Assert.Equal(
            ["ONLINE", "LOCAL_FORCED"],
            r.Object(1, 2).GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(
            ["ONLINE", "COMM_LOST"],
            r.Object(3, 1).GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(
            ["OVER_RANGE"],
            r.Object(30, 3).GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task EventPoll_OfAnUntouchedOutstationIsEmpty(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var outstation = new BaselineOutstation();

        var all = await ClientResult.RunAsync(adapter, outstation.Port, "event-poll");
        Assert.True(all.Ok, all.Raw);
        Assert.Equal("", all.ObjectSummary);

        var two = await ClientResult.RunAsync(adapter, outstation.Port, "event-poll", "--classes", "2");
        Assert.True(two.Ok, two.Raw);
        Assert.Equal("", two.ObjectSummary);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task EventPoll_ReportsAChangeInItsClass(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var outstation = new BaselineOutstation();
        outstation.Session.Update(db =>
            db.UpdateAnalog(4, new Analog(42, Flags.Online, Timestamp.NoTime())));

        var r = await ClientResult.RunAsync(adapter, outstation.Port, "event-poll", "--classes", "2");

        Assert.True(r.Ok, r.Raw);
        var e = r.Objects.Single();
        Assert.Equal(32, e.GetProperty("group").GetInt32());
        Assert.Equal(4, e.GetProperty("index").GetInt32());
        Assert.Equal(42, e.GetProperty("value").GetDouble());
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Read_ClassZeroAndRanges(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var outstation = new BaselineOutstation();

        var c0 = await ClientResult.RunAsync(
            adapter, outstation.Port, "read", "--group", "60", "--variation", "1");
        Assert.True(c0.Ok, c0.Raw);
        Assert.Equal(Baseline, c0.ObjectSummary);

        var binaries = await ClientResult.RunAsync(
            adapter, outstation.Port, "read", "--group", "1", "--variation", "2", "--start", "0", "--stop", "2");
        Assert.True(binaries.Ok, binaries.Raw);
        Assert.Equal("g1v2x3", binaries.ObjectSummary);

        var analogs = await ClientResult.RunAsync(
            adapter, outstation.Port, "read",
            "--group", "30", "--variation", "0", "--start", "0", "--stop", "1");
        Assert.True(analogs.Ok, analogs.Raw);
        Assert.Equal("g30v1x1,g30v5x1", analogs.ObjectSummary);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Read_AllObjectsOfAGroup(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        if (!Has(await FeaturesAsync(adapter), "readAllObjectsClient"))
        {
            Assert.Skip($"{adapter} cannot read all objects of a group");
        }

        await using var outstation = new BaselineOutstation();

        foreach (var (group, expected) in new (string, string)[]
        {
            ("1", "g1v1x1,g1v2x4"),
            ("3", "g3v2x2"),
            ("10", "g10v2x2"),
            ("20", "g20v1x1,g20v2x1,g20v5x1"),
            ("21", "g21v1x1,g21v2x1"),
            ("40", "g40v1x1,g40v3x1"),
            ("110", "g110v5x1"),
        })
        {
            var r = await ClientResult.RunAsync(
                adapter, outstation.Port, "read", "--group", group, "--variation", "0");
            Assert.True(r.Ok, r.Raw);
            Assert.Equal(expected, r.ObjectSummary);
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task AFrameForAnotherOutstationIsNotAnswered(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var outstation = new BaselineOutstation();

        var r = await ClientResult.RunAsync(
            adapter, outstation.Port, "integrity-poll", "--remote-addr", "99", "--timeout-ms", "1000");

        Assert.False(r.Ok, r.Raw);
        Assert.Equal("timeout", r.ErrorCode);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Operate_Crob(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var outstation = new BaselineOutstation();

        foreach (var (index, code, mode) in new (string, string, string)[]
        {
            ("0", "LATCH_ON", "direct"),
            ("0", "LATCH_OFF", "direct"),
            ("0", "CLOSE", "direct"),
            ("0", "TRIP", "sbo"),
            ("1", "LATCH_ON", "sbo"),
        })
        {
            var r = await ClientResult.RunAsync(
                adapter, outstation.Port, "operate",
                "--type", "g12v1", "--index", index, "--code", code, "--mode", mode);
            Assert.True(r.Ok, $"{index} {code} {mode}: {r.Raw}");
            Assert.Equal("SUCCESS", r.CommandStatus);
        }

        // What each master put on the wire, as our handler saw it. A select
        // is checked by the handler but only the operate is recorded.
        var crobs = outstation.Commands.Crobs;
        Assert.Equal(
            [(0, 0x03, OperateTypeOf.Direct), (0, 0x04, OperateTypeOf.Direct), (0, 0x41, OperateTypeOf.Direct),
             (0, 0x81, OperateTypeOf.Selected), (1, 0x03, OperateTypeOf.Selected)],
            crobs.Select(c => ((int)c.Index, (int)c.Crob.Code.Value, c.Type)).ToArray());
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Operate_CrobParameters(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var outstation = new BaselineOutstation();

        var r = await ClientResult.RunAsync(
            adapter, outstation.Port, "operate",
            "--type", "g12v1", "--index", "0", "--code", "CLOSE",
            "--count", "3", "--on-ms", "250", "--off-ms", "500");

        Assert.True(r.Ok, r.Raw);
        var (_, crob, _) = Assert.Single(outstation.Commands.Crobs);
        Assert.Equal(3, crob.Count);
        Assert.Equal(250u, crob.OnTime);
        Assert.Equal(500u, crob.OffTime);
        Assert.True(crob.Code.IsClose());
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Operate_Analog(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var outstation = new BaselineOutstation();

        foreach (var (type, index, value, mode) in new (string, string, string, string)[]
        {
            ("g41v1", "0", "-12345", "direct"),
            ("g41v2", "0", "42", "sbo"),
            ("g41v3", "1", "49.5", "sbo"),
            ("g41v4", "0", "1000", "direct"),
        })
        {
            var r = await ClientResult.RunAsync(
                adapter, outstation.Port, "operate",
                "--type", type, "--index", index, "--value", value, "--mode", mode);
            Assert.True(r.Ok, $"{type} {index} {value} {mode}: {r.Raw}");
            Assert.Equal("SUCCESS", r.CommandStatus);
        }

        Assert.Equal(
            [(0, -12345.0, (byte)1), (0, 42.0, (byte)2), (1, 49.5, (byte)3), (0, 1000.0, (byte)4)],
            outstation.Commands.Analogs.Select(a => ((int)a.Index, a.Command.Value, a.Command.Variation)).ToArray());
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Operate_OutOfRangeIsRefusedByOurHandler(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var outstation = new BaselineOutstation();

        var direct = await ClientResult.RunAsync(
            adapter, outstation.Port, "operate", "--type", "g41v1", "--index", "0", "--value", "2000000");
        Assert.False(direct.Ok);
        Assert.Equal("command-failed", direct.ErrorCode);
        Assert.Equal("OUT_OF_RANGE", direct.CommandStatus);

        var sbo = await ClientResult.RunAsync(
            adapter, outstation.Port, "operate",
            "--type", "g41v3", "--index", "1", "--value", "500", "--mode", "sbo");
        Assert.False(sbo.Ok);
        Assert.Equal("OUT_OF_RANGE", sbo.CommandStatus);

        Assert.Empty(outstation.Commands.Analogs);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Operate_OnAPointWeDoNotHaveIsRejected(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var outstation = new BaselineOutstation();

        var crob = await ClientResult.RunAsync(
            adapter, outstation.Port, "operate", "--type", "g12v1", "--index", "9", "--code", "LATCH_ON");
        Assert.False(crob.Ok);
        Assert.Equal("rejected", crob.ErrorCode);

        var analog = await ClientResult.RunAsync(
            adapter, outstation.Port, "operate", "--type", "g41v2", "--index", "5", "--value", "1");
        Assert.False(analog.Ok);
        Assert.Equal("rejected", analog.ErrorCode);

        Assert.Empty(outstation.Commands.Crobs);
        Assert.Empty(outstation.Commands.Analogs);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Operate_ACommandWeDoNotSupportIsRefused(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var outstation = new BaselineOutstation();

        var r = await ClientResult.RunAsync(
            adapter, outstation.Port, "operate", "--type", "g12v1", "--index", "0", "--code", "PULSE_ON");

        // Our outstation answers NOT_SUPPORTED together with PARAMETER_ERROR,
        // and a master library that hands the application the IIN of a
        // response reports a rejected request, one that does not, the status.
        Assert.False(r.Ok);
        Assert.Contains(r.ErrorCode, new[] { "rejected", "command-failed" });
        Assert.Empty(outstation.Commands.Crobs);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Restart_Cold(string adapter) => await RestartAsync(adapter, "cold", 5000);

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Restart_Warm(string adapter) => await RestartAsync(adapter, "warm", 1000);

    // Each restart gets its own outstation: one that has announced a restart
    // answers the next request with the time that is left of the first delay.
    private static async Task RestartAsync(string adapter, string type, int delayMs)
    {
        Reference.RequireOrSkip(adapter);

        var features = await FeaturesAsync(adapter);

        await using var outstation = new BaselineOutstation();

        var r = await ClientResult.RunAsync(adapter, outstation.Port, "restart", "--type", type);
        Assert.True(r.Ok, r.Raw);

        Assert.Equal(type == "cold" ? 1 : 0, outstation.Application.ColdRestarts);
        Assert.Equal(type == "warm" ? 1 : 0, outstation.Application.WarmRestarts);

        if (Has(features, "restartDelayClient"))
        {
            Assert.Equal(delayMs, r.Document.GetProperty("restartDelayMs").GetInt32());
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task TimeSync_WritesOurClockAndClearsNeedTime(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var outstation = new BaselineOutstation();

        var lan = await ClientResult.RunAsync(adapter, outstation.Port, "time-sync");
        Assert.True(lan.Ok, lan.Raw);
        Assert.NotNull(outstation.Application.TimeWritten);
        Assert.InRange(
            (DateTimeOffset.UtcNow - outstation.Application.TimeWritten.Value).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(30));

        // The outstation had asked for the time; now it does not.
        var poll = await ClientResult.RunAsync(adapter, outstation.Port, "integrity-poll");
        Assert.True(poll.Ok, poll.Raw);
        Assert.Equal("", Iin(poll));
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task TimeSync_NonLan(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var outstation = new BaselineOutstation();

        var r = await ClientResult.RunAsync(
            adapter, outstation.Port, "time-sync", "--procedure", "non-lan");

        Assert.True(r.Ok, r.Raw);
        Assert.NotNull(outstation.Application.TimeWritten);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Unsolicited_CanBeEnabledAndDisabled(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var outstation = new BaselineOutstation();

        var disable = await ClientResult.RunAsync(adapter, outstation.Port, "disable-unsolicited");
        Assert.True(disable.Ok, disable.Raw);
        var enable = await ClientResult.RunAsync(adapter, outstation.Port, "enable-unsolicited");
        Assert.True(enable.Ok, enable.Raw);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Unsolicited_ReportsAnEventOnceEnabled(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var outstation = new BaselineOutstation();

        // The master enables class 2 and stays for a while; the change is made
        // once it has spoken, so it arrives as an unsolicited response.
        var client = ClientResult.RunAsync(
            adapter, outstation.Port, "enable-unsolicited",
            "--classes", "2", "--collect-ms", "4000");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (outstation.Session.Stats.RequestsReceived == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the master never spoke");
            await Task.Delay(20);
        }

        await Task.Delay(500);
        outstation.Session.Update(db =>
            db.UpdateAnalog(4, new Analog(77, Flags.Online, Timestamp.NoTime())));

        var r = await client;

        Assert.True(r.Ok, r.Raw);
        var e = r.Objects.Single(o => o.GetProperty("unsolicited").GetBoolean());
        Assert.Equal(32, e.GetProperty("group").GetInt32());
        Assert.Equal(4, e.GetProperty("index").GetInt32());
        Assert.Equal(77, e.GetProperty("value").GetDouble());
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Freeze_CapturesTheCounters(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var outstation = new BaselineOutstation();

        var r = await ClientResult.RunAsync(adapter, outstation.Port, "freeze");
        Assert.True(r.Ok, r.Raw);

        Assert.True(outstation.Session.Database.TryGetFrozenCounter(0, out var frozen, out _));
        Assert.Equal(123456u, frozen.Value);
        Assert.True(outstation.Session.Database.TryGetFrozenCounter(1, out var other, out _));
        Assert.Equal(65535u, other.Value);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task FreezeAndClear_ZeroesTheCounters(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var outstation = new BaselineOutstation();

        var r = await ClientResult.RunAsync(adapter, outstation.Port, "freeze", "--clear");
        Assert.True(r.Ok, r.Raw);

        Assert.True(outstation.Session.Database.TryGetFrozenCounter(0, out var frozen, out _));
        Assert.Equal(123456u, frozen.Value);
        Assert.True(outstation.Session.Database.TryGetCounter(0, out var live, out _));
        Assert.Equal(0u, live.Value);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Monitor_KeepsTheConnectionUp(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var outstation = new BaselineOutstation();

        var r = await ClientResult.RunAsync(
            adapter, outstation.Port, "monitor", "--duration-ms", "300");

        Assert.True(r.Ok, r.Raw);
    }
}

/// <summary>Short names for the operate types the assertions spell out.</summary>
internal static class OperateTypeOf
{
    public const SharpDnp3.Outstation.OperateType Direct = SharpDnp3.Outstation.OperateType.Direct;

    public const SharpDnp3.Outstation.OperateType Selected = SharpDnp3.Outstation.OperateType.Selected;
}
