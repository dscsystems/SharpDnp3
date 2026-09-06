// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// A restart the master must not miss.
//
// The startup sequence is triggered by DEVICE_RESTART and its first step clears
// the indication, so every response until that clear lands still carries the
// bit. A master that reacted to each of them would restart its own sequence for
// ever, which is why the reaction is suppressed while the sequence runs.
//
// The trap is that the suppression must end when the echo does, not when the
// sequence does. The sequence takes several round trips after the clear, and a
// device that restarts during them has to be heard — its own clear has already
// taken the indication away, so nothing later will mention it.

using SharpDnp3.Channels;
using SharpDnp3.Master;
using SharpDnp3.Outstation;

namespace SharpDnp3.Tests;

public class RestartDuringStartupTests
{
    /// <summary>
    /// A restart asserted after the startup sequence's clear has landed, but
    /// while later steps are still running, must be counted and re-baselined.
    /// </summary>
    /// <remarks>
    /// The window is forced open rather than waited for: the outstation is made
    /// to restart from inside the handler, at the moment the first response
    /// without the indication arrives, which is precisely the instant the echo
    /// ends and the rest of the sequence has yet to run.
    /// </remarks>
    [Fact]
    public async Task ARestartAfterTheClearButDuringStartupIsSeen()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (masterSide, outstationSide) = Pipe.Create();
        using var mc = masterSide;
        using var oc = outstationSide;

        var outstation = new OutstationSession(new OutstationConfig
        {
            LocalAddr = 10,
            RemoteAddr = 1,
            Database = new DatabaseConfig { Binary = 8, Analog = 4, DefaultClass = Class.Class1 },
        });

        var outstationTask = outstation.RunAsync(oc, cts.Token);

        var handler = new RestartInjectingHandler(outstation);
        var master = new MasterSession(
            new MasterConfig
            {
                LocalAddr = 1,
                RemoteAddr = 10,
                IntegrityOnStartup = true,
                DisableUnsolOnStartup = true,
                UnsolClassMask = Class.Class123,
                ResponseTimeout = TimeSpan.FromSeconds(5),
            },
            handler);

        var masterTask = master.RunAsync(mc, cts.Token);

        try
        {
            // The handler fires the restart the first time it sees a response
            // with the indication already cleared — mid-sequence, by
            // construction.
            await WaitForAsync(
                () => handler.Injected, "the restart to be injected mid-sequence");

            // It must be noticed rather than swallowed. Before the fix the
            // count stayed where it was: the sequence's own clear had removed
            // the only signal that would ever have mentioned it.
            await WaitForAsync(
                () => master.Stats.RestartsSeen > 0,
                "the master to notice a restart raised during its startup sequence");
        }
        finally
        {
            await cts.CancelAsync();
            mc.Close();
            oc.Close();
            await Task.WhenAny(Task.WhenAll(masterTask, outstationTask), Task.Delay(3000));
        }
    }

    /// <summary>
    /// The suppression the fix narrows must still hold: the echo of the restart
    /// a sequence is already handling must not start another one.
    /// </summary>
    /// <remarks>
    /// Without it a master loops: every response before the clear lands carries
    /// the indication, and reacting to each restarts the sequence that was
    /// about to clear it.
    /// </remarks>
    [Fact]
    public async Task TheEchoOfTheRestartBeingHandledIsStillSuppressed()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (masterSide, outstationSide) = Pipe.Create();
        using var mc = masterSide;
        using var oc = outstationSide;

        var outstation = new OutstationSession(new OutstationConfig
        {
            LocalAddr = 10,
            RemoteAddr = 1,
            Database = new DatabaseConfig { Binary = 8, Analog = 4, DefaultClass = Class.Class1 },
        });

        var outstationTask = outstation.RunAsync(oc, cts.Token);

        var handler = new ChannelHandler();
        var master = new MasterSession(
            new MasterConfig
            {
                LocalAddr = 1,
                RemoteAddr = 10,
                IntegrityOnStartup = true,
                DisableUnsolOnStartup = true,
            },
            handler);

        var masterTask = master.RunAsync(mc, cts.Token);

        try
        {
            await WaitForAsync(() => master.Connected, "the master to connect");

            // The outstation asserts a restart at startup, which is what raises
            // the sequence in the first place. That one is the sequence's own
            // cause, so it is not a restart the master "saw" and reacted to.
            await WaitForAsync(
                () => master.Stats.TasksSucceeded >= 3,
                "the startup sequence to run to completion");

            await Task.Delay(300);

            Assert.Equal(0u, master.Stats.RestartsSeen);
        }
        finally
        {
            await cts.CancelAsync();
            mc.Close();
            oc.Close();
            await Task.WhenAny(Task.WhenAll(masterTask, outstationTask), Task.Delay(3000));
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail($"timed out waiting for {what}");
    }

    /// <summary>
    /// Restarts the outstation the moment the master sees its restart
    /// indication cleared, which lands a genuine restart inside the startup
    /// sequence's remaining steps.
    /// </summary>
    private sealed class RestartInjectingHandler : NopHandler
    {
        private readonly OutstationSession _outstation;
        private int _fired;

        public RestartInjectingHandler(OutstationSession outstation) =>
            _outstation = outstation;

        public bool Injected => Volatile.Read(ref _fired) != 0;

        public override void BeginFragment(ResponseInfo info)
        {
            if (info.Iin.Has(Iin.DeviceRestart))
            {
                return;
            }

            if (Interlocked.Exchange(ref _fired, 1) == 0)
            {
                _outstation.Restart();
            }
        }
    }
}
