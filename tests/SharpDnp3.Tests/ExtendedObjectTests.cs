// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Object codecs and framing rules added with the extended services. Each
// mirrors a test in go-dnp3.

using SharpDnp3.App;
using SharpDnp3.Objects;

namespace SharpDnp3.Tests;

public class ExtendedObjectTests
{
    [Fact]
    public void BinaryCommandEventGoldenBytes()
    {
        // The status sits in the low seven bits and the commanded state in bit 7.
        var dst = new List<byte>();
        CommandEventCodec.Append(dst, 13, 1, new CommandEvent((CommandStatus)4, false, true, 0, default));
        Assert.Equal(new byte[] { 0x84 }, dst);
    }

    [Theory]
    [InlineData((byte)13, (byte)1, 0.0, false)]
    [InlineData((byte)13, (byte)2, 0.0, true)]
    [InlineData((byte)43, (byte)1, -70000.0, false)]
    [InlineData((byte)43, (byte)2, 1234.0, false)]
    [InlineData((byte)43, (byte)3, 70000.0, true)]
    [InlineData((byte)43, (byte)4, -5.0, true)]
    [InlineData((byte)43, (byte)5, 1.5, false)]
    [InlineData((byte)43, (byte)6, 1e100, false)]
    [InlineData((byte)43, (byte)7, 2.5, true)]
    [InlineData((byte)43, (byte)8, -2.5e10, true)]
    public void CommandEventsRoundTrip(byte group, byte variation, double value, bool timed)
    {
        // The integer variations do not hold 1e100 or -70000 in 16 bits, so the
        // expected value is what the variation can carry.
        var at = Timestamp.Now(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var e = new CommandEvent(CommandStatus.Success, group == 43, group == 13, group == 43 ? value : 0, timed ? at : default);
        var buf = new List<byte>();
        CommandEventCodec.Append(buf, group, variation, e);
        Assert.True(CommandEventCodec.TrySize(group, variation, out var size));
        Assert.Equal(size, buf.Count);
        Assert.True(CommandEventCodec.TryParse(group, variation, buf.ToArray(), out var back));
        Assert.Equal(e.Status, back.Status);
        Assert.Equal(e.State, back.State);
        Assert.Equal(e.Analog, back.Analog);
        Assert.Equal(e.Value, back.Value);
        if (timed)
        {
            Assert.Equal(at.Time, back.Time.Time);
        }
    }

    /// <summary>An integer variation saturates rather than wrapping.</summary>
    [Fact]
    public void AnalogCommandEventSaturates()
    {
        var buf = new List<byte>();
        CommandEventCodec.Append(buf, 43, 2, new CommandEvent(CommandStatus.Success, true, false, 1e9, default));
        CommandEventCodec.TryParse(43, 2, buf.ToArray(), out var e);
        Assert.Equal(32767, e.Value);
    }

    [Fact]
    public void CommandEventRejectsUnknownVariations()
    {
        Assert.False(CommandEventCodec.TryParse(43, 9, new byte[20], out _));
        var dst = new List<byte>();
        CommandEventCodec.Append(dst, 13, 3, default);
        Assert.Empty(dst);
        Assert.False(CommandEventCodec.TryParse(43, 3, new byte[3], out _));
    }

    [Theory]
    [InlineData((byte)0x00, true)]
    [InlineData((byte)0x01, true)]
    [InlineData((byte)0x06, true)]
    [InlineData((byte)0x07, true)]
    [InlineData((byte)0x08, true)]
    [InlineData((byte)0x17, true)]
    [InlineData((byte)0x28, true)]
    [InlineData((byte)0x39, true)]
    [InlineData((byte)0x27, true)]
    [InlineData((byte)0x5B, true)]
    [InlineData((byte)0x4B, true)]
    [InlineData((byte)0x6B, true)]
    [InlineData((byte)0x10, false)]
    [InlineData((byte)0x11, false)]
    [InlineData((byte)0x16, false)]
    [InlineData((byte)0x0B, false)]
    [InlineData((byte)0x57, false)]
    [InlineData((byte)0x7B, false)]
    [InlineData((byte)0x0A, false)]
    [InlineData((byte)0x87, false)]
    public void QualifierConsistency(byte q, bool want) => Assert.Equal(want, new Qualifier(q).Consistent());

    /// <summary>The quality of a relative time comes from the group 51 variation, not the session's belief.</summary>
    [Fact]
    public void RelativeTimeQualityFollowsTheCtoVariation()
    {
        var baseTime = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        var ctx = new Context { Synchronized = true }.WithGroup51(baseTime, 2);
        Assert.Equal(TimestampQuality.Unsynchronized, ctx.RelativeTime(500).Quality);
        Assert.Equal(TimestampQuality.Synchronized, ctx.TimeQuality());

        ctx = new Context { Synchronized = false }.WithGroup51(baseTime, 1);
        Assert.Equal(TimestampQuality.Synchronized, ctx.RelativeTime(500).Quality);
        Assert.Equal(baseTime.AddMilliseconds(500), ctx.RelativeTime(500).Time);
    }

    [Fact]
    public void ActivationResultRoundTrips()
    {
        var value = new ActivationResult(
            TimeSpan.FromMilliseconds(1500),
            [new ActivationStatus(0, "a.cfg"), new ActivationStatus(3, "b.cfg")]);
        var buf = new List<byte>();
        ActivationCodec.Append(buf, value);
        var back = ActivationCodec.Parse(buf.ToArray());
        Assert.Equal(value.Delay, back.Delay);
        Assert.Equal(value.Statuses, back.Statuses);
        Assert.Throws<MalformedException>(() => ActivationCodec.Parse(buf.Take(buf.Count - 1).ToArray()));
    }

    /// <summary>The framing layer walks a group 91 object so the headers after it are found.</summary>
    [Fact]
    public void ActivationResultIsWalkedByTheFramingLayer()
    {
        var value = new List<byte>();
        ActivationCodec.Append(value, new ActivationResult(TimeSpan.FromSeconds(1), [new ActivationStatus(0, "x")]));
        var header = new ObjectHeader
        {
            Group = 91,
            Variation = 1,
            Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
            Data = value.ToArray(),
        };
        var bytes = FragmentFactory.BuildResponse(
            new AppControl(true, true, false, false, 0), FuncCode.Response, Iin.None, header, FragmentFactory.ReadAllObjects(60, 1));
        var status = FragmentParser.ParseFragment(null, bytes, out var frag, out var error);
        Assert.True(status == AppParseStatus.Ok, error);
        Assert.Equal(2, frag.Objects.Count);
        Assert.Equal((byte)91, frag.Objects[0].Group);
    }

    [Fact]
    public void DatasetDescriptorRoundTripsAndRejectsBadLengths()
    {
        var elements = new[] { new DatasetElement(1, 2, 3, [9, 8]), new DatasetElement(4, 5, 6, []) };
        var buf = new List<byte>();
        DatasetCodec.AppendDescriptor(buf, elements);
        var back = DatasetCodec.ParseDescriptor(buf.ToArray());
        Assert.Equal(2, back.Count);
        Assert.Equal(elements[0].Ancillary, back[0].Ancillary);
        Assert.Throws<MalformedException>(() => DatasetCodec.ParseDescriptor([2, 1, 2]));
        Assert.Throws<BadConfigException>(() =>
            DatasetCodec.AppendDescriptor([], [new DatasetElement(1, 1, 1, new byte[253])]));
    }

    [Fact]
    public void AuthenticationObjectsRoundTrip()
    {
        var challenge = new AuthChallenge(7, 1, 4, 1, new byte[32]);
        var buf = new List<byte>();
        AuthenticationCodec.Append(buf, challenge);
        Assert.Equal(challenge.Sequence, AuthenticationCodec.ParseChallenge(buf.ToArray()).Sequence);

        var status = new AuthKeyStatus(3, 1, 1, 1, 4, new byte[32], new byte[16]);
        buf.Clear();
        AuthenticationCodec.Append(buf, status);
        var back = AuthenticationCodec.ParseKeyStatus(buf.ToArray());
        Assert.Equal(status.Challenge, back.Challenge);
        Assert.Equal(status.Mac, back.Mac);

        Assert.Throws<MalformedException>(() => AuthenticationCodec.ParseKeyChange(new byte[29]));
        Assert.Throws<MalformedException>(() => AuthenticationCodec.ParseReply(new byte[6]));
    }

    /// <summary>
    /// A FREEZE_AT_TIME mixes a data-bearing time object with counter headers
    /// that only name what to freeze, so the framing rule is per object.
    /// </summary>
    [Fact]
    public void FreezeAtTimeMixesDataAndNamingHeaders()
    {
        var data = new List<byte>();
        CommandObjects.AppendTime48(data, Timestamp.Now(DateTimeOffset.UtcNow));
        data.AddRange(new byte[4]);
        var time = new ObjectHeader
        {
            Group = 50,
            Variation = 2,
            Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
            Data = data.ToArray(),
        };
        var bytes = FragmentFactory.BuildRequest(
            new AppControl(true, true, false, false, 1), FuncCode.FreezeAtTime, time, FragmentFactory.ReadRange(20, 1, 0, 0));
        var status = FragmentParser.ParseFragment(null, bytes, out var frag, out var error);
        Assert.True(status == AppParseStatus.Ok, error);
        Assert.Equal(2, frag.Objects.Count);
    }
}
