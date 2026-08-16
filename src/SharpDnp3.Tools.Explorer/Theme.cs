// Copyright (C) 2026 Ricardo Olsen / DSC Systems.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option)
// any later version. It is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details, in the LICENSE file at
// the root of this repository or at <https://www.gnu.org/licenses/>.

using System.Globalization;
using System.Text;

namespace SharpDnp3.Tools.Explorer;

/// <summary>One text style, rendered as an ANSI sequence around its content.</summary>
/// <remarks>
/// The palette stays inside the terminal's own sixteen colours, so the tool
/// inherits whatever theme the operator already trusts instead of imposing one
/// — a control room screen that fights the rest of the desktop is a screen
/// people stop reading. Colour carries meaning and nothing else: quality is
/// what an operator scans for, so quality gets the colour and the furniture
/// stays grey.
/// </remarks>
public readonly record struct Style
{
    /// <summary>The 8-colour foreground index, or -1 for the terminal default.</summary>
    public int Foreground { get; init; }

    /// <summary>Whether the text is drawn bold.</summary>
    public bool Bold { get; init; }

    /// <summary>Whether the text is drawn dim.</summary>
    public bool Faint { get; init; }

    /// <summary>Whether foreground and background are swapped.</summary>
    public bool Reverse { get; init; }

    /// <summary>The style that changes nothing.</summary>
    public static Style None => new() { Foreground = -1 };

    /// <summary>Returns this style with a foreground colour.</summary>
    public Style WithForeground(int color) => this with { Foreground = color };

    /// <summary>Returns this style drawn bold.</summary>
    public Style WithBold() => this with { Bold = true };

    /// <summary>Returns this style drawn dim.</summary>
    public Style WithFaint() => this with { Faint = true };

    /// <summary>Returns this style with the colours swapped.</summary>
    public Style WithReverse() => this with { Reverse = true };

    /// <summary>Reports whether the style would draw anything differently.</summary>
    public bool IsPlain => Foreground < 0 && !Bold && !Faint && !Reverse;

    /// <summary>Wraps <paramref name="text"/> in this style.</summary>
    public string Render(string text)
    {
        if (IsPlain || string.IsNullOrEmpty(text))
        {
            return text;
        }

        var sgr = new StringBuilder("\u001b[");
        var first = true;

        void Add(string code)
        {
            if (!first)
            {
                sgr.Append(';');
            }

            sgr.Append(code);
            first = false;
        }

        if (Bold)
        {
            Add("1");
        }

        if (Faint)
        {
            Add("2");
        }

        if (Reverse)
        {
            Add("7");
        }

        if (Foreground >= 0)
        {
            Add(Foreground < 8
                ? (30 + Foreground).ToString(CultureInfo.InvariantCulture)
                : (90 + Foreground - 8).ToString(CultureInfo.InvariantCulture));
        }

        sgr.Append('m').Append(text).Append("\u001b[0m");
        return sgr.ToString();
    }
}

/// <summary>The tool's palette and the text fitting every cell goes through.</summary>
public static class Theme
{
    /// <summary>The escape byte every terminal sequence starts with.</summary>
    public const char Esc = '\u001b';

    // cyan: structure and selection.
    private const int Accent = 6;
    private const int Good = 2;
    private const int Warn = 3;
    private const int Bad = 1;
    private const int Event = 5;
    private const int Muted = 8;

    /// <summary>The program name and other headline text.</summary>
    public static Style Title { get; } = Style.None.WithBold().WithForeground(Accent);

    /// <summary>Furniture: rules, labels, anything that is not the data.</summary>
    public static Style Dim { get; } = Style.None.WithForeground(Muted);

    /// <summary>Emphasis without colour.</summary>
    public static Style Strong { get; } = Style.None.WithBold();

    /// <summary>A table's column headings.</summary>
    public static Style ColHead { get; } = Style.None.WithBold().WithForeground(Accent);

    /// <summary>The row under the cursor.</summary>
    public static Style Selected { get; } = Style.None.WithReverse();

    /// <summary>The tab being shown.</summary>
    public static Style TabOn { get; } = Style.None.WithBold().WithReverse();

    /// <summary>The tabs that are not.</summary>
    public static Style TabOff { get; } = Style.None.WithFaint();

