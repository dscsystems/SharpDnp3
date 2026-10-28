// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Our master against the reference outstations of otfabric/dnp3-interop
// (opendnp3, DSC go-dnp3 and Step Function I/O dnp3, each in a container),
// serving that repository's baseline fixture. The cases follow its own
// tests/cases.tsv.

using SharpDnp3.Master;

namespace SharpDnp3.Interop.Tests.Otfabric;

public class MasterTests
{
    public static TheoryData<string> Adapters => Reference.AdapterData();

    /// <summary>Every group and variation the baseline fixture serves in a class 0 poll.</summary>
    private const string Baseline =
        "g10v2x2,g110v5x1,g1v1x1,g1v2x4,g20v1x1,g20v2x1,g20v5x1,g21v1x1,g21v2x1," +
        "g30v1x3,g30v2x1,g30v5x1,g3v2x2,g40v1x1,g40v3x1";

    private static async Task<(ReferenceServer, OurMaster)> StartAsync(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        var server = await ReferenceServer.StartAsync(adapter);
        try
        {
            return (server, await OurMaster.ConnectAsync(server.Address));
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task IntegrityPoll_ReturnsTheBaselineFixture(string adapter)
    {
        var (server, master) = await StartAsync(adapter);
        await using (server)
        await using (master)
        {
            await master.Session.IntegrityPollAsync(master.Token);

            Assert.Equal(Baseline, master.Received.Summary);

            // Spot checks of the values the fixture sets, one per type.
            Assert.Equal((true, Flags.Online | Flags.LocalForced), master.Received.Point(1, 2));
            Assert.Equal((false, Flags.None), master.Received.Point(1, 3));
            Assert.Equal((2, Flags.Online), master.Received.Point(3, 0));
            Assert.Equal((0, Flags.Online | Flags.CommLost), master.Received.Point(3, 1));
            Assert.Equal((230.5, Flags.Online), master.Received.Point(30, 0));
            Assert.Equal((-1234.0, Flags.Online), master.Received.Point(30, 1));
            Assert.Equal((32767.0, Flags.Online), master.Received.Point(30, 2));
            Assert.Equal((0.0, Flags.OverRange), master.Received.Point(30, 3));
            Assert.Equal((123456u, Flags.Online), master.Received.Point(20, 0));
            Assert.Equal((65535u, Flags.Online), master.Received.Point(20, 1));
            Assert.Equal((4294967295u, Flags.Online), master.Received.Point(20, 2));
            Assert.Equal("48656c6c6f", master.Received.Point(110, 0).Value);
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task EventPoll_OfAnUntouchedOutstationIsEmpty(string adapter)
    {
        var (server, master) = await StartAsync(adapter);
        await using (server)
        await using (master)
        {
            await master.Session.ScanClassesAsync(Class.Class123, master.Token);

            Assert.Equal("", master.Received.Summary);
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task RangeReads_ReturnWhatWasAsked(string adapter)
    {
        var (server, master) = await StartAsync(adapter);
        await using (server)
        await using (master)
        {
            await master.Session.ScanRangeAsync(1, 2, 0, 2, master.Token);
            Assert.Equal("g1v2x3", master.Received.Summary);

            // Variation 0 leaves the choice to the outstation: each point in
            // its own static variation.
            master.Received.Clear();
            await master.Session.ScanRangeAsync(30, 0, 0, 1, master.Token);
            Assert.Equal("g30v1x1,g30v5x1", master.Received.Summary);
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task DirectOperate_Crob(string adapter)
    {
        var (server, master) = await StartAsync(adapter);
        await using (server)
        await using (master)
        {
            var result = await master.Session.DirectOperateAsync(
                [Command.LatchOn(0)], master.Token);
            Assert.True(result.OK(), result.ToString());

            var e = (await server.WaitForEventsAsync("command")).Single();
            Assert.Equal("direct-operate", e.GetProperty("function").GetString());
            Assert.Equal("g12v1", e.GetProperty("type").GetString());
            Assert.Equal(0, e.GetProperty("index").GetInt32());
            Assert.Equal(3, e.GetProperty("code").GetInt32());
            Assert.Equal("SUCCESS", e.GetProperty("status").GetString());
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task SelectBeforeOperate_Crob(string adapter)
    {
        var (server, master) = await StartAsync(adapter);
        await using (server)
        await using (master)
        {
            var result = await master.Session.SelectAndOperateAsync(
                [Command.Trip(0, 100)], master.Token);
            Assert.True(result.OK(), result.ToString());

            var events = await server.WaitForEventsAsync("command", 2);
            Assert.Equal(["select", "operate"], events.Select(e => e.GetProperty("function").GetString()));
            Assert.All(events, e =>
            {
                // Trip is a pulse on (1) with the trip bits (0x80).
                Assert.Equal(129, e.GetProperty("code").GetInt32());
                Assert.Equal(100, e.GetProperty("onMs").GetInt32());
                Assert.Equal("SUCCESS", e.GetProperty("status").GetString());
            });
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task DirectOperate_AnalogOutputs(string adapter)
    {
        var (server, master) = await StartAsync(adapter);
        await using (server)
        await using (master)
        {
            Assert.True((await master.Session.DirectOperateAsync(
                [Command.AnalogOutputInt32(0, -12345)], master.Token)).OK());
            Assert.True((await master.Session.DirectOperateAsync(
                [Command.AnalogOutputFloat64(0, 1000)], master.Token)).OK());

            var events = await server.WaitForEventsAsync("command", 2);
            Assert.Equal(["g41v1", "g41v4"], events.Select(e => e.GetProperty("type").GetString()));
            Assert.Equal(-12345, events[0].GetProperty("value").GetDouble());
            Assert.Equal(1000, events[1].GetProperty("value").GetDouble());
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task SelectBeforeOperate_AnalogOutput(string adapter)
    {
        var (server, master) = await StartAsync(adapter);
        await using (server)
        await using (master)
        {
            var result = await master.Session.SelectAndOperateAsync(
                [Command.AnalogOutputFloat32(1, 49.5f)], master.Token);
            Assert.True(result.OK(), result.ToString());

            var events = await server.WaitForEventsAsync("command", 2);
            Assert.Equal(["select", "operate"], events.Select(e => e.GetProperty("function").GetString()));
            Assert.All(events, e => Assert.Equal(49.5, e.GetProperty("value").GetDouble()));
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Operate_OutOfRangeIsRefusedByTheOutstation(string adapter)
    {
        var (server, master) = await StartAsync(adapter);
        await using (server)
        await using (master)
        {
            var result = await master.Session.DirectOperateAsync(
                [Command.AnalogOutputInt32(0, 2_000_000)], master.Token);

            Assert.False(result.OK());
            Assert.Equal([CommandStatus.OutOfRange], result.Statuses);
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Operate_OnAPointTheOutstationDoesNotHaveIsRefused(string adapter)
    {
        var (server, master) = await StartAsync(adapter);
        await using (server)
        await using (master)
        {
            var result = await master.Session.DirectOperateAsync(
                [Command.LatchOn(9)], master.Token);

            Assert.False(result.OK());
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task TimeSync_Lan(string adapter)
    {
        var (server, master) = await StartAsync(adapter);
        await using (server)
        await using (master)
        {
            await master.Session.SyncTimeAsync(master.Token);

            var e = (await server.WaitForEventsAsync("time-sync")).Single();
            var written = DateTimeOffset.Parse(
                e.GetProperty("time").ToString(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange((DateTimeOffset.UtcNow - written).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(30));
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Restart_Cold(string adapter) => await RestartAsync(adapter, RestartMode.Cold, "cold");

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Restart_Warm(string adapter) => await RestartAsync(adapter, RestartMode.Warm, "warm");

    private static async Task RestartAsync(string adapter, RestartMode mode, string type)
    {
        var (server, master) = await StartAsync(adapter);
        await using (server)
        await using (master)
        {
            await master.Session.RestartAsync(mode, master.Token);

            var e = (await server.WaitForEventsAsync("restart")).Single();
            Assert.Equal(type, e.GetProperty("type").GetString());
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Unsolicited_CanBeEnabledAndDisabled(string adapter)
    {
        var (server, master) = await StartAsync(adapter);
        await using (server)
        await using (master)
        {
            await master.Session.DisableUnsolicitedAsync(Class.Class123, master.Token);
            await master.Session.EnableUnsolicitedAsync(Class.Class123, master.Token);
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task FreezeCounters_IsAccepted(string adapter)
    {
        var (server, master) = await StartAsync(adapter);
        await using (server)
        await using (master)
        {
            await master.Session.FreezeCountersAsync(FreezeMode.Freeze, cancellationToken: master.Token);
            await master.Session.FreezeCountersAsync(FreezeMode.FreezeAndClear, cancellationToken: master.Token);
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task AnOutstationAtAnotherAddressDoesNotAnswer(string adapter)
    {
        Reference.RequireOrSkip(adapter);

        await using var server = await ReferenceServer.StartAsync(adapter);
        await using var master = await OurMaster.ConnectAsync(
            server.Address, remote: 99, responseTimeout: TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<Dnp3Exception>(
            () => master.Session.IntegrityPollAsync(master.Token));
    }
}
