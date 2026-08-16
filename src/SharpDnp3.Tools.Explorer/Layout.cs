// Copyright (C) 2026 Ricardo Olsen / DSC Systems.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option)
// any later version. It is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details, in the LICENSE file at
// the root of this repository or at <https://www.gnu.org/licenses/>.

namespace SharpDnp3.Tools.Explorer;

// Geometry is computed once, in one place, and used by both the renderer and
// the mouse handler.
//
// That is the whole reason this file exists. A pointer-driven interface has to
// answer "what is under the cursor at (x, y)" with exactly the same arithmetic
// that put the thing there, and the usual way of getting that wrong is to let
// the view draw from one calculation and the click handler guess from another.
// Here the view and the hit test read the same object, so a region can never be
// clickable somewhere it is not drawn.

/// <summary>A rectangle of terminal cells.</summary>
public readonly record struct Rect(int X, int Y, int W, int H)
{
    /// <summary>Reports whether a point falls inside.</summary>
    public bool Contains(int x, int y) => x >= X && x < X + W && y >= Y && y < Y + H;

    /// <summary>Reports whether the rectangle has no area.</summary>
    public bool IsEmpty => W <= 0 || H <= 0;
}

/// <summary>The orders the points table can be put in.</summary>
public enum SortKey
{
    /// <summary>Not a sortable column.</summary>
    None = 0,

    /// <summary>By type and index, which is the natural order.</summary>
    Point,

    /// <summary>By value.</summary>
    Value,

    /// <summary>By quality, worst first.</summary>
    Quality,

    /// <summary>By how recently the point was updated.</summary>
    Age,

    /// <summary>By the outstation's own timestamp.</summary>
    Time,
}

/// <summary>Names a column's contents.</summary>
/// <remarks>
/// Rows are built against these rather than against positions, because a narrow
/// terminal drops columns: with positional cells, dropping TREND would slide the
/// quality of every point one column to the left and put a sparkline in the
/// column an operator reads for faults.
/// </remarks>
public enum ColId
{
    /// <summary>The point's identity, as "AI 3".</summary>
    Point,

    /// <summary>The value.</summary>
    Value,

    /// <summary>The recent history, as a sparkline.</summary>
    Trend,

    /// <summary>The quality flags.</summary>
    Quality,

    /// <summary>How long ago the value arrived.</summary>
    Age,

    /// <summary>The outstation's timestamp.</summary>
    Stamp,

    /// <summary>When this tool received the row.</summary>
    Received,

    /// <summary>The event class.</summary>
    Class,

    /// <summary>The group and variation it was encoded as.</summary>
    Source,

    /// <summary>A log line's level.</summary>
    Level,

    /// <summary>A log line's text.</summary>
    Message,
}

/// <summary>One table column, resolved to a concrete width by <see cref="Layout.LayoutColumns"/>.</summary>
public sealed record Column
{
    /// <summary>What the column holds.</summary>
    public ColId Id { get; init; }

    /// <summary>Its heading.</summary>
    public string Title { get; init; } = "";

    /// <summary>The sort it offers, if any.</summary>
    public SortKey Key { get; init; }

    /// <summary>Its fixed width, ignored when <see cref="Flex"/> is set.</summary>
    public int Width { get; init; }

    /// <summary>Its smallest acceptable width when <see cref="Flex"/> is set.</summary>
    public int Min { get; init; }

    /// <summary>Whether it absorbs the columns left over.</summary>
    public bool Flex { get; init; }

    /// <summary>Whether its content is right aligned.</summary>
    public bool Right { get; init; }

    /// <summary>
    /// Orders the columns for dropping when the terminal is narrow: the highest
    /// number goes first, and zero never goes.
    /// </summary>
    public int Prio { get; init; }
}

