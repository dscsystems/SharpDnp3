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

// The screen is drawn as a fixed frame: a header, a tab bar, a body that takes
// whatever is left, and a footer that is always in the same place. Nothing
// reflows as data arrives, because an operator reaching for a control should not
// have to find it again every time a value updates.

/// <summary>One drawn row of a table.</summary>
/// <remarks>
/// Cells are keyed by which column they belong to, with optional per-cell colour
/// and an optional style for the whole line.
/// </remarks>
public sealed class TableRow
{
    /// <summary>The text of each cell, by column.</summary>
    public Dictionary<ColId, string> Cells { get; } = [];

    /// <summary>Colours individual cells; a missing entry leaves them plain.</summary>
    public Dictionary<ColId, Style> CellStyle { get; } = [];

    /// <summary>
    /// Styles the whole row and overrides the cell colours, which is what a
    /// selected or failed row needs: a row that is half reversed and half red is
    /// unreadable.
    /// </summary>
    public Style Line { get; set; }

    /// <summary>Whether <see cref="Line"/> is in force.</summary>
    public bool LineSet { get; set; }
}

/// <summary>The parts of the interface that do not need the model.</summary>
public static class View
{
    /// <summary>Renders one tab's label.</summary>
    public static string TabLabel(int i, string name) => string.Format(
        CultureInfo.InvariantCulture, " {0} {1} ", i + 1, name);

    /// <summary>Renders a footer button, unstyled, for measuring.</summary>
    public static string ButtonLabel(Button b) => "[" + b.Key + " " + b.Label + "]";

    /// <summary>The column set for a screen, before widths are resolved.</summary>
    public static List<Column> ColumnsFor(Screen s) => s switch
    {
        Screen.Points =>
        [
            new Column { Id = ColId.Point, Title = "POINT", Key = SortKey.Point, Width = 7 },
            new Column
            {
                Id = ColId.Value, Title = "VALUE", Key = SortKey.Value, Width = 14, Right = true,
            },
            new Column { Id = ColId.Trend, Title = "TREND", Width = 12, Prio = 3 },
            new Column
            {
                Id = ColId.Quality, Title = "QUALITY", Key = SortKey.Quality, Min = 12, Flex = true,
            },
            new Column
            {
                Id = ColId.Age, Title = "AGE", Key = SortKey.Age, Width = 7, Right = true, Prio = 2,
            },
            new Column
            {
                Id = ColId.Stamp, Title = "TIMESTAMP", Key = SortKey.Time, Width = 12, Prio = 1,
            },
        ],

        Screen.Events =>
        [
            new Column { Id = ColId.Received, Title = "RECEIVED", Width = 12 },
            new Column { Id = ColId.Point, Title = "POINT", Width = 7 },
            new Column { Id = ColId.Value, Title = "VALUE", Width = 14, Right = true },
            new Column { Id = ColId.Class, Title = "CL", Width = 2, Prio = 3 },
            new Column { Id = ColId.Quality, Title = "QUALITY", Min = 10, Flex = true },
            new Column { Id = ColId.Source, Title = "SOURCE", Width = 7, Prio = 4 },
            new Column { Id = ColId.Stamp, Title = "TIMESTAMP", Width = 12, Prio = 1 },
        ],

        _ =>
        [
            new Column { Id = ColId.Received, Title = "TIME", Width = 12 },
            new Column { Id = ColId.Level, Title = "LEVEL", Width = 5 },
            new Column { Id = ColId.Message, Title = "MESSAGE", Min = 20, Flex = true },
        ],
    };

    /// <summary>
    /// Renders the quality flags, with the state bit dropped for binary points
    /// because the value column already says ON or OFF.
    /// </summary>
    public static string QualityText(Flags f, PointType t)
    {
        if (t is PointType.Binary or PointType.BinaryOutputStatus)
        {
            f = f.Clear(Flags.StateBit);
        }

        return f.StringFor(t);
    }

    /// <summary>Names an event class, or says there was none.</summary>
    public static string ClassText(Class c) => c == 0 ? "—" : c.ToDisplayString();

    /// <summary>
    /// Names a point type the way an engineer says it, rather than the way the
    /// type is spelled.
    /// </summary>
    /// <remarks>
    /// A panel column is not wide enough for "BinaryOutputStatus" and truncating
    /// it loses the word that matters.
    /// </remarks>
    public static string TypeLabel(PointType t) => t switch
    {
        PointType.Binary => "binary inputs",
        PointType.DoubleBitBinary => "double-bit",
        PointType.Counter => "counters",
        PointType.FrozenCounter => "frozen ctrs",
        PointType.Analog => "analog inputs",
        PointType.BinaryOutputStatus => "binary outputs",
        PointType.AnalogOutputStatus => "analog outputs",
        PointType.OctetString => "octet strings",
        _ => t.ToString(),
    };

