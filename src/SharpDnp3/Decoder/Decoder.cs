// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Turns DNP3 octets into a structured trace.
//
// It produces a tree, not log strings. One consumer renders it to a log, one to
// a terminal UI, one to text for the command-line decoder — and none of them
// re-implement any parsing. That is the whole point: there is exactly one place
// in this library that knows how to read a DNP3 frame.

using System.Globalization;
using System.Text;
using SharpDnp3.App;
using SharpDnp3.Link;
using SharpDnp3.Objects;
using SharpDnp3.Transport;

namespace SharpDnp3.Decoding;

/// <summary>Says which way octets travelled.</summary>
public enum Direction : byte
{
    /// <summary>Not known, as in an offline capture.</summary>
    Unknown = 0,

    /// <summary>Sent by us.</summary>
    Tx,

    /// <summary>Received by us.</summary>
    Rx,
}

/// <summary>Naming helpers for <see cref="Direction"/>.</summary>
public static class DirectionExtensions
{
    /// <summary>Renders the direction as the decoder prints it.</summary>
    public static string ToDisplayString(this Direction d) => d switch
    {
        Direction.Tx => "TX",
        Direction.Rx => "RX",
        _ => "--",
    };
}

/// <summary>Describes one decoded link frame.</summary>
public readonly record struct LinkInfo
{
    /// <summary>The link control octet.</summary>
    public Control Control { get; init; }

    /// <summary>The destination link address.</summary>
    public ushort Dest { get; init; }

    /// <summary>The source link address.</summary>
    public ushort Src { get; init; }

    /// <summary>The LEN octet.</summary>
    public byte Length { get; init; }

    /// <summary>The user data the frame carried.</summary>
    public int PayloadLen { get; init; }

    /// <summary>The total octets the frame occupied on the wire.</summary>
    public int FrameSize { get; init; }
}

/// <summary>Describes one decoded transport segment.</summary>
public readonly record struct TransportInfo
{
    /// <summary>The transport header octet, decoded.</summary>
    public TransportHeader Header { get; init; }

    /// <summary>
    /// Set when this segment completed an application fragment.
    /// </summary>
    public bool Complete { get; init; }

    /// <summary>What the reassembler dropped, if anything.</summary>
    public DiscardReason Discarded { get; init; }
}

/// <summary>Describes a decoded application fragment.</summary>
public sealed class AppInfo
{
    /// <summary>The fragment header.</summary>
    public AppHeader Header { get; init; }

    /// <summary>The object headers, in wire order.</summary>
    public IReadOnlyList<ObjectHeader> Objects { get; init; } = [];

    /// <summary>
    /// The measurements decoded from each object header, indexed to match
    /// <see cref="Objects"/>. Entries are empty for headers that carry no
    /// measurements — class objects, commands, times.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<Value>> Values { get; init; } = [];

    /// <summary>
    /// Set when the fragment header parsed but the object headers did not.
    /// </summary>
    /// <remarks>
    /// The headers decoded before the failure are still in
    /// <see cref="Objects"/>, because showing an operator what was understood
    /// before the corruption beats showing nothing.
    /// </remarks>
    public string? Error { get; init; }
}

/// <summary>Everything decodable about one link frame.</summary>
/// <remarks>
/// A frame always yields link and transport information. It yields application
/// information only when it completed a fragment, since a fragment can span
/// nine frames and only the last one finishes it.
/// </remarks>
public sealed class Trace
{
    /// <summary>Which way the octets travelled.</summary>
    public Direction Direction { get; init; }

    /// <summary>The link layer's view.</summary>
    public LinkInfo Link { get; init; }

    /// <summary>The transport function's view, when the frame carried one.</summary>
    public TransportInfo? Transport { get; init; }

    /// <summary>The application layer's view, when a fragment completed.</summary>
    public AppInfo? App { get; init; }

