// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

namespace SharpDnp3.Outstation;

/// <summary>One queued change of state.</summary>
/// <remarks>
/// Exactly one of the measurement fields is meaningful, selected by
/// <see cref="Type"/>. A tagged union rather than an interface keeps events
/// cheap in the buffer, which matters when a storm queues thousands per second.
/// </remarks>
public record struct Event
{
    /// <summary>Which of the measurement fields is populated.</summary>
    public PointType Type { get; set; }

    /// <summary>The point index the event came from.</summary>
    public ushort Index { get; set; }

    /// <summary>The event class the point is assigned to.</summary>
    public Class Class { get; set; }

    /// <summary>The variation the event will be reported in.</summary>
    public byte Variation { get; set; }

    /// <summary>When the change happened.</summary>
    public Timestamp Time { get; set; }

    /// <summary>Set when <see cref="Type"/> is <see cref="PointType.Binary"/>.</summary>
    public Binary Binary { get; set; }

    /// <summary>Set when <see cref="Type"/> is <see cref="PointType.DoubleBitBinary"/>.</summary>
    public DoubleBitBinary DoubleBit { get; set; }

    /// <summary>Set when <see cref="Type"/> is <see cref="PointType.Counter"/>.</summary>
    public Counter Counter { get; set; }

    /// <summary>Set when <see cref="Type"/> is <see cref="PointType.FrozenCounter"/>.</summary>
    public FrozenCounter FrozenCounter { get; set; }

    /// <summary>Set when <see cref="Type"/> is <see cref="PointType.Analog"/>.</summary>
    public Analog Analog { get; set; }

    /// <summary>Set when <see cref="Type"/> is <see cref="PointType.BinaryOutputStatus"/>.</summary>
    public BinaryOutputStatus BinaryOutput { get; set; }

    /// <summary>Set when <see cref="Type"/> is <see cref="PointType.AnalogOutputStatus"/>.</summary>
    public AnalogOutputStatus AnalogOutput { get; set; }

    /// <summary>Set when <see cref="Type"/> is <see cref="PointType.OctetString"/>.</summary>
    public byte[]? OctetString { get; set; }

    /// <summary>
    /// Marks an event that has been put into a response but not yet confirmed
    /// by the master.
    /// </summary>
    internal bool Selected { get; set; }
}

/// <summary>Sizes the event buffer.</summary>
public sealed class EventBufferConfig
{
    /// <summary>
    /// The total capacity across all classes. Zero uses
    /// <see cref="EventBuffer.DefaultMaxEvents"/>.
    /// </summary>
    public int MaxEvents { get; set; }
}

/// <summary>Holds events waiting to be reported.</summary>
/// <remarks>
/// The lifecycle is the part that matters, and the part implementations get
/// wrong: an event is queued, then <em>selected</em> when it goes into a
/// response, and only removed when the master confirms that response. An
/// outstation that drops events at transmission loses exactly the data a
/// sequence-of-events record exists to preserve — and loses it silently,
/// because the master has no way to know an event it never saw was sent.
/// </remarks>
public sealed class EventBuffer
{
    /// <summary>
    /// The buffer capacity when a configuration does not set one.
    /// </summary>
    public const int DefaultMaxEvents = 1000;

    private readonly Lock _gate = new();
    private readonly LinkedList<Event> _events = [];
    private readonly int _max;
    private bool _overflow;

    /// <summary>Creates an event buffer.</summary>
    public EventBuffer(EventBufferConfig? config = null)
    {
        var maxEvents = config?.MaxEvents ?? 0;
        _max = maxEvents <= 0 ? DefaultMaxEvents : maxEvents;
    }

    /// <summary>
    /// Queues an event, discarding the oldest if the buffer is full.
    /// </summary>
    /// <remarks>
    /// Dropping the oldest rather than the newest is deliberate: after an
    /// overflow the master's picture is already incomplete, and the recent past
    /// is what an operator needs. The overflow is latched so it can be reported
    /// in the internal indications, which is the only way the master learns
    /// there is a hole in its record.
    /// </remarks>
    public void Add(Event e)
    {
        lock (_gate)
        {
            if (_events.Count >= _max)
            {
                _events.RemoveFirst();
                _overflow = true;
            }

            _events.AddLast(e);
        }
    }

