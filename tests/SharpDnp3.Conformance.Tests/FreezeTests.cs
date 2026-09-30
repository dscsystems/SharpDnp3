// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// FREEZE_CLEAR, FREEZE_AT_TIME, command events and the refusal of pattern
// controls.

using SharpDnp3.App;
using SharpDnp3.Objects;
using SharpDnp3.Outstation;

namespace SharpDnp3.Conformance.Tests;

public class FreezeTests
{
    private static async Task<Harness> FreezeHarnessAsync()
    {
        var db = Requests.SmallDatabase();
        db.Counter = 3;
        db.FrozenCounter = 3;
        var h = new Harness(new OutstationConfig { Database = db });
        h.Outstation.Update(d =>
        {
            for (ushort i = 0; i < 3; i++)
            {
                d.UpdateCounter(i, new Counter((uint)(100 * (i + 1)), Flags.Online, default));
            }
        });
        await Harness.WaitForAsync(
            () => h.Outstation.Database.TryGetCounter(2, out var c, out _) && c.Value == 300, "counters");
        return h;
    }

    private static uint Frozen(Harness h, ushort i)
    {
        h.Outstation.Database.TryGetFrozenCounter(i, out var f, out _);
        return f.Value;
    }

    /// <summary>Builds the group 50 variation 2 object that leads a FREEZE_AT_TIME.</summary>
    private static ObjectHeader FreezeAt(DateTimeOffset at, TimeSpan interval)
    {
        var data = new List<byte>();
        CommandObjects.AppendTime48(data, Timestamp.Now(at));
        var ms = (uint)interval.TotalMilliseconds;
        data.AddRange([(byte)ms, (byte)(ms >> 8), (byte)(ms >> 16), (byte)(ms >> 24)]);
        return new ObjectHeader
        {
            Group = 50,
            Variation = 2,
            Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
            Data = data.ToArray(),
        };
    }

    [Fact]
    public async Task FreezeClearResetsTheRunningCounter()
    {
        await using var h = await FreezeHarnessAsync();
        var resp = await h.RequestAsync(FuncCode.FreezeClear, FragmentFactory.ReadRange(20, 0, 1, 1));
        Assert.False(resp.Header.Iin.Has(Iin.NoFuncCodeSupport), "FREEZE_CLEAR is not supported");

        Assert.Equal(200u, Frozen(h, 1));
        h.Outstation.Database.TryGetCounter(1, out var c1, out _);
        Assert.Equal(0u, c1.Value);
        h.Outstation.Database.TryGetCounter(0, out var c0, out _);
        Assert.Equal(100u, c0.Value);
        Assert.Equal(0u, Frozen(h, 0));
    }

    [Fact]
    public async Task FreezeAtTimeFreezesWhenTheTimeArrives()
    {
        await using var h = await FreezeHarnessAsync();
        var at = DateTimeOffset.UtcNow.AddMilliseconds(300);

        // A trailing header with a non-zero variation and a range qualifier:
        // with the per-fragment rule this was walked as though it carried data.
        var resp = await h.RequestAsync(FuncCode.FreezeAtTime, FreezeAt(at, TimeSpan.Zero), FragmentFactory.ReadRange(20, 1, 0, 0));
        Assert.False(resp.Header.Iin.HasAny(Iin.NoFuncCodeSupport | Iin.ParameterError), $"IIN = {resp.Header.Iin}");

        Assert.Equal(0u, Frozen(h, 0));
        await Harness.WaitForAsync(() => h.Outstation.Stats.ScheduledFreezes == 1, "the scheduled freeze");

        Assert.Equal(100u, Frozen(h, 0));
        Assert.Equal(0u, Frozen(h, 1));
        h.Outstation.Database.TryGetFrozenCounter(0, out var f, out _);
        Assert.True(Math.Abs((f.Time.Time - at).TotalMilliseconds) < 1, $"frozen stamped {f.Time.Time}, want {at}");
    }

