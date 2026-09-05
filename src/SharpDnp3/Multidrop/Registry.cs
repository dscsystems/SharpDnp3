// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// A Bus already solves one channel being opened twice by one part of a program
// that knows to share it. It does nothing for two parts that do not know about
// each other — a master built in one assembly, an outstation simulator built in
// another, both configured to talk to "COM3" — and each independently doing the
// right thing, by itself, produces the exact failure multidrop exists to
// prevent: two Buses, two calls to connect, and the OS refusing the second.
//
// A Registry is where independent callers meet. Each asks for a bus by the
// channel it would open; the first caller for a given channel builds one, and
// every later caller for an equivalent channel gets that same bus back instead
// of building a second one.

using SharpDnp3.Channels;

namespace SharpDnp3.Multidrop;

/// <summary>Shares buses across callers that ask for the same physical channel.</summary>
/// <remarks>
/// A program with several independent components that might reach the same
/// device keeps one registry — typically one for the whole process — and has
/// each ask it for a bus rather than constructing one directly.
/// </remarks>
public sealed class BusRegistry
{
    /// <summary>One shared bus and how many callers are using it.</summary>
    private sealed class Entry
    {
        public required Bus Bus { get; init; }

        public required IChannel Channel { get; init; }

        public int Refs { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = [];

    /// <summary>
    /// Finds the entry for a release without a linear scan.
    /// </summary>
    /// <remarks>
    /// It is why <see cref="Bus"/> is not its own key: two buses are never
    /// equal by construction, but looking one up by the reference a caller
    /// hands back is still a second index, not a property of the first.
    /// </remarks>
    private readonly Dictionary<Bus, string> _byBus = [];

    /// <summary>
    /// Returns the bus for <paramref name="channel"/>, reusing an existing one
    /// if this registry already has one for an equivalent channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Equivalence is the channel's own description: two channels built the
    /// same way — the same serial device at the same baud, the same host and
    /// port — describe themselves identically, which is what lets two callers
    /// that have never met recognise they mean the same line. Two channels that
    /// merely reach the same device through different settings (one with retry,
    /// one without) are not equivalent by this test and get separate buses; the
    /// description does not cover retry policy, only identity, so that is
    /// deliberate — arbitration timing and queue depth belong to whoever opens
    /// the bus first regardless, and giving every caller its own bus when the
    /// physical identity does differ is the correct behaviour.
    /// </para>
    /// <para>
    /// The bus is built, and the configuration takes effect, only on the first
    /// call for a given channel; the configuration on every later call for the
    /// same channel is ignored, since the bus already exists and is not rebuilt
    /// under an active caller. If the channel passed is not the one that ends
    /// up owning the bus — because an equivalent one already existed — it is
    /// disposed before this returns, since nothing will ever connect it and an
    /// undisposed one would otherwise sit there implying it is in use.
    /// </para>
    /// <para>
    /// Every call must be matched by exactly one <see cref="Release"/>. The
    /// bus, and the channel beneath it, stays open until the last caller that
    /// opened it releases it — not the first, since another caller may still
    /// depend on it.
    /// </para>
    /// </remarks>
    public Bus Open(IChannel channel, BusConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var key = channel.ToString() ?? channel.GetType().FullName!;

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.Refs++;

                if (!ReferenceEquals(channel, existing.Channel))
                {
                    // A distinct object describing the same channel: this
                    // caller built its own before asking, not knowing one
                    // already existed. Disposing it is safe before it is ever
                    // connected, and leaving it open would misrepresent it as
                    // live.
                    channel.Dispose();
                }

                return existing.Bus;
            }

            var bus = new Bus(channel, config);
            _entries[key] = new Entry { Bus = bus, Channel = channel, Refs = 1 };
            _byBus[bus] = key;
            return bus;
        }
    }

    /// <summary>Gives up one reference to <paramref name="bus"/>.</summary>
    /// <remarks>
    /// <para>
    /// The bus is closed once every caller that opened it has released it.
    /// Until then this only counts down: closing on an early release would drop
    /// the line out from under callers still using it, which is the whole
    /// failure this type exists to prevent — the same mistake one level up.
    /// </para>
    /// <para>
    /// Once a bus was obtained through a registry, release it through the
    /// registry rather than disposing it directly. <see cref="Bus.Dispose"/>
    /// does not know about other callers and would close the shared channel
    /// regardless of how many are still using it; the registry's bookkeeping
    /// would then disagree with the bus's own state, and a later
    /// <see cref="Open"/> for the same channel would return a bus that is
    /// already closed.
    /// </para>
    /// </remarks>
    public void Release(Bus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);

        lock (_gate)
        {
            if (!_byBus.TryGetValue(bus, out var key))
            {
                throw new BadConfigException(
                    "multidrop: release of a bus this registry did not open");
            }

            var entry = _entries[key];
            entry.Refs--;
            if (entry.Refs > 0)
            {
                return;
            }

            _entries.Remove(key);
            _byBus.Remove(bus);
        }

        bus.Dispose();
    }

    /// <summary>
    /// How many distinct buses the registry currently holds open, for
    /// diagnostics and tests.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }
}
