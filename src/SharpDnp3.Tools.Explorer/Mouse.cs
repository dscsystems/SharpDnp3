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

// The pointer never gets its own copy of any action.
//
// Every click resolves to a region from the layout and then either moves the
// cursor or presses the key the keyboard would have pressed. That is what keeps
// the two input methods from drifting: there is one implementation of "close
// this breaker", and the mouse is a second way of reaching it rather than a
// second version of it.

/// <summary>What the pointer did.</summary>
public enum MouseKind
{
    /// <summary>A button went down.</summary>
    Click,

    /// <summary>It came back up.</summary>
    Release,

    /// <summary>The wheel turned.</summary>
    Wheel,

    /// <summary>The pointer moved.</summary>
    Motion,
}

/// <summary>Which button.</summary>
public enum MouseButton
{
    /// <summary>None, which is what a bare motion reports.</summary>
    None,

    /// <summary>The left button.</summary>
    Left,

    /// <summary>The middle button.</summary>
    Middle,

    /// <summary>The right button.</summary>
    Right,

    /// <summary>The wheel, turned away from the operator.</summary>
    WheelUp,

    /// <summary>The wheel, turned towards them.</summary>
    WheelDown,
}

/// <summary>One pointer event, in terminal cells counted from zero.</summary>
public sealed record MouseMsg(int X, int Y, MouseButton Button, MouseKind Kind) : IMsg;

public sealed partial class Model
{
    /// <summary>
    /// How far one wheel notch scrolls. Three rows is the convention everywhere
    /// else, and a table that scrolls a whole page per notch is a table nobody
    /// can aim.
    /// </summary>
    public const int WheelStep = 3;

    /// <summary>Applies one pointer event.</summary>
    public Cmd? HandleMouse(MouseMsg e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!MouseEnabled)
        {
            return null;
        }

        var l = BuildLayout();
        if (!l.Ok)
        {
            return null;
        }

        switch (e.Kind)
        {
            case MouseKind.Wheel:
                return HandleWheel(e);
            case MouseKind.Motion:
                return HandleMotion(e, l);
            case MouseKind.Release:
                Dragging = false;
                return null;
            default:
                break;
        }

        // A prompt owns the pointer the same way it owns the keyboard. Routing a
        // click through HandleKey while one is open would type the button's key
        // into the prompt — clicking "Integrity" mid-filter would add an "i" to
        // the filter — and on the Points screen it would reach the control
        // dialog. So the click closes the prompt and stops there: the click that
        // dismisses is never also the click that acts.
        if (Prompt.Active)
        {
            Prompt = new PromptState();
            return null;
        }

        // The editor takes the pointer the way it takes the keyboard: a click
        // picks a field, the footer buttons still work, and a click outside is
        // left alone. Dismissing on a stray click would throw away a half-typed
        // address, which is the opposite of what a form is for.
        if (Form.Active)
        {
            if (!l.TryZoneAt(e.X, e.Y, out var fz))
            {
                return null;
            }

            if (fz.Kind == ZoneKind.Field && fz.N < Form.Fields.Count)
            {
                Form.Cursor = fz.N;
            }
            else if (fz.Kind == ZoneKind.Button && fz.N < l.Buttons.Count)
            {
                return HandleKey(l.Buttons[fz.N].Key);
            }

            return null;
        }

        // A dialog is modal for the pointer too: clicking its choices works, and
        // clicking anywhere else dismisses it rather than reaching the table
        // underneath.
        if (Modal.Kind != ModalKind.None)
        {
            if (l.TryZoneAt(e.X, e.Y, out var mz) &&
                mz.Kind == ZoneKind.Choice && mz.N < l.Choices.Count)
            {
                return HandleModalKey(l.Choices[mz.N].Key);
            }

            if (!l.Modal.Contains(e.X, e.Y))
            {
                Modal = new ModalState();
            }

            return null;
        }

        if (!l.TryZoneAt(e.X, e.Y, out var z))
        {
            return null;
        }

