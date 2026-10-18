// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The dnp3c device under test's checks (conformance/dnp3c/dut) are what the
// conformance run's verdicts rest on. One that passes what it should not turns
// a failing outstation into a passing report, so each is tested here against
// the reports it must reject.

using SharpDnp3.Conformance.Dut;

namespace SharpDnp3.Conformance.Tests;

public class Dnp3cDutTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static DutEvent Ev(Kind k, int i, double v, int ms) =>
        new(k, i, v, Flags.Online.Value, T0.AddMilliseconds(ms));

    private static Point Rep(Kind k, int i, double v, int ms) => new()
    {
        KindName = k.Name(),
        Index = i,
        Value = v,
        Flags = Flags.Online.Value,
        Time = T0.AddMilliseconds(ms).ToUnixTimeMilliseconds(),
        Event = true,
    };

    private static Point Static(Kind k, int i, double v, int? flags = null) =>
        new() { KindName = k.Name(), Index = i, Value = v, Flags = flags };

    [Fact]
    public void VerifyEvents()
    {
        DutEvent[] generated = [Ev(Kind.Binary, 0, 1, 0), Ev(Kind.Binary, 1, 1, 1), Ev(Kind.Binary, 0, 0, 2)];
        Point[] input = [Rep(Kind.Binary, 0, 1, 0), Rep(Kind.Binary, 1, 1, 1), Rep(Kind.Binary, 0, 0, 2)];

        Verify.Events(input, generated);

        var cases = new Dictionary<string, Point[]>
        {
            ["one missing"] = input[..2],
            ["one extra"] = [.. input, Rep(Kind.Binary, 2, 1, 3)],
            ["wrong value"] = [input[0], input[1], Rep(Kind.Binary, 0, 1, 2)],
            ["out of order"] = [input[1], input[0], input[2]],
            ["same point"] = [input[2], input[0], input[1]],
            ["gap stretched"] = [input[0], input[1], Rep(Kind.Binary, 0, 0, 50)],
            ["static value"] = [input[0], input[1], Static(Kind.Binary, 0, 0)],
        };
        foreach (var (name, points) in cases)
        {
            Assert.True(Throws(() => Verify.Events(points, generated)), $"{name}: passed");
        }
    }

    /// <summary>
    /// An analog's events need only be in order point by point: an outstation
    /// may report point 1's before point 0's.
    /// </summary>
    [Fact]
    public void VerifyEventsOrderAcrossAnalogPoints()
    {
        DutEvent[] generated = [Ev(Kind.Analog, 0, 7, 0), Ev(Kind.Analog, 1, 7, 1), Ev(Kind.Analog, 0, 14, 2)];
        Point[] input = [Rep(Kind.Analog, 1, 7, 1), Rep(Kind.Analog, 0, 7, 0), Rep(Kind.Analog, 0, 14, 2)];

        Verify.Events(input, generated);
        Assert.True(Throws(() => Verify.Events([input[0], input[2], input[1]], generated)), "reordered within a point: passed");
    }

    /// <summary>
    /// A binary's flags octet carries its state in bit 7; the device's flags do
    /// not, and the two must still compare equal.
    /// </summary>
    [Fact]
    public void VerifyEventsFlagsIgnoreState()
    {
        DutEvent[] generated = [Ev(Kind.Binary, 0, 1, 0)];

        Verify.Events([Rep(Kind.Binary, 0, 1, 0) with { Flags = 0x81 }], generated);
        Assert.True(
            Throws(() => Verify.Events([Rep(Kind.Binary, 0, 1, 0) with { Flags = 0x83 }], generated)),
            "a different flag: passed");
    }

    [Fact]
    public void VerifyStatic()
    {
        var held = new Snapshot
        {
            Binary = [new Binary(true, Flags.Online, default), new Binary(false, Flags.Online, default)],
            Analog = [new Analog(1.5, Flags.Online, default)],
        };
        Kind[] complete = [Kind.Binary, Kind.Analog];
        Point[] ok = [Static(Kind.Binary, 0, 1, 0x81), Static(Kind.Binary, 1, 0), Static(Kind.Analog, 0, 1.5)];

        Verify.Static(ok, held, complete);

        var cases = new Dictionary<string, Point[]>
        {
            ["wrong value"] = [ok[0], Static(Kind.Binary, 1, 1), ok[2]],
            ["missing"] = [ok[0], ok[2]],
            ["repeated"] = [ok[0], ok[1], ok[1], ok[2]],
            ["no point"] = [ok[0], ok[1], ok[2], Static(Kind.Binary, 7, 0)],
            ["an event"] = [ok[0], ok[1], Static(Kind.Analog, 0, 1.5) with { Event = true }],
        };
        foreach (var (name, points) in cases)
        {
            Assert.True(Throws(() => Verify.Static(points, held, complete)), $"{name}: passed");
        }
    }

    [Fact]
    public void VerifyCurrentWithEarlier()
    {
        DutEvent[] all = [Ev(Kind.Counter, 0, 1, 0), Ev(Kind.Counter, 0, 2, 1)];
        DutEvent[] latest = all[1..];
        var held = new Snapshot { Counter = [new Counter(2, Flags.Online, default)] };

        Verify.CurrentWithEarlier([Rep(Kind.Counter, 0, 2, 1)], latest, all, held);
        Verify.CurrentWithEarlier([Rep(Kind.Counter, 0, 1, 0), Rep(Kind.Counter, 0, 2, 1)], latest, all, held);

        var cases = new Dictionary<string, Point[]>
        {
            ["none"] = [],
            ["earlier last"] = [Rep(Kind.Counter, 0, 2, 1), Rep(Kind.Counter, 0, 1, 0)],
            ["never a value"] = [Rep(Kind.Counter, 0, 9, 1), Rep(Kind.Counter, 0, 2, 1)],
        };
        foreach (var (name, points) in cases)
        {
            Assert.True(Throws(() => Verify.CurrentWithEarlier(points, latest, all, held)), $"{name}: passed");
        }
    }

    [Fact]
    public void VerifyAttributesRejectsAWrongValue()
    {
        var want = new Dictionary<byte, DeviceAttribute>
        {
            [250] = DeviceAttribute.String(250, "SharpDnp3 outstation"),
            [239] = DeviceAttribute.Uint(239, 20),
        };
        AttributeIn Str(int v, string text) => new() { Variation = v, Type = "vstr", Text = text };
        AttributeIn Uint(int v, double n) => new() { Variation = v, Type = "uint", Number = n };

        Rpc.VerifyAttributes(new Reply(), [Str(250, "SharpDnp3 outstation"), Uint(239, 20)], want);

        Assert.True(Throws(() => Rpc.VerifyAttributes(new Reply(), [Str(250, "other")], want)), "a wrong string: passed");
        Assert.True(Throws(() => Rpc.VerifyAttributes(new Reply(), [Uint(239, 19)], want)), "a wrong count: passed");
        Assert.True(Throws(() => Rpc.VerifyAttributes(new Reply(), [Str(251, "x")], want)), "an attribute the device lacks: passed");
    }

    /// <summary>
    /// Binary inputs from the first one outside class 1 up are in classes 2 and
    /// 3; a generator that changed them would put binary events where
    /// device.ini says there are none.
    /// </summary>
    [Fact]
    public void ChangesStayInTheirClass()
    {
        foreach (var c in Changes.Of(Kind.Binary, 3 * Device.PointsPerType))
        {
            Assert.Equal(Class.Class1, Device.PointClass(Kind.Binary, c.Index));
        }
    }

    [Fact]
    public void StampsAreOrdered()
    {
        var clock = new DutClock(noColdRestart: false);
        var last = clock.Stamp().Time;
        for (var i = 0; i < 100; i++)
        {
            var next = clock.Stamp().Time;
            Assert.True(next > last, $"stamp {next:O} after {last:O}");
            last = next;
        }
    }

    /// <summary>An unknown method is the device's failure to report, not a crash.</summary>
    [Fact]
    public void UnknownMethodIsAnError()
    {
        var reply = new Rpc(new Device("127.0.0.1:0", 3, NullDnp3Logger.Instance)).Call("noSuchMethod", new Args());
        Assert.Equal("the device under test does not implement noSuchMethod", reply.Error);
    }

    private static bool Throws(Action check)
    {
        try
        {
            check();
            return false;
        }
        catch (DutException)
        {
            return true;
        }
    }
}