    [Fact]
    public async Task FreezeAtTimeRepeatsOnItsInterval()
    {
        await using var h = await FreezeHarnessAsync();
        await h.RequestAsync(
            FuncCode.FreezeAtTime,
            FreezeAt(DateTimeOffset.UtcNow.AddMilliseconds(100), TimeSpan.FromMilliseconds(200)),
            FragmentFactory.ReadAllObjects(20, 0));

        await Harness.WaitForAsync(() => h.Outstation.Stats.ScheduledFreezes >= 1, "the first freeze");
        h.Outstation.Update(d => d.UpdateCounter(0, new Counter(555, Flags.Online, default)));
        await Harness.WaitForAsync(() => Frozen(h, 0) == 555, "a later freeze to take the new value");
    }

    [Fact]
    public async Task FreezeAtTimeInThePastIsRefused()
    {
        await using var h = await FreezeHarnessAsync();
        var resp = await h.RequestAsync(
            FuncCode.FreezeAtTime, FreezeAt(DateTimeOffset.UtcNow.AddMinutes(-1), TimeSpan.Zero), FragmentFactory.ReadAllObjects(20, 0));
        Assert.True(resp.Header.Iin.Has(Iin.ParameterError), $"IIN = {resp.Header.Iin}");
        await Task.Delay(120);
        Assert.Equal(0UL, h.Outstation.Stats.ScheduledFreezes);
    }

    [Fact]
    public async Task FreezeAtTimeWithoutATimeIsRefused()
    {
        await using var h = await FreezeHarnessAsync();
        var resp = await h.RequestAsync(FuncCode.FreezeAtTime, FragmentFactory.ReadAllObjects(20, 0));
        Assert.True(resp.Header.Iin.Has(Iin.ParameterError), $"IIN = {resp.Header.Iin}");
    }

    [Fact]
    public async Task DuplicateFreezeIsAlreadyExecuting()
    {
        await using var h = await FreezeHarnessAsync();
        var at = DateTimeOffset.UtcNow.AddHours(1);
        var first = await h.RequestAsync(FuncCode.FreezeAtTime, FreezeAt(at, TimeSpan.Zero), FragmentFactory.ReadAllObjects(20, 0));
        Assert.False(first.Header.Iin.Has(Iin.AlreadyExecuting));
        var second = await h.RequestAsync(FuncCode.FreezeAtTime, FreezeAt(at, TimeSpan.Zero), FragmentFactory.ReadAllObjects(20, 0));
        Assert.True(second.Header.Iin.Has(Iin.AlreadyExecuting), $"IIN = {second.Header.Iin}");
    }

    [Fact]
    public async Task FrozenAnalogsFreezeAndReadBack()
    {
        var db = Requests.SmallDatabase();
        db.Analog = 3;
        db.FrozenAnalog = 3;
        await using var h = new Harness(new OutstationConfig { Database = db });
        h.Outstation.Update(d => d.UpdateAnalog(1, new Analog(42.5, Flags.Online, default)));
        await Harness.WaitForAsync(() => h.Outstation.Database.TryGetAnalog(1, out var a, out _) && a.Value == 42.5, "analog");

        await h.RequestAsync(FuncCode.ImmedFreeze);
        h.Outstation.Database.TryGetFrozenAnalog(1, out var f, out _);
        Assert.Equal(42.5, f.Value);

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadRange(31, 0, 1, 1));
        Assert.Contains(resp.Objects, o => o.Group == 31);

        // FREEZE_CLEAR zeroes the running analog.
        await h.RequestAsync(FuncCode.FreezeClear, FragmentFactory.ReadRange(30, 0, 1, 1));
        h.Outstation.Database.TryGetAnalog(1, out var run, out _);
        Assert.Equal(0, run.Value);
    }
}

public class CommandEventTests
{
    private static Harness CommandEventHarness(Class cls, RecordingCommandHandler? rec = null)
    {
        var h = new Harness(new OutstationConfig { Database = Requests.SmallDatabase() }, rec ?? new RecordingCommandHandler());
        h.Outstation.Update(db =>
        {
            for (ushort i = 0; i < 2; i++)
            {
                db.Configure(PointType.BinaryOutputStatus, i, new PointConfig { Class = Class.None, CommandEventClass = cls });
                db.Configure(PointType.AnalogOutputStatus, i, new PointConfig { Class = Class.None, CommandEventClass = cls });
            }
        });
        return h;
    }

