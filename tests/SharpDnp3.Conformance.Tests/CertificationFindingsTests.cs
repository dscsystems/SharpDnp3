// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The tests in this file each pin an answer the DNP Users Group's IED
// certification procedures expect and the outstation once got wrong, as dnp3c
// (an independent implementation of those procedures, run against go-dnp3)
// found. Each names the procedure that found it.

using SharpDnp3.App;
using SharpDnp3.Link;
using SharpDnp3.Objects;
using SharpDnp3.Outstation;

namespace SharpDnp3.Conformance.Tests;

public class CertificationFindingsTests
{
    private static OutstationConfig Config() => new() { Database = Requests.SmallDatabase() };

    /// <summary>
    /// 8.2.1.2.3: a command to an output the device does not have is refused
    /// with NOT_SUPPORTED and PARAMETER_ERROR, and reaches no handler.
    /// </summary>
    [Fact]
    public async Task CommandToAMissingOutputIsAParameterError()
    {
        var plant = new RecordingCommandHandler();
        await using var h = new Harness(Config(), plant);

        var resp = await h.RequestAsync(FuncCode.DirectOperate, Requests.CrobHeader(5, ControlCode.LatchOn));
        Assert.Equal(CommandStatus.NotSupported, Requests.CommandStatusOf(resp));
        Assert.True(resp.Header.Iin.Has(Iin.ParameterError), $"IIN = {resp.Header.Iin}, want PARAMETER_ERROR");
        Assert.Equal(0, plant.Operates);
    }

    /// <summary>
    /// 8.2.1.2.4: an operate after the select has timed out is refused with
    /// TIMEOUT, which tells the operator why, not NO_SELECT.
    /// </summary>
    [Fact]
    public async Task OperateAfterTheSelectTimeoutIsTimeout()
    {
        var plant = new RecordingCommandHandler();
        var config = Config();
        config.SelectTimeout = TimeSpan.FromMilliseconds(100);
        await using var h = new Harness(config, plant);

        var crob = Requests.CrobHeader(0, ControlCode.LatchOn);
        Assert.Equal(CommandStatus.Success, Requests.CommandStatusOf(await h.RequestAsync(FuncCode.Select, crob)));
        await Task.Delay(250);
        Assert.Equal(CommandStatus.Timeout, Requests.CommandStatusOf(await h.RequestAsync(FuncCode.Operate, crob)));
        Assert.Equal(0, plant.Operates);
    }

    /// <summary>
    /// 8.2.4: the controls past MaxControlsPerRequest are refused with
    /// TOO_MANY_OPS, and the ones within it are carried out.
    /// </summary>
    [Fact]
    public async Task ControlsPastTheCapAreTooManyOps()
    {
        var plant = new RecordingCommandHandler();
        var config = Config();
        config.MaxControlsPerRequest = 1;
        await using var h = new Harness(config, plant);

        var data = new List<byte>();
        foreach (var index in new byte[] { 0, 1 })
        {
            data.Add(index);
            CommandObjects.AppendCrob(data, new ControlRelayOutputBlock { Code = ControlCode.LatchOn, Count = 1 });
        }

        var resp = await h.RequestAsync(FuncCode.DirectOperate, new ObjectHeader
        {
            Group = 12,
            Variation = 1,
            Qualifier = Qualifier.Make(IndexPrefix.Index1, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 2 },
            Data = data.ToArray(),
        });

        Assert.Equal([CommandStatus.Success, CommandStatus.TooManyOps], CommandStatuses(resp));
        Assert.Equal(1, plant.Operates);
    }

