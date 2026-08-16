// Copyright (C) 2026 Ricardo Olsen / DSC Systems.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option)
// any later version. It is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details, in the LICENSE file at
// the root of this repository or at <https://www.gnu.org/licenses/>.

using SharpDnp3.Channels;
using SharpDnp3.Outstation;

namespace SharpDnp3.Tools.Explorer;

/// <summary>The transports the explorer can be pointed at.</summary>
public static class Demo
{
    /// <summary>
    /// Selects the transport, starting a simulated outstation for the demo.
    /// </summary>
    /// <remarks>
    /// The demo device is built per connection rather than once for the process,
    /// so that reconnecting to it works the same way as reconnecting to anything
    /// else. It comes up fresh, which is the honest outcome: the pipe the old
    /// one was reached over has been closed.
    /// </remarks>
    public static (IChannel Channel, Task Device) BuildChannel(
        LinkParams p, CancellationToken cancellationToken)
    {
        if (p.Demo)
        {
            var (masterEnd, outstationEnd) = Pipe.Create();
            var sim = new DemoOutstation();

            var device = Task.WhenAll(
                RunDeviceAsync(sim, outstationEnd, cancellationToken),
                sim.RunAsync(cancellationToken));

            return (masterEnd, device);
        }

        if (!string.IsNullOrEmpty(p.Serial))
        {
            return (
                new SerialChannel(new SerialConfig { Device = p.Serial, Baud = p.Baud }),
                Task.CompletedTask);
        }

        return (new TcpClientChannel(p.Host, Retry.Default), Task.CompletedTask);
    }

    private static async Task RunDeviceAsync(
        DemoOutstation sim, IChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            await sim.Session.RunAsync(channel, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or Dnp3Exception or IOException)
        {
            // The demo device goes away with the connection it was built for.
        }
        finally
        {
            channel.Dispose();
        }
    }
}

/// <summary>A small simulated device for the demo mode.</summary>
/// <remarks>
/// <para>
/// It is deliberately modest — six binaries, six analogs, two counters, two
/// controls and the strings a device reports about itself — because its job is
/// to make the interface explorable, not to be a second simulator.
/// SharpDnp3.Tools.Outstation is the one with plant behind it.
/// </para>
/// <para>
/// What it does contain is one of everything the interface has to draw: a point
/// that goes offline, a point that is locally forced, a value that ramps so the
/// trend has a shape, controls that actually move something, and setpoints that
/// come back as analog output status. Otherwise half the tool can only be tested
/// against real hardware.
/// </para>
/// </remarks>
public sealed class DemoOutstation : ICommandHandler
{
    // Guards the plant state. Updates normally run on the session's own loop,
    // but a control is applied on whichever task delivered it, and that is
    // enough to make a race real.
    private readonly Lock _gate = new();
    private readonly bool[] _breaker = [true, false];
    private readonly double[] _setpoint = [13.75, 50];

    /// <summary>Builds the device and its database.</summary>
    public DemoOutstation()
    {
        Session = new OutstationSession(
            new OutstationConfig
            {
                LocalAddr = 10,
                RemoteAddr = 1,
                Database = new DatabaseConfig
                {
                    Binary = 6,
                    Analog = 6,
                    Counter = 2,
                    BinaryOutputStatus = 2,
                    AnalogOutputStatus = 2,
                    OctetString = 2,
                    DefaultClass = Class.Class1,
                },
                Log = NullDnp3Logger.Instance,
            },
            null,
            this);

        var db = Session.Database;
        for (ushort i = 0; i < 6; i++)
        {
            db.Configure(PointType.Analog, i, new PointConfig
            {
                Class = Class.Class2,
                Deadband = 0.5,
                StaticVariation = 5, // single precision; the default would truncate
                EventVariation = 7,
            });
        }

        // The strings never change, so they are written once rather than on
        // every tick: a device name that produced an event every half second
        // would be a device nobody would ship.
        db.UpdateOctetString(0, "SHARPDNP3 DEMO RTU"u8);
        db.UpdateOctetString(1, "firmware 1.0.0-demo"u8);
    }

    /// <summary>The session an explorer connects to over the pipe.</summary>
    public OutstationSession Session { get; }

    /// <summary>Drives the simulated plant until the token is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        double n = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                n += 0.5;
                var tick = n;
                var now = DateTimeOffset.UtcNow;

