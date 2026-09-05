// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using SharpDnp3.Channels;

namespace SharpDnp3.Multidrop;

/// <summary>Gives the line to one master at a time.</summary>
/// <remarks>
/// <para>
/// A multi-drop line is half duplex: if two masters transmit requests at once,
/// the two outstations answer at once and both replies are lost. Nothing in a
/// session knows that — each one believes it owns its channel — so the turn
/// taking has to happen here, where the transmissions meet.
/// </para>
/// <para>
/// A master takes the line when it transmits and gives it back when the reply
/// arrives complete, or when the turnaround elapses with nothing having come
/// back. That second case is what keeps one unresponsive outstation from
/// stopping the line for good: the hold is a reservation with an expiry, not a
/// lock.
/// </para>
/// <para>
/// Outstations do not take the line at all. They transmit only when addressed,
/// so they are already taking turns; making them wait would deadlock the case
/// where a master holds the line and the reply it is waiting for is queued
/// behind that hold.
/// </para>
/// </remarks>
internal sealed class Arbiter : IDisposable
{
    /// <summary>The turnaround. Zero disables arbitration entirely.</summary>
    private readonly TimeSpan _period;

    private readonly Lock _gate = new();

    /// <summary>
    /// Released whenever the line becomes free, so a waiter can re-check.
    /// </summary>
    /// <remarks>
    /// A semaphore rather than a monitor pulse: a waiter has to be woken both
    /// by a release and by the hold's own expiry, and only a waitable primitive
    /// with a timeout can do the second without a timer per waiter.
    /// </remarks>
    private readonly SemaphoreSlim _free = new(0);

    private StationConnection? _holder;
    private DateTimeOffset _until;
    private bool _closed;

    /// <summary>Creates an arbiter with the given turnaround.</summary>
    public Arbiter(TimeSpan period) => _period = period;

    /// <summary>Blocks until the caller may transmit.</summary>
    public async Task AcquireAsync(
        StationConnection c, CancellationToken cancellationToken)
    {
        if (_period <= TimeSpan.Zero)
        {
            return;
        }

        while (true)
        {
            TimeSpan wait;

            lock (_gate)
            {
                if (_closed)
                {
                    throw new ChannelClosedException();
                }

                // An expired hold is taken over rather than waited on: the
                // station that had it asked a question nobody answered.
                if (_holder is null || ReferenceEquals(_holder, c) ||
                    DateTimeOffset.UtcNow >= _until)
                {
                    Hold(c);
                    return;
                }

                wait = _until - DateTimeOffset.UtcNow;
                if (wait < TimeSpan.Zero)
                {
                    wait = TimeSpan.Zero;
                }
            }

            // Waking on the hold's expiry as well as on a release is what makes
            // the reservation self-clearing.
            await _free.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports a frame routed to <paramref name="c"/>, extending or ending its
    /// hold.
    /// </summary>
    public void Observe(StationConnection c, bool complete)
    {
        if (_period <= TimeSpan.Zero)
        {
            return;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_holder, c))
            {
                return;
            }

            if (complete)
            {
                // The fragment is whole; the exchange is over and the line is
                // free.
                Release();
                return;
            }

            // Traffic is flowing but the reply is not finished — more segments,
            // or a link-layer acknowledgement with the response still to come.
            // Hold on for another turnaround rather than letting it expire
            // mid-answer.
            _until = DateTimeOffset.UtcNow + _period;
        }
    }

    /// <summary>Gives up <paramref name="c"/>'s hold, if it has one.</summary>
    public void ReleaseFor(StationConnection c)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_holder, c))
            {
                Release();
            }
        }
    }

    /// <summary>Frees the line and fails every waiter.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Close();
        _free.Dispose();
    }

    private void Hold(StationConnection c)
    {
        _holder = c;
        _until = DateTimeOffset.UtcNow + _period;
    }

    private void Release()
    {
        _holder = null;

        // One permit is enough to start the cascade: whoever wakes and cannot
        // take the line waits again, and whoever takes it will release in turn.
        if (_free.CurrentCount == 0)
        {
            _free.Release();
        }
    }
}