    /// <summary>The frame's octets as they appeared on the wire.</summary>
    public ReadOnlyMemory<byte> Raw { get; init; }

    /// <summary>Set when the frame itself could not be decoded.</summary>
    public string? Error { get; init; }

    /// <summary>Writes a human-readable form of the trace.</summary>
    /// <remarks>
    /// The layout is a layer tree, indented, so an operator can see at a glance
    /// which layer a problem lives in.
    /// </remarks>
    public void Render(StringBuilder b, bool showHex)
    {
        ArgumentNullException.ThrowIfNull(b);

        b.AppendFormat(
            CultureInfo.InvariantCulture, "{0}  {1}\n", Direction.ToDisplayString(), LinkLine());

        if (Error is not null)
        {
            b.AppendFormat(CultureInfo.InvariantCulture, "      error: {0}\n", Error);
            return;
        }

        if (Transport is { } t)
        {
            b.AppendFormat(CultureInfo.InvariantCulture, "      transport  {0}", t.Header);
            if (t.Discarded != DiscardReason.None)
            {
                b.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "  DISCARDED: {0}", t.Discarded.ToDisplayString());
            }

            b.Append('\n');
        }

        if (App is { } a)
        {
            b.AppendFormat(CultureInfo.InvariantCulture, "      application  {0}\n", a.Header);

            for (var i = 0; i < a.Objects.Count; i++)
            {
                var o = a.Objects[i];
                b.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "        g{0}v{1}  {2,-22} {3,-14} {4} object(s)",
                    o.Group, o.Variation, o.Qualifier, o.Range, o.Count);

                if (!o.Data.IsEmpty)
                {
                    b.AppendFormat(CultureInfo.InvariantCulture, "  {0} octets", o.Data.Length);
                }

                b.Append('\n');

                if (i < a.Values.Count)
                {
                    foreach (var v in a.Values[i])
                    {
                        b.AppendFormat(CultureInfo.InvariantCulture, "          {0}\n", v);
                    }
                }
            }

            if (a.Error is not null)
            {
                b.AppendFormat(CultureInfo.InvariantCulture, "        error: {0}\n", a.Error);
            }
        }

        if (showHex)
        {
            WriteHex(b, Raw.Span, "      ");
        }
    }

    private string LinkLine() => string.Format(
        CultureInfo.InvariantCulture,
        "link  {0}  {1}→{2}  len={3}  frame={4}B",
        Link.Control, Link.Src, Link.Dest, Link.Length, Link.FrameSize);

    /// <summary>Renders the trace without the hex dump.</summary>
    public override string ToString()
    {
        var b = new StringBuilder();
        Render(b, showHex: false);
        return b.ToString().TrimEnd('\n');
    }

    /// <summary>Writes a classic offset / hex / ASCII dump.</summary>
    internal static void WriteHex(StringBuilder b, ReadOnlySpan<byte> data, string indent)
    {
        const int PerLine = 16;
        for (var off = 0; off < data.Length; off += PerLine)
        {
            var end = Math.Min(off + PerLine, data.Length);
            var line = data[off..end];

            b.AppendFormat(CultureInfo.InvariantCulture, "{0}{1:x4}  ", indent, off);
            for (var i = 0; i < PerLine; i++)
            {
                if (i < line.Length)
                {
                    b.AppendFormat(CultureInfo.InvariantCulture, "{0:x2} ", line[i]);
                }
                else
                {
                    b.Append("   ");
                }

                if (i == 7)
                {
                    b.Append(' ');
                }
            }

            b.Append(" |");
            foreach (var c in line)
            {
                b.Append(c is >= 0x20 and < 0x7F ? (char)c : '.');
            }

            b.Append("|\n");
        }
    }

    /// <summary>Returns a standalone hex dump of <paramref name="data"/>.</summary>
    public static string HexDump(ReadOnlySpan<byte> data)
    {
        var b = new StringBuilder();
        WriteHex(b, data, "");
        return b.ToString();
    }
}

