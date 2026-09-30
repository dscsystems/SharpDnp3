// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Event reads by group and variation, common times of occurrence, command
// events, freezes, and unsolicited fragment limits. Each mirrors a procedure
// in go-dnp3's conformance suite.

using SharpDnp3.App;
using SharpDnp3.Objects;
using SharpDnp3.Outstation;

namespace SharpDnp3.Conformance.Tests;

public class EventReadTests
{
    /// <summary>Binary points report into class 2 and analog points into class 1.</summary>
    private static async Task<Harness> EventHarnessAsync(int maxFragment)
    {
        var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { Binary = 40, Analog = 10, DefaultClass = Class.Class1 },
            MaxTxFragment = maxFragment,
            ConfirmTimeout = TimeSpan.FromSeconds(2),
        });
        h.Outstation.Update(db =>
        {
            for (ushort i = 0; i < 40; i++)
            {
                db.Configure(PointType.Binary, i, new PointConfig { Class = Class.Class2 });
            }
        });
        await Task.CompletedTask;
        return h;
    }

    /// <summary>Sends a read and gathers every fragment of its response, confirming each.</summary>
    private static async Task<List<Fragment>> CollectAsync(Harness h, params ObjectHeader[] objs)
    {
        var before = h.Count;
        await h.SendAsync(FuncCode.Read, objs);
        var output = new List<Fragment>();
        while (true)
        {
            var frag = await h.AwaitAsync(before + output.Count);
            output.Add(frag);
            if (frag.Header.Control.Con)
            {
                await h.SendConfirmAsync(frag.Header.Control.Seq);
            }

            if (frag.Header.Control.Fin)
            {
                return output;
            }
        }
    }

    private static List<ObjectHeader> Groups(List<Fragment> frags, byte group) =>
        [.. frags.SelectMany(f => f.Objects).Where(o => o.Group == group)];

    private static int Count(IEnumerable<ObjectHeader> hs) => hs.Sum(h => (int)h.Range.Count);

    private static ObjectHeader CountHeader(byte group, byte variation, byte n) => new()
    {
        Group = group,
        Variation = variation,
        Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.Count8),
        Range = new ObjectRange { Spec = RangeSpec.Count8, Count = n },
    };

    private static Binary On(DateTimeOffset at) => new(true, Flags.Online, Timestamp.Now(at));

    /// <summary>A read of an event group returns events in the variation named, not static data.</summary>
    [Fact]
    public async Task ReadOfAnEventGroupReturnsEventsInTheRequestedVariation()
    {
        await using var h = await EventHarnessAsync(0);
        var now = DateTimeOffset.UtcNow;
        h.Outstation.Update(db =>
        {
            db.UpdateBinary(3, On(now));
            db.UpdateAnalog(1, new Analog(5, Flags.Online, Timestamp.Now(now)));
        });
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total == 2, "two events");

        var frags = await CollectAsync(h, FragmentFactory.ReadAllObjects(2, 1));
        Assert.Empty(Groups(frags, 1));
        var ev = Groups(frags, 2);
        Assert.True(ev.Count == 1 && ev[0].Variation == 1 && ev[0].Range.Count == 1, "want one g2v1 carrying one event");
        Assert.Empty(Groups(frags, 32));
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total == 1, "the analog event to stay queued");
    }

    /// <summary>A read by type ignores class, and an unknown variation says so.</summary>
    [Fact]
    public async Task EventReadIgnoresClassAndRejectsUnknownVariations()
    {
        await using var h = await EventHarnessAsync(0);
        h.Outstation.Update(db => db.UpdateBinary(0, On(DateTimeOffset.UtcNow)));
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total == 1, "an event");

        var frags = await CollectAsync(h, FragmentFactory.ReadAllObjects(2, 2));
        var ev = Groups(frags, 2);
        Assert.True(ev.Count == 1 && ev[0].Variation == 2, "want one g2v2");

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(2, 9));
        Assert.True(resp.Header.Iin.Has(Iin.ObjectUnknown), $"IIN = {resp.Header.Iin}");

        // A start-stop range means nothing for events.
        resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadRange(2, 1, 0, 3));
        Assert.True(resp.Header.Iin.Has(Iin.ParameterError), $"IIN = {resp.Header.Iin}");
    }

    /// <summary>A count limits how many events come back; the rest stay queued.</summary>
    [Fact]
    public async Task EventReadsHonourTheirCount()
    {
        await using var h = await EventHarnessAsync(0);
        h.Outstation.Update(db =>
        {
            for (ushort i = 0; i < 10; i++)
            {
                db.UpdateBinary(i, On(DateTimeOffset.UtcNow));
            }
        });
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total == 10, "ten events");

        var frags = await CollectAsync(h, CountHeader(2, 2, 4));
        Assert.Equal(4, Count(Groups(frags, 2)));
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total == 6, "four of ten to be confirmed");

        frags = await CollectAsync(h, CountHeader(60, 3, 2));
        Assert.Equal(2, Count(Groups(frags, 2)));
    }

    /// <summary>Every fragment of a relative-time response carries its own base.</summary>
    [Fact]
    public async Task RelativeTimeEventsCarryACtoInEveryFragment()
    {
        await using var h = await EventHarnessAsync(60);
        var baseTime = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var want = new Dictionary<uint, DateTimeOffset>();
        h.Outstation.Update(db =>
        {
            for (ushort i = 0; i < 12; i++)
            {
                var at = baseTime.AddMilliseconds(i * 1500);
                want[i] = at;
                db.UpdateBinary(i, On(at));
            }
        });
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total == 12, "twelve events");

        var frags = await CollectAsync(h, FragmentFactory.ReadAllObjects(2, 3));
        Assert.True(frags.Count >= 2, $"{frags.Count} fragment(s); the test needs several");
        var got = new Dictionary<uint, DateTimeOffset>();
        ObjectRegistry.TryBinaryCodec(GroupVar.GV(2, 3), out var codec);
        for (var n = 0; n < frags.Count; n++)
        {
            var ctx = new Context();
            foreach (var o in frags[n].Objects)
            {
                if (o.Group == 51)
                {
                    ctx = ctx.WithCto(CommandObjects.ParseTime48(o.Data.Span).Time);
                }
                else if (o.Group == 2)
                {
                    Assert.True(ctx.HasCto, $"fragment {n}: g2v3 with no CTO before it in the same fragment");
                    var prefix = o.Qualifier.IndexPrefix.Octets();
                    var step = prefix + 3;
                    for (var k = 0; k < o.Range.Count; k++)
                    {
                        var idx = (uint)o.Data.Span[k * step];
                        got[idx] = codec.Parse(o.Data.Span[(k * step + prefix)..], ctx).Time.Time;
                    }
                }
            }
        }

        Assert.Equal(12, got.Count);
        foreach (var (i, w) in want)
        {
            Assert.True(Math.Abs((got[i] - w).TotalMilliseconds) <= 1, $"event {i} resolves to {got[i]}, want {w}");
        }
    }

    /// <summary>An offset is sixteen bits of milliseconds, so later events start a new base.</summary>
    [Fact]
    public async Task RelativeTimeEventsRenewTheCtoPastSixteenBits()
    {
        await using var h = await EventHarnessAsync(0);
        var baseTime = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        h.Outstation.Update(db =>
        {
            ushort i = 0;
            foreach (var offset in new[] { 0, 30, 70, 71 })
            {
                db.UpdateBinary(i++, On(baseTime.AddSeconds(offset)));
            }
        });
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total == 4, "four events");

        var frags = await CollectAsync(h, FragmentFactory.ReadAllObjects(2, 3));
        var ctos = Groups(frags, 51);
        Assert.Equal(2, ctos.Count);
        var second = CommandObjects.ParseTime48(ctos[1].Data.Span).Time;
        Assert.True(Math.Abs((second - baseTime.AddSeconds(70)).TotalMilliseconds) <= 1, $"second CTO = {second}");
    }

    /// <summary>The CTO variation says whether the clock behind it was synchronised.</summary>
    [Fact]
    public async Task CtoVariationReflectsClockSynchronisation()
    {
        await using var h = await EventHarnessAsync(0);
        h.Outstation.Update(db => db.UpdateBinary(0, On(DateTimeOffset.UtcNow)));
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total == 1, "an event");

        var frags = await CollectAsync(h, FragmentFactory.ReadAllObjects(2, 3));
        var c = Groups(frags, 51);
        Assert.True(c.Count == 1 && c[0].Variation == 2, "want one g51v2 while the clock is unset");

        await h.RequestAsync(FuncCode.Write, Requests.TimeWrite(1, DateTimeOffset.UtcNow));
        h.Outstation.Update(db => db.UpdateBinary(1, On(DateTimeOffset.UtcNow)));
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total == 1, "a new event");

        frags = await CollectAsync(h, FragmentFactory.ReadAllObjects(2, 3));
        c = Groups(frags, 51);
        Assert.True(c.Count == 1 && c[0].Variation == 1, "want one g51v1 once the clock is set");
    }
}