/// <summary>Classifies a clickable region.</summary>
public enum ZoneKind
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>A tab; <see cref="Zone.N"/> is the screen.</summary>
    Tab,

    /// <summary>The table body; the row is derived from y.</summary>
    Rows,

    /// <summary>A column heading; <see cref="Zone.N"/> indexes the columns.</summary>
    Column,

    /// <summary>A footer button; <see cref="Zone.N"/> indexes the buttons.</summary>
    Button,

    /// <summary>The scrollbar track.</summary>
    Scroll,

    /// <summary>The point inspector.</summary>
    Detail,

    /// <summary>A dialog choice; <see cref="Zone.N"/> indexes them.</summary>
    Choice,

    /// <summary>A form field; <see cref="Zone.N"/> indexes them.</summary>
    Field,
}

/// <summary>One clickable region of the frame.</summary>
public readonly record struct Zone(Rect Rect, ZoneKind Kind, int N);

/// <summary>A footer action.</summary>
/// <remarks>
/// Clicking one presses its key, so the pointer and the keyboard can never
/// drift apart: there is one implementation of every action and the mouse is
/// just another way to reach it.
/// </remarks>
public readonly record struct Button(string Label, string Key, bool On = false);

/// <summary>The resolved geometry of one frame.</summary>
public sealed class Layout
{
    // Screen furniture is a fixed number of rows, and the table gets what is
    // left. Above the body: the title bar with the link state and the clock, the
    // tab bar, and a rule. Below it: a rule, the toolbar, and the hint line.
    // Nothing reflows as data arrives, because an operator reaching for a
    // control should not have to find it again every time a value updates.

    /// <summary>The row the tab bar is drawn on.</summary>
    public const int RowTabs = 1;

    /// <summary>How many rows the header takes.</summary>
    public const int ChromeTop = 3;

    /// <summary>How many rows the footer takes.</summary>
    public const int ChromeBottom = 3;

    /// <summary>The narrowest terminal this can lay out honestly.</summary>
    public const int MinWidth = 60;

    /// <summary>The shortest terminal this can lay out honestly.</summary>
    public const int MinHeight = 12;

    /// <summary>The width of the point inspector.</summary>
    public const int DetailWidth = 34;

    /// <summary>The narrowest terminal the inspector may appear on.</summary>
    public const int DetailMinCol = 96;

    /// <summary>
    /// The standing notice that controls are not being confirmed.
    /// </summary>
    /// <remarks>
    /// It lives here rather than in the view because the toolbar has to reserve
    /// its width before the buttons are laid out. A warning that is dropped
    /// whenever the toolbar happens to be full is a warning that disappears
    /// exactly when the screen is busiest.
    /// </remarks>
    public const string NoConfirmWarning = "! controls send immediately ";

    /// <summary>The terminal width this frame was laid out for.</summary>
    public int W { get; set; }

    /// <summary>The terminal height this frame was laid out for.</summary>
    public int H { get; set; }

    /// <summary>Whether the terminal is big enough to draw.</summary>
    public bool Ok { get; set; }

    /// <summary>The whole content area, between the rules.</summary>
    public Rect Body { get; set; }

    /// <summary>The table within the body, excluding any detail panel.</summary>
    public Rect Table { get; set; }

    /// <summary>Just the data rows of the table.</summary>
    public Rect Rows { get; set; }

    /// <summary>The point inspector, empty when closed.</summary>
    public Rect Detail { get; set; }

    /// <summary>The scrollbar track, empty when everything fits.</summary>
    public Rect Scroll { get; set; }

    /// <summary>The open dialog, empty when none.</summary>
    public Rect Modal { get; set; }

    /// <summary>The open editor, empty when none.</summary>
    public Rect Form { get; set; }

    /// <summary>How many fields the editor has room to draw.</summary>
    public int FormRows { get; set; }

    /// <summary>The first field it draws, so a short terminal scrolls the list.</summary>
    public int FormFirst { get; set; }

    /// <summary>This frame's columns, at their resolved widths.</summary>
    public List<Column> Cols { get; } = [];

    /// <summary>This frame's footer buttons.</summary>
    public List<Button> Buttons { get; set; } = [];

