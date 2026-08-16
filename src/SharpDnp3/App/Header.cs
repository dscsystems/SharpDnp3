// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Globalization;

namespace SharpDnp3.App;

/// <summary>Fragment header sizes and the application sequence space.</summary>
public static class AppConstants
{
    /// <summary>The application control octet plus the function code.</summary>
    public const int RequestHeaderSize = 2;

    /// <summary>Adds the two internal indication octets.</summary>
    public const int ResponseHeaderSize = 4;

    /// <summary>
    /// The application sequence space. Four bits, distinct from the transport
    /// function's six.
    /// </summary>
    public const int SeqModulus = 16;

    /// <summary>
    /// The standard's default maximum application fragment size.
    /// </summary>
    public const int DefaultMaxFragment = 2048;

    // ---- Application control octet bit masks ----
    internal const byte FirBit = 0x80;
    internal const byte FinBit = 0x40;
    internal const byte ConBit = 0x20;
    internal const byte UnsBit = 0x10;
    internal const byte SeqMask = 0x0F;
}

/// <summary>The application control octet.</summary>
/// <remarks>
/// <code>
/// bit 7  FIR  first fragment of a response series
/// bit 6  FIN  final fragment of a response series
/// bit 5  CON  the sender requires an application-layer confirmation
/// bit 4  UNS  this fragment belongs to the unsolicited sequence space
/// bits 3-0    sequence number
/// </code>
/// </remarks>
/// <param name="Fir">First fragment of a response series.</param>
/// <param name="Fin">Final fragment of a response series.</param>
/// <param name="Con">The sender requires an application-layer confirmation.</param>
/// <param name="Uns">The fragment belongs to the unsolicited sequence space.</param>
/// <param name="Seq">The sequence number, 0..15.</param>
public readonly record struct AppControl(bool Fir, bool Fin, bool Con, bool Uns, byte Seq)
{
    /// <summary>Decodes an application control octet.</summary>
    public static AppControl Parse(byte b) => new(
        Fir: (b & AppConstants.FirBit) != 0,
        Fin: (b & AppConstants.FinBit) != 0,
        Con: (b & AppConstants.ConBit) != 0,
        Uns: (b & AppConstants.UnsBit) != 0,
        Seq: (byte)(b & AppConstants.SeqMask));

    /// <summary>Encodes the application control octet.</summary>
    public byte ToByte()
    {
        byte b = 0;
        if (Fir)
        {
            b |= AppConstants.FirBit;
        }

        if (Fin)
        {
            b |= AppConstants.FinBit;
        }

        if (Con)
        {
            b |= AppConstants.ConBit;
        }

        if (Uns)
        {
            b |= AppConstants.UnsBit;
        }

        return (byte)(b | (Seq & AppConstants.SeqMask));
    }

    /// <summary>
    /// Reports whether the fragment is both the first and the last of its
    /// series, which is the common case.
    /// </summary>
    public bool Single => Fir && Fin;

    /// <inheritdoc/>
    public override string ToString()
    {
        var s = string.Format(CultureInfo.InvariantCulture, "seq={0:D2}", Seq);
        if (Fir)
        {
            s += " FIR";
        }

        if (Fin)
        {
            s += " FIN";
        }

        if (Con)
        {
            s += " CON";
        }

        if (Uns)
        {
            s += " UNS";
        }

        return s;
    }
}

/// <summary>A decoded application fragment header.</summary>
/// <remarks>
/// <see cref="Iin"/> is meaningful only when <see cref="Func"/> is a response
/// code; for requests it is zero and <see cref="IsResponse"/> reports
/// <see langword="false"/>.
/// </remarks>
/// <param name="Control">The application control octet.</param>
/// <param name="Func">The function code.</param>
/// <param name="Iin">The internal indications, on responses only.</param>
public readonly record struct AppHeader(AppControl Control, FuncCode Func, Iin Iin)
{
    /// <summary>Reports whether the fragment carries an IIN field.</summary>
    public bool IsResponse => Func.IsResponse();

    /// <summary>The encoded size of the header.</summary>
    public int Size => IsResponse ? AppConstants.ResponseHeaderSize : AppConstants.RequestHeaderSize;

    /// <inheritdoc/>
    public override string ToString() => IsResponse
        ? string.Format(CultureInfo.InvariantCulture, "{0} {1} iin={2}", Func.ToDisplayString(), Control, Iin)
        : string.Format(CultureInfo.InvariantCulture, "{0} {1}", Func.ToDisplayString(), Control);
}

