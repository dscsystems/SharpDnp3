// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using SharpDnp3.App;
using SharpDnp3.Outstation;

namespace SharpDnp3.Conformance.Tests;

public class ConformanceTests
{
    private static OutstationConfig Config(
        DatabaseConfig? database = null,
        EventBufferConfig? events = null,
        TimeSpan selectTimeout = default) => new()
        {
            Database = database ?? Requests.SmallDatabase(),
            Events = events ?? new EventBufferConfig(),
            SelectTimeout = selectTimeout,
        };

    /// <summary>A fresh outstation asserts DEVICE_RESTART until a master clears it.</summary>
    [Fact]
    public async Task RestartIndicationAssertedUntilCleared()
    {
        await using var h = new Harness(Config());

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 1));
        Assert.True(
            resp.Header.Iin.Has(Iin.DeviceRestart),
            $"a fresh outstation must assert DEVICE_RESTART; IIN = {resp.Header.Iin}");

        // Writing zero to internal indication index 7 clears it.
        resp = await h.RequestAsync(FuncCode.Write, Requests.ClearRestart());
        Assert.False(
            resp.Header.Iin.Has(Iin.DeviceRestart),
            $"DEVICE_RESTART survived the write that clears it; IIN = {resp.Header.Iin}");
    }

    /// <summary>A class 0 read returns every static point the device has.</summary>
    [Fact]
    public async Task ClassZeroReturnsAllStaticData()
    {
        await using var h = new Harness(Config());

        h.Outstation.Update(db =>
        {
            db.UpdateBinary(2, new Binary(true, Flags.Online, Timestamp.NoTime()));
            db.UpdateAnalog(1, new Analog(77, Flags.Online, Timestamp.NoTime()));
        });

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 1));
        Assert.Equal(FuncCode.Response, resp.Header.Func);

        var seen = new Dictionary<byte, uint>();
        foreach (var o in resp.Objects)
        {
            seen[o.Group] = seen.GetValueOrDefault(o.Group) + o.Count;
        }

        var cfg = Requests.SmallDatabase();
        var expected = new Dictionary<byte, uint>
        {
            [1] = (uint)cfg.Binary,
            [10] = (uint)cfg.BinaryOutputStatus,
            [20] = (uint)cfg.Counter,
            [30] = (uint)cfg.Analog,
            [40] = (uint)cfg.AnalogOutputStatus,
        };

        foreach (var (group, want) in expected)
        {
            Assert.True(
                seen.GetValueOrDefault(group) == want,
                $"class 0 returned {seen.GetValueOrDefault(group)} objects of group {group}, want {want}");
        }
    }

    /// <summary>
    /// An unknown function code is refused with the matching indication, not
    /// ignored.
    /// </summary>
    [Fact]
    public async Task UnknownFunctionCode()
    {
        await using var h = new Harness(Config());

        var resp = await h.RequestAsync((FuncCode)0x70);
        Assert.True(
            resp.Header.Iin.Has(Iin.NoFuncCodeSupport),
            $"IIN = {resp.Header.Iin}, want NO_FUNC_CODE_SUPPORT");
    }

    /// <summary>
    /// A request for a group the device does not implement sets OBJECT_UNKNOWN.
    /// </summary>
    [Fact]
    public async Task UnknownObjectGroup()
    {
        await using var h = new Harness(Config());

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadRange(88, 1, 0, 1));
        Assert.True(
            resp.Header.Iin.Has(Iin.ObjectUnknown),
            $"IIN = {resp.Header.Iin}, want OBJECT_UNKNOWN");
    }

    /// <summary>
    /// The response's sequence number echoes the request's, or a master cannot
    /// match answers to questions.
    /// </summary>
    [Fact]
    public async Task ResponseEchoesRequestSequence()
    {
        await using var h = new Harness(Config());

        for (var i = 0; i < 4; i++)
        {
            var before = h.Count;
            await h.SendAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 1));
            var resp = await h.AwaitAsync(before);
            Assert.Equal(h.Seq, resp.Header.Control.Seq);
        }
    }

    /// <summary>
    /// A broadcast request is executed but never answered — every outstation
    /// answering at once would collide — and the next response says so.
    /// </summary>
    [Fact]
    public async Task BroadcastIsExecutedButNotAnswered()
    {
        await using var h = new Harness(Config());

        var before = h.Count;
        await h.SendToAsync(0xFFFF, FuncCode.Read, FragmentFactory.ReadAllObjects(60, 1));
        await Task.Delay(200);

        Assert.True(
            h.Count == before,
            $"the outstation answered a broadcast: {h.Count - before} new fragments");

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 1));
        Assert.True(
            resp.Header.Iin.Has(Iin.Broadcast),
            $"IIN = {resp.Header.Iin}, want the BROADCAST indication on the next response");
    }

    /// <summary>
    /// DELAY_MEASURE is answered with a group 52 variation 2 time delay.
    /// </summary>
    [Fact]
    public async Task DelayMeasure()
    {
        await using var h = new Harness(Config());

        var resp = await h.RequestAsync(FuncCode.DelayMeasure);
        Assert.Single(resp.Objects);

        var o = resp.Objects[0];
        Assert.Equal(52, o.Group);
        Assert.Equal(2, o.Variation);
        Assert.Equal(2, o.Data.Length);
    }

    /// <summary>
    /// A cold restart is answered with the time the device expects to be away,
    /// and re-asserts the restart indication.
    /// </summary>
    [Fact]
    public async Task ColdRestart()
    {
        await using var h = new Harness(Config());

        // Clear the initial indication so the re-assertion is unambiguous.
        await h.RequestAsync(FuncCode.Write, Requests.ClearRestart());

        var resp = await h.RequestAsync(FuncCode.ColdRestart);
        Assert.True(
            resp.Header.Iin.Has(Iin.DeviceRestart),
            $"IIN = {resp.Header.Iin}, want DEVICE_RESTART re-asserted");

        Assert.Single(resp.Objects);
        Assert.Equal(52, resp.Objects[0].Group);
    }

    /// <summary>Writing the time clears NEED_TIME.</summary>
    [Fact]
    public async Task WriteTimeClearsNeedTime()
    {
        await using var h = new Harness(Config());

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 1));
        Assert.True(
            resp.Header.Iin.Has(Iin.NeedTime),
            $"a fresh outstation should ask for the time; IIN = {resp.Header.Iin}");

        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        resp = await h.RequestAsync(FuncCode.Write, Requests.TimeWrite(1, now));

        Assert.False(
            resp.Header.Iin.Has(Iin.NeedTime),
            $"NEED_TIME survived a successful clock write; IIN = {resp.Header.Iin}");
    }

    /// <summary>
    /// The recorded-time procedure is the LAN clock synchronisation the standard
    /// describes. An outstation that refuses it leaves that master unable to set
    /// the clock at all.
    /// </summary>
    /// <remarks>
    /// The master sends RECORD_CURRENT_TIME, the outstation notes when it
    /// arrived, and the master then <em>writes</em> group 50 variation 3 with
    /// what its own clock read at that moment. The outstation adds however long
    /// it has taken since, which is what makes this better than a plain clock
    /// write: the transit delay is measured rather than assumed.
    /// </remarks>
    [Fact]
    public async Task RecordCurrentTimeProcedure()
    {
        await using var h = new Harness(Config());

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 1));
        Assert.True(
            resp.Header.Iin.Has(Iin.NeedTime),
            $"a fresh outstation should be asking for the time; IIN = {resp.Header.Iin}");

        // Step one: the outstation records when this arrived.
        resp = await h.RequestAsync(FuncCode.RecordCurrentTime);
        Assert.False(
            resp.Header.Iin.HasAny(Iin.NoFuncCodeSupport),
            $"RECORD_CURRENT_TIME was refused; IIN = {resp.Header.Iin}");

        // Step two: the master writes what its clock read at that moment.
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        resp = await h.RequestAsync(FuncCode.Write, Requests.TimeWrite(3, now));

        Assert.False(
            resp.Header.Iin.HasError(),
            $"the recorded-time write was refused; IIN = {resp.Header.Iin}");
        Assert.False(
            resp.Header.Iin.Has(Iin.NeedTime),
            "NEED_TIME survived a successful recorded-time synchronisation");
    }

    /// <summary>
    /// A group 50 variation 3 write with no RECORD_CURRENT_TIME before it has no
    /// reference to correct against, so it must be refused rather than silently
    /// treated as a plain clock write.
    /// </summary>
    [Fact]
    public async Task RecordedTimeWriteWithoutRecord()
    {
        await using var h = new Harness(Config());

        var resp = await h.RequestAsync(
            FuncCode.Write, Requests.TimeWrite(3, DateTimeOffset.UtcNow));

        Assert.True(
            resp.Header.Iin.Has(Iin.ParameterError),
            $"IIN = {resp.Header.Iin}, want PARAMETER_ERROR");
        Assert.True(resp.Header.Iin.Has(Iin.NeedTime), "the clock should not have been set");
    }

    /// <summary>
    /// Events are reported on a class poll and only dropped once confirmed.
    /// </summary>
    [Fact]
    public async Task EventsRequireConfirmation()
    {
        await using var h = new Harness(Config());

        h.Outstation.Update(db =>
            db.UpdateBinary(1, new Binary(true, Flags.Online, Timestamp.NoTime())));

        await Harness.WaitForAsync(() => h.Outstation.Events!.Total >= 1, "an event to be queued");

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 2));
        Assert.NotEmpty(resp.Objects);
        Assert.True(
            resp.Header.Control.Con,
            "a response carrying events must ask to be confirmed");

        // Until the confirm arrives, the events stay put.
        Assert.True(
            h.Outstation.Events!.Total > 0,
            "events were dropped before being confirmed");

        await h.SendConfirmAsync(resp.Header.Control.Seq);
        await Harness.WaitForAsync(
            () => h.Outstation.Events!.Total == 0, "the events to be dropped after the confirm");
    }

    /// <summary>
    /// An event buffer that overflows reports it, which is the only way a master
    /// learns its record has a hole in it.
    /// </summary>
    [Fact]
    public async Task EventBufferOverflowIndication()
    {
        await using var h = new Harness(
            Config(events: new EventBufferConfig { MaxEvents = 2 }));

        h.Outstation.Update(db =>
        {
            for (var i = 0; i < 20; i++)
            {
                db.UpdateBinary(0, new Binary(i % 2 == 0, Flags.Online, Timestamp.NoTime()));
            }
        });

        await Harness.WaitForAsync(
            () => h.Outstation.Events!.Overflowed, "the event buffer to overflow");

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 2));
        Assert.True(
            resp.Header.Iin.Has(Iin.EventBufferOverflow),
            $"IIN = {resp.Header.Iin}, want EVENT_BUFFER_OVERFLOW");
    }

    /// <summary>
    /// A select reserves and an operate executes; the outstation echoes each
    /// command with its status.
    /// </summary>
    [Fact]
    public async Task SelectBeforeOperate()
    {
        var plant = new RecordingCommandHandler();
        await using var h = new Harness(
            Config(selectTimeout: TimeSpan.FromSeconds(5)), plant);

        var crob = Requests.CrobHeader(3, ControlCode.LatchOn);

        var resp = await h.RequestAsync(FuncCode.Select, crob);
        Assert.Equal(CommandStatus.Success, Requests.CommandStatusOf(resp));
        Assert.Equal(0, plant.Operates);

        resp = await h.RequestAsync(FuncCode.Operate, crob);
        Assert.Equal(CommandStatus.Success, Requests.CommandStatusOf(resp));
        Assert.Equal(1, plant.Operates);
    }

    /// <summary>An operate with no live selection is refused with NO_SELECT.</summary>
    [Fact]
    public async Task OperateWithoutSelect()
    {
        var plant = new RecordingCommandHandler();
        await using var h = new Harness(Config(), plant);

        var resp = await h.RequestAsync(
            FuncCode.Operate, Requests.CrobHeader(1, ControlCode.LatchOn));

        Assert.Equal(CommandStatus.NoSelect, Requests.CommandStatusOf(resp));
        Assert.Equal(0, plant.Operates);
    }

    /// <summary>
    /// An operate naming different objects from the select is refused. The whole
    /// point of the two-pass sequence is that the operator confirms exactly what
    /// was proposed.
    /// </summary>
    [Fact]
    public async Task OperateMustMatchSelect()
    {
        var plant = new RecordingCommandHandler();
        await using var h = new Harness(
            Config(selectTimeout: TimeSpan.FromSeconds(5)), plant);

        await h.RequestAsync(FuncCode.Select, Requests.CrobHeader(0, ControlCode.LatchOn));

        // A different point from the one that was selected.
        var resp = await h.RequestAsync(
            FuncCode.Operate, Requests.CrobHeader(1, ControlCode.LatchOn));

        Assert.NotEqual(CommandStatus.Success, Requests.CommandStatusOf(resp));
        Assert.Equal(0, plant.Operates);
    }

    /// <summary>
    /// Enable and disable unsolicited are accepted for the event classes.
    /// </summary>
    [Theory]
    [InlineData((int)FuncCode.EnableUnsolicited)]
    [InlineData((int)FuncCode.DisableUnsolicited)]
    public async Task UnsolicitedControlAccepted(int funcCode)
    {
        await using var h = new Harness(Config());

        var resp = await h.RequestAsync(
            (FuncCode)funcCode,
            FragmentFactory.ReadAllObjects(60, 2),
            FragmentFactory.ReadAllObjects(60, 3),
            FragmentFactory.ReadAllObjects(60, 4));

        Assert.False(
            resp.Header.Iin.HasAny(Iin.NoFuncCodeSupport | Iin.ObjectUnknown),
            $"{(FuncCode)funcCode} was refused; IIN = {resp.Header.Iin}");
    }

    /// <summary>A range read returns exactly the points asked for.</summary>
    [Fact]
    public async Task RangeReadReturnsRequestedPoints()
    {
        await using var h = new Harness(Config());

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadRange(30, 1, 1, 2));
        Assert.Single(resp.Objects);

        var o = resp.Objects[0];
        Assert.Equal(2u, o.Count);
        Assert.Equal(1u, o.Range.Start);
        Assert.Equal(2u, o.Range.Stop);
    }

    /// <summary>
    /// A read of a range beyond the database is clamped rather than refused, and
    /// returns the points that do exist.
    /// </summary>
    [Fact]
    public async Task RangeBeyondDatabaseIsClamped()
    {
        await using var h = new Harness(Config());

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadRange(30, 1, 0, 200));
        Assert.Single(resp.Objects);
        Assert.Equal((uint)Requests.SmallDatabase().Analog, resp.Objects[0].Count);
    }

    /// <summary>Deadbands written by a master take effect.</summary>
    [Fact]
    public async Task WriteAnalogDeadband()
    {
        await using var h = new Harness(Config());

        // 100.0 as a single-precision float, at index 0.
        var resp = await h.RequestAsync(FuncCode.Write, new ObjectHeader
        {
            Group = 34,
            Variation = 3,
            Qualifier = Qualifier.Make(IndexPrefix.Index1, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
            Data = new byte[] { 0, 0x00, 0x00, 0xC8, 0x42 },
        });

        Assert.False(
            resp.Header.Iin.HasError(),
            $"the deadband write was refused; IIN = {resp.Header.Iin}");

        Assert.True(h.Outstation.Database.TryGetAnalog(0, out _, out var cfg));
        Assert.Equal(100, cfg.Deadband);

        // And it is honoured: a move inside it generates no event. Written
        // straight to the database so the assertion does not race the queue.
        var db = h.Outstation.Database;
        db.UpdateAnalog(0, new Analog(0, Flags.Online, Timestamp.NoTime()));

        await Harness.WaitForAsync(
            () => h.Outstation.Events!.Total >= 1, "the first analog event");
        var before = h.Outstation.Events!.Total;

        db.UpdateAnalog(0, new Analog(50, Flags.Online, Timestamp.NoTime()));

        Assert.True(
            h.Outstation.Events.Total == before,
            "a move of 50 inside a deadband of 100 produced an event");
    }
}
