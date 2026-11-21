// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Threading.Channels;

namespace SharpDnp3.Security;

/// <summary>Bounds the dispatcher's queue and repeat aggregation.</summary>
public sealed record SecurityDispatcherOptions
{
    /// <summary>Maximum number of events waiting for delivery.</summary>
    public int QueueSize { get; init; } = 1024;
    /// <summary>Duration of the repeat window. A negative value disables aggregation.</summary>
    public TimeSpan AggregateWindow { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Maximum number of repeat windows held at once.</summary>
    public int MaxAggregates { get; init; } = 4096;
}

/// <summary>Snapshot of delivery counters.</summary>
public readonly record struct SecurityDispatcherStats(long Submitted, long Dropped, long Delivered, long SinkErrors);

/// <summary>Delivers security events without blocking the protocol sessions that submit them.</summary>
public sealed class SecurityDispatcher : IAsyncDisposable
{
    private readonly SecurityEventSink _sink;
    private readonly SecurityDispatcherOptions _options;
    private readonly Channel<SecurityEvent> _queue;
    private readonly Task _worker;
    private readonly Dictionary<AggregateKey, Aggregate> _aggregates = new();
    private long _submitted;
    private long _dropped;
    private long _delivered;
    private long _sinkErrors;
    private long _unreportedDrops;

    /// <summary>Starts a dispatcher with the given sink and options.</summary>
    public SecurityDispatcher(SecurityEventSink sink, SecurityDispatcherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.QueueSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxAggregates);
        _queue = Channel.CreateBounded<SecurityEvent>(new BoundedChannelOptions(_options.QueueSize)
        {
            SingleReader = true,
        });
        _worker = Task.Run(DeliverAsync);
    }

    /// <summary>Attempts to enqueue an event without waiting for the sink.</summary>
    public bool Submit(SecurityEvent observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (_queue.Writer.TryWrite(observation.ApplyDefaults()))
        {
            Interlocked.Increment(ref _submitted);
            return true;
        }

        Interlocked.Increment(ref _dropped);
        Interlocked.Increment(ref _unreportedDrops);
        return false;
    }

    /// <summary>Returns the current delivery counters.</summary>
    public SecurityDispatcherStats Stats => new(
        Interlocked.Read(ref _submitted), Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _delivered), Interlocked.Read(ref _sinkErrors));

    /// <summary>Flushes queued events and aggregates, then stops delivery.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private async Task DeliverAsync()
    {
        var reader = _queue.Reader;
        while (true)
        {
            if (_aggregates.Count == 0)
            {
                if (!await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    break;
                }
            }
            else
            {
                var next = _aggregates.Values.Min(a => a.Deadline);
                var wait = next - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    using var timer = new CancellationTokenSource(wait);
                    try
                    {
                        if (!await reader.WaitToReadAsync(timer.Token).ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (timer.IsCancellationRequested)
                    {
                    }
                }
            }

            FlushExpired(DateTimeOffset.UtcNow);
            if (reader.TryRead(out var observation))
            {
                ReportDrops();
                Process(observation);
            }
        }

        FlushExpired(DateTimeOffset.MaxValue);
        ReportDrops();
    }

    private void Process(SecurityEvent observation)
    {
        if (_options.AggregateWindow <= TimeSpan.Zero ||
            SecurityEventKind.Catalog.TryGetValue(observation.Kind, out var info) && info.AlwaysDeliver)
        {
            Deliver(observation);
            return;
        }

        var key = new AggregateKey(observation.ConnId, observation.Kind, observation.Action,
            observation.Peer?.Remote, observation.LinkSrc, observation.LinkDest,
            observation.AppFunc, observation.Group, observation.Variation, observation.Index);
        if (_aggregates.TryGetValue(key, out var aggregate))
        {
            aggregate.Count += observation.Count;
            aggregate.Latest = observation;
            return;
        }

        if (_aggregates.Count >= _options.MaxAggregates)
        {
            Flush(_aggregates.MinBy(pair => pair.Value.Deadline).Key);
        }

        _aggregates.Add(key, new Aggregate(observation, DateTimeOffset.UtcNow + _options.AggregateWindow));
        Deliver(observation);
    }

    private void FlushExpired(DateTimeOffset now)
    {
        foreach (var (key, aggregate) in _aggregates)
        {
            if (aggregate.Deadline <= now)
            {
                Flush(key);
            }
        }
    }

    private void Flush(AggregateKey key)
    {
        var aggregate = _aggregates[key];
        _aggregates.Remove(key);
        if (aggregate.Count > 0)
        {
            Deliver(aggregate.Latest with { FirstSeen = aggregate.FirstSeen, Count = aggregate.Count });
        }
    }

    private void ReportDrops()
    {
        var dropped = Interlocked.Exchange(ref _unreportedDrops, 0);
        if (dropped > 0)
        {
            Deliver(new SecurityEvent(SecurityEventKind.MetaEventsDropped)
            {
                Count = (int)Math.Min(dropped, int.MaxValue),
            }.ApplyDefaults());
        }
    }

    private void Deliver(SecurityEvent observation)
    {
        try
        {
            _sink(observation);
            Interlocked.Increment(ref _delivered);
        }
        catch (Exception)
        {
            if (Interlocked.Increment(ref _sinkErrors) == 1)
            {
                Deliver(new SecurityEvent(SecurityEventKind.MetaSinkPanic).ApplyDefaults());
            }
        }
    }

    private readonly record struct AggregateKey(
        long? Connection, string Kind, SecurityAction Action, string? Remote, ushort? Source,
        ushort? Destination, string? Function, byte? Group, byte? Variation, uint? Index);

    private sealed class Aggregate(SecurityEvent first, DateTimeOffset deadline)
    {
        public DateTimeOffset FirstSeen { get; } = first.Time;
        public DateTimeOffset Deadline { get; } = deadline;
        public SecurityEvent Latest { get; set; } = first;
        public int Count { get; set; }
    }
}
