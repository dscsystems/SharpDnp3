// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The DNP3 transport function: the single-octet layer that cuts application
// fragments into link-sized segments and puts them back together.
//
// It is the thinnest layer in the stack and the one most worth getting exactly
// right. A reassembler that accepts a segment it should have dropped hands a
// corrupted fragment to the application layer, which then reports measurements
// that were never sent.
//
// Nothing here performs I/O or reads a clock.

using System.Globalization;

namespace SharpDnp3.Transport;

/// <summary>Wire constants fixed by IEEE 1815 clause 8.</summary>
public static class TransportConstants
{
    /// <summary>The transport header: one octet per segment.</summary>
    public const int HeaderSize = 1;

    /// <summary>
    /// The largest application-fragment slice one segment can carry: the link
    /// layer's 250-octet payload less the transport header.
    /// </summary>
    public const int MaxSegmentPayload = 249;

    /// <summary>A full segment including its header.</summary>
    public const int MaxSegmentSize = HeaderSize + MaxSegmentPayload;

    /// <summary>The transport sequence space. Six bits.</summary>
    public const int SeqModulus = 64;

    /// <summary>
    /// The default cap on a reassembled fragment. The standard's default
    /// maximum application fragment size is 2048 octets; larger values are
    /// legal by negotiation but must be bounded, because the reassembler
    /// buffers the whole fragment before delivering it.
    /// </summary>
    public const int DefaultMaxFragment = 2048;

    // ---- Transport header bit masks ----
    internal const byte FinBit = 0x80;
    internal const byte FirBit = 0x40;
    internal const byte SeqMask = 0x3F;

    /// <summary>
    /// Returns how many segments a fragment of <paramref name="n"/> octets
    /// requires.
    /// </summary>
    public static int SegmentsFor(int n) => n <= 0
        ? 1 // a zero-length fragment still occupies one segment
        : (n + MaxSegmentPayload - 1) / MaxSegmentPayload;
}

/// <summary>A decoded transport header.</summary>
/// <param name="Fir">First segment of a fragment.</param>
/// <param name="Fin">Final segment of a fragment.</param>
/// <param name="Seq">The sequence number, 0..63.</param>
public readonly record struct TransportHeader(bool Fir, bool Fin, byte Seq)
{
    /// <summary>Decodes a transport header octet.</summary>
    public static TransportHeader Parse(byte b) => new(
        Fir: (b & TransportConstants.FirBit) != 0,
        Fin: (b & TransportConstants.FinBit) != 0,
        Seq: (byte)(b & TransportConstants.SeqMask));

    /// <summary>Encodes the header octet.</summary>
    public byte ToByte()
    {
        byte b = 0;
        if (Fin)
        {
            b |= TransportConstants.FinBit;
        }

        if (Fir)
        {
            b |= TransportConstants.FirBit;
        }

        return (byte)(b | (Seq & TransportConstants.SeqMask));
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var f = (Fir, Fin) switch
        {
            (true, true) => "FIR|FIN",
            (true, false) => "FIR",
            (false, true) => "FIN",
            _ => "   ",
        };

        return string.Format(CultureInfo.InvariantCulture, "seq={0:D2} {1}", Seq, f);
    }
}

/// <summary>Cuts application fragments into segments.</summary>
/// <remarks>
/// It holds one fragment at a time. Call <see cref="Reset"/> to load a
/// fragment, then <see cref="TryNext"/> until it reports done. The sequence
/// number persists across fragments, which is what the standard requires: the
/// counter is per-link, not per-fragment.
/// </remarks>
internal sealed class Segmenter
{
    private byte[]? _frag;
    private int _off;
    private byte _seq;
    private bool _live;

    /// <summary>The sequence number the next segment will carry.</summary>
    public byte Seq => _seq;

    /// <summary>
    /// Forces the sequence counter, for tests and for resuming a session.
    /// </summary>
    public void SetSeq(byte value) => _seq = (byte)(value % TransportConstants.SeqModulus);

    /// <summary>
    /// Loads a fragment for segmentation, discarding any fragment in progress.
    /// </summary>
    public void Reset(byte[] fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        _frag = fragment;
        _off = 0;
        _live = true;
    }

    /// <summary>Reports whether segments remain to be emitted.</summary>
    public bool Pending => _live;

    /// <summary>Abandons the loaded fragment.</summary>
    /// <remarks>
    /// It is distinct from <c>Reset([])</c>, which loads an <em>empty</em>
    /// fragment — a real message that still occupies one segment. A caller
    /// wanting "nothing to send" must say so, or a segmenter reset with an
    /// empty array reports itself pending forever.
    /// </remarks>
    public void Clear()
    {
        _frag = null;
        _off = 0;
        _live = false;
    }