    /// <summary>
    /// Renders how long ago something happened, in as few characters as it can
    /// be said honestly.
    /// </summary>
    public static string FmtAge(TimeSpan d)
    {
        if (d < TimeSpan.Zero)
        {
            return "0s";
        }

        if (d < TimeSpan.FromSeconds(10))
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0}s", d.TotalSeconds);
        }

        if (d < TimeSpan.FromMinutes(1))
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}s", (int)d.TotalSeconds);
        }

        if (d < TimeSpan.FromHours(1))
        {
            return string.Format(
                CultureInfo.InvariantCulture, "{0}m{1:00}s", (int)d.TotalMinutes, d.Seconds);
        }

        return string.Format(
            CultureInfo.InvariantCulture, "{0}h{1:00}m", (int)d.TotalHours, d.Minutes);
    }

    /// <summary>Renders an elapsed time as a clock.</summary>
    public static string FmtDuration(TimeSpan d)
    {
        var h = (int)d.TotalHours;
        var m = d.Minutes;
        var s = d.Seconds;
        return h > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, s)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", m, s);
    }

    /// <summary>
    /// Names what the current screen is a list of, because "1 rows" is the sort
    /// of thing that makes an operator distrust the rest of the numbers.
    /// </summary>
    public static string RowNoun(Screen s) => s switch
    {
        Screen.Points => "point",
        Screen.Events => "event",
        _ => "line",
    };

    /// <summary>Counts a noun.</summary>
    public static string Plural(int n, string noun) => n == 1
        ? "1 " + noun
        : string.Format(CultureInfo.InvariantCulture, "{0} {1}s", n, noun);

    /// <summary>Shortens a word to the space a cell has for it.</summary>
    public static string Short(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Length > 4 ? s[..4] : s;
    }

    /// <summary>Colours the internal indications by what they mean.</summary>
    public static string IinStyled(string s)
    {
        if (string.IsNullOrEmpty(s) || s == "—")
        {
            return Theme.Dim.Render("—");
        }

        if (s.Contains("OVERFLOW", StringComparison.Ordinal) ||
            s.Contains("TROUBLE", StringComparison.Ordinal))
        {
            return Theme.Error.Render(s);
        }

        if (s.Contains("RESTART", StringComparison.Ordinal) ||
            s.Contains("NEED_TIME", StringComparison.Ordinal))
        {
            return Theme.Warning.Render(s);
        }

        return s;
    }

    /// <summary>Renders one "name   value" row of a panel.</summary>
    public static string Field(string name, string value) =>
        Theme.Dim.Render(Theme.Cell(name, 16)) + value;

    /// <summary>Renders one "name  value" row of the inspector.</summary>
    public static string DetailField(string name, string value) =>
        Theme.Dim.Render(Theme.Cell(name, 11)) + value;
}

public sealed partial class Model
{
    private const string ClockFormat = "HH:mm:ss";
    private const string StampFormat = "HH:mm:ss.fff";

    /// <summary>Draws the whole frame.</summary>
    public string Render()
    {
        if (Quitting)
        {
            return "";
        }

        if (Width == 0)
        {
            return "starting…";
        }

        var l = BuildLayout();
        if (!l.Ok)
        {
            // A terminal too small to lay out honestly gets told so, rather than
            // a mangled table that looks like corrupted data.
            return string.Format(
                CultureInfo.InvariantCulture,
                "terminal is {0}x{1}; this needs at least {2}x{3}",
                Width, Height, Layout.MinWidth, Layout.MinHeight);
        }

        var lines = new List<string>(Height)
        {
            ViewHeader(),
            ViewTabs(l),
            Theme.Dim.Render(Theme.Repeat("─", Width)),
        };

        lines.AddRange(ViewBody(l));
        lines.Add(Theme.Dim.Render(Theme.Repeat("─", Width)));
        lines.Add(ViewToolbar(l));
        lines.Add(ViewHint());

        return string.Join('\n', lines);
    }

    // ---------- frame ----------

    private string ViewHeader()
    {
        var state = Connected
            ? Theme.Ok.Render("● connected")
            : Theme.Error.Render("○ disconnected");

        var left = Theme.Title.Render("dnp3-explorer") + "  " +
            Theme.Dim.Render(Conn.Target) + "  " + state;

        // The right-hand side gives up its least important part first, so a
        // narrow terminal loses the uptime rather than the clock.
        var clock = Clock().ToString(ClockFormat, CultureInfo.InvariantCulture);
        var candidates = new List<string> { clock, RateText() + "  " + clock };
        if (LinkSince != default)
        {
            candidates.Add(
                "up " + View.FmtDuration(Clock() - LinkSince) + "  " + RateText() + "  " + clock);
        }

        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            var gap = Width - Theme.Width(left) - Theme.Width(candidates[i]) - 1;
            if (gap >= 1)
            {
                return left + Theme.Repeat(" ", gap + 1) + Theme.Dim.Render(candidates[i]);
            }
        }

