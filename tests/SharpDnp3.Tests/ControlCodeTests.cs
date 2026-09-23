// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The trip-close code is the one field of a CROB where a wrong constant does
// not fail loudly: the outstation simply drives the other coil. These pin the
// wire values to the standard's rather than to the library's own round trip,
// which would pass with the two swapped.

using SharpDnp3.Master;

namespace SharpDnp3.Tests;

public sealed class ControlCodeTests
{
    [Fact]
    public void CloseIsTripCloseCodeOne()
    {
        Assert.Equal(0x40, ControlCode.Close.Value);
        Assert.Equal(0x41, (ControlCode.PulseOn | ControlCode.Close).Value);
    }

    [Fact]
    public void TripIsTripCloseCodeTwo()
    {
        Assert.Equal(0x80, ControlCode.Trip.Value);
        Assert.Equal(0x81, (ControlCode.PulseOn | ControlCode.Trip).Value);
    }

    [Fact]
    public void FactoriesUseTheStandardCodes()
    {
        Assert.Equal(0x81, Command.Trip(3, 1000).Data[0]);
        Assert.Equal(0x41, Command.Close(3, 1000).Data[0]);
    }

    [Fact]
    public void DecodesRawOctetsFromOtherMasters()
    {
        Assert.True(new ControlCode(0x41).IsClose());
        Assert.False(new ControlCode(0x41).IsTrip());
        Assert.True(new ControlCode(0x81).IsTrip());
        Assert.Equal("PULSE_ON|TRIP", new ControlCode(0x81).ToString());
        Assert.Equal("PULSE_ON|CLOSE", new ControlCode(0x41).ToString());
    }
}
