// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Indexes that do not fit where they are being put.
//
// The protocol addresses points with one, two or four octets, and every place
// those widths meet is somewhere a value can be silently attributed to the wrong
// point: an event index written into a one-octet prefix, a four-octet command
// index narrowed onto a point that exists, a reported index narrowed to fit a
// master's own type. None of these fail loudly — they report a real measurement,
// or operate a real point, under a number nobody asked about.

using SharpDnp3.App;
using SharpDnp3.Objects;
using SharpDnp3.Outstation;

namespace SharpDnp3.Conformance.Tests;

public class WideIndexTests
{
    /// <summary>
    /// An event above index 255 cannot travel in a one-octet prefix. The run
    /// has to move to two-octet prefixes — and so to a two-octet count — or the
    /// event is reported against a point 256 lower.
    /// </summary>
    [Fact]
    public async Task EventsAboveIndex255UseAWiderPrefix()
    {
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { Binary = 400, DefaultClass = Class.Class1 },
        });

        await h.RequestAsync(FuncCode.Write, Requests.ClearRestart());

        h.Outstation.Update(db =>
            db.UpdateBinary(300, new Binary(true, Flags.Online, default)));

        await Harness.WaitForAsync(() => h.Outstation.Events?.Total >= 1, "the event to queue");

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 2));

        var o = Assert.Single(resp.Objects);
        Assert.Equal(IndexPrefix.Index2, o.Qualifier.IndexPrefix);
        Assert.Equal(RangeSpec.Count16, o.Qualifier.RangeSpec);

        // The index as it went on the wire, little-endian across two octets.
        var data = o.Data.Span;
        Assert.Equal(300, data[0] | (data[1] << 8));
    }

    /// <summary>
    /// A run that stays inside 255 keeps the one-octet prefix, which is the
    /// common case and the smaller encoding.
    /// </summary>
    [Fact]
    public async Task EventsBelowIndex256KeepTheNarrowPrefix()
    {
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { Binary = 400, DefaultClass = Class.Class1 },
        });

        await h.RequestAsync(FuncCode.Write, Requests.ClearRestart());

        h.Outstation.Update(db =>
            db.UpdateBinary(7, new Binary(true, Flags.Online, default)));

        await Harness.WaitForAsync(() => h.Outstation.Events?.Total >= 1, "the event to queue");

        var resp = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 2));

        var o = Assert.Single(resp.Objects);
        Assert.Equal(IndexPrefix.Index1, o.Qualifier.IndexPrefix);
        Assert.Equal(RangeSpec.Count8, o.Qualifier.RangeSpec);
        Assert.Equal(7, o.Data.Span[0]);
    }

    /// <summary>
    /// A four-octet prefix can name an index no point has. Narrowing it would
    /// wrap it onto one that does — a command for point 65541 operating point
    /// 5 — so it is refused before any handler sees it.
    /// </summary>
    [Fact]
    public async Task ACommandIndexAboveThePointSpaceIsRefused()
    {
        var commands = new RecordingCommandHandler();
        await using var h = new Harness(
            new OutstationConfig { Database = Requests.SmallDatabase() }, commands);

        // Index 65541 with a four-octet prefix: the low 16 bits are 5.
        var wide = 65541u;
        var data = new List<byte>
        {
            (byte)wide, (byte)(wide >> 8), (byte)(wide >> 16), (byte)(wide >> 24),
        };
        CommandObjects.AppendCrob(
            data, new ControlRelayOutputBlock { Code = ControlCode.LatchOn, Count = 1 });

        var resp = await h.RequestAsync(FuncCode.DirectOperate, new ObjectHeader
        {
            Group = 12,
            Variation = 1,
            Qualifier = Qualifier.Make(IndexPrefix.Index4, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
            Data = data.ToArray(),
        });

        Assert.Equal(0, commands.Operates);
        Assert.Equal(CommandStatus.NotSupported, Requests.CommandStatusOf(resp));
    }

    /// <summary>
    /// A request naming individual points carries their indexes as prefixes
    /// even though no object data follows. A parser that skipped them would
    /// read the first index as the next object header and reject the request.
    /// </summary>
    [Fact]
    public void AReadNamingPointsByIndexParses()
    {
        // Read g1v2 at points 3 and 7, count qualifier with one-octet prefixes.
        var request = FragmentFactory.BuildRequest(
            new AppControl(Fir: true, Fin: true, Con: false, Uns: false, Seq: 1),
            FuncCode.Read,
            new ObjectHeader
            {
                Group = 1,
                Variation = 2,
                Qualifier = Qualifier.Make(IndexPrefix.Index1, RangeSpec.Count8),
                Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 2 },
                Data = new byte[] { 3, 7 },
            });

        var status = FragmentParser.ParseFragment(null, request, out var frag, out var error);

        Assert.True(status == AppParseStatus.Ok, $"the request should parse: {error}");
        var o = Assert.Single(frag.Objects);
        Assert.Equal(2u, o.Count);
        Assert.Equal(new byte[] { 3, 7 }, o.Data.ToArray());
    }

    /// <summary>
    /// And the outstation answers exactly the points it named, rather than
    /// every point of the type.
    /// </summary>
    [Fact]
    public async Task AReadNamingPointsReturnsOnlyThose()
    {
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { Binary = 16, DefaultClass = Class.Class1 },
        });

        var resp = await h.RequestAsync(FuncCode.Read, new ObjectHeader
        {
            Group = 1,
            Variation = 2,
            Qualifier = Qualifier.Make(IndexPrefix.Index1, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 2 },
            Data = new byte[] { 3, 7 },
        });

        // Two runs, one per named point, because 3 and 7 are not consecutive.
        Assert.Equal(2, resp.Objects.Count);
        Assert.Equal(3u, resp.Objects[0].Range.Start);
        Assert.Equal(3u, resp.Objects[0].Range.Stop);
        Assert.Equal(7u, resp.Objects[1].Range.Start);
        Assert.Equal(7u, resp.Objects[1].Range.Stop);
    }
}