    /// <summary>The open dialog's choices.</summary>
    public List<ModalChoice> Choices { get; set; } = [];

    /// <summary>Everything clickable, innermost first.</summary>
    public List<Zone> Zones { get; } = [];

    /// <summary>How many rows the current screen holds, after clamping.</summary>
    public int Total { get; set; }

    /// <summary>The first row drawn, after clamping.</summary>
    public int Offset { get; set; }

    /// <summary>Returns the region under a point.</summary>
    public bool TryZoneAt(int x, int y, out Zone zone)
    {
        foreach (var z in Zones)
        {
            if (z.Rect.Contains(x, y))
            {
                zone = z;
                return true;
            }
        }

        zone = default;
        return false;
    }

    /// <summary>Maps a screen row to an index in the current list.</summary>
    public bool TryRowAt(int y, out int row)
    {
        row = 0;
        if (!Rows.Contains(Rows.X, y))
        {
            return false;
        }

        var i = Offset + (y - Rows.Y);
        if (i < 0 || i >= Total)
        {
            return false;
        }

        row = i;
        return true;
    }

    /// <summary>
    /// Resolves column widths for the space available, dropping the least
    /// important columns rather than squeezing every column into illegibility.
    /// </summary>
    public static List<Column> LayoutColumns(IReadOnlyList<Column> cols, int width)
    {
        ArgumentNullException.ThrowIfNull(cols);

        var outp = new List<Column>(cols);

        static int Used(List<Column> cs)
        {
            var total = Math.Max(cs.Count - 1, 0); // one space between columns
            foreach (var c in cs)
            {
                total += c.Flex ? c.Min : c.Width;
            }

            return total;
        }

        while (Used(outp) > width)
        {
            // Drop the least important remaining column.
            var worst = 0;
            var at = -1;
            for (var i = 0; i < outp.Count; i++)
            {
                if (outp[i].Prio > worst)
                {
                    worst = outp[i].Prio;
                    at = i;
                }
            }

            if (at < 0)
            {
                break; // everything left is essential; the flex column absorbs it
            }

            outp.RemoveAt(at);
        }

        var slack = width - Used(outp);
        for (var i = 0; i < outp.Count; i++)
        {
            if (outp[i].Flex)
            {
                outp[i] = outp[i] with { Width = Math.Max(outp[i].Min + slack, 1) };
                slack = 0;
            }
        }

        return outp;
    }

    /// <summary>Centres a dialog in the body, sized to its content.</summary>
    /// <remarks>
    /// The dialog's rows are load-bearing rather than decorative: the choice
    /// list is drawn flush to the bottom of the box, and the hit test finds it
    /// by counting up from there, so the two must agree on the size.
    /// </remarks>
    public static Rect ModalRect(ModalState d, Rect body)
    {
        ArgumentNullException.ThrowIfNull(d);

        var w = Theme.Width(d.Title) + 6;
        foreach (var l in d.Lines)
        {
            w = Math.Max(w, Theme.Width(l) + 6);
        }

        foreach (var c in d.Choices)
        {
            w = Math.Max(w, Theme.Width(c.Label) + 14);
        }

        w = Math.Min(Math.Max(w, 36), Math.Max(body.W - 4, 12));

        // Two rows of frame, the message, a blank row, then one row per choice.
        var h = Math.Min(d.Lines.Count + d.Choices.Count + 3, body.H);
        return new Rect(
            body.X + Math.Max((body.W - w) / 2, 0),
            body.Y + Math.Max((body.H - h) / 2, 0),
            w, h);
    }