/// <summary>Reassembles a stream of octets into traces.</summary>
/// <remarks>
/// It holds link and transport state, so one decoder belongs to one direction
/// of one connection. Feeding both directions into a single decoder would
/// interleave two independent transport sequences and produce nonsense.
/// </remarks>
public sealed class Dnp3Decoder
{
    /// <summary>
    /// The common-time-of-occurrence group, whose objects set the base for the
    /// relative-time event variations that follow them.
    /// </summary>
    private const byte GroupCto = 51;

    private readonly Direction _dir;
    private FrameParser _parser;
    private readonly Reassembler _reasm;
    private readonly IObjectSizer _sizer;

    /// <summary>
    /// Records whether the outstation's clock is believed to be set, which
    /// decides the quality stamped on every timestamp decoded. A session
    /// updates it from the NEED_TIME internal indication.
    /// </summary>
    private bool _synchronized;

    /// <summary>
    /// Creates a decoder for one direction of one connection. Pass a null sizer
    /// to use the application layer's default.
    /// </summary>
    /// <remarks>
    /// Timestamps are treated as synchronized until told otherwise; call
    /// <see cref="SetSynchronized"/> from a session that has seen NEED_TIME. An
    /// offline tool has no way to know, and marking every timestamp in a
    /// capture as unsynchronized would be a claim the octets do not support
    /// either way.
    /// </remarks>
    public Dnp3Decoder(Direction direction = Direction.Unknown, IObjectSizer? sizer = null)
    {
        _dir = direction;
        _parser = new FrameParser();
        _reasm = new Reassembler(0);
        _sizer = sizer ?? ObjectSizing.DefaultSizer;
        _synchronized = true;
    }

    /// <summary>
    /// Records whether the outstation's clock is set, which decides the quality
    /// stamped on decoded timestamps.
    /// </summary>
    public void SetSynchronized(bool value) => _synchronized = value;

    /// <summary>
    /// Clears link and transport state, as when a connection is re-established.
    /// </summary>
    public void Reset()
    {
        _parser = new FrameParser();
        _reasm.Reset();
    }

    /// <summary>
    /// Returns the underlying parser and reassembler counters.
    /// </summary>
    public (LinkStats Link, TransportStats Transport) Stats => (_parser.Stats, _reasm.Stats);

    /// <summary>
    /// Decodes octets and invokes <paramref name="onTrace"/> for each frame
    /// found.
    /// </summary>
    /// <remarks>
    /// Octets that do not yet form a complete frame are buffered until they do.
    /// </remarks>
    public void Feed(ReadOnlySpan<byte> data, Action<Trace> onTrace)
    {
        ArgumentNullException.ThrowIfNull(onTrace);

        while (!data.IsEmpty)
        {
            var n = _parser.Write(data);
            if (n == 0)
            {
                // The buffer is full of octets that cannot form a frame. Drain
                // what we can and drop forward rather than spinning.
                Drain(onTrace);
                n = _parser.Write(data);
                if (n == 0)
                {
                    return;
                }
            }

            data = data[n..];
            Drain(onTrace);
        }
    }

    private void Drain(Action<Trace> onTrace)
    {
        while (_parser.TryNext(out var f))
        {
            onTrace(BuildTrace(f));
        }
    }