    private static Dictionary<uint, CommandEvent> EventsIn(Fragment frag, byte group)
    {
        var output = new Dictionary<uint, CommandEvent>();
        foreach (var o in frag.Objects.Where(o => o.Group == group))
        {
            Assert.True(CommandEventCodec.TrySize(o.Group, o.Variation, out var size));
            var prefix = o.Qualifier.IndexPrefix.Octets();
            for (var k = 0; k < o.Range.Count; k++)
            {
                var off = k * (prefix + size);
                var idx = (uint)o.Data.Span[off];
                if (prefix == 2)
                {
                    idx |= (uint)o.Data.Span[off + 1] << 8;
                }

                Assert.True(CommandEventCodec.TryParse(o.Group, o.Variation, o.Data.Span[(off + prefix)..], out var e));
                output[idx] = e;
            }
        }

        return output;
    }

    [Fact]
    public async Task OperatedControlsRaiseCommandEvents()
    {
        await using var h = CommandEventHarness(Class.Class2);
        var status = Requests.CommandStatusOf(await h.RequestAsync(FuncCode.DirectOperate, Requests.CrobHeader(1, ControlCode.LatchOn)));
        Assert.Equal(CommandStatus.Success, status);

        var data = new List<byte> { 0 };
        CommandObjects.AppendAnalogOutputFloat32(data, new AnalogOutputFloat32(12.5f, CommandStatus.Success));
        await h.RequestAsync(FuncCode.DirectOperate, new ObjectHeader
        {
            Group = 41,
            Variation = 3,
            Qualifier = Qualifier.Make(IndexPrefix.Index1, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
            Data = data.ToArray(),
        });

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 3));
        var bin = EventsIn(resp, 13);
        Assert.True(bin.TryGetValue(1, out var b) && b.State && b.Status == CommandStatus.Success, "binary command event");
        var an = EventsIn(resp, 43);
        Assert.True(an.TryGetValue(0, out var a) && a.Analog && a.Value == 12.5 && a.Status == CommandStatus.Success, "analog command event");
        Assert.True(b.Time.IsValid, "the command event carries no time");
    }

    [Fact]
    public async Task SelectAndUnconfiguredPointsRaiseNoCommandEvents()
    {
        await using var h = CommandEventHarness(Class.Class2);
        await h.RequestAsync(FuncCode.Select, Requests.CrobHeader(0, ControlCode.LatchOn));
        Assert.Equal(0, h.Outstation.Events!.Total);

        await using var off = CommandEventHarness(Class.None);
        await off.RequestAsync(FuncCode.DirectOperate, Requests.CrobHeader(0, ControlCode.LatchOn));
        Assert.Equal(0, off.Outstation.Events!.Total);
    }

    [Theory]
    [InlineData((byte)2, FuncCode.DirectOperate)]
    [InlineData((byte)2, FuncCode.Select)]
    [InlineData((byte)3, FuncCode.DirectOperate)]
    [InlineData((byte)3, FuncCode.Select)]
    public async Task PatternControlsAreRefusedWithoutReachingTheHandler(byte variation, FuncCode fc)
    {
        var rec = new RecordingCommandHandler();
        await using var h = new Harness(new OutstationConfig { Database = Requests.SmallDatabase() }, rec);
        var data = new byte[12];
        data[0] = 1;
        data[1] = ControlCode.LatchOn.Value;
        data[2] = 1;
        var resp = await h.RequestAsync(fc, new ObjectHeader
        {
            Group = 12,
            Variation = variation,
            Qualifier = Qualifier.Make(IndexPrefix.Index1, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
            Data = data,
        });
        Assert.True(resp.Header.Iin.HasAny(Iin.ObjectUnknown | Iin.ParameterError), $"IIN = {resp.Header.Iin}");
        Assert.Equal(0, rec.Selects);
        Assert.Equal(0, rec.Operates);
    }
}
