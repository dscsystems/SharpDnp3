// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// What happens after a master goes away.
//
// An outstation spends most of its life waiting, and the interesting part of
// that is what it does when the waiting ends badly: a master that closes its
// socket, a link that drops, a session that reconnects. Getting it wrong does
// not show up as a failed request — it shows up as a device that served the
// first master it ever saw and nothing afterwards.

using SharpDnp3.App;
using SharpDnp3.Channels;
using SharpDnp3.Master;
using SharpDnp3.Outstation;

namespace SharpDnp3.Conformance.Tests;

public class ReconnectTests
{
    /// <summary>
    /// An outstation must serve the master that comes after the one that left.
    /// </summary>
    /// <remarks>
    /// The failure this guards against is not a refused connection: the socket
    /// is accepted and then nothing answers, because the connection loop never
    /// noticed the previous master had gone and is still spinning on it.
    /// </remarks>
    [Fact]
    public async Task AnOutstationServesEachMasterInTurn()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var listener = new PipeListener();
        var outstation = new OutstationSession(new OutstationConfig
        {
            LocalAddr = Addresses.Outstation,
            RemoteAddr = Addresses.Master,
            Database = Requests.SmallDatabase(),
        });

        var outstationTask = outstation.RunAsync(listener.Server, cts.Token);

        try
        {
            // Three masters, one after another, each on its own connection.
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                using var channel = listener.Connect();
                var master = new MasterSession(new MasterConfig
                {
                    LocalAddr = Addresses.Master,
                    RemoteAddr = Addresses.Outstation,
                    ResponseTimeout = TimeSpan.FromSeconds(3),
                });

                using var run = new CancellationTokenSource();
                var masterTask = master.RunAsync(channel, run.Token);

                await Harness.WaitForAsync(
                    () => master.Connected, $"master {attempt} to connect");

                // A real request, not just a socket: the point is that the
                // outstation answers, and only an answer proves it.
                await master.IntegrityPollAsync(cts.Token);

                await run.CancelAsync();
                channel.Close();

                await Task.WhenAny(masterTask, Task.Delay(3000));
            }

            Assert.True(
                outstation.Stats.Connections >= 3,
                $"the outstation saw {outstation.Stats.Connections} connections, expected 3");
        }
        finally
        {
            await cts.CancelAsync();
            listener.Dispose();
            await Task.WhenAny(outstationTask, Task.Delay(3000));
        }
    }

    /// <summary>
    /// A disconnection must release the connection promptly rather than leaving
    /// the loop turning over it.
    /// </summary>
    /// <remarks>
    /// A spin would still pass the test above, given enough patience; what it
    /// would not do is stop. This asserts the outstation notices within a
    /// window far shorter than any timeout it configures, which a loop that
    /// only learns of the disconnection by timing out cannot manage.
    /// </remarks>
    [Fact]
    public async Task ADisconnectionIsNoticedAtOnce()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var listener = new PipeListener();
        var outstation = new OutstationSession(new OutstationConfig
        {
            LocalAddr = Addresses.Outstation,
            RemoteAddr = Addresses.Master,
            Database = Requests.SmallDatabase(),

            // Deliberately long: if the loop needed a timeout to notice, it
            // would have to wait this out.
            ConfirmTimeout = TimeSpan.FromSeconds(20),
        });

        var outstationTask = outstation.RunAsync(listener.Server, cts.Token);

        try
        {
            var channel = listener.Connect();
            var master = new MasterSession(new MasterConfig
            {
                LocalAddr = Addresses.Master,
                RemoteAddr = Addresses.Outstation,
            });

            using var run = new CancellationTokenSource();
            var masterTask = master.RunAsync(channel, run.Token);

            await Harness.WaitForAsync(() => master.Connected, "the master to connect");
            await Harness.WaitForAsync(
                () => outstation.MastersAttached == 1, "the outstation to attach it");

            await run.CancelAsync();
            channel.Close();
            channel.Dispose();
            await Task.WhenAny(masterTask, Task.Delay(3000));

            await Harness.WaitForAsync(
                () => outstation.MastersAttached == 0,
                "the outstation to let go of the departed master");
        }
        finally
        {
            await cts.CancelAsync();
            listener.Dispose();
            await Task.WhenAny(outstationTask, Task.Delay(3000));
        }
    }
}