        switch (z.Kind)
        {
            case ZoneKind.Tab:
                SetScreen((Screen)z.N);
                break;

            case ZoneKind.Button:
                if (z.N < l.Buttons.Count)
                {
                    return HandleKey(l.Buttons[z.N].Key);
                }

                break;

            case ZoneKind.Column:
                if (z.N < l.Cols.Count)
                {
                    return SortByColumn(l.Cols[z.N].Key);
                }

                break;

            case ZoneKind.Scroll:
                Dragging = true;
                ScrollToTrack(e.Y, l);
                break;

            case ZoneKind.Rows:
                if (!l.TryRowAt(e.Y, out var row))
                {
                    return null;
                }

                return ClickRow(row, e.Button);

            case ZoneKind.Detail:
                // Clicking the inspector when nothing is selected is a request
                // to see it; clicking it again is how it goes away.
                if (e.Button == MouseButton.Right)
                {
                    Detail = false;
                }

                break;

            default:
                break;
        }

        return null;
    }

    private Cmd? HandleWheel(MouseMsg e)
    {
        // The wheel over the tab bar walks the tabs, which is what a browser
        // does and what people try first.
        if (e.Y <= Layout.RowTabs)
        {
            if (e.Button == MouseButton.WheelUp)
            {
                SetScreen((Screen)(((int)Screen + ScreenNames.Length - 1) % ScreenNames.Length));
            }
            else if (e.Button == MouseButton.WheelDown)
            {
                SetScreen((Screen)(((int)Screen + 1) % ScreenNames.Length));
            }

            return null;
        }

        if (!Screen.Scrolls())
        {
            return null;
        }

        if (e.Button == MouseButton.WheelUp)
        {
            ScrollBy(-WheelStep);
        }
        else if (e.Button == MouseButton.WheelDown)
        {
            ScrollBy(WheelStep);
        }

        return null;
    }

    private Cmd? HandleMotion(MouseMsg e, Layout l)
    {
        if (Dragging && e.Button == MouseButton.Left)
        {
            ScrollToTrack(e.Y, l);
            return null;
        }

        // Hover is only tracked for things that light up. Anything else would
        // repaint the screen for every pixel of travel and buy nothing.
        if (l.TryZoneAt(e.X, e.Y, out var z) &&
            z.Kind is ZoneKind.Tab or ZoneKind.Button or ZoneKind.Choice or ZoneKind.Column)
        {
            Hover = z;
        }
        else
        {
            Hover = default;
        }

        return null;
    }

    /// <summary>Maps a position on the scrollbar to a position in the list.</summary>
    private void ScrollToTrack(int y, Layout l)
    {
        if (l.Scroll.IsEmpty || l.Total <= l.Rows.H)
        {
            return;
        }

        var span = Math.Max(l.Scroll.H - 1, 1);
        var rel = Math.Clamp(y - l.Scroll.Y, 0, span);
        var off = rel * (l.Total - l.Rows.H) / span;

        if (Follow && Screen.Follows())
        {
            Follow = false;
        }

        _offset[(int)Screen] = off;
        _cursor[(int)Screen] =
            Math.Clamp(_cursor[(int)Screen], off, off + Math.Max(l.Rows.H - 1, 0));
    }

    /// <summary>Selects a row, and acts on it when it was already selected.</summary>
    /// <remarks>
    /// Selecting first and acting second is deliberate: on a screen that can
    /// trip a breaker, a single click must never be the whole gesture. Clicking
    /// a row that is already under the cursor is the second half of it, and it
    /// opens the control dialog rather than operating anything.
    /// </remarks>
    private Cmd? ClickRow(int row, MouseButton button)
    {
        var already = _cursor[(int)Screen] == row;

        if (Follow && Screen.Follows())
        {
            Follow = false;
        }

        _cursor[(int)Screen] = row;

        if (button == MouseButton.Right)
        {
            Detail = true;
        }
        else if (already && Screen == Screen.Points)
        {
            return ContextAction("enter");
        }

        return null;
    }

    /// <summary>
    /// Sorts by a clicked column, reversing when it is already the sort column.
    /// </summary>
    private Cmd? SortByColumn(SortKey key)
    {
        if (key == SortKey.None)
        {
            return null;
        }

        if (SortBy == key)
        {
            SortDesc = !SortDesc;
        }
        else
        {
            SortBy = key;
            SortDesc = false;
        }

        _cursor[(int)Screen] = 0;
        _offset[(int)Screen] = 0;
        return null;
    }
}
