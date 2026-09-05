// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Group 0, where an outstation says what it is.
//
// The variation is the attribute's identity rather than an encoding, which is
// what makes these worth their own procedures: the object header means
// something different here than it does anywhere else in the protocol, and a
// parser that reads it the ordinary way rejects the whole fragment.

using SharpDnp3.App;
using SharpDnp3.Objects;
using SharpDnp3.Outstation;

namespace SharpDnp3.Conformance.Tests;

public class AttributeTests
{
    private static OutstationConfig Config()
    {
        var cfg = new OutstationConfig { Database = Requests.SmallDatabase() };
        cfg.Attributes.Add(DeviceAttribute.String(252, "DSC Systems"));
        cfg.Attributes.Add(DeviceAttribute.String(250, "RTU-9000"));
        cfg.Attributes.Add(DeviceAttribute.String(242, "1.4.2"));
        return cfg;
    }

    /// <summary>
    /// The range names the attribute set, so a read of group 0 is built with a
    /// start-stop range over the set number rather than a point index.
    /// </summary>
    private static ObjectHeader Read(byte set, byte variation) => new()
    {
        Group = 0,
        Variation = variation,
        Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.StartStop8),
        Range = new ObjectRange
        {
            Spec = RangeSpec.StartStop8, Start = set, Stop = set, Count = 1,
        },
    };

    private static List<DeviceAttribute> AttributesOf(Fragment resp)
    {
        var output = new List<DeviceAttribute>();
        foreach (var h in resp.Objects)
        {
            if (h.Group != 0)
            {
                continue;
            }

            var set = (byte)h.Range.Start;
            var off = 0;
            while (off < h.Data.Length)
            {
                output.Add(AttributeObjects.Parse(set, h.Variation, h.Data.Span[off..], out var n));
                off += n;
            }
        }

        return output;
    }

    /// <summary>
    /// Variation 254 asks for everything the device has, which is the one
    /// request a master makes when commissioning an unfamiliar panel.
    /// </summary>
    [Fact]
    public async Task ReadAllReturnsEveryAttribute()
    {
        await using var h = new Harness(Config());

        var resp = await h.RequestAsync(
            FuncCode.Read, Read(AttributeNumbers.StandardSet, AttributeNumbers.All));

        var attrs = AttributesOf(resp);
        Assert.NotEmpty(attrs);

        // Each attribute gets its own object header, because the variation is
        // what names it.
        Assert.Equal(attrs.Count, resp.Objects.Count);

        // Sorted, so two reads of the same device produce the same fragment.
        for (var i = 1; i < attrs.Count; i++)
        {
            Assert.True(attrs[i - 1].Variation < attrs[i].Variation);
        }

        Assert.Contains(attrs, a => a.Variation == 252 && a.ValueText() == "DSC Systems");
        Assert.Contains(attrs, a => a.Variation == 250 && a.ValueText() == "RTU-9000");
    }

    /// <summary>A named attribute comes back on its own.</summary>
    [Fact]
    public async Task ReadOneReturnsJustThatAttribute()
    {
        await using var h = new Harness(Config());

        var resp = await h.RequestAsync(FuncCode.Read, Read(AttributeNumbers.StandardSet, 252));

        var attrs = AttributesOf(resp);
        var a = Assert.Single(attrs);
        Assert.Equal(252, a.Variation);
        Assert.Equal("DSC Systems", a.ValueText());
        Assert.Equal(AttributeType.VisibleString, a.Type);
    }

    /// <summary>
    /// The point counts are derived from the database rather than configured,
    /// so a master reading them gets numbers that match what it is about to
    /// poll.
    /// </summary>
    [Fact]
    public async Task DerivedCountsMatchTheDatabase()
    {
        var db = Requests.SmallDatabase();
        await using var h = new Harness(Config());

        var resp = await h.RequestAsync(
            FuncCode.Read, Read(AttributeNumbers.StandardSet, AttributeNumbers.All));
        var attrs = AttributesOf(resp);

        long ValueOf(byte variation) =>
            attrs.Single(a => a.Variation == variation).Number;

        Assert.Equal(db.Binary, ValueOf(226));
        Assert.Equal(db.Analog, ValueOf(220));
        Assert.Equal(db.Counter, ValueOf(216));
        Assert.Equal(db.BinaryOutputStatus, ValueOf(211));
        Assert.Equal(db.AnalogOutputStatus, ValueOf(208));
    }

    /// <summary>
    /// A point type the device does not have is left unreported rather than
    /// reported as zero: "none" and "I did not say" are different answers.
    /// </summary>
    [Fact]
    public async Task AbsentPointTypesAreNotReported()
    {
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { Binary = 4, DefaultClass = Class.Class1 },
        });

        var resp = await h.RequestAsync(
            FuncCode.Read, Read(AttributeNumbers.StandardSet, AttributeNumbers.All));
        var attrs = AttributesOf(resp);

        Assert.Contains(attrs, a => a.Variation == 226);
        Assert.DoesNotContain(attrs, a => a.Variation == 220);
        Assert.DoesNotContain(attrs, a => a.Variation == 216);
    }

    /// <summary>
    /// A configured attribute replaces the derived one with the same number: a
    /// device that has been told its own point count should report that.
    /// </summary>
    [Fact]
    public async Task ConfiguredAttributesOverrideDerivedOnes()
    {
        var cfg = new OutstationConfig { Database = Requests.SmallDatabase() };
        cfg.Attributes.Add(DeviceAttribute.Uint(226, 999));

        await using var h = new Harness(cfg);

        var resp = await h.RequestAsync(FuncCode.Read, Read(AttributeNumbers.StandardSet, 226));
        var a = Assert.Single(AttributesOf(resp));
        Assert.Equal(999, a.Number);
    }

    /// <summary>An attribute the device does not keep is reported as unknown.</summary>
    [Fact]
    public async Task UnknownVariationIsRefused()
    {
        await using var h = new Harness(Config());

        var resp = await h.RequestAsync(FuncCode.Read, Read(AttributeNumbers.StandardSet, 199));

        Assert.True(resp.Header.Iin.Has(Iin.ObjectUnknown));
        Assert.Empty(AttributesOf(resp));
    }

    /// <summary>And so is a set it does not use.</summary>
    [Fact]
    public async Task UnknownSetIsRefused()
    {
        await using var h = new Harness(Config());

        var resp = await h.RequestAsync(FuncCode.Read, Read(7, AttributeNumbers.All));

        Assert.True(resp.Header.Iin.Has(Iin.ObjectUnknown));
        Assert.Empty(AttributesOf(resp));
    }

    /// <summary>
    /// Reporting which attributes exist is a distinct encoding this
    /// implementation does not have, and answering it with the attributes
    /// themselves would be a different answer to the question asked.
    /// </summary>
    [Fact]
    public async Task ListRequestIsRefused()
    {
        await using var h = new Harness(Config());

        var resp = await h.RequestAsync(
            FuncCode.Read, Read(AttributeNumbers.StandardSet, AttributeNumbers.List));

        Assert.True(resp.Header.Iin.Has(Iin.ObjectUnknown));
        Assert.Empty(AttributesOf(resp));
    }

    /// <summary>
    /// An attribute read must not disturb the rest of the conversation: it
    /// carries no events, selects nothing, and leaves the sequence space alone.
    /// </summary>
    [Fact]
    public async Task AttributeReadLeavesTheSessionAlone()
    {
        await using var h = new Harness(Config());

        await h.RequestAsync(FuncCode.Write, Requests.ClearRestart());

        h.Outstation.Update(db =>
            db.UpdateBinary(0, new Binary(true, Flags.Online, default)));
        await Harness.WaitForAsync(() => h.Outstation.Events?.Total >= 1, "an event to queue");

        var resp = await h.RequestAsync(
            FuncCode.Read, Read(AttributeNumbers.StandardSet, AttributeNumbers.All));

        Assert.False(resp.Header.Control.Con, "an attribute read carries no events to confirm");
        Assert.Equal(h.Seq, resp.Header.Control.Seq);

        // The event is untouched and a class 1 poll still finds it.
        Assert.Equal(1, h.Outstation.Events?.Total);
    }

    /// <summary>
    /// A group 0 response must parse. Before the attribute walk existed the
    /// framing layer asked the size table for a group whose variation is not an
    /// encoding, got nothing, and rejected the entire fragment.
    /// </summary>
    [Fact]
    public async Task AttributeResponsesParse()
    {
        await using var h = new Harness(Config());

        // The harness itself asserts the fragment parses; this states why it
        // matters.
        var resp = await h.RequestAsync(
            FuncCode.Read, Read(AttributeNumbers.StandardSet, AttributeNumbers.All));

        Assert.NotEmpty(resp.Objects);
        foreach (var o in resp.Objects)
        {
            Assert.Equal(0, o.Group);
            Assert.False(o.Data.IsEmpty, "every attribute header introduces a value");
        }
    }
}