        return Theme.Fit(left, Width);
    }

    private string ViewTabs(Layout l)
    {
        var b = new StringBuilder();
        for (var i = 0; i < ScreenNames.Length; i++)
        {
            var label = View.TabLabel(i, ScreenNames[i]);
            if ((Screen)i == Screen)
            {
                b.Append(Theme.TabOn.Render(label));
            }
            else if (Hover.Kind == ZoneKind.Tab && Hover.N == i)
            {
                b.Append(Theme.Key.Render(label));
            }
            else
            {
                b.Append(Theme.TabOff.Render(label));
            }
        }

        // The right of the tab bar is where the view's own state lives: what is
        // being filtered out, and how much of the list is on screen. Without it
        // a filtered table is indistinguishable from a device that stopped
        // talking.
        var status = new List<string>();
        if (Filter.Length > 0)
        {
            status.Add("filter \"" + Filter + "\"");
        }

        if (Screen.IsTable())
        {
            status.Add(View.Plural(l.Total, View.RowNoun(Screen)));
        }

        if (Screen.Follows() && Follow)
        {
            status.Add("following");
        }

        if (status.Count == 0)
        {
            return Theme.Fit(b.ToString(), Width);
        }

        var right = Theme.Dim.Render(string.Join(" · ", status) + " ");
        var gap = Width - Theme.Width(b.ToString()) - Theme.Width(right);
        return gap < 1
            ? Theme.Fit(b.ToString(), Width)
            : b.ToString() + Theme.Repeat(" ", gap) + right;
    }

    private List<string> ViewBody(Layout l)
    {
        if (Form.Active)
        {
            return Theme.Clip(ViewForm(l), l.Body.H, l.Body.W);
        }

        if (Modal.Kind != ModalKind.None)
        {
            return Theme.Clip(ViewModal(l), l.Body.H, l.Body.W);
        }

        var body = Screen switch
        {
            Screen.Overview => ViewOverview(l.Body),
            Screen.Points => ViewPoints(l),
            Screen.Events => ViewEvents(l),
            Screen.Log => ViewLog(l),
            Screen.Help => ViewHelp(l),
            _ => [],
        };

        if (l.Detail.IsEmpty)
        {
            return Theme.Clip(body, l.Body.H, l.Body.W);
        }

        // The inspector is a second column of the body, joined row by row so
        // that neither side can push the other out of the frame.
        return Theme.JoinColumns(
            [
                Theme.Clip(body, l.Body.H, l.Table.W),
                Theme.Box("Inspector", l.Detail.W, l.Detail.H, ViewDetail(l.Detail.W - 4)),
            ],
            l.Body.H);
    }

    /// <summary>
    /// Draws the clickable actions, and the standing warning that controls are
    /// not being confirmed.
    /// </summary>
    /// <remarks>
    /// That warning has no key and cannot be dismissed. Running with -no-confirm
    /// means the next c or o goes to the plant with nothing in between, and the
    /// one moment an operator needs to be told that is the moment they have
    /// stopped expecting a dialog to appear.
    /// </remarks>
    private string ViewToolbar(Layout l)
    {
        var b = new StringBuilder(" ");
        for (var i = 0; i < l.Buttons.Count; i++)
        {
            if (i > 0)
            {
                b.Append(' ');
            }

            var hovered = Hover.Kind == ZoneKind.Button && Hover.N == i;
            b.Append(RenderButton(l.Buttons[i], hovered));
        }

        if (Confirm)
        {
            return Theme.Fit(b.ToString(), Width);
        }

        var warn = Theme.Warning.Render(Layout.NoConfirmWarning);
        var gap = Width - Theme.Width(b.ToString()) - Theme.Width(warn);
        return gap < 1
            ? Theme.Fit(b.ToString(), Width)
            : b.ToString() + Theme.Repeat(" ", gap) + warn;
    }

    private static string RenderButton(Button b, bool hovered)
    {
        if (hovered)
        {
            return Theme.Selected.Render(View.ButtonLabel(b));
        }

        var key = Theme.Key.Render(b.Key);
        if (b.On)
        {
            // An engaged mode is drawn as engaged, so the toolbar reports state
            // rather than only offering actions.
            return Theme.Dim.Render("[") + key + " " + Theme.Ok.Render(b.Label) +
                Theme.Dim.Render("]");
        }

        return Theme.Dim.Render("[") + key + " " + b.Label + Theme.Dim.Render("]");
    }

    /// <summary>The action set for the current screen.</summary>
    public List<Button> FooterButtons()
    {
        if (Form.Active)
        {
            return [new Button("Connect", "enter"), new Button("Cancel", "esc")];
        }

        if (Modal.Kind != ModalKind.None)
        {
            return [new Button("Cancel", "esc")];
        }

        return Screen switch
        {
            Screen.Points =>
            [
                new Button("Integrity", "i"),
                new Button("Poll", "p"),
                new Button("Filter", "/"),
                new Button("Inspect", "d", Detail),
                new Button("Close", "c"),
                new Button("Open", "o"),
                new Button(SboLabel(Sbo), "S"),
                new Button("Export", "e"),
                new Button("Help", "?"),
            ],

            Screen.Events =>
            [
                new Button("Poll", "p"),
                new Button("Follow", "f", Follow),
                new Button("Filter", "/"),
                new Button("Clear", "x"),
                new Button("Export", "e"),
                new Button("Help", "?"),
            ],

            Screen.Log =>
            [
                new Button("Follow", "f", Follow),
                new Button("Filter", "/"),
                new Button("Clear", "x"),
                new Button("Export", "e"),
                new Button("Help", "?"),
            ],

            Screen.Help =>
            [
                new Button("Points", "2"),
                new Button("Quit", "q"),
            ],

            _ =>
            [
                new Button("Integrity", "i"),
                new Button("Poll", "p"),
                new Button("Set clock", "t"),
                new Button("Unsol on", "u"),
                new Button("Restart", "R"),
                new Button("Connection", "C"),
                new Button("Help", "?"),
                new Button("Quit", "q"),
            ],
        };
    }

    /// <summary>
    /// Says whether anything stands between the keystroke and the plant.
    /// </summary>
    /// <remarks>
    /// It gets a row of its own rather than a clause appended to the control
    /// mode, because in a narrow column that clause is the half that gets
    /// truncated.
    /// </remarks>
    private string ConfirmText() =>
        Confirm ? "asks first" : Theme.Warning.Render("none — sends immediately");

    private static string SboLabel(bool sbo) => sbo ? "SBO" : "Direct";

    /// <summary>
    /// The bottom line: a prompt while one is open, then whatever the last
    /// action had to say, then the keys for this screen.
    /// </summary>
    private string ViewHint()
    {
        if (Prompt.Active)
        {
            return Theme.Fit(
                Theme.TabOn.Render(" " + Prompt.Label + " › " + Prompt.Input + "▏"), Width);
        }

        if (Form.Active)
        {
            return Theme.Fit(
                Theme.Dim.Render(
                    " ↑↓ or tab move between fields · enter connects · esc cancels"), Width);
        }

        if (Modal.Kind != ModalKind.None)
        {
            return Theme.Fit(
                Theme.Dim.Render(
                    " press a key from the list, click a line, or esc to cancel"), Width);
        }

        if (Toast.Active)
        {
            return Theme.Fit(" " + Theme.ForLevel(Toast.Level).Render(Toast.Text), Width);
        }

        var hint = Screen switch
        {
            Screen.Points =>
                "↑↓ move · enter act · d inspect · / filter · < > r sort · b deadband · " +
                "s range scan · ? help",
            Screen.Events => "↑↓ move · f follow · x clear · / filter · p poll · ? help",
            Screen.Log => "↑↓ move · f follow · x clear · / filter · ? help",
            Screen.Help => "↑↓ scroll · tab or 1-5 change screen · q quit",
            _ => "i integrity · p poll · t clock · u/U unsolicited · R restart · " +
                "click anything · ? help",
        };

        return Theme.Fit(Theme.Dim.Render(" " + hint), Width);
    }

    // ---------- overview ----------

    private List<string> ViewOverview(Rect b)
    {
        var session = (Title: "Session", Lines: OverviewSession());
        var traffic = (Title: "Traffic", Lines: OverviewTraffic());
        var database = (Title: "Database", Lines: OverviewDatabase());
        var activity = (Title: "Recent activity", Lines: OverviewActivity((b.H / 2) - 2));

        // Two columns when the terminal can hold them without squeezing the
        // numbers; one when it cannot.
        if (b.W >= 92)
        {
            var colW = (b.W - 1) / 2;
            var left = StackPanels([session, traffic], colW, b.H);
            var right = StackPanels([database, activity], b.W - 1 - colW, b.H);
            return Theme.JoinColumns([left, right], b.H);
        }

        return StackPanels([session, database, traffic, activity], b.W, b.H);
    }

    /// <summary>
    /// Gives each panel its natural height and lets the last one absorb whatever
    /// is left over, so the column always fills the body exactly.
    /// </summary>
    private static List<string> StackPanels(
        IReadOnlyList<(string Title, List<string> Lines)> panels, int w, int h)
    {
        if (panels.Count == 0)
        {
            return [];
        }

        var heights = new int[panels.Count];
        var total = 0;
        for (var i = 0; i < panels.Count; i++)
        {
            heights[i] = panels[i].Lines.Count + 2;
            total += heights[i];
        }

        // Shrink from the last panel backwards when there is not enough room, so
        // the first panel — the one that says whether the link is up — survives.
        for (var i = panels.Count - 1; i >= 0 && total > h; i--)
        {
            var shrink = Math.Min(total - h, Math.Max(heights[i] - 3, 0));
            heights[i] -= shrink;
            total -= shrink;
        }

        if (total < h)
        {
            heights[^1] += h - total;
        }

        var outp = new List<string>(h);
        for (var i = 0; i < panels.Count; i++)
        {
            if (heights[i] < 3)
            {
                continue;
            }

            outp.AddRange(Theme.Box(panels[i].Title, w, heights[i], panels[i].Lines));
        }

        return Theme.Clip(outp, h, w);
    }

    private List<string> OverviewSession()
    {
        var state = Connected ? Theme.Ok.Render(Status) : Theme.Error.Render(Status);
        var up = LinkSince == default ? "—" : View.FmtDuration(Clock() - LinkSince);

        var lines = new List<string>
        {
            View.Field("outstation", Conn.Target),
            View.Field("transport", Conn.Transport),
            View.Field("state", state),
            View.Field("link up for", up),
            View.Field("indications", View.IinStyled(Iin)),
            View.Field("controls", ControlMode(Sbo)),
            View.Field("confirmation", ConfirmText()),
        };

        if (LastError.Length > 0)
        {
            lines.Add(View.Field("last error", Theme.Error.Render(LastError)));
        }

        return lines;
    }

    private List<string> OverviewTraffic()
    {
        var st = Stats;
        var lines = new List<string>
        {
            View.Field("tasks", string.Format(
                CultureInfo.InvariantCulture,
                "{0} run · {1} · {2}",
                st.TasksRun,
                Theme.Ok.Render(string.Format(
                    CultureInfo.InvariantCulture, "{0} ok", st.TasksSucceeded)),
                FailedText(st.TasksFailed))),
            View.Field("timeouts", CountText(st.ResponseTimeouts)),
            View.Field("fragments", string.Format(
                CultureInfo.InvariantCulture,
                "{0} received · {1} unsolicited", st.FragmentsRx, st.Unsolicited)),
            View.Field("restarts seen", CountText(st.RestartsSeen)),
            View.Field("connections", st.Connections.ToString(CultureInfo.InvariantCulture)),
            View.Field("update rate", RateText()),
        };

        var dropped = Conn.Dropped;
        if (dropped > 0)
        {
            lines.Add(View.Field("dropped", Theme.Warning.Render(string.Format(
                CultureInfo.InvariantCulture, "{0} (UI fell behind)", dropped))));
        }

        if (RateHist.Count > 1)
        {
            lines.Add("");
            lines.Add(Theme.Dim.Render("measurements per second, last minute"));
            lines.Add(Theme.Dim.Render(Theme.Sparkline(RateHist, 60)));
        }

        return lines;
    }

    private List<string> OverviewDatabase()
    {
        if (Points.Count == 0)
        {
            // Split over two lines so it survives the narrow column: an empty
            // tool that cannot fit the sentence telling you how to fill it is an
            // empty tool that looks broken.
            return
            [
                Theme.Dim.Render("nothing polled yet"),
                Theme.Dim.Render("press i for an integrity poll"),
            ];
        }

        var counts = new Dictionary<PointType, int>();
        var bad = 0;
        var forced = 0;
        var stale = 0;
        var now = Clock();

        foreach (var (k, p) in Points)
        {
            counts[k.Type] = counts.GetValueOrDefault(k.Type) + 1;

            if (!p.Flags.Has(Flags.Online) || p.Flags.HasAny(Flags.CommLost))
            {
                bad++;
            }
            else if (p.Flags.HasAny(Flags.RemoteForced | Flags.LocalForced))
            {
                forced++;
            }

            if (p.Stale(now, StaleAge))
            {
                stale++;
            }
        }

        var lines = new List<string>();
        PointType[] order =
        [
            PointType.Binary, PointType.DoubleBitBinary, PointType.Counter,
            PointType.FrozenCounter, PointType.Analog,
            PointType.BinaryOutputStatus, PointType.AnalogOutputStatus,
            PointType.OctetString,
        ];

        foreach (var t in order)
        {
            if (counts.TryGetValue(t, out var n) && n > 0)
            {
                lines.Add(View.Field(
                    View.TypeLabel(t), n.ToString(CultureInfo.InvariantCulture)));
            }
        }

        var health = Theme.Ok.Render(string.Format(
            CultureInfo.InvariantCulture, "{0} good", Points.Count - bad - forced));

        if (bad > 0)
        {
            health += " · " + Theme.Error.Render(string.Format(
                CultureInfo.InvariantCulture, "{0} bad", bad));
        }

        if (forced > 0)
        {
            health += " · " + Theme.Warning.Render(string.Format(
                CultureInfo.InvariantCulture, "{0} forced", forced));
        }

        if (stale > 0)
        {
            health += " · " + Theme.Dim.Render(string.Format(
                CultureInfo.InvariantCulture, "{0} stale", stale));
        }

        lines.Add(View.Field("quality", health));
        lines.Add(View.Field(
            "events held", Events.Count.ToString(CultureInfo.InvariantCulture)));
        return lines;
    }

    /// <summary>
    /// Shows the newest events, so the first screen answers "is anything
    /// happening" without changing tabs.
    /// </summary>
    private List<string> OverviewActivity(int n)
    {
        n = Math.Max(n, 1);
        if (Events.Count == 0)
        {
            return
            [
                Theme.Dim.Render("no events yet — press p to poll classes 1, 2 and 3"),
            ];
        }

        var start = Math.Max(Events.Count - n, 0);
        var lines = new List<string>(n);
        for (var i = Events.Count - 1; i >= start; i--)
        {
            var e = Events[i];
            lines.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0}  {1}  {2}",
                Theme.Dim.Render(e.At.ToString(StampFormat, CultureInfo.InvariantCulture)),
                Theme.Cell(PointLabel(e.Key), 7),
                e.Value));
        }

        return lines;
    }

    private static string FailedText(ulong n)
    {
        var s = string.Format(CultureInfo.InvariantCulture, "{0} failed", n);
        return n > 0 ? Theme.Error.Render(s) : s;
    }

    private static string CountText(ulong n)
    {
        var s = n.ToString(CultureInfo.InvariantCulture);
        return n > 0 ? Theme.Warning.Render(s) : s;
    }

    private string RateText()
    {
        var r = EventRate();
        return r == 0 ? "0/s" : string.Format(CultureInfo.InvariantCulture, "{0:0.0}/s", r);
    }

    // ---------- tables ----------

    /// <summary>Draws the column header and the visible slice of rows.</summary>
    private List<string> RenderTable(Layout l, string empty, Func<int, TableRow> row)
    {
        var outp = new List<string>(Math.Max(l.Table.H, 1)) { RenderColumnHeader(l) };

        if (l.Total == 0)
        {
            outp.Add("");
            outp.Add("  " + Theme.Dim.Render(empty));
            return outp;
        }

        var cursor = _cursor[(int)Screen];
        for (var i = 0; i < l.Rows.H; i++)
        {
            var idx = l.Offset + i;
            var line = idx < l.Total ? RenderRow(l.Cols, row(idx), idx == cursor) : "";

            if (!l.Scroll.IsEmpty)
            {
                var bar = Theme.ScrollbarRune(i, l.Rows.H, l.Offset, l.Total);
                line = Theme.Fit(" " + line, l.Table.W - 1) + Theme.Dim.Render(bar);
            }
            else
            {
                line = " " + line;
            }

            outp.Add(line);
        }

        return outp;
    }

    private string RenderColumnHeader(Layout l)
    {
        var b = new StringBuilder(" ");
        for (var i = 0; i < l.Cols.Count; i++)
        {
            if (i > 0)
            {
                b.Append(' ');
            }

            var c = l.Cols[i];
            var title = c.Title;
            if (c.Key != SortKey.None && c.Key == SortBy && Screen == Screen.Points)
            {
                title += SortDesc ? " ▼" : " ▲";
            }

            var text = Theme.Cell(title, c.Width, c.Right);
            b.Append(Hover.Kind == ZoneKind.Column && Hover.N == i && c.Key != SortKey.None
                ? Theme.Selected.Render(text)
                : Theme.ColHead.Render(text));
        }

        return Theme.Fit(b.ToString(), l.Table.W);
    }

    private static string RenderRow(List<Column> cols, TableRow r, bool selected)
    {
        var b = new StringBuilder();
        for (var i = 0; i < cols.Count; i++)
        {
            if (i > 0)
            {
                b.Append(' ');
            }

            var text = Theme.Cell(r.Cells.GetValueOrDefault(cols[i].Id, ""),
                cols[i].Width, cols[i].Right);

            if (!selected && !r.LineSet && r.CellStyle.TryGetValue(cols[i].Id, out var st))
            {
                text = st.Render(text);
            }

            b.Append(text);
        }

        if (selected)
        {
            return Theme.Selected.Render(b.ToString());
        }

        return r.LineSet ? r.Line.Render(b.ToString()) : b.ToString();
    }

    private List<string> ViewPoints(Layout l)
    {
        var rows = VisiblePoints();
        var empty = Filter.Length > 0
            ? $"no points match \"{Filter}\" — press esc to clear the filter"
            : "nothing polled yet — press i for an integrity poll";
        var now = Clock();

        return RenderTable(l, empty, i =>
        {
            var p = rows[i];
            var stamp = p.Time.IsValid
                ? p.Time.Time.ToLocalTime().ToString(StampFormat, CultureInfo.InvariantCulture)
                : "—";
            var trend = p.Hist.Count > 1 ? Theme.Sparkline(p.Hist, 12) : "";

            var r = new TableRow();
            r.Cells[ColId.Point] = PointLabel(p.Key);
            r.Cells[ColId.Value] = p.Value;
            r.Cells[ColId.Trend] = trend;
            r.Cells[ColId.Quality] = View.QualityText(p.Flags, p.Key.Type);
            r.Cells[ColId.Age] = View.FmtAge(now - p.Updated);
            r.Cells[ColId.Stamp] = stamp;
            r.CellStyle[ColId.Trend] = Theme.Dim;
            r.CellStyle[ColId.Stamp] = Theme.Dim;

            if (!p.Flags.Has(Flags.Online))
            {
                r.Line = Theme.Error;
                r.LineSet = true;
            }
            else if (p.Flags.HasAny(Flags.Restart | Flags.CommLost))
            {
                r.Line = Theme.Warning;
                r.LineSet = true;
            }
            else if (p.Stale(now, StaleAge))
            {
                // Nothing is wrong with the value; it is just old, and the row
                // says so by fading rather than by shouting.
                r.Line = Theme.Stale;
                r.LineSet = true;
            }
            else if (p.IsEvent && now - p.Updated < TimeSpan.FromSeconds(1))
            {
                r.Line = Theme.EventStyle;
                r.LineSet = true;
            }

            return r;
        });
    }

    private List<string> ViewEvents(Layout l)
    {
        var rows = VisibleEvents();
        var empty = Filter.Length > 0
            ? $"no events match \"{Filter}\""
            : "no events yet — press p to poll classes 1, 2 and 3";

        return RenderTable(l, empty, i =>
        {
            var e = rows[i];
            var stamp = e.Stamp.IsValid
                ? e.Stamp.Time.ToLocalTime().ToString(StampFormat, CultureInfo.InvariantCulture)
                : "—";

            var r = new TableRow();
            r.Cells[ColId.Received] = e.At.ToString(StampFormat, CultureInfo.InvariantCulture);
            r.Cells[ColId.Point] = PointLabel(e.Key);
            r.Cells[ColId.Value] = e.Value;
            r.Cells[ColId.Class] = View.ClassText(e.Class);
            r.Cells[ColId.Quality] = View.QualityText(e.Flags, e.Key.Type);
            r.Cells[ColId.Source] = e.GV.ToString();
            r.Cells[ColId.Stamp] = stamp;
            r.CellStyle[ColId.Received] = Theme.Dim;
            r.CellStyle[ColId.Class] = Theme.Dim;
            r.CellStyle[ColId.Source] = Theme.Dim;
            r.CellStyle[ColId.Stamp] = Theme.Dim;

            if (!e.Flags.Has(Flags.Online))
            {
                r.Line = Theme.Error;
                r.LineSet = true;
            }

            return r;
        });
    }

    private List<string> ViewLog(Layout l)
    {
        var rows = VisibleLogs();
        var empty = Filter.Length > 0
            ? $"no log lines match \"{Filter}\""
            : "nothing logged yet";

        return RenderTable(l, empty, i =>
        {
            var e = rows[i];
            var r = new TableRow();
            r.Cells[ColId.Received] = e.At.ToString(StampFormat, CultureInfo.InvariantCulture);
            r.Cells[ColId.Level] = e.Level;
            r.Cells[ColId.Message] = e.Text;
            r.CellStyle[ColId.Received] = Theme.Dim;

            switch (e.Level)
            {
                case "error":
                    r.Line = Theme.Error;
                    r.LineSet = true;
                    break;
                case "warn":
                    r.Line = Theme.Warning;
                    r.LineSet = true;
                    break;
                case "ok":
                    r.CellStyle[ColId.Level] = Theme.Ok;
                    break;
                default:
                    break;
            }

            return r;
        });
    }

    // ---------- inspector ----------

    /// <summary>
    /// The point inspector: everything known about one point, including the
    /// things a table has no room for.
    /// </summary>
    private List<string> ViewDetail(int w)
    {
        if (!TrySelectedPoint(out var p))
        {
            return
            [
                Theme.Dim.Render("no point selected"),
                "",
                Theme.Dim.Render("Move the cursor with ↑↓ or click"),
                Theme.Dim.Render("a row. Right-click opens this"),
                Theme.Dim.Render("panel from anywhere."),
            ];
        }

        var now = Clock();

        var lines = new List<string>
        {
            Theme.Title.Render(PointLabel(p.Key)) + Theme.Dim.Render("  " + p.Key.Type),
            "",
            Theme.Strong.Render(Theme.Truncate(p.Value, w)),
        };

        if (p.Previous.Length > 0 && !string.Equals(p.Previous, p.Value, StringComparison.Ordinal))
        {
            lines.Add(Theme.Dim.Render("was " + Theme.Truncate(p.Previous, w - 4)));
        }

        if (p.Hist.Count > 1)
        {
            var lo = p.Hist[0];
            var hi = p.Hist[0];
            foreach (var v in p.Hist)
            {
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }

            lines.Add("");
            lines.Add(Theme.Dim.Render(Theme.Sparkline(p.Hist, w)));
            lines.Add(Theme.Dim.Render(string.Format(
                CultureInfo.InvariantCulture,
                "{0} … {1} over {2} samples",
                Connection.FormatFloat(lo), Connection.FormatFloat(hi), p.Hist.Count)));
        }

        var stamp = p.Time.IsValid
            ? p.Time.Time.ToLocalTime().ToString(StampFormat, CultureInfo.InvariantCulture) + " " +
                View.Short(p.Time.Quality.ToDisplayString())
            : "—";

        lines.Add("");
        lines.Add(View.DetailField("age", View.FmtAge(now - p.Updated)));
        lines.Add(View.DetailField("stamp", stamp));
        lines.Add(View.DetailField("source", p.GV.ToString()));
        lines.Add(View.DetailField("class", View.ClassText(p.Class)));
        lines.Add(View.DetailField("updates", string.Format(
            CultureInfo.InvariantCulture, "{0} ({1} events)", p.Updates, p.Events)));
        lines.Add(View.DetailField(
            "first seen", p.First.ToString(ClockFormat, CultureInfo.InvariantCulture)));
        lines.Add("");
        lines.Add(Theme.Dim.Render("QUALITY"));
        lines.AddRange(QualityLines(p.Flags, p.Key.Type));

        var actions = DetailActions(p.Key.Type);
        if (actions.Count > 0)
        {
            lines.Add("");
            lines.Add(Theme.Dim.Render("ACTIONS"));
            lines.AddRange(actions);
        }

        return lines;
    }

    /// <summary>
    /// Lists the flags one to a line, coloured by what they mean, so the
    /// inspector answers "why is this row red".
    /// </summary>
    private static List<string> QualityLines(Flags f, PointType t)
    {
        if (t is PointType.Binary or PointType.BinaryOutputStatus)
        {
            f = f.Clear(Flags.StateBit);
        }

        if (f == Flags.None)
        {
            return [Theme.Error.Render("  no flags at all")];
        }

        var outp = new List<string>();
        foreach (var name in f.StringFor(t).Split('|'))
        {
            switch (name)
            {
                case "ONLINE":
                    outp.Add(Theme.Ok.Render("  ✓ " + name));
                    break;
                case "RESTART" or "COMM_LOST":
                    outp.Add(Theme.Error.Render("  ✗ " + name));
                    break;
                case "—":
                    break;
                default:
                    outp.Add(Theme.Warning.Render("  ! " + name));
                    break;
            }
        }

        if (!f.Has(Flags.Online))
        {
            outp.Insert(0, Theme.Error.Render("  ✗ NOT ONLINE"));
        }

        return outp;
    }

    private static List<string> DetailActions(PointType t) => t switch
    {
        PointType.BinaryOutputStatus =>
        [
            "  " + Theme.Key.Render("c") + " close   " + Theme.Key.Render("o") + " open",
            "  " + Theme.Key.Render("enter") + " control dialog",
        ],
        PointType.AnalogOutputStatus => ["  " + Theme.Key.Render("enter") + " write a setpoint"],
        PointType.Analog => ["  " + Theme.Key.Render("b") + " write a deadband"],
        _ => [],
    };

    // ---------- dialogs ----------

    private List<string> ViewModal(Layout l)
    {
        var d = Modal;
        var content = new List<string>(Math.Max(l.Modal.H - 2, 1));
        content.AddRange(d.Lines);

        // Pad so the choices sit flush against the bottom of the box, where the
        // hit test expects to find them.
        while (content.Count < l.Modal.H - 2 - d.Choices.Count)
        {
            content.Add("");
        }

        for (var i = 0; i < d.Choices.Count; i++)
        {
            var marker = Hover.Kind == ZoneKind.Choice && Hover.N == i
                ? Theme.Key.Render("▸ ")
                : "  ";
            content.Add(marker + Theme.Key.Render(Theme.Cell(d.Choices[i].Key, 5)) +
                d.Choices[i].Label);
        }

        var drawn = Theme.Box(d.Title, l.Modal.W, l.Modal.H, content);

        // The dialog is centred on an otherwise empty body: splicing a box into
        // live table rows means measuring around escape sequences, and getting
        // that wrong on the screen that operates plant is not a trade worth
        // making.
        return Compose(drawn, l.Modal, l.Body);
    }

    /// <summary>Draws a box onto an otherwise empty body.</summary>
    private static List<string> Compose(List<string> drawn, Rect frame, Rect body)
    {
        var outp = new List<string>(Math.Max(body.H, 0));
        for (var i = 0; i < body.H; i++)
        {
            outp.Add(Theme.Repeat(" ", body.W));
        }

        for (var i = 0; i < drawn.Count; i++)
        {
            var y = frame.Y - body.Y + i;
            if (y >= 0 && y < outp.Count)
            {
                outp[y] = Theme.Repeat(" ", frame.X - body.X) + drawn[i];
            }
        }

        return outp;
    }

    // ---------- connection editor ----------

    /// <summary>Draws the connection editor.</summary>
    /// <remarks>
    /// The focused field is drawn with a cursor in it and its explanation
    /// beneath, so the operator is told what a field means at the moment they
    /// are typing into it rather than having to remember from a manual.
    /// </remarks>
    private List<string> ViewForm(Layout l)
    {
        var f = Form;
        var inner = l.Form.W - 4;

        var content = new List<string>(Math.Max(l.Form.H, 1));
        for (var i = 0; i < l.FormRows; i++)
        {
            var idx = l.FormFirst + i;
            if (idx >= f.Fields.Count)
            {
                break;
            }

            var fld = f.Fields[idx];
            var focused = idx == f.Cursor;

            var name = Theme.Cell(fld.Label, inner);
            content.Add(focused ? Theme.Key.Render(name) : Theme.Dim.Render(name));

            var value = fld.Value;
            if (focused)
            {
                value += "▏";
            }

            var entry = "  " + Theme.Cell(value, Math.Max(inner - 2, 1));
            content.Add(focused ? Theme.Selected.Render(entry) : entry);
        }

        // The footer says what is wrong if anything is, and otherwise explains
        // the field being edited.
        var footer = "";
        var current = f.Fields[Math.Min(f.Cursor, f.Fields.Count - 1)];
        if (current.Hint.Length > 0)
        {
            footer = Theme.Dim.Render(Theme.Truncate(current.Hint, inner));
        }

        if (f.Error.Length > 0)
        {
            footer = Theme.Error.Render(Theme.Truncate(f.Error, inner));
        }

        content.Add("");
        content.Add(footer);

        return Compose(Theme.Box(f.Title, l.Form.W, l.Form.H, content), l.Form, l.Body);
    }

    // ---------- help ----------

    /// <summary>Draws the reference, scrolled to wherever the operator has got to.</summary>
    private List<string> ViewHelp(Layout l)
    {
        var lines = HelpLines(l.Body);
        return l.Offset >= lines.Count ? lines : lines[l.Offset..];
    }

    /// <summary>Composes the whole reference, in two columns when there is room.</summary>
    /// <remarks>
    /// It is composed rather than drawn so the screen can be scrolled: on a
    /// terminal shorter than the reference, the alternative is a reference whose
    /// last third does not exist.
    /// </remarks>
    public List<string> HelpLines(Rect b)
    {
        (string Key, string What)[] keys =
        [
            ("1 – 5, tab", "change screen"),
            ("↑ ↓ j k", "move the cursor"),
            ("pgup pgdn", "move a page"),
            ("home end g G", "first and last row"),
            ("/", "filter the list"),
            ("esc", "clear the filter, close a dialog"),
            ("f", "follow the newest row"),
            ("d, enter", "inspector, or act on the row"),
            ("< >", "change the sort column"),
            ("r", "reverse the sort"),
            ("x", "clear this list"),
            ("e", "export the list as CSV"),
            ("q, ctrl+c", "quit"),
        ];

        (string Key, string What)[] proto =
        [
            ("i", "integrity poll (classes 0–3)"),
            ("p", "poll event classes 1, 2, 3"),
            ("s", "range scan a group"),
            ("t", "set the outstation clock"),
            ("T", "set it measuring the link delay"),
            ("u / U", "enable / disable unsolicited"),
            ("R", "restart the outstation"),
            ("c / o", "close / open the selected output"),
            ("enter", "control dialog, or write a setpoint"),
            ("b", "write an analog deadband"),
            ("S", "select-before-operate or direct"),
            ("C", "change the connection"),
        ];

        (string Key, string What)[] mouse =
        [
            ("click a tab", "change screen"),
            ("click a row", "select it"),
            ("click it again", "act on it"),
            ("right-click a row", "open the inspector"),
            ("click a heading", "sort by that column"),
            ("click it again", "reverse the sort"),
            ("wheel", "scroll the list"),
            ("wheel on the tabs", "change screen"),
            ("drag the scrollbar", "scroll the list"),
            ("click a button", "run that action"),
        ];

        List<string> about =
        [
            "", Theme.ColHead.Render("CONNECTION"), "",
            " " + Theme.Dim.Render("C opens the connection editor:"),
            " " + Theme.Dim.Render("the address, the two link addresses,"),
            " " + Theme.Dim.Render("the timeout and the poll interval."),
            " " + Theme.Dim.Render("Applying it reconnects in place, so a"),
            " " + Theme.Dim.Render("guessed link address can be corrected"),
            " " + Theme.Dim.Render("without restarting the tool."),
            "", Theme.ColHead.Render("ABOUT"), "",
            " " + Theme.Dim.Render("dnp3-explorer, part of SharpDnp3:"),
            " " + Theme.Dim.Render("an IEEE 1815-2012 master and"),
            " " + Theme.Dim.Render("outstation stack in C#. GPLv3."),
            "",
            " " + Theme.Dim.Render("Controls ask before they operate"),
            " " + Theme.Dim.Render("unless started with -no-confirm."),
        ];

        static List<string> Render(string title, (string Key, string What)[] rows, int w)
        {
            var outp = new List<string>(rows.Length + 2)
            {
                Theme.ColHead.Render(title),
                "",
            };

            foreach (var r in rows)
            {
                outp.Add(" " + Theme.Key.Render(Theme.Cell(r.Key, 18)) +
                    Theme.Truncate(r.What, Math.Max(w - 19, 4)));
            }

            return outp;
        }

        if (b.W >= 96)
        {
            var colW = (b.W - 1) / 2;
            var left = Render("KEYS", keys, colW);
            left.AddRange(about);

            var right = Render("PROTOCOL", proto, b.W - colW - 1);
            right.Add("");
            right.AddRange(Render("MOUSE", mouse, b.W - colW - 1));

            var h = Math.Max(left.Count, right.Count);
            return Theme.JoinColumns(
                [Theme.Clip(left, h, colW), Theme.Clip(right, h, b.W - colW - 1)], h);
        }

        var single = Render("KEYS", keys, b.W);
        single.Add("");
        single.AddRange(Render("PROTOCOL", proto, b.W));
        single.Add("");
        single.AddRange(Render("MOUSE", mouse, b.W));
        single.AddRange(about);
        return single;
    }

    /// <summary>
    /// The model's idea of now, which the tick keeps current.
    /// </summary>
    /// <remarks>
    /// Reading it from one place keeps every age on a repainted screen
    /// consistent with every other.
    /// </remarks>
    public DateTimeOffset Clock() => Now == default ? DateTimeOffset.Now : Now;
}