    /// <summary>
    /// Builds the trace for one decoded link frame, advancing transport
    /// reassembly and parsing the application fragment when one completes.
    /// </summary>
    private Trace BuildTrace(LinkFrame f)
    {
        var raw = FrameCodec.Encode(f.Header, f.Payload.Span);

        var link = new LinkInfo
        {
            Control = f.Header.Control,
            Dest = f.Header.Dest,
            Src = f.Header.Src,
            Length = f.Header.Length,
            PayloadLen = f.Payload.Length,
            FrameSize = LinkConstants.FrameSize(f.Payload.Length),
        };

        // Only user-data frames carry a transport segment. A link ACK or a link
        // status reply has no payload above it.
        var fn = f.Header.Control.Func;
        if (!f.Header.Control.Prm ||
            (fn != LinkFunction.ConfirmedUserData && fn != LinkFunction.UnconfirmedUserData) ||
            f.Payload.IsEmpty)
        {
            return new Trace { Direction = _dir, Raw = raw, Link = link };
        }

        var res = _reasm.Accept(f.Payload.Span);
        var transport = new TransportInfo
        {
            Header = TransportHeader.Parse(f.Payload.Span[0]),
            Complete = res.Complete,
            Discarded = res.Discarded,
        };

        if (!res.Complete)
        {
            return new Trace
            {
                Direction = _dir,
                Raw = raw,
                Link = link,
                Transport = transport,
            };
        }

        var status = FragmentParser.ParseFragment(_sizer, res.Fragment, out var frag, out var error);

        return new Trace
        {
            Direction = _dir,
            Raw = raw,
            Link = link,
            Transport = transport,
            App = new AppInfo
            {
                Header = frag.Header,
                Objects = frag.Objects,
                Values = DecodeValues(frag.Objects),
                Error = status == AppParseStatus.Ok ? null : error,
            },
        };
    }

    /// <summary>
    /// Decodes the measurements in each object header, threading the common
    /// time of occurrence forward.
    /// </summary>
    /// <remarks>
    /// A group 51 object sets the base that any relative-time event <em>after
    /// it in the same fragment</em> is measured from, so this has to walk the
    /// headers in order and carry the context along rather than decoding each
    /// independently.
    /// </remarks>
    private List<IReadOnlyList<Value>> DecodeValues(IReadOnlyList<ObjectHeader> headers)
    {
        var output = new List<IReadOnlyList<Value>>(headers.Count);
        if (headers.Count == 0)
        {
            return output;
        }

        var ctx = new Context { Synchronized = _synchronized };

        foreach (var h in headers)
        {
            if (h.Group == GroupCto && h.Data.Length >= CommandObjects.Time48Size)
            {
                ctx = ctx.WithCto(CommandObjects.ParseTime48(h.Data.Span).Time);
                output.Add([]);
                continue;
            }

            output.Add(ValueDecoder.TryDecodeValues(h, ctx, out var vals) ? vals : []);
        }

        return output;
    }

    /// <summary>
    /// Decodes a single self-contained frame without any session state.
    /// </summary>
    /// <remarks>
    /// It is the one-shot form used by offline tools: a frame pasted from a
    /// capture is assumed to carry a complete fragment, which is true for the
    /// single-frame messages that make up most DNP3 traffic. Multi-frame
    /// fragments need a <see cref="Dnp3Decoder"/>, which carries the reassembly
    /// state across frames.
    /// </remarks>
    public static bool TryDecodeFrame(
        IObjectSizer? sizer,
        ReadOnlySpan<byte> data,
        out Trace trace,
        out int consumed)
    {
        consumed = 0;

        // Decode once up front purely to learn the frame's length, so the
        // caller gets a precise error and an accurate consumed count rather
        // than having to infer either from the stream decoder.
        var payload = new byte[LinkConstants.MaxPayload];
        var status = FrameCodec.Decode(data, payload, out _, out var n, out _);
        if (status != LinkDecodeStatus.Ok)
        {
            trace = new Trace { Raw = data.ToArray(), Error = status.ToDisplayString() };
            return false;
        }

        var d = new Dnp3Decoder(Direction.Unknown, sizer);
        Trace? found = null;
        d.Feed(data[..n], t => found ??= t);

        consumed = n;
        if (found is null)
        {
            trace = new Trace
            {
                Raw = data[..n].ToArray(),
                Error = LinkDecodeStatus.ShortFrame.ToDisplayString(),
            };
            return false;
        }

        trace = found;
        return true;
    }
}
