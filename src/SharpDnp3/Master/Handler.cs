// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The DNP3 master role: the station that polls outstations, receives their
// events, and issues commands.

using System.Threading.Channels;
using SharpDnp3.Objects;

namespace SharpDnp3.Master;

/// <summary>Describes the fragment a set of measurements arrived in.</summary>
public readonly record struct ResponseInfo
{
    /// <summary>The internal indications the outstation reported.</summary>
    public Iin Iin { get; init; }

    /// <summary>
    /// Reports whether the fragment arrived unsolicited rather than in answer
    /// to a poll.
    /// </summary>
    public bool Unsolicited { get; init; }

    /// <summary>The application sequence number.</summary>
    public byte Sequence { get; init; }

    /// <summary>When the fragment was decoded.</summary>
    public DateTimeOffset Received { get; init; }
}

/// <summary>Describes the object header a measurement came from.</summary>
/// <remarks>
/// Consumers need this more often than it looks: the same analog point read as
/// a static value and received as an event mean different things to a
/// historian, and only the group tells them apart.
/// </remarks>
public readonly record struct HeaderInfo
{
    /// <summary>The group and variation the measurement was encoded as.</summary>
    public GroupVar GV { get; init; }

    /// <summary>Distinguishes a static value from an event.</summary>
    public Kind Kind { get; init; }

    /// <summary>
    /// The event class, when the measurement came from a class poll.
    /// </summary>
    public Class Class { get; init; }

    /// <summary>Reports whether the measurement came from an event object.</summary>
    public bool IsEvent => Kind == Kind.Event;
}

/// <summary>Receives the measurements a master decodes.</summary>
/// <remarks>
/// <para>
/// <see cref="BeginFragment"/> and <see cref="EndFragment"/> bracket every
/// fragment, so a consumer that needs a consistent set — a database
/// transaction, a UI repaint — has somewhere to open and close it.
/// </para>
/// <para>
/// Handler methods are called from the session loop. A slow handler delays the
/// session's polling, so anything expensive belongs behind a queue;
/// <see cref="ChannelHandler"/> is that queue.
/// </para>
/// </remarks>
public interface IMasterHandler
{
    /// <summary>Called before any measurement from a fragment.</summary>
    void BeginFragment(ResponseInfo info);

    /// <summary>Called after the last measurement from a fragment.</summary>
    void EndFragment(ResponseInfo info);

    /// <summary>Receives binary inputs.</summary>
    void HandleBinary(HeaderInfo info, IReadOnlyList<Indexed<Binary>> values);

    /// <summary>Receives double-bit binary inputs.</summary>
    void HandleDoubleBit(HeaderInfo info, IReadOnlyList<Indexed<DoubleBitBinary>> values);

    /// <summary>Receives counters.</summary>
    void HandleCounter(HeaderInfo info, IReadOnlyList<Indexed<Counter>> values);

    /// <summary>Receives frozen counters.</summary>
    void HandleFrozenCounter(HeaderInfo info, IReadOnlyList<Indexed<FrozenCounter>> values);

    /// <summary>Receives analog inputs.</summary>
    void HandleAnalog(HeaderInfo info, IReadOnlyList<Indexed<Analog>> values);

    /// <summary>Receives binary output statuses.</summary>
    void HandleBinaryOutputStatus(HeaderInfo info, IReadOnlyList<Indexed<BinaryOutputStatus>> values);

    /// <summary>Receives analog output statuses.</summary>
    void HandleAnalogOutputStatus(HeaderInfo info, IReadOnlyList<Indexed<AnalogOutputStatus>> values);

    /// <summary>
    /// Receives group 110 and 111 objects: the point names, firmware versions
    /// and serial numbers a device reports as text rather than as measurements.
    /// </summary>
    void HandleOctetString(HeaderInfo info, IReadOnlyList<Indexed<byte[]>> values);
}

/// <summary>
/// Discards everything. Derive from it to implement only the methods you care
/// about.
/// </summary>
public class NopHandler : IMasterHandler
{
    /// <inheritdoc/>
    public virtual void BeginFragment(ResponseInfo info) { }

    /// <inheritdoc/>
    public virtual void EndFragment(ResponseInfo info) { }

    /// <inheritdoc/>
    public virtual void HandleBinary(HeaderInfo info, IReadOnlyList<Indexed<Binary>> values) { }

    /// <inheritdoc/>
    public virtual void HandleDoubleBit(
        HeaderInfo info, IReadOnlyList<Indexed<DoubleBitBinary>> values) { }

    /// <inheritdoc/>
    public virtual void HandleCounter(HeaderInfo info, IReadOnlyList<Indexed<Counter>> values) { }

    /// <inheritdoc/>
    public virtual void HandleFrozenCounter(
        HeaderInfo info, IReadOnlyList<Indexed<FrozenCounter>> values) { }

    /// <inheritdoc/>
    public virtual void HandleAnalog(HeaderInfo info, IReadOnlyList<Indexed<Analog>> values) { }

    /// <inheritdoc/>
    public virtual void HandleBinaryOutputStatus(
        HeaderInfo info, IReadOnlyList<Indexed<BinaryOutputStatus>> values) { }

    /// <inheritdoc/>
    public virtual void HandleAnalogOutputStatus(
        HeaderInfo info, IReadOnlyList<Indexed<AnalogOutputStatus>> values) { }

    /// <inheritdoc/>
    public virtual void HandleOctetString(
        HeaderInfo info, IReadOnlyList<Indexed<byte[]>> values) { }
}

