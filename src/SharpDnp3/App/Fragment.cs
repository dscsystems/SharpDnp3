// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Globalization;
using System.Text;

namespace SharpDnp3.App;

/// <summary>A fully parsed application fragment.</summary>
/// <remarks>
/// Objects alias the buffer the fragment was parsed from and are invalidated
/// when that buffer is reused.
/// </remarks>
internal sealed class Fragment
{
    /// <summary>The fragment header.</summary>
    public AppHeader Header { get; init; }

    /// <summary>The decoded object headers, in wire order.</summary>
    public List<ObjectHeader> Objects { get; } = [];

    /// <summary>The octets the fragment was parsed from.</summary>
    public ReadOnlyMemory<byte> Raw { get; init; }

    /// <inheritdoc/>
    public override string ToString()
    {
        var b = new StringBuilder();
        b.Append(Header.ToString());
        foreach (var o in Objects)
        {
            b.Append("\n  ");
            b.Append(o.ToString());
        }

        return b.ToString();
    }
}

/// <summary>Parses application fragments.</summary>
internal static class FragmentParser
{
    /// <summary>Decodes a complete application fragment.</summary>
    /// <remarks>
    /// <paramref name="sizer"/> resolves object sizes; pass
    /// <see langword="null"/> to use <see cref="ObjectSizing.DefaultSizer"/>.
    /// The returned fragment aliases <paramref name="buf"/>.
    /// <para>
    /// Parsing stops at the first malformed object header and returns an error
    /// along with the headers decoded so far, because a decoder showing an
    /// operator the three headers it understood before the corruption is more
    /// useful than one showing nothing.
    /// </para>
    /// </remarks>
    public static AppParseStatus ParseFragment(
        IObjectSizer? sizer,
        ReadOnlyMemory<byte> buf,
        out Fragment fragment,
        out string? error)
    {
        sizer ??= ObjectSizing.DefaultSizer;
        error = null;

        var status = HeaderCodec.ParseHeader(buf.Span, out var header, out var n);
        if (status != AppParseStatus.Ok)
        {
            fragment = new Fragment { Raw = buf };
            error = status.ToDisplayString();
            return status;
        }

        fragment = new Fragment { Header = header, Raw = buf };
        var carriesData = header.Func.CarriesObjectData();

        for (var off = n; off < buf.Length;)
        {
            var objStatus = ObjectHeaderCodec.ParseObjectHeader(
                sizer, buf[off..], off, carriesData, out var oh, out var used);

            if (objStatus != AppParseStatus.Ok)
            {
                error = string.Format(
                    CultureInfo.InvariantCulture,
                    "object header at offset {0}: {1}",
                    off, objStatus.ToDisplayString());
                return objStatus;
            }

            if (used == 0)
            {
                // A header that consumes nothing would loop forever. No valid
                // encoding does this, but a parser must not depend on that.
                error = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: zero-length object header at offset {1}",
                    AppParseStatus.Truncated.ToDisplayString(), off);
                return AppParseStatus.Truncated;
            }

            fragment.Objects.Add(oh);
            off += used;
        }

        return AppParseStatus.Ok;
    }

    /// <summary>
    /// Decodes a complete application fragment, throwing on malformed input.
    /// </summary>
    public static Fragment ParseFragment(IObjectSizer? sizer, ReadOnlyMemory<byte> buf)
    {
        var status = ParseFragment(sizer, buf, out var fragment, out var error);
        return status == AppParseStatus.Ok ? fragment : throw status.ToException(error);
    }

    /// <summary>Decodes a fragment and confirms it is a request.</summary>
    public static Fragment ParseRequest(IObjectSizer? sizer, ReadOnlyMemory<byte> buf)
    {
        var f = ParseFragment(sizer, buf);
        return !f.Header.IsResponse
            ? f
            : throw new MalformedException(string.Format(
                CultureInfo.InvariantCulture,
                "app: expected a request, got {0}",
                f.Header.Func.ToDisplayString()));
    }

    /// <summary>Decodes a fragment and confirms it is a response.</summary>
    public static Fragment ParseResponse(IObjectSizer? sizer, ReadOnlyMemory<byte> buf)
    {
        var f = ParseFragment(sizer, buf);
        return f.Header.IsResponse
            ? f
            : throw new MalformedException(string.Format(
                CultureInfo.InvariantCulture,
                "app: expected a response, got {0}",
                f.Header.Func.ToDisplayString()));
    }
}

/// <summary>Assembles an application fragment.</summary>
/// <remarks>
/// It enforces the fragment size limit as headers are added, so a caller
/// discovers a fragment will not fit while it can still do something about it —
/// splitting the response across fragments — rather than after encoding.
/// </remarks>
internal sealed class FragmentBuilder
{
    private readonly List<byte> _buf;
    private readonly int _max;

    /// <summary>
    /// Guards against emitting object headers before the fragment header.
    /// </summary>
    private bool _headerWritten;

