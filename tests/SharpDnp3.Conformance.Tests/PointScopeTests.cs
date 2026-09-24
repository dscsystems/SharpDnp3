// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Which points a request actually names.
//
// A header carries a group and a range, and it is easy to act on the group and
// ignore the range: freeze every counter, reclassify every analog, report every
// point in the first one's variation. Each of those does something the master
// did not ask for and has no way to see — the response looks the same either
// way.

using SharpDnp3.App;
using SharpDnp3.Objects;
using SharpDnp3.Outstation;

namespace SharpDnp3.Conformance.Tests;

public class PointScopeTests
{
    /// <summary>Reads a range of one group and variation.</summary>
    private static ObjectHeader ReadRange(byte group, byte variation, ushort start, ushort stop) =>
        FragmentFactory.ReadRange(group, variation, start, stop);

    /// <summary>
    /// Variation zero means "each point's own default". Points configured for
    /// different static variations cannot share an object header, so the range
    /// is reported as runs that agree — reporting them all in the first point's
    /// variation would put a float analog through the integer codec and hand
    /// the master a truncated value it cannot know is wrong.
    /// </summary>
    [Fact]
    public async Task MixedStaticVariationsAreReportedInRuns()
    {
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { Analog = 4, DefaultClass = Class.Class1 },
        });

        // Points 0 and 1 as 16-bit integers, 2 and 3 as single-precision float.
        h.Outstation.Database.Configure(
            PointType.Analog, 0, new PointConfig { StaticVariation = 2 });
        h.Outstation.Database.Configure(
            PointType.Analog, 1, new PointConfig { StaticVariation = 2 });
        h.Outstation.Database.Configure(
            PointType.Analog, 2, new PointConfig { StaticVariation = 5 });
        h.Outstation.Database.Configure(
            PointType.Analog, 3, new PointConfig { StaticVariation = 5 });

        h.Outstation.Update(db =>
        {
            for (ushort i = 0; i < 4; i++)
            {
                db.UpdateAnalog(i, new Analog(1234.5, Flags.Online, default));
            }
        });

        var resp = await h.RequestAsync(FuncCode.Read, ReadRange(30, 0, 0, 3));

        Assert.Equal(2, resp.Objects.Count);

        Assert.Equal(2, resp.Objects[0].Variation);
        Assert.Equal(0u, resp.Objects[0].Range.Start);
        Assert.Equal(1u, resp.Objects[0].Range.Stop);

        Assert.Equal(5, resp.Objects[1].Variation);
        Assert.Equal(2u, resp.Objects[1].Range.Start);
        Assert.Equal(3u, resp.Objects[1].Range.Stop);
    }

    /// <summary>
    /// An explicit variation is the master's choice and applies to the whole
    /// range, whatever each point is configured for.
    /// </summary>
    [Fact]
    public async Task AnExplicitVariationAppliesToTheWholeRange()
    {
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { Analog = 4, DefaultClass = Class.Class1 },
        });

        h.Outstation.Database.Configure(
            PointType.Analog, 0, new PointConfig { StaticVariation = 2 });
        h.Outstation.Database.Configure(
            PointType.Analog, 2, new PointConfig { StaticVariation = 5 });

        var resp = await h.RequestAsync(FuncCode.Read, ReadRange(30, 1, 0, 3));

        var o = Assert.Single(resp.Objects);
        Assert.Equal(1, o.Variation);
        Assert.Equal(0u, o.Range.Start);
        Assert.Equal(3u, o.Range.Stop);
    }

    /// <summary>
    /// Only the points an ASSIGN_CLASS header names change class. Assigning the
    /// whole type regardless would move every other point too.
    /// </summary>
    [Fact]
    public async Task AssignClassTouchesOnlyThePointsItNames()
    {
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { Analog = 6, DefaultClass = Class.Class1 },
        });

        // Class 2 for analogs 1 and 2 only.
        await h.RequestAsync(
            FuncCode.AssignClass,
            FragmentFactory.ReadAllObjects(60, 3),
            ReadRange(30, 0, 1, 2));

        for (ushort i = 0; i < 6; i++)
        {
            Assert.True(h.Outstation.Database.TryGetAnalog(i, out _, out var cfg));

            var expected = i is 1 or 2 ? Class.Class2 : Class.Class1;
            Assert.True(
                cfg.Class == expected,
                $"analog {i} should be {expected} but is {cfg.Class}");
        }
    }

    /// <summary>
    /// A freeze naming counters freezes only those. Freezing the lot regardless
    /// overwrites frozen values the master never asked to change.
    /// </summary>
    [Fact]
    public async Task FreezeTouchesOnlyTheCountersItNames()
    {
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { Counter = 4, FrozenCounter = 4, DefaultClass = Class.Class1 },
        });

        h.Outstation.Update(db =>
        {
            for (ushort i = 0; i < 4; i++)
            {
                db.UpdateCounter(i, new Counter(100u + i, Flags.Online, default));
            }
        });

        // Freeze everything first, so every frozen counter holds a known value.
        await h.RequestAsync(FuncCode.ImmedFreeze);

        // Now move the running counters and freeze only counter 1.
        h.Outstation.Update(db =>
        {
            for (ushort i = 0; i < 4; i++)
            {
                db.UpdateCounter(i, new Counter(900u + i, Flags.Online, default));
            }
        });

        await h.RequestAsync(FuncCode.ImmedFreeze, ReadRange(20, 0, 1, 1));

        for (ushort i = 0; i < 4; i++)
        {
            Assert.True(h.Outstation.Database.TryGetFrozenCounter(i, out var frozen, out _));

            var expected = i == 1 ? 901u : 100u + i;
            Assert.True(
                frozen.Value == expected,
                $"frozen counter {i} should be {expected} but is {frozen.Value}");
        }
    }

    /// <summary>
    /// A freeze stamps its snapshot with the moment of the freeze, and raises
    /// an event so a master polling events learns it happened.
    /// </summary>
    [Fact]
    public async Task FreezeStampsAndReportsTheSnapshot()
    {
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig
            {
                Counter = 2, FrozenCounter = 2, DefaultClass = Class.Class1,
            },
        });

        await h.RequestAsync(FuncCode.Write, Requests.ClearRestart());

        h.Outstation.Update(db =>
            db.UpdateCounter(0, new Counter(42, Flags.Online, default)));

        await h.RequestAsync(FuncCode.ImmedFreeze);

        Assert.True(h.Outstation.Database.TryGetFrozenCounter(0, out var frozen, out _));
        Assert.Equal(42u, frozen.Value);
        Assert.NotEqual(default, frozen.Time.Time);

        // The freeze produced something new, and an event says so.
        await Harness.WaitForAsync(
            () => h.Outstation.Events?.Total >= 1,
            "the freeze to raise a frozen counter event");
    }

    /// <summary>
    /// An analog that saturates the variation it is reported in carries
    /// OVER_RANGE. Without it a pegged 32767 is indistinguishable from a real
    /// one.
    /// </summary>
    [Fact]
    public async Task ASaturatedAnalogIsFlaggedOverRange()
    {
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { Analog = 2, DefaultClass = Class.Class1 },
        });

        h.Outstation.Update(db =>
        {
            // Past what a 16-bit point can hold, and inside it.
            db.UpdateAnalog(0, new Analog(50000, Flags.Online, default));
            db.UpdateAnalog(1, new Analog(1000, Flags.Online, default));
        });

        var resp = await h.RequestAsync(FuncCode.Read, ReadRange(30, 2, 0, 1));

        var o = Assert.Single(resp.Objects);
        var data = o.Data.Span;

        // g30v2 is a flags octet then a 16-bit value, three octets per point.
        var saturated = new Flags(data[0]);
        var ordinary = new Flags(data[3]);

        Assert.True(saturated.Has(Flags.OverRange), "the saturated point should be over range");
        Assert.False(ordinary.Has(Flags.OverRange), "the in-range point should not be");
    }

    /// <summary>
    /// A reading that becomes NaN reports, and so does one that recovers from
    /// it. Every comparison against NaN is false, so a deadband check alone
    /// leaves a point stuck at NaN silent for ever.
    /// </summary>
    [Fact]
    public async Task APointMovingIntoAndOutOfNaNStillReports()
    {
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { Analog = 1, DefaultClass = Class.Class1 },
        });

        await h.RequestAsync(FuncCode.Write, Requests.ClearRestart());

        h.Outstation.Update(db => db.UpdateAnalog(0, new Analog(10, Flags.Online, default)));
        await Harness.WaitForAsync(() => h.Outstation.Events?.Total >= 1, "the first reading");
        var afterFirst = h.Outstation.Events!.Total;

        h.Outstation.Update(db =>
            db.UpdateAnalog(0, new Analog(double.NaN, Flags.Online, default)));
        await Harness.WaitForAsync(
            () => h.Outstation.Events?.Total > afterFirst, "the move into NaN to report");
        var afterNaN = h.Outstation.Events!.Total;

        h.Outstation.Update(db => db.UpdateAnalog(0, new Analog(11, Flags.Online, default)));
        await Harness.WaitForAsync(
            () => h.Outstation.Events?.Total > afterNaN, "the recovery from NaN to report");
    }
}