/// <summary>One measurement delivered through a <see cref="ChannelHandler"/>.</summary>
public readonly record struct Update
{
    /// <summary>The object header the measurement came from.</summary>
    public HeaderInfo Info { get; init; }

    /// <summary>The fragment the measurement arrived in.</summary>
    public ResponseInfo Fragment { get; init; }

    /// <summary>Which of the value fields below is populated.</summary>
    public PointType Type { get; init; }

    /// <summary>The point index.</summary>
    public ushort Index { get; init; }

    /// <summary>Set when <see cref="Type"/> is <see cref="PointType.Binary"/>.</summary>
    public Binary Binary { get; init; }

    /// <summary>Set when <see cref="Type"/> is <see cref="PointType.DoubleBitBinary"/>.</summary>
    public DoubleBitBinary DoubleBit { get; init; }

    /// <summary>Set when <see cref="Type"/> is <see cref="PointType.Counter"/>.</summary>
    public Counter Counter { get; init; }

    /// <summary>Set when <see cref="Type"/> is <see cref="PointType.FrozenCounter"/>.</summary>
    public FrozenCounter FrozenCounter { get; init; }

    /// <summary>Set when <see cref="Type"/> is <see cref="PointType.Analog"/>.</summary>
    public Analog Analog { get; init; }

    /// <summary>Set when <see cref="Type"/> is <see cref="PointType.BinaryOutputStatus"/>.</summary>
    public BinaryOutputStatus BinaryOutput { get; init; }

    /// <summary>Set when <see cref="Type"/> is <see cref="PointType.AnalogOutputStatus"/>.</summary>
    public AnalogOutputStatus AnalogOutput { get; init; }

    /// <summary>Set when <see cref="Type"/> is <see cref="PointType.OctetString"/>.</summary>
    public byte[]? OctetString { get; init; }
}

/// <summary>Fans measurements into a channel.</summary>
/// <remarks>
/// This is what a terminal UI or a recorder consumes: the session loop stays
/// responsive because it only ever does a non-blocking write, and the consumer
/// reads at its own pace. Updates are dropped rather than blocking the session
/// when the consumer falls behind, and the drop is counted — a stalled UI must
/// not stall the protocol.
/// </remarks>
public sealed class ChannelHandler : NopHandler
{
    private readonly Channel<Update> _channel;
    private ulong _dropped;
    private ResponseInfo _info;

    /// <summary>
    /// Creates a handler delivering to a bounded channel of
    /// <paramref name="buffer"/> updates.
    /// </summary>
    public ChannelHandler(int buffer = 256)
    {
        if (buffer <= 0)
        {
            buffer = 256;
        }

        _channel = Channel.CreateBounded<Update>(new BoundedChannelOptions(buffer)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleWriter = true,
        });
    }

    /// <summary>The channel measurements are delivered on.</summary>
    public ChannelReader<Update> Updates => _channel.Reader;

    /// <summary>
    /// How many updates were discarded because the consumer was not keeping up.
    /// </summary>
    public ulong Dropped => _dropped;

    /// <inheritdoc/>
    public override void BeginFragment(ResponseInfo info) => _info = info;

    private void Send(Update u)
    {
        if (!_channel.Writer.TryWrite(u with { Fragment = _info }))
        {
            _dropped++;
        }
    }

    /// <inheritdoc/>
    public override void HandleBinary(HeaderInfo info, IReadOnlyList<Indexed<Binary>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
        {
            Send(new Update
            {
                Info = info,
                Type = PointType.Binary,
                Index = v.Index,
                Binary = v.Value,
            });
        }
    }

    /// <inheritdoc/>
    public override void HandleDoubleBit(
        HeaderInfo info, IReadOnlyList<Indexed<DoubleBitBinary>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
        {
            Send(new Update
            {
                Info = info,
                Type = PointType.DoubleBitBinary,
                Index = v.Index,
                DoubleBit = v.Value,
            });
        }
    }

    /// <inheritdoc/>
    public override void HandleCounter(HeaderInfo info, IReadOnlyList<Indexed<Counter>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
        {
            Send(new Update
            {
                Info = info,
                Type = PointType.Counter,
                Index = v.Index,
                Counter = v.Value,
            });
        }
    }

    /// <inheritdoc/>
    public override void HandleFrozenCounter(
        HeaderInfo info, IReadOnlyList<Indexed<FrozenCounter>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
        {
            Send(new Update
            {
                Info = info,
                Type = PointType.FrozenCounter,
                Index = v.Index,
                FrozenCounter = v.Value,
            });
        }
    }

    /// <inheritdoc/>
    public override void HandleAnalog(HeaderInfo info, IReadOnlyList<Indexed<Analog>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
        {
            Send(new Update
            {
                Info = info,
                Type = PointType.Analog,
                Index = v.Index,
                Analog = v.Value,
            });
        }
    }

    /// <inheritdoc/>
    public override void HandleBinaryOutputStatus(
        HeaderInfo info, IReadOnlyList<Indexed<BinaryOutputStatus>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
        {
            Send(new Update
            {
                Info = info,
                Type = PointType.BinaryOutputStatus,
                Index = v.Index,
                BinaryOutput = v.Value,
            });
        }
    }

    /// <inheritdoc/>
    public override void HandleAnalogOutputStatus(
        HeaderInfo info, IReadOnlyList<Indexed<AnalogOutputStatus>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
        {
            Send(new Update
            {
                Info = info,
                Type = PointType.AnalogOutputStatus,
                Index = v.Index,
                AnalogOutput = v.Value,
            });
        }
    }

    /// <inheritdoc/>
    public override void HandleOctetString(HeaderInfo info, IReadOnlyList<Indexed<byte[]>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
        {
            Send(new Update
            {
                Info = info,
                Type = PointType.OctetString,
                Index = v.Index,
                OctetString = v.Value,
            });
        }
    }
}