    /// <summary>
    /// Creates a builder capped at <paramref name="max"/> octets. Pass zero for
    /// <see cref="AppConstants.DefaultMaxFragment"/>.
    /// </summary>
    public FragmentBuilder(int max = 0)
    {
        if (max <= 0)
        {
            max = AppConstants.DefaultMaxFragment;
        }

        _max = max;
        _buf = new List<byte>(max);
    }

    /// <summary>Clears the builder for reuse, keeping its buffer.</summary>
    public void Reset()
    {
        _buf.Clear();
        _headerWritten = false;
    }

    /// <summary>The octets written so far.</summary>
    public int Length => _buf.Count;

    /// <summary>How many octets can still be written.</summary>
    public int Remaining => _max - _buf.Count;

    /// <summary>Returns the fragment built so far as a fresh array.</summary>
    public byte[] ToArray() => [.. _buf];

    /// <summary>
    /// Writes the fragment header. It must be called before any object header.
    /// </summary>
    public void SetHeader(AppHeader h)
    {
        if (_headerWritten)
        {
            throw new Dnp3Exception("app: fragment header already written");
        }

        if (h.Size > Remaining)
        {
            throw AppParseStatus.FragmentTooLarge.ToException();
        }

        HeaderCodec.AppendHeader(_buf, h);
        _headerWritten = true;
    }

    /// <summary>Appends an object header and its data.</summary>
    /// <returns>
    /// <see langword="false"/> when the object would overflow the fragment,
    /// leaving the builder unchanged so the caller can close this fragment and
    /// start the next.
    /// </returns>
    public bool TryAddObject(ObjectHeader h)
    {
        if (!_headerWritten)
        {
            throw new Dnp3Exception("app: object header written before the fragment header");
        }

        if (h.Size > Remaining)
        {
            return false;
        }

        ObjectHeaderCodec.AppendObjectHeader(_buf, h);
        return true;
    }

    /// <summary>Reports whether an object header would still fit.</summary>
    public bool Fits(ObjectHeader h) => h.Size <= Remaining;

    /// <summary>Appends raw pre-encoded octets, bypassing object framing.</summary>
    internal bool TryAddRaw(ReadOnlySpan<byte> raw)
    {
        if (raw.Length > Remaining)
        {
            return false;
        }

        _buf.AddRange(raw);
        return true;
    }
}

/// <summary>Short forms for building complete fragments.</summary>
internal static class FragmentFactory
{
    /// <summary>
    /// Builds a request carrying zero or more object headers.
    /// </summary>
    public static byte[] BuildRequest(AppControl control, FuncCode fc, params ObjectHeader[] objects)
    {
        var dst = new List<byte>(AppConstants.RequestHeaderSize + (objects.Length * 8));
        HeaderCodec.AppendHeader(dst, new AppHeader(control, fc, Iin.None));
        foreach (var o in objects)
        {
            ObjectHeaderCodec.AppendObjectHeader(dst, o);
        }

        return [.. dst];
    }

    /// <summary>
    /// Builds a response carrying zero or more object headers.
    /// </summary>
    public static byte[] BuildResponse(
        AppControl control,
        FuncCode fc,
        Iin iin,
        params ObjectHeader[] objects)
    {
        var dst = new List<byte>(AppConstants.ResponseHeaderSize + (objects.Length * 8));
        HeaderCodec.AppendHeader(dst, new AppHeader(control, fc, iin));
        foreach (var o in objects)
        {
            ObjectHeaderCodec.AppendObjectHeader(dst, o);
        }

        return [.. dst];
    }

    /// <summary>
    /// Builds the object header that asks for every object of a group and
    /// variation — qualifier 0x06.
    /// </summary>
    /// <remarks>
    /// Class polls are expressed this way: group 60 variation 1 for static
    /// data, variations 2 through 4 for the event classes.
    /// </remarks>
    public static ObjectHeader ReadAllObjects(byte group, byte variation) => new()
    {
        Group = group,
        Variation = variation,
        Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.AllObjects),
        Range = new ObjectRange { Spec = RangeSpec.AllObjects },
    };

    /// <summary>
    /// Builds the object header that asks for an inclusive index range,
    /// choosing the narrowest range encoding that fits.
    /// </summary>
    public static ObjectHeader ReadRange(byte group, byte variation, uint start, uint stop)
    {
        var spec = stop switch
        {
            <= 0xFF => RangeSpec.StartStop8,
            <= 0xFFFF => RangeSpec.StartStop16,
            _ => RangeSpec.StartStop32,
        };

        return new ObjectHeader
        {
            Group = group,
            Variation = variation,
            Qualifier = Qualifier.Make(IndexPrefix.None, spec),
            Range = new ObjectRange
            {
                Spec = spec,
                Start = start,
                Stop = stop,
                Count = stop - start + 1,
            },
        };
    }
}