    /// <summary>
    /// Centres the editor in the body and works out how much of the field list
    /// fits, scrolling that list to keep the focused field on screen.
    /// </summary>
    /// <remarks>
    /// It returns the frame, how many fields can be drawn, and which one is
    /// first, because the renderer and the hit test both need all three and
    /// neither may work them out for itself.
    /// </remarks>
    public static (Rect Frame, int Rows, int First) FormRect(FormState f, Rect body)
    {
        ArgumentNullException.ThrowIfNull(f);

        var w = 40;
        foreach (var fld in f.Fields)
        {
            w = Math.Max(w, Theme.Width(fld.Label) + 6);
            w = Math.Max(w, Theme.Width(fld.Value) + 8);
            w = Math.Max(w, Theme.Width(fld.Hint) + 8);
        }

        if (!string.IsNullOrEmpty(f.Error))
        {
            w = Math.Max(w, Theme.Width(f.Error) + 6);
        }

        w = Math.Min(w, Math.Max(body.W - 4, 12));

        // Two rows of frame, two of footer — the last error and the key hint —
        // and two rows for every field: its name and the box holding its value.
        var h = Math.Min((f.Fields.Count * 2) + 4, body.H);

        var rows = Math.Min(Math.Max((h - 4) / 2, 1), f.Fields.Count);
        if (f.Cursor < f.Offset)
        {
            f.Offset = f.Cursor;
        }

        if (f.Cursor >= f.Offset + rows)
        {
            f.Offset = f.Cursor - rows + 1;
        }

        f.Offset = Math.Clamp(f.Offset, 0, Math.Max(f.Fields.Count - rows, 0));

        var frame = new Rect(
            body.X + Math.Max((body.W - w) / 2, 0),
            body.Y + Math.Max((body.H - h) / 2, 0),
            w, h);
        return (frame, rows, f.Offset);
    }
}

public sealed partial class Model
{
    /// <summary>
    /// Computes this frame's geometry, and clamps the scroll position to it.
    /// </summary>
    /// <remarks>
    /// It is called by the view before drawing and by the mouse handler before
    /// hit testing; both get the same answer because both run this code.
    /// </remarks>
    public Layout BuildLayout()
    {
        var l = new Layout { W = Width, H = Height };
        if (Width < Layout.MinWidth || Height < Layout.MinHeight)
        {
            return l;
        }

        l.Ok = true;

        var bodyH = Height - Layout.ChromeTop - Layout.ChromeBottom;
        l.Body = new Rect(0, Layout.ChromeTop, Width, bodyH);

        // Tabs, laid out left to right in the order they are drawn.
        var x = 0;
        for (var i = 0; i < ScreenNames.Length; i++)
        {
            var w = Theme.Width(View.TabLabel(i, ScreenNames[i]));
            if (x + w > Width)
            {
                break;
            }

            l.Zones.Add(new Zone(new Rect(x, Layout.RowTabs, w, 1), ZoneKind.Tab, i));
            x += w;
        }

        l.Total = RowCount();
        l.Offset = _offset[(int)Screen];

        // The editor owns the body the same way a dialog does, and for the same
        // reason: it is a set of values being changed together, and a click that
        // reached the table behind it would act on a device the operator is in
        // the middle of navigating away from.
        if (Form.Active)
        {
            var (frame, rows, first) = Layout.FormRect(Form, l.Body);
            l.Form = frame;
            l.FormRows = rows;
            l.FormFirst = first;

            for (var i = 0; i < l.FormRows; i++)
            {
                l.Zones.Add(new Zone(
                    new Rect(l.Form.X + 2, l.Form.Y + 1 + (i * 2), l.Form.W - 4, 2),
                    ZoneKind.Field, l.FormFirst + i));
            }

            l.Buttons = FooterButtons();
            LayoutButtons(l, ToolbarWidth(), Height);
            return l;
        }

        // A dialog owns the body while it is open: nothing behind it is
        // clickable, which is the point of a modal.
        if (Modal.Kind != ModalKind.None)
        {
            l.Choices = Modal.Choices;
            l.Modal = Layout.ModalRect(Modal, l.Body);
            for (var i = 0; i < l.Choices.Count; i++)
            {
                l.Zones.Add(new Zone(
                    new Rect(
                        l.Modal.X + 2,
                        l.Modal.Y + l.Modal.H - 1 - l.Choices.Count + i,
                        l.Modal.W - 4, 1),
                    ZoneKind.Choice, i));
            }

            l.Buttons = FooterButtons();
            LayoutButtons(l, ToolbarWidth(), Height);
            return l;
        }

        l.Table = l.Body;
        if (Screen == Screen.Points && Detail && Width >= Layout.DetailMinCol)
        {
            l.Table = l.Table with { W = Width - Layout.DetailWidth - 1 };
            l.Detail = new Rect(l.Table.W + 1, l.Body.Y, Layout.DetailWidth, bodyH);
            l.Zones.Add(new Zone(l.Detail, ZoneKind.Detail, 0));
        }

        if (Screen.IsTable())
        {
            // One row of the table is its column header; the rest are data.
            l.Rows = new Rect(l.Table.X, l.Table.Y + 1, l.Table.W, Math.Max(l.Table.H - 1, 0));

            var tableW = l.Table.W - 2; // one column of margin either side
            if (l.Total > l.Rows.H)
            {
                tableW--; // make room for the scrollbar
                l.Scroll = new Rect(l.Table.X + l.Table.W - 1, l.Rows.Y, 1, l.Rows.H);
                l.Zones.Add(new Zone(l.Scroll, ZoneKind.Scroll, 0));
            }

            l.Cols.AddRange(Layout.LayoutColumns(View.ColumnsFor(Screen), tableW));

            var cx = l.Table.X + 1;
            for (var i = 0; i < l.Cols.Count; i++)
            {
                if (l.Cols[i].Key != SortKey.None)
                {
                    l.Zones.Add(new Zone(
                        new Rect(cx, l.Table.Y, l.Cols[i].Width, 1), ZoneKind.Column, i));
                }

                cx += l.Cols[i].Width + 1;
            }

            l.Zones.Add(new Zone(l.Rows, ZoneKind.Rows, 0));
        }

        if (Screen == Screen.Help)
        {
            // The reference is not a list, but on a short terminal it is longer
            // than the body, and a reference you cannot reach the end of is not
            // a reference.
            l.Rows = l.Body;
        }

        ClampScroll(l.Total, l.Rows.H);
        l.Offset = _offset[(int)Screen];

        l.Buttons = FooterButtons();
        LayoutButtons(l, ToolbarWidth(), Height);
        return l;
    }