/// <summary>Why parsing a fragment or an object header failed.</summary>
public enum AppParseStatus
{
    /// <summary>Parsed cleanly.</summary>
    Ok = 0,

    /// <summary>The fragment is too short to hold its header.</summary>
    ShortFragment,

    /// <summary>
    /// An object header or its data ran past the end of the fragment.
    /// </summary>
    Truncated,

    /// <summary>A qualifier octet used a reserved encoding.</summary>
    BadQualifier,

    /// <summary>
    /// A range field was internally inconsistent, such as a stop index below
    /// its start.
    /// </summary>
    BadRange,

    /// <summary>
    /// The object's size could not be resolved, so the fragment cannot be
    /// walked past this header.
    /// </summary>
    UnknownObject,

    /// <summary>The fragment exceeded the configured maximum.</summary>
    FragmentTooLarge,
}

/// <summary>Naming and exception mapping for <see cref="AppParseStatus"/>.</summary>
public static class AppParseStatusExtensions
{
    /// <summary>Renders the status as the equivalent Go sentinel error text.</summary>
    public static string ToDisplayString(this AppParseStatus status) => status switch
    {
        AppParseStatus.Ok => "app: ok",
        AppParseStatus.ShortFragment => "app: fragment shorter than its header",
        AppParseStatus.Truncated => "app: truncated object data",
        AppParseStatus.BadQualifier => "app: reserved qualifier encoding",
        AppParseStatus.BadRange => "app: invalid range",
        AppParseStatus.UnknownObject => "app: unknown object size",
        AppParseStatus.FragmentTooLarge => "app: fragment exceeds maximum size",
        _ => "app: unknown",
    };

    /// <summary>Wraps the status as the exception the public API surfaces.</summary>
    public static MalformedException ToException(this AppParseStatus status, string? detail = null) =>
        new(detail is null
            ? status.ToDisplayString()
            : string.Format(CultureInfo.InvariantCulture, "{0}: {1}", status.ToDisplayString(), detail));
}

/// <summary>Encodes and decodes application fragment headers.</summary>
internal static class HeaderCodec
{
    /// <summary>
    /// Decodes the fragment header at the front of <paramref name="buf"/> and
    /// reports the number of octets it occupied.
    /// </summary>
    public static AppParseStatus ParseHeader(
        ReadOnlySpan<byte> buf,
        out AppHeader header,
        out int consumed)
    {
        header = default;
        consumed = 0;

        if (buf.Length < AppConstants.RequestHeaderSize)
        {
            return AppParseStatus.ShortFragment;
        }

        var control = AppControl.Parse(buf[0]);
        var func = (FuncCode)buf[1];

        if (!func.IsResponse())
        {
            header = new AppHeader(control, func, Iin.None);
            consumed = AppConstants.RequestHeaderSize;
            return AppParseStatus.Ok;
        }

        if (buf.Length < AppConstants.ResponseHeaderSize)
        {
            return AppParseStatus.ShortFragment;
        }

        header = new AppHeader(control, func, Iin.Parse(buf[2], buf[3]));
        consumed = AppConstants.ResponseHeaderSize;
        return AppParseStatus.Ok;
    }

    /// <summary>Appends the encoded header to <paramref name="dst"/>.</summary>
    public static void AppendHeader(List<byte> dst, AppHeader header)
    {
        dst.Add(header.Control.ToByte());
        dst.Add((byte)header.Func);
        if (header.IsResponse)
        {
            var (iin1, iin2) = header.Iin.Octets();
            dst.Add(iin1);
            dst.Add(iin2);
        }
    }
}