public class UnsolicitedOversizeTests
{
    /// <summary>
    /// An unsolicited response is one fragment; the surplus events must stay
    /// queued rather than be confirmed away.
    /// </summary>
    [Fact]
    public async Task UnsolicitedSurplusEventsAreNotDiscarded()
    {
        const int Points = 60;
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { Binary = Points, DefaultClass = Class.Class1 },
            MaxTxFragment = 40,
            Unsolicited = new UnsolicitedConfig
            {
                Enabled = true,
                ConfirmTimeout = TimeSpan.FromSeconds(1),
                MaxRetries = 3,
            },
        });

        var announcement = await h.AwaitAsync(0);
        await h.SendConfirmAsync(announcement.Header.Control.Seq, unsolicited: true);
        await h.RequestAsync(FuncCode.Write, Requests.ClearRestart());
        await h.RequestAsync(FuncCode.EnableUnsolicited, FragmentFactory.ReadAllObjects(60, 2));

        var seen = new HashSet<uint>();
        h.Outstation.Update(db =>
        {
            for (ushort i = 0; i < Points; i++)
            {
                db.UpdateBinary(i, new Binary(true, Flags.Online, default));
            }
        });

        var next = h.Count;
        while (seen.Count < Points)
        {
            var frag = await h.AwaitAsync(next);
            next++;
            if (frag.Header.Func != FuncCode.UnsolicitedResponse)
            {
                continue;
            }

            foreach (var o in frag.Objects.Where(o => o.Group == 2))
            {
                var step = o.Data.Length / (int)o.Range.Count;
                for (var k = 0; k < o.Range.Count; k++)
                {
                    seen.Add(o.Data.Span[k * step]);
                }
            }

            await h.SendConfirmAsync(frag.Header.Control.Seq, unsolicited: true);
        }

        Assert.Equal(Points, seen.Count);
    }
}