    /// <summary>
    /// How much of the footer the buttons may use, which is all of it unless
    /// the unconfirmed-controls warning is claiming the right-hand end.
    /// </summary>
    public int ToolbarWidth() =>
        Confirm ? Width : Width - Theme.Width(Layout.NoConfirmWarning) - 1;

    /// <summary>Places the footer toolbar and registers its click targets.</summary>
    private static void LayoutButtons(Layout l, int w, int h)
    {
        var x = 1;
        for (var i = 0; i < l.Buttons.Count; i++)
        {
            var bw = Theme.Width(View.ButtonLabel(l.Buttons[i]));
            if (x + bw > w)
            {
                l.Buttons = l.Buttons[..i];
                break;
            }

            l.Zones.Add(new Zone(new Rect(x, h - 2, bw, 1), ZoneKind.Button, i));
            x += bw + 1;
        }
    }

    /// <summary>
    /// Keeps the cursor inside the list and the window around the cursor, after
    /// anything that could have moved either.
    /// </summary>
    public void ClampScroll(int total, int visible)
    {
        var cur = _cursor[(int)Screen];
        var off = _offset[(int)Screen];

        cur = Math.Clamp(cur, 0, Math.Max(total - 1, 0));
        if (Follow && Screen.Follows())
        {
            cur = Math.Max(total - 1, 0);
        }

        if (visible <= 0 || total <= visible)
        {
            off = 0;
        }
        else
        {
            off = Math.Clamp(off, 0, total - visible);
            if (cur < off)
            {
                off = cur;
            }

            if (cur >= off + visible)
            {
                off = cur - visible + 1;
            }
        }

        _cursor[(int)Screen] = cur;
        _offset[(int)Screen] = off;
    }
}