    /// <summary>
    /// 8.5.2.2.1: the response that carries the last events of a class does not
    /// say the class still has events.
    /// </summary>
    [Fact]
    public async Task ClassBitClearsOnTheResponseThatEmptiesTheClass()
    {
        await using var h = new Harness(Config());
        h.Outstation.Update(db => db.UpdateBinary(0, new Binary(true, Flags.Online, default)));
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total > 0, "the event");

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 2));
        Assert.NotEmpty(resp.Objects);
        Assert.False(
            resp.Header.Iin.Has(Iin.Class1Events),
            $"IIN = {resp.Header.Iin}: CLASS_1_EVENTS on the response carrying every class 1 event");
    }

    /// <summary>
    /// 8.5.2.2.4, 8.26.2: a confirmation and the next request arriving
    /// together, in one read from the connection. The confirm is acted on first,
    /// so the events it acknowledges are gone by the time the request is
    /// answered.
    /// </summary>
    [Fact]
    public async Task ConfirmAndRequestInOneReadConfirmsTheEvents()
    {
        await using var h = new Harness(Config());
        h.Outstation.Update(db => db.UpdateBinary(0, new Binary(true, Flags.Online, default)));
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total > 0, "the event");

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 2));
        Assert.True(
            resp.Objects.Count > 0 && resp.Header.Control.Con,
            $"CON = {resp.Header.Control.Con}, {resp.Objects.Count} objects: want the event asking for confirmation");

        var confirm = Harness.Confirm(h.Seq);
        var read = h.NextRequest(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 2));
        var before = h.Count;
        await h.SendTogetherAsync(confirm, read);

        var next = await h.AwaitAsync(before);
        Assert.True(next.Objects.Count == 0, $"{Groups(next)}: the confirmed event was reported again");
    }

    /// <summary>
    /// 8.5.5.2.2: a read of static data and events reports the events first, so
    /// the static values, which are newer, are what the master is left with.
    /// </summary>
    [Fact]
    public async Task EventsComeBeforeStaticData()
    {
        await using var h = new Harness(Config());
        h.Outstation.Update(db => db.UpdateBinary(0, new Binary(true, Flags.Online, default)));
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total > 0, "the event");

        var resp = await h.RequestAsync(
            FuncCode.Read, FragmentFactory.ReadAllObjects(60, 1), FragmentFactory.ReadAllObjects(60, 2));

        var staticSeen = false;
        foreach (var o in resp.Objects)
        {
            switch (o.Group)
            {
                case 2:
                    Assert.False(staticSeen, $"an event object follows static data: {Groups(resp)}");
                    break;
                case 1 or 10 or 20 or 30 or 40:
                    staticSeen = true;
                    break;
            }
        }

        Assert.True(staticSeen, $"no static data in the response: {Groups(resp)}");
    }

    /// <summary>
    /// 8.6.5b: a broadcast to 0xFFFE has the response reporting it ask for
    /// confirmation, and the indication stays set until it is confirmed; one to
    /// 0xFFFD does not ask, and clears once reported.
    /// </summary>
    [Fact]
    public async Task BroadcastIndicationIsConfirmedWhenAsked()
    {
        await using var h = new Harness(Config());
        Task<Fragment> Read() => h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 2));

        await h.SendToAsync(LinkConstants.BroadcastMandatoryConfirm, FuncCode.ImmedFreezeNR);
        await Task.Delay(100);
        for (var i = 0; i < 2; i++)
        {
            // Unconfirmed, the second read reports the broadcast again.
            var resp = await Read();
            Assert.True(
                resp.Header.Iin.Has(Iin.Broadcast) && resp.Header.Control.Con,
                $"IIN = {resp.Header.Iin}, CON = {resp.Header.Control.Con}: want BROADCAST, and a confirmation asked for");
        }

        await h.SendConfirmAsync(h.Seq);
        var confirmed = await Read();
        Assert.False(
            confirmed.Header.Iin.Has(Iin.Broadcast),
            $"IIN = {confirmed.Header.Iin}: BROADCAST still set once its response was confirmed");

        await h.SendToAsync(LinkConstants.BroadcastNoConfirm, FuncCode.ImmedFreezeNR);
        await Task.Delay(100);
        var reported = await Read();
        Assert.True(
            reported.Header.Iin.Has(Iin.Broadcast) && !reported.Header.Control.Con,
            $"IIN = {reported.Header.Iin}, CON = {reported.Header.Control.Con}: want BROADCAST, and no confirmation asked for");
        var after = await Read();
        Assert.False(
            after.Header.Iin.Has(Iin.Broadcast),
            $"IIN = {after.Header.Iin}: BROADCAST still set after it was reported");
    }

    /// <summary>
    /// 8.6.6.2.1: a read that replaces an unfinished multi-fragment response
    /// keeps the events it selected, and its confirmation removes them.
    /// </summary>
    [Fact]
    public async Task ReadReplacingAnUnfinishedResponseKeepsItsEvents()
    {
        var config = Config();
        config.MaxTxFragment = 128;
        await using var h = new Harness(config);

        const int N = 60;
        h.Outstation.Update(db =>
        {
            for (var i = 0; i < N; i++)
            {
                db.UpdateBinary(0, new Binary(i % 2 == 0, Flags.Online, default));
            }
        });
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total == N, "the events");

        var first = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 2));
        Assert.False(first.Header.Control.Fin, "the class 1 read fitted one fragment; the test needs more events");

        // Left unconfirmed, and replaced by a read of one event.
        var resp = await h.RequestAsync(FuncCode.Read, new ObjectHeader
        {
            Group = 2,
            Variation = 0,
            Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
        });
        await h.SendConfirmAsync(resp.Header.Control.Seq);
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total == N - 1, "the confirmation removing the one event read");
    }

    /// <summary>
    /// 8.17.2c: a read that arrives while a response carrying events awaits
    /// confirmation is answered, not left behind the confirmation.
    /// </summary>
    [Fact]
    public async Task ReadWhileAwaitingConfirmationIsAnswered()
    {
        var config = Config();
        config.ConfirmTimeout = TimeSpan.FromSeconds(2);
        await using var h = new Harness(config);
        h.Outstation.Update(db => db.UpdateBinary(0, new Binary(true, Flags.Online, default)));
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total > 0, "the event");

        var first = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 2));
        Assert.True(first.Header.Control.Con, "the response carrying events did not ask for confirmation");

        var before = h.Count;
        await h.SendAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 2));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (h.Count == before)
        {
            Assert.True(
                DateTime.UtcNow < deadline,
                "the second read was not answered while the first response awaited confirmation");
            await Task.Delay(2);
        }

        var resp = await h.AwaitAsync(before);
        Assert.Equal(h.Seq, resp.Header.Control.Seq);
    }

    /// <summary>An application that can turn cold restart off.</summary>
    private sealed class RestartSupport : NopApplication, IRestartSupport
    {
        public volatile bool Cold;

        public bool SupportsColdRestart() => Cold;

        public bool SupportsWarmRestart() => true;
    }

    /// <summary>
    /// 8.8.2: a device that does not support cold restart refuses it with
    /// NO_FUNC_CODE_SUPPORT, and does not restart.
    /// </summary>
    [Fact]
    public async Task ColdRestartRefusedWhenUnsupported()
    {
        var appl = new RestartSupport();
        await using var h = new Harness(Config(), application: appl);
        await h.RequestAsync(FuncCode.Write, Requests.ClearRestart());

        var resp = await h.RequestAsync(FuncCode.ColdRestart);
        Assert.True(
            resp.Header.Iin.Has(Iin.NoFuncCodeSupport) && !resp.Header.Iin.Has(Iin.DeviceRestart),
            $"IIN = {resp.Header.Iin}: want NO_FUNC_CODE_SUPPORT, and no restart");
        Assert.True(resp.Objects.Count == 0, $"an unsupported restart answered with a delay: {Groups(resp)}");

        appl.Cold = true;
        resp = await h.RequestAsync(FuncCode.ColdRestart);
        Assert.True(resp.Header.Iin.Has(Iin.DeviceRestart), $"IIN = {resp.Header.Iin}: a supported cold restart did not restart");
    }

    private static OutstationConfig UnsolicitedConfig(TimeSpan confirmTimeout, int maxRetries = 5)
    {
        var config = Config();
        config.Unsolicited = new UnsolicitedConfig
        {
            Enabled = true,
            ConfirmTimeout = confirmTimeout,
            MaxRetries = maxRetries,
        };
        return config;
    }

    /// <summary>
    /// 8.11.2a: an unsolicited retry reports the indications as they are now,
    /// not as they were on the first attempt.
    /// </summary>
    [Fact]
    public async Task UnsolicitedRetryReportsCurrentIndications()
    {
        await using var h = new Harness(UnsolicitedConfig(TimeSpan.FromMilliseconds(300)));

        var nullResponse = await h.AwaitAsync(0);
        Assert.True(
            nullResponse.Header.Iin.Has(Iin.DeviceRestart),
            $"the null unsolicited response does not report the restart: {nullResponse.Header.Iin}");

        var cleared = await h.RequestAsync(FuncCode.Write, Requests.ClearRestart());
        Assert.False(cleared.Header.Iin.Has(Iin.DeviceRestart), $"IIN = {cleared.Header.Iin} after clearing DEVICE_RESTART");

        var retry = await h.AwaitUnsolicitedAsync(h.Count);
        Assert.Equal(nullResponse.Header.Control.Seq, retry.Header.Control.Seq);
        Assert.False(
            retry.Header.Iin.Has(Iin.DeviceRestart),
            $"IIN = {retry.Header.Iin}: the retry still reports the restart the master cleared");
    }

    /// <summary>
    /// 8.11.2.5.4: a read that arrives while an unsolicited response awaits
    /// confirmation is answered once the confirmation arrives.
    /// </summary>
    [Fact]
    public async Task ReadWaitsForTheUnsolicitedConfirmation()
    {
        await using var h = new Harness(UnsolicitedConfig(TimeSpan.FromSeconds(2)));
        var nullResponse = await h.AwaitAsync(0);

        var before = h.Count;
        await h.SendAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 1));
        await Task.Delay(300);
        Assert.True(h.Count == before, "the read was answered while the unsolicited response awaited confirmation");

        await h.SendConfirmAsync(nullResponse.Header.Control.Seq, unsolicited: true);
        var resp = await h.AwaitAsync(before);
        Assert.False(resp.Header.Control.Uns, "after the confirmation: want the answer to the read");
        Assert.Equal(h.Seq, resp.Header.Control.Seq);
    }

    /// <summary>
    /// 8.11.2a: a read deferred behind the null unsolicited response is
    /// answered when its confirmation times out, and the null response is still
    /// retried.
    /// </summary>
    [Fact]
    public async Task NullUnsolicitedIsRetriedAfterADeferredRead()
    {
        // A pause long enough that a series ended by the read could not start
        // again inside the harness's wait.
        var config = UnsolicitedConfig(TimeSpan.FromMilliseconds(300));
        config.Unsolicited.RetryDelay = TimeSpan.FromSeconds(10);
        await using var h = new Harness(config);
        var nullResponse = await h.AwaitAsync(0);
        var seq = nullResponse.Header.Control.Seq;

        var before = h.Count;
        await h.SendAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 1));
        var resp = await h.AwaitAsync(before);
        Assert.False(resp.Header.Control.Uns, $"{resp.Header}: want the answer to the read first");
        Assert.Equal(h.Seq, resp.Header.Control.Seq);

        var retry = await h.AwaitUnsolicitedAsync(before + 1);
        Assert.Equal(seq, retry.Header.Control.Seq);

        // The null response holds no events for the poll to have collected, so
        // the poll is no reason to stop announcing: the retries go on at the
        // confirm timeout, and a confirm of the wrong sequence number confirms
        // nothing.
        await h.SendConfirmAsync((byte)((seq + 1) % AppConstants.SeqModulus), unsolicited: true);
        var again = await h.AwaitUnsolicitedAsync(h.Count);
        Assert.Equal(seq, again.Header.Control.Seq);
    }

    /// <summary>A command handler that says it has binary outputs 0 to 9, whatever the database holds.</summary>
    private sealed class OutputPoints : ICommandHandler, IOutputPoints
    {
        public RecordingCommandHandler Plant { get; } = new();

        public bool HasOutput(PointType pt, ushort index) => pt == PointType.BinaryOutputStatus && index < 10;

        public CommandStatus SelectCrob(ushort index, ControlRelayOutputBlock c) => Plant.SelectCrob(index, c);

        public CommandStatus OperateCrob(ushort index, ControlRelayOutputBlock c, OperateType op) =>
            Plant.OperateCrob(index, c, op);

        public CommandStatus SelectAnalog(ushort index, AnalogOutputCommand v) => Plant.SelectAnalog(index, v);

        public CommandStatus OperateAnalog(ushort index, AnalogOutputCommand v, OperateType op) =>
            Plant.OperateAnalog(index, v, op);
    }

    /// <summary>
    /// 8.2.1.2.10: a handler implementing <see cref="IOutputPoints"/> decides
    /// which outputs exist, for a device whose database has no output status to
    /// go by.
    /// </summary>
    [Fact]
    public async Task OutputPointsDecideWhichOutputsExist()
    {
        var handler = new OutputPoints();
        await using var h = new Harness(new OutstationConfig { Database = new DatabaseConfig { Binary = 1 } }, handler);

        var resp = await h.RequestAsync(FuncCode.DirectOperate, Requests.CrobHeader(7, ControlCode.LatchOn));
        Assert.Equal(CommandStatus.Success, Requests.CommandStatusOf(resp));

        resp = await h.RequestAsync(FuncCode.DirectOperate, Requests.CrobHeader(12, ControlCode.LatchOn));
        Assert.Equal(CommandStatus.NotSupported, Requests.CommandStatusOf(resp));
        Assert.True(resp.Header.Iin.Has(Iin.ParameterError), $"IIN = {resp.Header.Iin}, want PARAMETER_ERROR");
        Assert.Equal(1, handler.Plant.Operates);
    }

    /// <summary>
    /// 8.13.2d, 8.23.2.1.3: a read naming points the device does not have says
    /// so, and still reports the ones it does.
    /// </summary>
    [Fact]
    public async Task ReadOfMissingPointsIsAnError()
    {
        await using var h = new Harness(Config());

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadRange(1, 2, 2, 5));
        Assert.True(resp.Header.Iin.Has(Iin.ParameterError), $"IIN = {resp.Header.Iin}: a range past the last binary input");
        Assert.Single(resp.Objects);
        Assert.Equal(2u, resp.Objects[0].Range.Start);
        Assert.Equal(3u, resp.Objects[0].Range.Stop);

        resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(21, 0));
        Assert.True(
            resp.Header.Iin.HasAny(Iin.ObjectUnknown | Iin.ParameterError),
            $"IIN = {resp.Header.Iin}: a read of frozen counters on a device with none");

        resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(4, 0));
        Assert.True(
            resp.Header.Iin.HasAny(Iin.ObjectUnknown | Iin.ParameterError),
            $"IIN = {resp.Header.Iin}: a read of double-bit events on a device with no double-bit inputs");

        resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(1, 0));
        Assert.False(resp.Header.Iin.HasAny(Iin.RequestErrorMask), $"IIN = {resp.Header.Iin}: a read of every binary input is not an error");
    }

    /// <summary>
    /// 8.15.3, 8.14.2m: a clock set by the master is asked for again after the
    /// sync interval, and stops being trusted after the holdover.
    /// </summary>
    [Fact]
    public async Task ClockIsAskedForAgainAndExpires()
    {
        var config = Config();
        config.Clock = new ClockConfig
        {
            SyncInterval = TimeSpan.FromMilliseconds(200),
            Holdover = TimeSpan.FromMilliseconds(200),
        };
        await using var h = new Harness(config);
        await h.RequestAsync(FuncCode.Write, Requests.ClearRestart());

        var set = await h.RequestAsync(FuncCode.Write, Requests.TimeWrite(1, DateTimeOffset.UtcNow));
        Assert.False(set.Header.Iin.Has(Iin.NeedTime), $"IIN = {set.Header.Iin} after the clock was set");
        Assert.Equal(1, await RelativeEventCtoAsync(h));

        await Task.Delay(250);
        var due = await h.RequestAsync(FuncCode.DelayMeasure);
        Assert.True(due.Header.Iin.Has(Iin.NeedTime), $"IIN = {due.Header.Iin} after the sync interval, want NEED_TIME");
        Assert.Equal(1, await RelativeEventCtoAsync(h));

        await Task.Delay(250);
        Assert.Equal(2, await RelativeEventCtoAsync(h));
    }

    /// <summary>
    /// 8.24.2a: a read of the packed variation g1v1 reports the points, a bit
    /// each.
    /// </summary>
    [Fact]
    public async Task PackedBinaryRead()
    {
        await using var h = new Harness(Config());
        h.Outstation.Update(db =>
        {
            bool[] values = [true, false, true, true];
            for (var i = 0; i < values.Length; i++)
            {
                db.UpdateBinary((ushort)i, new Binary(values[i], Flags.Online, default));
            }
        });

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(1, 1));
        var o = Assert.Single(resp.Objects);
        Assert.Equal((1, 1, 4u), (o.Group, o.Variation, o.Count));
        Assert.Equal(new byte[] { 0x0D }, o.Data.ToArray());
    }

    /// <summary>
    /// 8.24.2d: a read of a variation without flags reports a point whose flags
    /// are abnormal with them.
    /// </summary>
    [Fact]
    public async Task FlaglessReadReportsAnAbnormalPointWithFlags()
    {
        await using var h = new Harness(Config());
        h.Outstation.Update(db =>
        {
            db.UpdateAnalog(0, new Analog(1, Flags.Online | Flags.LocalForced, default));
            db.UpdateAnalog(1, new Analog(2, Flags.Online, default));
            db.UpdateAnalog(2, new Analog(3, Flags.Online, default));
        });

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(30, 3));
        Assert.Equal(["g30v1 0-0", "g30v3 1-2"], Groups(resp));
    }

    // ---------- Helpers ----------

    /// <summary>Reads every status out of a command echo.</summary>
    private static List<CommandStatus> CommandStatuses(Fragment resp)
    {
        var output = new List<CommandStatus>();
        foreach (var o in resp.Objects)
        {
            if (o.Group is not (12 or 41) || !ObjectRegistry.TryLookup(GroupVar.GV(o.Group, o.Variation), out var d))
            {
                continue;
            }

            d.TrySizeOctets(out var size);
            var step = o.Qualifier.IndexPrefix.Octets() + size;
            var data = o.Data.Span;
            for (var off = 0; off + step <= data.Length; off += step)
            {
                output.Add((CommandStatus)data[off + step - 1]);
            }
        }

        return output;
    }

    private static List<string> Groups(Fragment resp) =>
        [.. resp.Objects.Select(o => o.Range.Spec.IsStartStop()
            ? $"g{o.Group}v{o.Variation} {o.Range.Start}-{o.Range.Stop}"
            : $"g{o.Group}v{o.Variation}")];

    /// <summary>
    /// Makes a binary input event, reads it as g2v3, and returns the variation
    /// of the common time of occurrence it is measured from.
    /// </summary>
    private static async Task<int> RelativeEventCtoAsync(Harness h)
    {
        var total = h.Outstation.Events!.Total;
        h.Outstation.Update(db =>
        {
            db.TryGetBinary(0, out var v, out _);
            db.UpdateBinary(0, new Binary(!v.Value, Flags.Online, Timestamp.Now(DateTimeOffset.UtcNow)));
        });
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total > total, "the event");

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(2, 3));
        await h.SendConfirmAsync(resp.Header.Control.Seq);
        foreach (var o in resp.Objects)
        {
            if (o.Group == 51)
            {
                return o.Variation;
            }
        }

        Assert.Fail($"no common time of occurrence in the g2v3 response: {string.Join(", ", Groups(resp))}");
        return 0;
    }
}
