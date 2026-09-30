// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Request validation, response sequencing, device-controlled indications and
// malformed-request handling. Each mirrors a procedure in go-dnp3's
// conformance suite.

using SharpDnp3.App;
using SharpDnp3.Outstation;

namespace SharpDnp3.Conformance.Tests;

public class ValidationTests
{
    private static OutstationConfig Config() => new() { Database = Requests.SmallDatabase() };

    /// <summary>CON and UNS belong to responses: a request setting either is discarded.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RequestWithConOrUnsIsDiscarded(bool con, bool uns)
    {
        await using var h = new Harness(Config());
        var before = h.Count;
        await h.SendRawAsync(FragmentFactory.BuildRequest(
            new AppControl(true, true, con, uns, 3), FuncCode.Read, FragmentFactory.ReadAllObjects(60, 1)));
        await h.ExpectSilenceAsync(before);
        Assert.Equal(1UL, h.Outstation.Stats.MalformedRequests);
    }

    /// <summary>A CONFIRM is a bare header; objects after it acknowledge nothing.</summary>
    [Fact]
    public async Task ConfirmCarryingObjectsIsDiscarded()
    {
        await using var h = new Harness(Config());
        var before = h.Count;
        await h.SendRawAsync(FragmentFactory.BuildRequest(
            new AppControl(true, true, false, false, 1), FuncCode.Confirm, FragmentFactory.ReadAllObjects(60, 1)));
        await h.ExpectSilenceAsync(before);
        Assert.Equal(1UL, h.Outstation.Stats.MalformedRequests);
    }

    /// <summary>A prefix and range that do not compose name no points: refused.</summary>
    [Fact]
    public async Task InconsistentQualifierIsRefused()
    {
        await using var h = new Harness(Config());
        var resp = await h.RequestAsync(FuncCode.Read, new ObjectHeader
        {
            Group = 1,
            Variation = 2,
            Qualifier = Qualifier.Make(IndexPrefix.Index1, RangeSpec.AllObjects),
            Range = new ObjectRange { Spec = RangeSpec.AllObjects },
        });
        Assert.True(resp.Header.Iin.Has(Iin.ParameterError), $"IIN = {resp.Header.Iin}");
        Assert.Empty(resp.Objects);
    }