    /// <summary>Writes the next segment into <paramref name="dst"/>.</summary>
    /// <returns>
    /// <see langword="false"/> when no fragment is loaded. A zero-length
    /// fragment produces exactly one segment carrying FIR and FIN, which is how
    /// a zero-object response is framed.
    /// </returns>
    public bool TryNext(Span<byte> dst, out int written)
    {
        written = 0;
        if (!_live || _frag is null)
        {
            return false;
        }

        var first = _off == 0;
        var end = Math.Min(_off + TransportConstants.MaxSegmentPayload, _frag.Length);
        var last = end >= _frag.Length;

        var h = new TransportHeader(Fir: first, Fin: last, Seq: _seq);
        dst[0] = h.ToByte();
        var body = _frag.AsSpan(_off, end - _off);
        body.CopyTo(dst[TransportConstants.HeaderSize..]);
        written = TransportConstants.HeaderSize + body.Length;

        _off = end;
        _seq = (byte)((_seq + 1) % TransportConstants.SeqModulus);
        if (last)
        {
            _live = false;
            _frag = null;
        }

        return true;
    }

    /// <summary>Returns the next segment as a newly allocated array.</summary>
    /// <returns><see langword="null"/> when no fragment is loaded.</returns>
    public byte[]? Next()
    {
        Span<byte> buffer = stackalloc byte[TransportConstants.MaxSegmentSize];
        return TryNext(buffer, out var written) ? buffer[..written].ToArray() : null;
    }

    /// <summary>
    /// The batch form of <see cref="Reset"/> and <see cref="TryNext"/>: returns
    /// every segment of <paramref name="fragment"/> as independently allocated
    /// arrays.
    /// </summary>
    /// <remarks>
    /// Session code uses the incremental API; this exists for tests and for
    /// callers that would rather have the whole list.
    /// </remarks>
    public List<byte[]> SegmentAll(byte[] fragment)
    {
        Reset(fragment);
        var output = new List<byte[]>(TransportConstants.SegmentsFor(fragment.Length));
        while (true)
        {
            var segment = Next();
            if (segment is null)
            {
                return output;
            }

            output.Add(segment);
        }
    }
}

/// <summary>Says why a segment or partial fragment was dropped.</summary>
/// <remarks>
/// These are counted separately because "the link is unreliable" is not a
/// diagnosis: a session dropping segments for out-of-order sequence numbers
/// has a very different problem from one whose peer keeps restarting
/// mid-fragment.
/// </remarks>
public enum DiscardReason : byte
{
    /// <summary>Nothing was dropped.</summary>
    None = 0,

    /// <summary>A segment carried no header octet.</summary>
    EmptySegment,

    /// <summary>
    /// A continuation segment arrived with no fragment in progress, usually the
    /// tail of a fragment whose start was lost.
    /// </summary>
    NoFir,

    /// <summary>
    /// A new fragment started before the previous one finished. The partial
    /// fragment is dropped and the new one begins.
    /// </summary>
    UnexpectedFir,

    /// <summary>
    /// A segment's sequence number was not the expected successor.
    /// </summary>
    BadSequence,

    /// <summary>The fragment exceeded the configured maximum.</summary>
    Overflow,
}

/// <summary>Naming helpers for <see cref="DiscardReason"/>.</summary>
public static class DiscardReasonExtensions
{
    /// <summary>Renders the reason using the protocol tools' spelling.</summary>
    public static string ToDisplayString(this DiscardReason reason) => reason switch
    {
        DiscardReason.None => "none",
        DiscardReason.EmptySegment => "empty segment",
        DiscardReason.NoFir => "continuation without FIR",
        DiscardReason.UnexpectedFir => "FIR during assembly",
        DiscardReason.BadSequence => "sequence mismatch",
        DiscardReason.Overflow => "fragment overflow",
        _ => "DiscardReason(?)",
    };
}

/// <summary>Counts what the reassembler saw.</summary>
public struct TransportStats
{
    /// <summary>Segments fed to the reassembler.</summary>
    public ulong SegmentsReceived;

    /// <summary>Fragments delivered whole.</summary>
    public ulong FragmentsCompleted;

    /// <summary>Segments dropped for any reason.</summary>
    public ulong SegmentsDiscarded;

    /// <summary>Per-reason discard counters, indexed by <see cref="DiscardReason"/>.</summary>
    public ulong[] Discards;

    /// <summary>Creates a zeroed set of counters.</summary>
    public TransportStats() => Discards = new ulong[6];

    /// <summary>Returns the count for one discard reason.</summary>
    public readonly ulong Discarded(DiscardReason reason)
    {
        var i = (int)reason;
        return Discards is not null && i < Discards.Length ? Discards[i] : 0;
    }
}

/// <summary>The outcome of feeding one segment.</summary>
internal readonly struct TransportResult
{
    /// <summary>
    /// The completed application fragment. It aliases the reassembler's buffer
    /// and is valid only until the next call to
    /// <see cref="Reassembler.Accept"/>. Meaningful only when
    /// <see cref="Complete"/> is set.
    /// </summary>
    public ReadOnlyMemory<byte> Fragment { get; init; }

    /// <summary>
    /// Reports whether a fragment was delivered. A completed fragment may
    /// legitimately be empty, so this flag rather than the fragment's length is
    /// what distinguishes "no fragment" from "empty fragment" — conflating them
    /// would swallow a valid zero-object response.
    /// </summary>
    public bool Complete { get; init; }

    /// <summary>
    /// What was dropped, if anything. A segment can both complete a fragment
    /// and report a discard: an unexpected FIR drops the partial fragment and
    /// starts a new one in the same call.
    /// </summary>
    public DiscardReason Discarded { get; init; }
}

