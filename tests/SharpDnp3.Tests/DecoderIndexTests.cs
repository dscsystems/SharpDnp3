// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// A decoder exists to show what was on the wire. An index past 0xFFFF, carried
// by a 32-bit range or a four-octet prefix, has to come out as it went in:
// narrowed to 16 bits it reports a different point, and nothing on the line
// says so.

using SharpDnp3.App;
using SharpDnp3.Decoding;
using SharpDnp3.Objects;

namespace SharpDnp3.Tests;

public sealed class DecoderIndexTests
{
    [Fact]
    public void AThirtyTwoBitRangeKeepsItsIndexes()
    {
        // g1v2, binary input with flags: one octet per point.
        var h = new ObjectHeader
        {
            Group = 1,
            Variation = 2,
            Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.StartStop32),
            Range = new ObjectRange { Spec = RangeSpec.StartStop32, Start = 70000, Stop = 70001, Count = 2 },
            Data = new byte[] { 0x81, 0x01 },
        };

        Assert.True(ValueDecoder.TryDecodeValues(h, default, out var values));
        Assert.Equal([70000u, 70001u], values.Select(v => v.Index));
    }

    [Fact]
    public void AFourOctetPrefixKeepsItsIndex()
    {
        // g2v1, binary input event without time, behind a four-octet index.
        var h = new ObjectHeader
        {
            Group = 2,
            Variation = 1,
            Qualifier = Qualifier.Make(IndexPrefix.Index4, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
            Data = new byte[] { 0x45, 0x23, 0x01, 0x00, 0x81 },
        };

        Assert.True(ValueDecoder.TryDecodeValues(h, default, out var values));
        Assert.Equal(0x12345u, Assert.Single(values).Index);
    }
}