    /// <summary>Healthy.</summary>
    public static Style Ok { get; } = Style.None.WithForeground(Good);

    /// <summary>Worth looking at.</summary>
    public static Style Warning { get; } = Style.None.WithForeground(Warn);

    /// <summary>Wrong.</summary>
    public static Style Error { get; } = Style.None.WithForeground(Bad);

    /// <summary>A row that has just changed.</summary>
    public static Style EventStyle { get; } = Style.None.WithForeground(Event);

    /// <summary>A key an operator can press.</summary>
    public static Style Key { get; } = Style.None.WithBold().WithForeground(Accent);

    /// <summary>A value that has not been refreshed recently enough to trust.</summary>
    public static Style Stale { get; } = Style.None.WithFaint();

    /// <summary>Maps a log level to how loudly it should be drawn.</summary>
    public static Style ForLevel(string level) => level switch
    {
        "error" => Error,
        "warn" => Warning,
        "ok" => Ok,
        _ => Style.None,
    };

    // ---------- text fitting ----------
    //
    // Every cell in this interface is drawn at an exact column width. Styles are
    // applied to already-padded text, never inside it, because measuring a
    // string that has escape sequences in the middle of it is how tables come
    // out ragged.

    /// <summary>Measures a string in display columns, ignoring any styling.</summary>
    public static int Width(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return 0;
        }