/// <summary>Rebuilds application fragments from segments.</summary>
/// <remarks>
/// A reassembler is not safe for concurrent use; one belongs to one session.
/// </remarks>
internal sealed class Reassembler
{
    /// <summary>
    /// Caps a reassembled fragment. Zero means
    /// <see cref="TransportConstants.DefaultMaxFragment"/>.
    /// </summary>
    public int MaxFragment { get; set; }

    private byte[] _buf;
    private int _len;
    private byte _expect;
    private bool _assembly;
    private TransportStats _stats = new();

    /// <summary>
    /// Creates a reassembler with the given fragment cap. Pass zero for
    /// <see cref="TransportConstants.DefaultMaxFragment"/>.
    /// </summary>
    public Reassembler(int maxFragment = 0)
    {
        if (maxFragment <= 0)
        {
            maxFragment = TransportConstants.DefaultMaxFragment;
        }

        MaxFragment = maxFragment;
        _buf = new byte[maxFragment];
    }

    /// <summary>Returns a snapshot of the counters.</summary>
    public TransportStats Stats => _stats;

    /// <summary>Reports whether a fragment is partially assembled.</summary>
    public bool InProgress => _assembly;

    /// <summary>The octet count assembled so far.</summary>
    public int Buffered => _len;

    /// <summary>
    /// Abandons any fragment in progress. A session calls this when the link is
    /// re-established, because a fragment cannot span a connection.
    /// </summary>
    public void Reset()
    {
        _len = 0;
        _assembly = false;
    }

    private int EffectiveMaxFragment =>
        MaxFragment <= 0 ? TransportConstants.DefaultMaxFragment : MaxFragment;

    /// <summary>Feeds one transport segment, header octet included.</summary>
    /// <remarks>
    /// The returned fragment aliases the reassembler's internal buffer and is
    /// invalidated by the next call; copy it if it must outlive that.
    /// </remarks>
    public TransportResult Accept(ReadOnlySpan<byte> segment)
    {
        _stats.SegmentsReceived++;

        if (segment.Length < TransportConstants.HeaderSize)
        {
            return Discard(DiscardReason.EmptySegment);
        }

        var h = TransportHeader.Parse(segment[0]);
        var payload = segment[TransportConstants.HeaderSize..];

        if (h.Fir)
        {
            // A FIR while assembling means the peer restarted the fragment —
            // typically after its own timeout. The partial fragment is
            // unrecoverable, but the new one is perfectly good, so drop the
            // old, report it, and carry on rather than dropping both.
            var reported = DiscardReason.None;
            if (_assembly && _len > 0)
            {
                reported = DiscardReason.UnexpectedFir;
                _stats.SegmentsDiscarded++;
                _stats.Discards[(int)DiscardReason.UnexpectedFir]++;
            }

            _len = 0;
            _assembly = true;
            _expect = h.Seq;

            var res = Append(h, payload);
            if (res.Discarded == DiscardReason.None && reported != DiscardReason.None)
            {
                res = new TransportResult
                {
                    Fragment = res.Fragment,
                    Complete = res.Complete,
                    Discarded = reported,
                };
            }

            return res;
        }

        if (!_assembly)
        {
            // A continuation with nothing to continue. The fragment's opening
            // segment was lost, so every segment until the next FIR is useless.
            return Discard(DiscardReason.NoFir);
        }

        if (h.Seq != _expect)
        {
            // A gap or a duplicate. Either way the fragment is now unreliable:
            // silently stitching around the hole would deliver a fragment the
            // peer never sent.
            Reset();
            return Discard(DiscardReason.BadSequence);
        }

        return Append(h, payload);
    }

    /// <summary>
    /// Adds a validated segment's payload and completes the fragment if the
    /// segment carried FIN.
    /// </summary>
    private TransportResult Append(TransportHeader h, ReadOnlySpan<byte> payload)
    {
        if (_len + payload.Length > EffectiveMaxFragment)
        {
            Reset();
            return Discard(DiscardReason.Overflow);
        }

        if (_len + payload.Length > _buf.Length)
        {
            Array.Resize(ref _buf, Math.Max(_len + payload.Length, EffectiveMaxFragment));
        }

        payload.CopyTo(_buf.AsSpan(_len));
        _len += payload.Length;
        _expect = (byte)((h.Seq + 1) % TransportConstants.SeqModulus);

        if (!h.Fin)
        {
            return default;
        }

        _assembly = false;
        _stats.FragmentsCompleted++;
        return new TransportResult
        {
            Fragment = _buf.AsMemory(0, _len),
            Complete = true,
        };
    }

    private TransportResult Discard(DiscardReason reason)
    {
        _stats.SegmentsDiscarded++;
        _stats.Discards[(int)reason]++;
        return new TransportResult { Discarded = reason };
    }
}