    /// <summary>Each later fragment of a response increments the request's sequence number.</summary>
    [Fact]
    public async Task MultiFragmentResponseSequenceNumbersIncrement()
    {
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { Binary = 60, DefaultClass = Class.Class1 },
            MaxTxFragment = 30,
            ConfirmTimeout = TimeSpan.FromSeconds(2),
        });
        var before = h.Count;
        await h.SendAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 1));
        var reqSeq = h.Seq;
        var frag = await h.AwaitAsync(before);
        for (var i = 0; ; i++)
        {
            var want = (byte)((reqSeq + i) % AppConstants.SeqModulus);
            Assert.True(frag.Header.Control.Seq == want, $"fragment {i} has sequence {frag.Header.Control.Seq}, want {want}");
            Assert.Equal(i == 0, frag.Header.Control.Fir);
            if (frag.Header.Control.Fin)
            {
                Assert.True(i > 0, "the response fit in one fragment; the test needs several");
                return;
            }

            Assert.True(frag.Header.Control.Con, $"fragment {i} of a series does not ask for a confirm");
            before = h.Count;
            await h.SendConfirmAsync(frag.Header.Control.Seq);
            frag = await h.AwaitAsync(before);
        }
    }

    private static async Task<Iin> IinOfReadAsync(Harness h) =>
        (await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 1))).Header.Iin;

    /// <summary>The three device-controlled indications are asserted while set.</summary>
    [Fact]
    public async Task DeviceControlledIndications()
    {
        var cfg = Config();
        cfg.Indications = Indication.ConfigCorrupt;
        await using var h = new Harness(cfg);

        Assert.True((await IinOfReadAsync(h)).Has(Iin.ConfigCorrupt));
        h.Outstation.SetIndication(Indication.LocalControl | Indication.DeviceTrouble, true);
        var iin = await IinOfReadAsync(h);
        Assert.True(iin.Has(Iin.LocalControl) && iin.Has(Iin.DeviceTrouble) && iin.Has(Iin.ConfigCorrupt), $"IIN = {iin}");
        Assert.True((await IinOfReadAsync(h)).Has(Iin.LocalControl), "LOCAL_CONTROL did not persist");
        h.Outstation.SetIndication(Indication.DeviceTrouble | Indication.ConfigCorrupt, false);
        iin = await IinOfReadAsync(h);
        Assert.True(!iin.Has(Iin.DeviceTrouble) && !iin.Has(Iin.ConfigCorrupt) && iin.Has(Iin.LocalControl), $"IIN = {iin}");

        // NEED_TIME belongs to the library.
        h.Outstation.SetIndication((Indication)(Iin.NeedTime.Value | Iin.DeviceRestart.Value), false);
        Assert.True((await IinOfReadAsync(h)).Has(Iin.NeedTime));
    }

    private sealed class RestartApp : NopApplication
    {
        public int Colds;

        public override TimeSpan ColdRestart()
        {
            Interlocked.Increment(ref Colds);
            return TimeSpan.FromSeconds(2);
        }
    }

    /// <summary>A second restart request while the first is under way is not carried out again.</summary>
    [Fact]
    public async Task RepeatedRestartIsAlreadyExecuting()
    {
        var app = new RestartApp();
        await using var h = new Harness(Config(), application: app);
        var first = await h.RequestAsync(FuncCode.ColdRestart);
        Assert.False(first.Header.Iin.Has(Iin.AlreadyExecuting));
        var second = await h.RequestAsync(FuncCode.ColdRestart);
        Assert.True(second.Header.Iin.Has(Iin.AlreadyExecuting), $"IIN = {second.Header.Iin}");
        Assert.Equal(1, app.Colds);
    }

    private sealed class ClockApp : NopApplication
    {
        public readonly List<DateTimeOffset> Set = [];

        public override bool WriteAbsoluteTime(DateTimeOffset t)
        {
            lock (Set)
            {
                Set.Add(t);
            }

            return true;
        }
    }

    /// <summary>Both 0x17 and 0x28 are legal for a write; the prefix is not part of the time.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WriteTimeWithIndexPrefixedQualifierSetsTheRightClock(int prefixOctets)
    {
        var want = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var ms = Dnp3Time.ToDnp3(want);
        byte[] stamp = [(byte)ms, (byte)(ms >> 8), (byte)(ms >> 16), (byte)(ms >> 24), (byte)(ms >> 32), (byte)(ms >> 40)];
        var (prefix, range) = prefixOctets switch
        {
            0 => (IndexPrefix.None, RangeSpec.Count8),
            1 => (IndexPrefix.Index1, RangeSpec.Count8),
            _ => (IndexPrefix.Index2, RangeSpec.Count16),
        };
        var app = new ClockApp();
        await using var h = new Harness(Config(), application: app);
        await h.RequestAsync(FuncCode.Write, new ObjectHeader
        {
            Group = 50,
            Variation = 1,
            Qualifier = Qualifier.Make(prefix, range),
            Range = new ObjectRange { Spec = range, Count = 1 },
            Data = new byte[prefixOctets].Concat(stamp).ToArray(),
        });
        lock (app.Set)
        {
            Assert.Single(app.Set);
            Assert.Equal(want, app.Set[0]);
        }
    }

    /// <summary>A request whose object section cannot be parsed still gets an answer.</summary>
    [Fact]
    public async Task UnparseableObjectSectionGetsAResponse()
    {
        ObjectHeader Unknown(byte[]? data) => new()
        {
            Group = 99,
            Variation = 7,
            Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
            Data = data ?? [],
        };

        await using (var h = new Harness(Config()))
        {
            var resp = await h.RequestAsync(FuncCode.Write, Unknown([1, 2]));
            Assert.True(resp.Header.Iin.Has(Iin.ObjectUnknown), $"IIN = {resp.Header.Iin}");
            Assert.Empty(resp.Objects);
        }

        await using (var h = new Harness(Config()))
        {
            var resp = await h.RequestAsync(FuncCode.InitializeData, Unknown(null));
            Assert.True(resp.Header.Iin.Has(Iin.NoFuncCodeSupport), $"IIN = {resp.Header.Iin}");
            Assert.Empty(resp.Objects);
        }
    }

    /// <summary>A READ of a variation the group does not have is not an empty database.</summary>
    [Fact]
    public async Task ReadOfUnsupportedVariationSetsObjectUnknown()
    {
        await using var h = new Harness(Config());
        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadRange(1, 99, 0, 1));
        Assert.True(resp.Header.Iin.Has(Iin.ObjectUnknown), $"IIN = {resp.Header.Iin}");
    }
}