        var width = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == Esc)
            {
                // Skip the escape sequence: a CSI runs to its final byte, which
                // is the first in the range @ to ~ after the bracket itself.
                i++;
                if (i < s.Length && s[i] == '[')
                {
                    i++;
                    while (i < s.Length && !char.IsBetween(s[i], '@', '~'))
                    {
                        i++;
                    }
                }

                continue;
            }

            if (char.IsHighSurrogate(s[i]))
            {
                i++;
            }

            width++;
        }

        return width;
    }

    /// <summary>Fits <paramref name="s"/> into <paramref name="w"/> columns, marking any loss.</summary>
    public static string Truncate(string? s, int w)
    {
        s ??= "";
        if (w <= 0)
        {
            return "";
        }

        if (Width(s) <= w)
        {
            return s;
        }

        if (w == 1)
        {
            return "…";
        }

        // Escape sequences are copied through without being counted: they take
        // no columns, and counting them is how a styled line gets cut a dozen
        // characters short of where the eye says it should be.
        var kept = new StringBuilder();
        var used = 0;
        var styled = false;

        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == Esc)
            {
                var start = i;
                i++;
                if (i < s.Length && s[i] == '[')
                {
                    i++;
                    while (i < s.Length && !char.IsBetween(s[i], '@', '~'))
                    {
                        i++;
                    }
                }

                kept.Append(s, start, Math.Min(i, s.Length - 1) - start + 1);
                styled = true;
                continue;
            }

            if (used + 1 > w - 1)
            {
                break;
            }

            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length)
            {
                kept.Append(s[i]).Append(s[i + 1]);
                i++;
            }
            else
            {
                kept.Append(s[i]);
            }

            used++;
        }

        kept.Append('…');

        // A style opened before the cut has to be closed, or it bleeds into
        // whatever the next column draws.
        if (styled)
        {
            kept.Append("\u001b[0m");
        }

        return kept.ToString();
    }

    /// <summary>Renders <paramref name="s"/> as exactly <paramref name="w"/> columns.</summary>
    public static string Cell(string? s, int w, bool right = false)
    {
        var text = Truncate(s, w);
        var padding = w - Width(text);
        if (padding <= 0)
        {
            return text;
        }

        return right ? new string(' ', padding) + text : text + new string(' ', padding);
    }

    /// <summary>
    /// Extends <paramref name="s"/> to <paramref name="w"/> columns without
    /// truncating it, for lines already known to fit.
    /// </summary>
    public static string Pad(string? s, int w)
    {
        s ??= "";
        var n = w - Width(s);
        return n > 0 ? s + new string(' ', n) : s;
    }

    /// <summary>Forces <paramref name="s"/> to exactly <paramref name="w"/> columns.</summary>
    public static string Fit(string? s, int w) => Width(s) > w ? Truncate(s, w) : Pad(s, w);

    /// <summary>Repeats <paramref name="s"/> <paramref name="n"/> times.</summary>
    public static string Repeat(string s, int n) => n <= 0 ? "" : string.Concat(Enumerable.Repeat(s, n));

    // ---------- drawing ----------

    /// <summary>
    /// Draws a titled frame of exactly <paramref name="w"/> columns and
    /// <paramref name="h"/> rows around the given content lines.
    /// </summary>
    /// <remarks>
    /// Drawn by hand rather than with a border helper because the title belongs
    /// in the top rule: a panel that names itself in the frame costs no content
    /// row, and on a twelve-row terminal every row is contested.
    /// </remarks>
    public static List<string> Box(string title, int w, int h, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (w < 4 || h < 2)
        {
            return Clip(lines, h, w);
        }

        var outp = new List<string>(h);

        var head = "╭─ " + title + " ";
        if (Width(head) > w - 1)
        {
            head = Truncate(head, w - 1);
        }

        outp.Add(Dim.Render("╭─ ") + ColHead.Render(title) + " " +
            Dim.Render(Repeat("─", w - Width(head) - 1) + "╮"));

        var inner = w - 4; // one space of padding either side of the frame
        for (var i = 0; i < h - 2; i++)
        {
            var content = i < lines.Count ? lines[i] : "";
            outp.Add(Dim.Render("│ ") + Fit(content, inner) + Dim.Render(" │"));
        }

        outp.Add(Dim.Render("╰" + Repeat("─", w - 2) + "╯"));
        return outp;
    }

    /// <summary>
    /// Forces a block of lines to exactly <paramref name="h"/> rows of
    /// <paramref name="w"/> columns.
    /// </summary>
    public static List<string> Clip(IReadOnlyList<string> lines, int h, int w)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var outp = new List<string>(Math.Max(h, 0));
        for (var i = 0; i < h; i++)
        {
            outp.Add(i < lines.Count ? Fit(lines[i], w) : Repeat(" ", w));
        }

        return outp;
    }

    /// <summary>Places blocks side by side with a single column of gutter.</summary>
    public static List<string> JoinColumns(IReadOnlyList<IReadOnlyList<string>> blocks, int h)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var outp = new List<string>(Math.Max(h, 0));
        for (var row = 0; row < h; row++)
        {
            var b = new StringBuilder();
            for (var i = 0; i < blocks.Count; i++)
            {
                if (i > 0)
                {
                    b.Append(' ');
                }

                if (row < blocks[i].Count)
                {
                    b.Append(blocks[i][row]);
                }
            }

            outp.Add(b.ToString());
        }

        return outp;
    }

    // An eight-level bar, low to high.
    private const string SparkRunes = "▁▂▃▄▅▆▇█";

    /// <summary>Renders recent history as a single row of blocks.</summary>
    /// <remarks>
    /// It is scaled to the window it shows rather than to the point's
    /// engineering range, because the question it answers is "is this moving,
    /// and which way", not "what is the value" — the value column already says
    /// that.
    /// </remarks>
    public static string Sparkline(IReadOnlyList<double> values, int w)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (w <= 0 || values.Count == 0)
        {
            return "";
        }

        var from = Math.Max(values.Count - w, 0);
        var lo = values[from];
        var hi = values[from];
        for (var i = from; i < values.Count; i++)
        {
            lo = Math.Min(lo, values[i]);
            hi = Math.Max(hi, values[i]);
        }

        var span = hi - lo;
        var b = new StringBuilder();
        for (var i = from; i < values.Count; i++)
        {
            if (span <= 1e-12)
            {
                // A flat trace must still read as a trace, not as an empty cell.
                b.Append(SparkRunes[3]);
                continue;
            }

            var idx = (int)((values[i] - lo) / span * (SparkRunes.Length - 1));
            b.Append(SparkRunes[Math.Clamp(idx, 0, SparkRunes.Length - 1)]);
        }

        return b.ToString();
    }

    /// <summary>Returns the character for one row of a scrollbar track.</summary>
    public static string ScrollbarRune(int row, int height, int offset, int total)
    {
        if (total <= height || height <= 0)
        {
            return " ";
        }

        // The thumb is at least one row tall, and its position reflects how far
        // down the list the window sits.
        var thumb = Math.Max(height * height / total, 1);
        var top = offset * (height - thumb) / Math.Max(total - height, 1);
        return row >= top && row < top + thumb ? "█" : "│";
    }
}