    /// <summary>Returns how many events are queued for the given classes.</summary>
    public int Count(Class mask)
    {
        lock (_gate)
        {
            var n = 0;
            foreach (var e in _events)
            {
                if ((e.Class & mask) != 0)
                {
                    n++;
                }
            }

            return n;
        }
    }

    /// <summary>Returns how many events are queued in all.</summary>
    public int Total
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }

    /// <summary>
    /// Returns the mask of classes that have events waiting, which is what the
    /// internal indications report.
    /// </summary>
    public Class Classes()
    {
        lock (_gate)
        {
            var mask = Class.None;
            foreach (var e in _events)
            {
                mask |= e.Class;
            }

            return mask;
        }
    }

    /// <summary>
    /// Reports whether events have been lost since the flag was last cleared.
    /// </summary>
    public bool Overflowed
    {
        get
        {
            lock (_gate)
            {
                return _overflow;
            }
        }
    }

    /// <summary>
    /// Resets the overflow flag, which a master does implicitly by reading the
    /// events that remain.
    /// </summary>
    public void ClearOverflow()
    {
        lock (_gate)
        {
            _overflow = false;
        }
    }

    /// <summary>
    /// Marks up to <paramref name="limit"/> unselected events of the given
    /// classes and returns copies of them, oldest first.
    /// </summary>
    /// <remarks>
    /// The events stay in the buffer. They are removed only by
    /// <see cref="Confirm"/>.
    /// </remarks>
    public List<Event> Select(Class mask, int limit)
    {
        lock (_gate)
        {
            if (limit <= 0)
            {
                return [];
            }

            var output = new List<Event>(Math.Min(limit, _events.Count));
            for (var node = _events.First; node is not null; node = node.Next)
            {
                if (output.Count >= limit)
                {
                    break;
                }

                var e = node.Value;
                if (e.Selected || (e.Class & mask) == 0)
                {
                    continue;
                }

                e.Selected = true;
                node.Value = e;
                output.Add(e);
            }

            return output;
        }
    }

    /// <summary>Returns how many events are awaiting confirmation.</summary>
    public int SelectedCount
    {
        get
        {
            lock (_gate)
            {
                var n = 0;
                foreach (var e in _events)
                {
                    if (e.Selected)
                    {
                        n++;
                    }
                }

                return n;
            }
        }
    }

    /// <summary>
    /// Removes every selected event. The master has acknowledged the response
    /// that carried them, so they can finally be dropped.
    /// </summary>
    public int Confirm()
    {
        lock (_gate)
        {
            var removed = 0;
            var node = _events.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.Selected)
                {
                    _events.Remove(node);
                    removed++;
                }

                node = next;
            }

            return removed;
        }
    }

    /// <summary>
    /// Returns every selected event to the queue, which is what happens when a
    /// confirmation does not arrive. The events are re-sent rather than lost.
    /// </summary>
    public int Unselect()
    {
        lock (_gate)
        {
            var n = 0;
            for (var node = _events.First; node is not null; node = node.Next)
            {
                if (node.Value.Selected)
                {
                    var e = node.Value;
                    e.Selected = false;
                    node.Value = e;
                    n++;
                }
            }

            return n;
        }
    }

    /// <summary>Empties the buffer, as a cold restart does.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _events.Clear();
            _overflow = false;
        }
    }

    /// <summary>
    /// Empties the buffer and returns what was in it, with every selection
    /// undone.
    /// </summary>
    /// <remarks>
    /// This is how a queue moves between buffers when a master connects or
    /// disconnects. The selections are dropped because they record a response
    /// that a particular master was sent, and that fact does not travel: the
    /// events go back to being unreported, which is what they are as far as
    /// whoever picks them up next is concerned.
    /// </remarks>
    internal List<Event> Drain(out bool overflowed)
    {
        lock (_gate)
        {
            var output = new List<Event>(_events.Count);
            foreach (var e in _events)
            {
                output.Add(e with { Selected = false });
            }

            overflowed = _overflow;
            _events.Clear();
            _overflow = false;
            return output;
        }
    }

    /// <summary>Adds a queue taken from another buffer, oldest first.</summary>
    internal void Seed(List<Event> events, bool overflowed)
    {
        lock (_gate)
        {
            foreach (var e in events)
            {
                if (_events.Count >= _max)
                {
                    _events.RemoveFirst();
                    overflowed = true;
                }

                _events.AddLast(e);
            }

            _overflow |= overflowed;
        }
    }
}