                Session.Update(db =>
                {
                    bool[] breaker;
                    double[] setpoint;
                    lock (_gate)
                    {
                        breaker = [.. _breaker];
                        setpoint = [.. _setpoint];
                    }

                    var stamp = Timestamp.Now(now);

                    db.UpdateAnalog(0, new Analog(
                        11000 + (200 * Math.Sin(tick / 10)), Flags.Online, stamp));
                    db.UpdateAnalog(1, new Analog(
                        150 + (120 * Math.Sin(tick / 7)), Flags.Online, stamp));
                    db.UpdateAnalog(2, new Analog(
                        45 + (20 * Math.Sin(tick / 23)), Flags.Online, stamp));
                    db.UpdateAnalog(3, new Analog(8, Flags.Online, stamp));

                    // A sensor that drops out for ten seconds in every forty, so
                    // the quality column and the stale fade have something to
                    // show.
                    var dropped = (int)tick % 40 < 10;
                    db.UpdateAnalog(4, new Analog(
                        0.42, OnlineUnless(dropped, Flags.CommLost), stamp));

                    // A ramp, because a trend needs a shape to be worth drawing.
                    db.UpdateAnalog(5, new Analog(tick % 60, Flags.Online, stamp));

                    for (ushort i = 0; i < 2; i++)
                    {
                        db.UpdateBinary(i, new Binary(breaker[i], Flags.Online, stamp));
                        db.UpdateBinaryOutputStatus(
                            i, new BinaryOutputStatus(breaker[i], Flags.Online, stamp));
                        db.UpdateAnalogOutputStatus(
                            i, new AnalogOutputStatus(setpoint[i], Flags.Online, stamp));
                    }

                    // A point that toggles on its own, so the Events screen has
                    // something to show without the operator doing anything.
                    db.UpdateBinary(2, new Binary((int)tick % 20 < 10, Flags.Online, stamp));
                    db.UpdateBinary(3, new Binary(
                        (int)tick % 14 < 3, Flags.Online | Flags.ChatterFilter, stamp));
                    db.UpdateBinary(4, new Binary(
                        false, Flags.Online | Flags.LocalForced, stamp));
                    db.UpdateBinary(5, new Binary(true, Flags.CommLost, stamp));

                    db.UpdateCounter(0, new Counter((uint)(tick * 3), Flags.Online, stamp));
                    db.UpdateCounter(1, new Counter((uint)(tick * 2), Flags.Online, stamp));
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The quality of a point that is healthy unless something is wrong with it.
    /// </summary>
    private static Flags OnlineUnless(bool faulted, Flags fault) =>
        faulted ? fault : Flags.Online;

    /// <inheritdoc/>
    public CommandStatus SelectCrob(ushort index, ControlRelayOutputBlock c) =>
        index >= _breaker.Length ? CommandStatus.NotSupported : CommandStatus.Success;

    /// <inheritdoc/>
    public CommandStatus OperateCrob(
        ushort index, ControlRelayOutputBlock c, OperateType op)
    {
        if (index >= _breaker.Length)
        {
            return CommandStatus.NotSupported;
        }

        lock (_gate)
        {
            if (c.Code.IsClose() || c.Code.OpType() == ControlCode.LatchOn)
            {
                _breaker[index] = true;
            }
            else if (c.Code.IsTrip() || c.Code.OpType() == ControlCode.LatchOff)
            {
                _breaker[index] = false;
            }
            else
            {
                return CommandStatus.NotSupported;
            }
        }

        return CommandStatus.Success;
    }

    /// <inheritdoc/>
    public CommandStatus SelectAnalog(ushort index, AnalogOutputCommand v) =>
        index >= _setpoint.Length ? CommandStatus.NotSupported : CommandStatus.Success;

    /// <summary>
    /// Stores the setpoint, which the next tick reports back as analog output
    /// status — the round trip an operator is really checking when they write
    /// one.
    /// </summary>
    public CommandStatus OperateAnalog(ushort index, AnalogOutputCommand v, OperateType op)
    {
        if (index >= _setpoint.Length)
        {
            return CommandStatus.NotSupported;
        }

        lock (_gate)
        {
            _setpoint[index] = v.Value;
        }

        return CommandStatus.Success;
    }
}
