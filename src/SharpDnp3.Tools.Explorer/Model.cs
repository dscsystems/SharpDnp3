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
using SharpDnp3.Master;
using SharpDnp3.Objects;

namespace SharpDnp3.Tools.Explorer;

// The concurrency rule this whole file is built around: the DNP3 session runs
// on its own tasks and never touches the model. It pushes messages in; the
// model only ever reads them. Update never blocks — every action is dispatched
// as a command that runs off the loop and returns a result message — because a
// UI that blocks on a protocol timeout stops repainting for five seconds, and an
// operator reads that as the tool having crashed.

/// <summary>Identifies a tab.</summary>
public enum Screen
{
    /// <summary>The session at a glance.</summary>
    Overview = 0,

    /// <summary>Every point the device has reported.</summary>
    Points,

    /// <summary>The sequence of events.</summary>
    Events,

    /// <summary>What this tool has been doing.</summary>
    Log,

    /// <summary>The key and mouse reference.</summary>
    Help,
}

/// <summary>Screen predicates.</summary>
public static class ScreenExtensions
{
    /// <summary>Reports whether the screen draws a scrollable list.</summary>
    public static bool IsTable(this Screen s) =>
        s is Screen.Points or Screen.Events or Screen.Log;

    /// <summary>Reports whether the screen has a newest row worth pinning to.</summary>
    public static bool Follows(this Screen s) => s is Screen.Events or Screen.Log;

    /// <summary>
    /// Reports whether the screen has more content than fits, which the help
    /// screen does on a short terminal even though it is not a list.
    /// </summary>
    public static bool Scrolls(this Screen s) => s.IsTable() || s == Screen.Help;
}

/// <summary>Identifies a point in the model's tables.</summary>
public readonly record struct PointKey(PointType Type, ushort Index);

/// <summary>One measurement as the UI knows it.</summary>
public sealed class PointState
{
    /// <summary>Which point this is.</summary>
    public PointKey Key { get; init; }

    /// <summary>The latest value, rendered.</summary>
    public string Value { get; set; } = "";

    /// <summary>The latest value as a number, when it has one.</summary>
    public double Num { get; set; }

    /// <summary>Whether <see cref="Num"/> means anything.</summary>
    public bool HasNum { get; set; }

    /// <summary>The quality octet.</summary>
    public Flags Flags { get; set; }

    /// <summary>The outstation's timestamp.</summary>
    public Timestamp Time { get; set; }

    /// <summary>When this tool last heard about the point.</summary>
    public DateTimeOffset Updated { get; set; }

    /// <summary>When it first heard about it.</summary>
    public DateTimeOffset First { get; init; }

    /// <summary>Whether the latest report was an event.</summary>
    public bool IsEvent { get; set; }

    /// <summary>The value before the current one.</summary>
    public string Previous { get; set; } = "";

    /// <summary>How many reports have arrived.</summary>
    public int Updates { get; set; }

    /// <summary>How many of them were events.</summary>
    public int Events { get; set; }

    /// <summary>The recent numeric history, for the trend.</summary>
    public List<double> Hist { get; } = [];

    /// <summary>The group and variation the value was encoded as.</summary>
    public GroupVar GV { get; set; }

    /// <summary>The event class it came from.</summary>
    public Class Class { get; set; }

    /// <summary>
    /// Reports whether a point has not been refreshed recently enough to be
    /// trusted as live.
    /// </summary>
    /// <remarks>
    /// Quality flags say what the device thinks; this says whether the device
    /// is still talking.
    /// </remarks>
    public bool Stale(DateTimeOffset now, TimeSpan limit) =>
        limit > TimeSpan.Zero && now - Updated > limit;
}

/// <summary>One entry in the sequence-of-events list.</summary>
public readonly record struct EventRow(
    DateTimeOffset At,
    PointKey Key,
    string Value,
    Flags Flags,
    Timestamp Stamp,
    Class Class,
    GroupVar GV);

/// <summary>One line of the activity log.</summary>
public readonly record struct LogRow(DateTimeOffset At, string Level, string Text);

/// <summary>A unit of work dispatched off the update loop.</summary>
/// <returns>The message to feed back into the model, if any.</returns>
public delegate Task<IMsg?> Cmd();

/// <summary>The whole application state.</summary>
public sealed partial class Model
{
    /// <summary>The tabs, in order.</summary>
    public static readonly string[] ScreenNames = ["Overview", "Points", "Events", "Log", "Help"];

    /// <summary>
    /// Bounds the per-point trend. Two minutes of a one-second scan is enough
    /// to see a trend and small enough to keep ten thousand points cheap.
    /// </summary>
    public const int HistCap = 120;

    private readonly int[] _cursor = new int[5];
    private readonly int[] _offset = new int[5];
    private readonly int[] _rate = new int[20];
    private int _rateIdx;

    /// <summary>Builds the initial model.</summary>
    public Model(Connection conn)
    {
        Conn = conn;
        Screen = Screen.Overview;
        Follow = true;
        Status = "connecting";
        SortBy = SortKey.Point;
        Confirm = true;
        Sbo = true;
        PulseMs = 1000;
        MouseEnabled = true;
        AltMode = true;
        StaleAge = TimeSpan.FromSeconds(30);
        StartedAt = DateTimeOffset.Now;
        Now = DateTimeOffset.Now;
    }

    /// <summary>The terminal width.</summary>
    public int Width { get; set; }

    /// <summary>The terminal height.</summary>
    public int Height { get; set; }

    /// <summary>The tab being shown.</summary>
    public Screen Screen { get; private set; }

    /// <summary>The session this interface is driving.</summary>
    public Connection Conn { get; }

    /// <summary>What the session last said about itself.</summary>
    public string Status { get; private set; } = "connecting";

    /// <summary>The last error reported, kept so it is not logged twice.</summary>
    public string LastError { get; private set; } = "";

    /// <summary>When the tool started.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>When the link came up, or default while it is down.</summary>
    public DateTimeOffset LinkSince { get; private set; }

    /// <summary>The model's idea of now, kept current by the tick.</summary>
    public DateTimeOffset Now { get; private set; }

    /// <summary>Every point, by identity.</summary>
    public Dictionary<PointKey, PointState> Points { get; private set; } = [];

    /// <summary>
    /// The natural order of the points, so a table does not reshuffle under the
    /// cursor every time a value arrives.
    /// </summary>
    public List<PointKey> PointsOrder { get; private set; } = [];

    /// <summary>The events received, oldest first.</summary>
    public List<EventRow> Events { get; private set; } = [];

    /// <summary>The activity log.</summary>
    public List<LogRow> Logs { get; private set; } = [];

    /// <summary>Whether the list stays pinned to its newest row.</summary>
    public bool Follow { get; private set; }

    /// <summary>Whether the point inspector is open.</summary>
    public bool Detail { get; private set; }

    /// <summary>The text every visible row must contain.</summary>
    public string Filter { get; private set; } = "";

    /// <summary>The column the points table is sorted by.</summary>
    public SortKey SortBy { get; private set; }

    /// <summary>Whether that sort is reversed.</summary>
    public bool SortDesc { get; private set; }

    /// <summary>The open single-line prompt.</summary>
    public PromptState Prompt { get; private set; } = new();

    /// <summary>The open dialog.</summary>
    public ModalState Modal { get; private set; } = new();

    /// <summary>The open editor.</summary>
    public FormState Form { get; private set; } = new();

    /// <summary>The transient banner.</summary>
    public ToastState Toast { get; } = new();

    /// <summary>
    /// Selects between select-before-operate and direct operate.
    /// </summary>
    /// <remarks>
    /// Both this and <see cref="Confirm"/> are on screen — this as a toolbar
    /// button, confirmation as a standing warning in the toolbar when it is off
    /// — because an operator must never have to remember which mode a control
    /// tool is in. Confirmation is deliberately not bindable to a key: it is a
    /// decision made when starting the tool, not one to be turned off by a
    /// keystroke next to the one that closes a breaker.
    /// </remarks>
    public bool Sbo { get; set; }

    /// <summary>Whether a control asks before it is issued.</summary>
    public bool Confirm { get; set; }

    /// <summary>The pulse duration for trip and close, in milliseconds.</summary>
    public uint PulseMs { get; set; }

    /// <summary>Whether the mouse is enabled.</summary>
    public bool MouseEnabled { get; set; }

    /// <summary>Whether the tool takes the whole terminal.</summary>
    public bool AltMode { get; set; }

    /// <summary>How long a point may go without an update before it fades.</summary>
    public TimeSpan StaleAge { get; set; }

    /// <summary>The region under the pointer.</summary>
    public Zone Hover { get; private set; }

    /// <summary>Whether the scrollbar is being pulled.</summary>
    public bool Dragging { get; private set; }

    /// <summary>The session's counters.</summary>
    public MasterStats Stats { get; private set; }

    /// <summary>The internal indications last reported.</summary>
    public string Iin { get; private set; } = "";

    /// <summary>Whether the link is up.</summary>
    public bool Connected { get; private set; }

    /// <summary>
    /// The measurement rate over the last minute, one sample per tick.
    /// </summary>
    /// <remarks>
    /// A device that has gone quiet looks exactly like a healthy idle one until
    /// you can see that it used to be busy.
    /// </remarks>
    public List<double> RateHist { get; } = [];

    /// <summary>Whether the tool is shutting down.</summary>
    public bool Quitting { get; private set; }

    /// <summary>The cursor row of the current screen.</summary>
    public int Cursor => _cursor[(int)Screen];

    /// <summary>Applies one message.</summary>
    public Cmd? Update(IMsg msg)
    {
        switch (msg)
        {
            case TickMsg tick:
                Now = tick.At;
                RateHist.Add(EventRate());
                if (RateHist.Count > HistCap)
                {
                    RateHist.RemoveRange(0, RateHist.Count - HistCap);
                }

                _rateIdx = (_rateIdx + 1) % _rate.Length;
                _rate[_rateIdx] = 0;
                Toast.Expire(Now);
                return null;

            case KeyMsg key:
                return HandleKey(key.Key);

            case MouseMsg mouse:
                return HandleMouse(mouse);

            case UpdateMsg u:
                ApplyUpdate(u);
                return null;

            case StatusMsg status:
                ApplyStatus(status);
                return null;

            case LogMsg log:
                AddLog(log.Level, log.Text);
                return null;

            case CommandResultMsg result:
                AddLog(result.Level, result.Text);
                Toast.Show(result.Level, result.Text, Now);
                return null;

            default:
                return null;
        }
    }

    private void ApplyStatus(StatusMsg msg)
    {
        var was = Connected;
        Status = msg.Text;
        Connected = msg.Connected;
        Stats = msg.Stats;
        Iin = msg.Iin;

        if (msg.Connected && !was)
        {
            LinkSince = DateTimeOffset.Now;
            AddLog("ok", "link up");
        }

        if (!msg.Connected && was)
        {
            LinkSince = default;
            AddLog("warn", "link down");
        }

        if (!string.IsNullOrEmpty(msg.Error) &&
            !string.Equals(msg.Error, LastError, StringComparison.Ordinal))
        {
            LastError = msg.Error;
            AddLog("error", msg.Error);
        }
    }

    /// <summary>Applies one keystroke, named the way the terminal reader names it.</summary>
    /// <remarks>
    /// Taking the key as a string is what lets the whole interface be driven
    /// from a test without a terminal.
    /// </remarks>
    public Cmd? HandleKey(string key)
    {
        // A prompt or a dialog owns the keyboard while it is open. Controls are
        // issued from this interface, so a keystroke must never fall through to
        // a breaker while the operator believes they are typing.
        if (Prompt.Active)
        {
            return HandlePromptKey(key);
        }

        if (Form.Active)
        {
            return HandleFormKey(key);
        }

        if (Modal.Kind != ModalKind.None)
        {
            return HandleModalKey(key);
        }

        switch (key)
        {
            case "q" or "ctrl+c":
                Quitting = true;
                return null;

            case "esc":
                if (Filter.Length > 0)
                {
                    Filter = "";
                    Toast.Show("info", "filter cleared", Now);
                }
                else if (Detail)
                {
                    Detail = false;
                }

                return null;

            // ---- navigation ----
            case "tab" or "right":
                SetScreen((Screen)(((int)Screen + 1) % ScreenNames.Length));
                return null;
            case "shift+tab" or "left":
                SetScreen((Screen)(((int)Screen + ScreenNames.Length - 1) % ScreenNames.Length));
                return null;
            case "1" or "2" or "3" or "4" or "5":
                SetScreen((Screen)(key[0] - '1'));
                return null;
            case "?":
                SetScreen(Screen.Help);
                return null;

            case "up" or "k":
                MoveCursor(-1);
                return null;
            case "down" or "j":
                MoveCursor(1);
                return null;
            case "pgup" or "ctrl+b":
                MoveCursor(-PageSize());
                return null;
            case "pgdown" or "ctrl+f":
                MoveCursor(PageSize());
                return null;
            case "home" or "g":
                JumpTo(0);
                return null;
            case "end" or "G":
                JumpTo(RowCount() - 1);
                return null;

            // ---- view ----
            case "/":
                Prompt = new PromptState
                {
                    Active = true, Kind = PromptKind.Filter, Label = "filter", Input = Filter,
                };
                if (Screen is Screen.Overview or Screen.Help)
                {
                    SetScreen(Screen.Points);
                }

                return null;

            case "f":
                Follow = !Follow;
                Toast.Show("info", "follow " + OnOff(Follow), Now);
                return null;

            case "d" or "enter" or " ":
                return ContextAction(key);

            case "r":
                SortDesc = !SortDesc;
                return null;
            case "<":
                CycleSort(-1);
                return null;
            case ">":
                CycleSort(1);
                return null;
            case "x":
                ClearList();
                return null;

            // ---- protocol ----
            case "i":
                AddLog("info", "integrity poll requested");
                return Conn.IntegrityPoll();
            case "p":
                AddLog("info", "class 1/2/3 poll requested");
                return Conn.ClassPoll();
            case "t":
                AddLog("info", "time sync requested");
                return Conn.SyncTime(false);
            case "T":
                AddLog("info", "time sync with delay measurement requested");
                return Conn.SyncTime(true);
            case "u":
                AddLog("info", "enabling unsolicited classes 1/2/3");
                return Conn.Unsolicited(true);
            case "U":
                AddLog("info", "disabling unsolicited classes 1/2/3");
                return Conn.Unsolicited(false);
            case "s":
                Prompt = new PromptState
                {
                    Active = true,
                    Kind = PromptKind.Range,
                    Label = "scan range  group[.var] start-stop",
                };
                return null;
            case "R":
                OpenRestartDialog();
                return null;
            case "C":
                OpenConnectionForm();
                return null;
            case "S":
                Sbo = !Sbo;
                Toast.Show("info", "controls: " + ControlMode(Sbo), Now);
                return null;
            case "e":
                return Export();

            // ---- controls ----
            case "c" or "o":
                return QuickControl(key == "c");
            case "b":
                return StartDeadbandPrompt();

            default:
                return null;
        }
    }

    private void SetScreen(Screen s)
    {
        if ((int)s >= 0 && (int)s < ScreenNames.Length)
        {
            Screen = s;
        }
    }

    private int PageSize() =>
        Math.Max(Height - Layout.ChromeTop - Layout.ChromeBottom - 2, 1);

    private void MoveCursor(int delta)
    {
        // Moving the cursor by hand is a deliberate statement that the operator
        // wants to read something, so it takes the view off the live tail.
        if (delta != 0 && Follow && Screen.Follows())
        {
            Follow = false;
        }

        _cursor[(int)Screen] =
            Math.Clamp(_cursor[(int)Screen] + delta, 0, Math.Max(RowCount() - 1, 0));
    }

    private void JumpTo(int row)
    {
        if (Follow && Screen.Follows() && row < RowCount() - 1)
        {
            Follow = false;
        }

        _cursor[(int)Screen] = Math.Clamp(row, 0, Math.Max(RowCount() - 1, 0));
    }

    /// <summary>
    /// Moves the window without moving the cursor, which is what a wheel does
    /// everywhere else.
    /// </summary>
    private void ScrollBy(int delta)
    {
        if (delta != 0 && Follow && Screen.Follows())
        {
            Follow = false;
        }

        _offset[(int)Screen] = Math.Max(_offset[(int)Screen] + delta, 0);

        // The cursor is dragged along only when the scroll would leave it
        // behind; ClampScroll does that with the real geometry at draw time.
        var visible = Math.Max(Height - Layout.ChromeTop - Layout.ChromeBottom - 1, 1);
        var off = _offset[(int)Screen];
        var c = _cursor[(int)Screen];
        if (c < off)
        {
            _cursor[(int)Screen] = off;
        }
        else if (c >= off + visible)
        {
            _cursor[(int)Screen] = off + visible - 1;
        }
    }

    private void CycleSort(int dir)
    {
        if (Screen != Screen.Points)
        {
            return;
        }

        SortKey[] order =
            [SortKey.Point, SortKey.Value, SortKey.Quality, SortKey.Age, SortKey.Time];

        var at = Array.IndexOf(order, SortBy);
        if (at < 0)
        {
            at = 0;
        }

        SortBy = order[(at + dir + order.Length) % order.Length];
        Toast.Show("info", "sorted by " + SortName(SortBy), Now);
    }

    private void ClearList()
    {
        switch (Screen)
        {
            case Screen.Events:
                Events = [];
                _cursor[(int)Screen.Events] = 0;
                _offset[(int)Screen.Events] = 0;
                break;

            case Screen.Log:
                Logs = [];
                _cursor[(int)Screen.Log] = 0;
                _offset[(int)Screen.Log] = 0;
                break;

            case Screen.Points:
                // Forgetting the points is how an operator re-reads a device
                // after changing its configuration, rather than restarting the
                // tool.
                Points = [];
                PointsOrder = [];
                _cursor[(int)Screen.Points] = 0;
                _offset[(int)Screen.Points] = 0;
                Toast.Show("info", "point table cleared — press i to re-read", Now);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// What enter, space and d do, which depends on what is under the cursor: a
    /// control point offers its controls, anything else opens the inspector.
    /// </summary>
    private Cmd? ContextAction(string key)
    {
        if (key == "d" || Screen != Screen.Points)
        {
            Detail = !Detail;
            return null;
        }

        if (!TrySelectedPoint(out var p))
        {
            Detail = !Detail;
            return null;
        }

        switch (p.Key.Type)
        {
            case PointType.BinaryOutputStatus:
                OpenControlDialog(p);
                break;
            case PointType.AnalogOutputStatus:
                StartAnalogPrompt(p);
                break;
            default:
                Detail = !Detail;
                break;
        }

        return null;
    }

    /// <summary>The two-keystroke path for a breaker: c closes, o opens.</summary>
    /// <remarks>
    /// Spelled out rather than offered as a generic "toggle", because a control
    /// that depends on the operator's idea of the current state is how the
    /// wrong breaker gets opened.
    /// </remarks>
    private Cmd? QuickControl(bool closing)
    {
        if (!TrySelectedControl(out var key))
        {
            AddLog("warn", "select a binary output point first (Points screen)");
            Toast.Show("warn", "no binary output selected", Now);
            return null;
        }

        return IssueControl(ControlOp.Latch(key.Index, closing));
    }

    /// <summary>Either asks first or sends, depending on the confirm setting.</summary>
    private Cmd? IssueControl(ControlOp op)
    {
        if (!Confirm)
        {
            AddLog("info", op.Describe(Sbo));
            return Conn.Operate(op, Sbo);
        }

        Modal = new ModalState
        {
            Kind = ModalKind.Confirm,
            Title = "Confirm control",
            Lines =
            [
                op.Describe(Sbo),
                "",
                "This operates plant. Check the point before confirming.",
            ],
            Choices =
            [
                new ModalChoice("y", "Send it"),
                new ModalChoice("n", "Cancel"),
            ],
            Pending = Conn.Operate(op, Sbo),
            Desc = op.Describe(Sbo),
        };
        return null;
    }

    private void OpenControlDialog(PointState p)
    {
        Modal = new ModalState
        {
            Kind = ModalKind.Control,
            Title = "Control " + PointLabel(p.Key),
            Lines =
            [
                string.Format(
                    CultureInfo.InvariantCulture,
                    "currently {0}, {1}", p.Value, View.QualityText(p.Flags, p.Key.Type)),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "mode {0}, pulse {1}ms", ControlMode(Sbo), PulseMs),
            ],
            Choices =
            [
                new ModalChoice("c", "Latch ON  (close)"),
                new ModalChoice("o", "Latch OFF (open)"),
                new ModalChoice("l", string.Format(
                    CultureInfo.InvariantCulture, "Pulse close {0}ms", PulseMs)),
                new ModalChoice("t", string.Format(
                    CultureInfo.InvariantCulture, "Pulse trip  {0}ms", PulseMs)),
                new ModalChoice("esc", "Cancel"),
            ],
            Target = p.Key,
        };
    }

    private void OpenRestartDialog()
    {
        Modal = new ModalState
        {
            Kind = ModalKind.Restart,
            Title = "Restart outstation",
            Lines =
            [
                "The device stops answering until it comes back.",
                "A cold restart reinitialises everything; a warm",
                "restart only the communications process.",
            ],
            Choices =
            [
                new ModalChoice("c", "Cold restart"),
                new ModalChoice("w", "Warm restart"),
                new ModalChoice("esc", "Cancel"),
            ],
        };
    }

    private void StartAnalogPrompt(PointState p)
    {
        Prompt = new PromptState
        {
            Active = true,
            Kind = PromptKind.Analog,
            Target = p.Key,
            Label = "write " + PointLabel(p.Key) + "  value[i16|i32|f32|f64]",
        };
    }

    private Cmd? StartDeadbandPrompt()
    {
        if (!TrySelectedPoint(out var p) || p.Key.Type != PointType.Analog)
        {
            Toast.Show("warn", "select an analog input first", Now);
            return null;
        }

        Prompt = new PromptState
        {
            Active = true,
            Kind = PromptKind.Deadband,
            Target = p.Key,
            Label = "deadband for " + PointLabel(p.Key),
        };
        return null;
    }

    /// <summary>Returns the row under the cursor on the Points screen.</summary>
    public bool TrySelectedPoint(out PointState point)
    {
        point = null!;
        if (Screen != Screen.Points)
        {
            return false;
        }

        var rows = VisiblePoints();
        var c = _cursor[(int)Screen.Points];
        if (c < 0 || c >= rows.Count)
        {
            return false;
        }

        point = rows[c];
        return true;
    }

    /// <summary>Returns the binary output under the cursor.</summary>
    public bool TrySelectedControl(out PointKey key)
    {
        key = default;
        if (!TrySelectedPoint(out var p) || p.Key.Type != PointType.BinaryOutputStatus)
        {
            return false;
        }

        key = p.Key;
        return true;
    }

    /// <summary>Folds one measurement into the model.</summary>
    private void ApplyUpdate(UpdateMsg u)
    {
        var k = new PointKey(u.Type, u.Index);
        var now = DateTimeOffset.Now;

        if (!Points.TryGetValue(k, out var p))
        {
            p = new PointState { Key = k, First = now };
            Points[k] = p;
            PointsOrder.Add(k);
            PointsOrder.Sort(static (a, b) =>
                a.Type != b.Type ? a.Type.CompareTo(b.Type) : a.Index.CompareTo(b.Index));
        }

        if (!string.Equals(p.Value, u.Value, StringComparison.Ordinal))
        {
            p.Previous = p.Value;
        }

        p.Value = u.Value;
        p.Flags = u.Flags;
        p.Time = u.Stamp;
        p.Num = u.Num;
        p.HasNum = u.HasNum;
        p.Updated = now;
        p.IsEvent = u.IsEvent;
        p.Updates++;
        p.GV = u.GV;
        p.Class = u.Class;

        if (u.HasNum)
        {
            p.Hist.Add(u.Num);
            if (p.Hist.Count > HistCap)
            {
                p.Hist.RemoveRange(0, p.Hist.Count - HistCap);
            }
        }

        _rate[_rateIdx]++;

        if (u.IsEvent)
        {
            p.Events++;
            Events.Add(new EventRow(now, k, u.Value, u.Flags, u.Stamp, u.Class, u.GV));

            // Bound the list: an event storm would otherwise grow it without
            // limit, and nobody scrolls back ten thousand rows.
            if (Events.Count > 2000)
            {
                var drop = Events.Count - 2000;
                Events.RemoveRange(0, drop);

                // Keep the cursor on the row it was on rather than letting the
                // list slide under a reader.
                _cursor[(int)Screen.Events] = Math.Max(_cursor[(int)Screen.Events] - drop, 0);
                _offset[(int)Screen.Events] = Math.Max(_offset[(int)Screen.Events] - drop, 0);
            }
        }
    }

    /// <summary>Adds one line to the activity log.</summary>
    public void AddLog(string level, string text)
    {
        Logs.Add(new LogRow(DateTimeOffset.Now, level, text));
        if (Logs.Count > 2000)
        {
            var drop = Logs.Count - 2000;
            Logs.RemoveRange(0, drop);
            _cursor[(int)Screen.Log] = Math.Max(_cursor[(int)Screen.Log] - drop, 0);
            _offset[(int)Screen.Log] = Math.Max(_offset[(int)Screen.Log] - drop, 0);
        }
    }

    /// <summary>Returns measurements per second over the last ten seconds.</summary>
    public double EventRate()
    {
        var total = 0;
        foreach (var n in _rate)
        {
            total += n;
        }

        return total / (_rate.Length * 0.5);
    }

    /// <summary>Tests a row against the filter.</summary>
    /// <remarks>
    /// The filter reads the whole row rather than just the point name: an
    /// operator hunting a fault types "comm_lost", and one hunting a value
    /// types "11".
    /// </remarks>
    public static bool MatchesFilter(string filter, params string[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (string.IsNullOrEmpty(filter))
        {
            return true;
        }

        foreach (var s in fields)
        {
            if (s is not null &&
                s.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the point rows after filtering and sorting.</summary>
    public List<PointState> VisiblePoints()
    {
        var outp = new List<PointState>(PointsOrder.Count);
        foreach (var k in PointsOrder)
        {
            var p = Points[k];
            if (!MatchesFilter(
                Filter, PointLabel(k), p.Value,
                View.QualityText(p.Flags, k.Type), k.Type.ToString()))
            {
                continue;
            }

            outp.Add(p);
        }

        if (SortBy != SortKey.Point && SortBy != SortKey.None)
        {
            // OrderBy is stable, which keeps points that compare equal in their
            // natural order rather than shuffling them on every repaint.
            outp = [.. outp.OrderBy(static p => p, new PointComparer(SortBy))];
        }

        if (SortDesc)
        {
            outp.Reverse();
        }

        return outp;
    }

    private sealed class PointComparer : IComparer<PointState>
    {
        private readonly SortKey _key;

        public PointComparer(SortKey key) => _key = key;

        public int Compare(PointState? a, PointState? b)
        {
            if (a is null || b is null)
            {
                return 0;
            }

            return PointLess(a, b, _key) ? -1 : PointLess(b, a, _key) ? 1 : 0;
        }
    }

    private static bool PointLess(PointState a, PointState b, SortKey key) => key switch
    {
        // Numbers compare as numbers; ON and OFF fall back to text, which puts
        // them in a stable order rather than an arbitrary one.
        SortKey.Value => a.HasNum && b.HasNum
            ? a.Num < b.Num
            : string.CompareOrdinal(a.Value, b.Value) < 0,

        // Worst first: a sort on quality is a search for the broken points.
        SortKey.Quality => QualityRank(a.Flags) < QualityRank(b.Flags),

        SortKey.Age => a.Updated > b.Updated,
        SortKey.Time => a.Time.Time > b.Time.Time,
        _ => false,
    };

    /// <summary>Orders points by how much they should worry an operator.</summary>
    private static int QualityRank(Flags f)
    {
        if (!f.Has(Flags.Online))
        {
            return 0;
        }

        if (f.HasAny(Flags.CommLost))
        {
            return 1;
        }

        if (f.HasAny(Flags.Restart))
        {
            return 2;
        }

        if (f.HasAny(Flags.RemoteForced | Flags.LocalForced))
        {
            return 3;
        }

        return 4;
    }

    /// <summary>Returns the event rows after filtering.</summary>
    public List<EventRow> VisibleEvents()
    {
        if (string.IsNullOrEmpty(Filter))
        {
            return Events;
        }

        var outp = new List<EventRow>(Events.Count);
        foreach (var e in Events)
        {
            if (MatchesFilter(
                Filter, PointLabel(e.Key), e.Value, View.QualityText(e.Flags, e.Key.Type)))
            {
                outp.Add(e);
            }
        }

        return outp;
    }

    /// <summary>Returns the log rows after filtering.</summary>
    public List<LogRow> VisibleLogs()
    {
        if (string.IsNullOrEmpty(Filter))
        {
            return Logs;
        }

        var outp = new List<LogRow>(Logs.Count);
        foreach (var l in Logs)
        {
            if (MatchesFilter(Filter, l.Text, l.Level))
            {
                outp.Add(l);
            }
        }

        return outp;
    }

    /// <summary>How many rows the current screen holds.</summary>
    public int RowCount() => Screen switch
    {
        Screen.Points => VisiblePoints().Count,
        Screen.Events => VisibleEvents().Count,
        Screen.Log => VisibleLogs().Count,
        Screen.Help => HelpLines(BodyRect()).Count,
        _ => 0,
    };

    /// <summary>
    /// The content area, derived from the terminal size alone so that anything
    /// needing it — including the row count — can have it without a full layout
    /// pass.
    /// </summary>
    public Rect BodyRect() => new(
        0, Layout.ChromeTop, Width, Height - Layout.ChromeTop - Layout.ChromeBottom);

    // ---------- prompts ----------

    /// <summary>What a single-line prompt is collecting.</summary>
    public enum PromptKind
    {
        /// <summary>Text every visible row must contain.</summary>
        Filter,

        /// <summary>A setpoint to write.</summary>
        Analog,

        /// <summary>A deadband to write.</summary>
        Deadband,

        /// <summary>A range of one group to scan.</summary>
        Range,
    }

    /// <summary>The open single-line prompt.</summary>
    public sealed class PromptState
    {
        /// <summary>Whether a prompt is open.</summary>
        public bool Active { get; init; }

        /// <summary>What it is collecting.</summary>
        public PromptKind Kind { get; init; }

        /// <summary>What it asks.</summary>
        public string Label { get; init; } = "";

        /// <summary>What has been typed.</summary>
        public string Input { get; set; } = "";

        /// <summary>The point it applies to, when it applies to one.</summary>
        public PointKey Target { get; init; }
    }

    private Cmd? HandlePromptKey(string key)
    {
        switch (key)
        {
            case "esc":
                // A filter abandoned halfway has already been applied to the
                // list with every keystroke, so it is cleared; anything that
                // would send something is simply dropped, because a prompt
                // abandoned halfway is not an instruction.
                if (Prompt.Kind == PromptKind.Filter)
                {
                    Filter = "";
                }

                Prompt = new PromptState();
                return null;

            case "enter":
                var p = Prompt;
                Prompt = new PromptState();
                return SubmitPrompt(p);

            case "backspace":
                if (Prompt.Input.Length > 0)
                {
                    Prompt.Input = Prompt.Input[..^1];
                }

                break;

            case "ctrl+u":
                Prompt.Input = "";
                break;

            case "space":
                Prompt.Input += " ";
                break;

            default:
                if (key.Length == 1)
                {
                    Prompt.Input += key;
                }

                break;
        }

        if (Prompt.Kind == PromptKind.Filter)
        {
            // The filter applies as it is typed: an operator narrowing a list
            // wants to see it narrow.
            Filter = Prompt.Input;
            _cursor[(int)Screen] = 0;
            _offset[(int)Screen] = 0;
        }

        return null;
    }

    private Cmd? SubmitPrompt(PromptState p)
    {
        switch (p.Kind)
        {
            case PromptKind.Filter:
                Filter = p.Input;
                return null;

            case PromptKind.Analog:
                try
                {
                    var (command, desc) = ParseAnalogWrite(p.Target.Index, p.Input);
                    return IssueControl(new ControlOp(desc, command));
                }
                catch (FormatException ex)
                {
                    Toast.Show("error", ex.Message, Now);
                    return null;
                }

            case PromptKind.Deadband:
                if (!float.TryParse(
                    p.Input.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    Toast.Show("error", $"deadband: \"{p.Input.Trim()}\" is not a number", Now);
                    return null;
                }

                AddLog("info", string.Format(
                    CultureInfo.InvariantCulture,
                    "writing deadband {0} to AI {1}", v, p.Target.Index));
                return Conn.WriteDeadband(p.Target.Index, v);

            case PromptKind.Range:
                try
                {
                    var (g, variation, start, stop) = ParseRangeScan(p.Input);
                    AddLog("info", string.Format(
                        CultureInfo.InvariantCulture,
                        "range scan g{0}v{1} {2}-{3}", g, variation, start, stop));
                    return Conn.RangeScan(g, variation, start, stop);
                }
                catch (FormatException ex)
                {
                    Toast.Show("error", ex.Message, Now);
                    return null;
                }

            default:
                return null;
        }
    }

    /// <summary>Reads "30.5 0-15", "30 0-15" or "30.5 0 15".</summary>
    public static (byte Group, byte Variation, ushort Start, ushort Stop) ParseRangeScan(string s)
    {
        ArgumentNullException.ThrowIfNull(s);

        var f = s.Trim().Split([' ', ',', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 3)
        {
            throw new FormatException("range: want \"group[.var] start-stop\"");
        }

        byte variation = 0;
        var gv = f[0].Split('.', 2);
        if (!byte.TryParse(gv[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var group))
        {
            throw new FormatException($"range: bad group \"{gv[0]}\"");
        }

        var at = 1;
        if (gv.Length == 2)
        {
            if (!byte.TryParse(
                gv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out variation))
            {
                throw new FormatException($"range: bad variation \"{gv[1]}\"");
            }
        }
        else if (f.Length >= 4)
        {
            // Four numbers and no dot means the variation was written out
            // separately, as "30 2 0 15" or "30,2,0,15".
            if (!byte.TryParse(
                f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out variation))
            {
                throw new FormatException($"range: bad variation \"{f[1]}\"");
            }

            at = 2;
        }

        if (!ushort.TryParse(
            f[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start))
        {
            throw new FormatException($"range: bad start \"{f[at]}\"");
        }

        if (!ushort.TryParse(
            f[at + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stop))
        {
            throw new FormatException($"range: bad stop \"{f[at + 1]}\"");
        }

        if (start > stop)
        {
            throw new FormatException(string.Format(
                CultureInfo.InvariantCulture,
                "range: start {0} is above stop {1}", start, stop));
        }

        return (group, variation, start, stop);
    }

    /// <summary>Reads a value and an optional explicit encoding.</summary>
    /// <remarks>
    /// The encoding matters on the wire: an outstation that expects a 16-bit
    /// setpoint will reject a float, and an operator who has to guess will guess
    /// wrong. Bare integers go out as 32-bit integers and bare decimals as
    /// 32-bit floats, which is what most devices accept, and a suffix overrides
    /// that.
    /// </remarks>
    public static (Command Command, string Description) ParseAnalogWrite(ushort index, string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var s = input.Trim().ToLowerInvariant();
        if (s.Length == 0)
        {
            throw new FormatException("write: no value given");
        }

        var enc = "";
        foreach (var suffix in (string[])["i16", "i32", "f32", "f64"])
        {
            if (s.EndsWith(suffix, StringComparison.Ordinal))
            {
                enc = suffix;
                s = s[..^suffix.Length].Trim();
                break;
            }
        }

        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            throw new FormatException($"write: \"{input}\" is not a number");
        }

        if (enc.Length == 0)
        {
            enc = v == Math.Truncate(v) ? "i32" : "f32";
        }

        var desc = string.Format(
            CultureInfo.InvariantCulture, "write AO {0} = {1} ({2})", index, s, enc);

        switch (enc)
        {
            case "i16":
                if (v is < -32768 or > 32767)
                {
                    throw new FormatException(string.Format(
                        CultureInfo.InvariantCulture, "write: {0} does not fit in an int16", v));
                }

                return (Command.AnalogOutputInt16(index, (short)v), desc);

            case "i32":
                if (v is < -2147483648 or > 2147483647)
                {
                    throw new FormatException(string.Format(
                        CultureInfo.InvariantCulture, "write: {0} does not fit in an int32", v));
                }

                return (Command.AnalogOutputInt32(index, (int)v), desc);

            case "f64":
                return (Command.AnalogOutputFloat64(index, v), desc);

            default:
                return (Command.AnalogOutputFloat32(index, (float)v), desc);
        }
    }

    // ---------- dialogs ----------

    private Cmd? HandleModalKey(string key)
    {
        if (key is "esc" or "q" or "n")
        {
            Modal = new ModalState();
            return null;
        }

        switch (Modal.Kind)
        {
            case ModalKind.Confirm:
                if (key is "y" or "enter")
                {
                    var cmd = Modal.Pending;
                    var desc = Modal.Desc;
                    Modal = new ModalState();
                    AddLog("info", desc);
                    return cmd;
                }

                return null;

            case ModalKind.Control:
                var idx = Modal.Target.Index;
                ControlOp op;
                switch (key)
                {
                    case "c":
                        op = ControlOp.Latch(idx, true);
                        break;
                    case "o":
                        op = ControlOp.Latch(idx, false);
                        break;
                    case "l":
                        op = new ControlOp(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "pulse close BO {0} for {1}ms", idx, PulseMs),
                            Command.Close(idx, PulseMs));
                        break;
                    case "t":
                        op = new ControlOp(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "pulse trip BO {0} for {1}ms", idx, PulseMs),
                            Command.Trip(idx, PulseMs));
                        break;
                    default:
                        return null;
                }

                Modal = new ModalState();
                return IssueControl(op);

            case ModalKind.Restart:
                RestartMode mode;
                switch (key)
                {
                    case "c":
                        mode = RestartMode.Cold;
                        break;
                    case "w":
                        mode = RestartMode.Warm;
                        break;
                    default:
                        return null;
                }

                Modal = new ModalState();
                AddLog("warn", mode.ToDisplayString() + " restart requested");
                return Conn.Restart(mode);

            default:
                return null;
        }
    }

    // ---------- labels ----------

    /// <summary>Renders a point's identity: "AI 3", "BO 0".</summary>
    public static string PointLabel(PointKey k) => string.Format(
        CultureInfo.InvariantCulture, "{0} {1}", TypeAbbrev(k.Type), k.Index);

    /// <summary>The two-letter name an engineer uses for a point type.</summary>
    public static string TypeAbbrev(PointType t) => t switch
    {
        PointType.Binary => "BI",
        PointType.DoubleBitBinary => "DB",
        PointType.Counter => "CT",
        PointType.FrozenCounter => "FC",
        PointType.Analog => "AI",
        PointType.BinaryOutputStatus => "BO",
        PointType.AnalogOutputStatus => "AO",
        PointType.OctetString => "OS",
        _ => "??",
    };

    /// <summary>Names a sort order.</summary>
    public static string SortName(SortKey k) => k switch
    {
        SortKey.Value => "value",
        SortKey.Quality => "quality",
        SortKey.Age => "age",
        SortKey.Time => "timestamp",
        _ => "point",
    };

    /// <summary>Names the control mode in the words the standard uses.</summary>
    public static string ControlMode(bool sbo) =>
        sbo ? "select-before-operate" : "direct operate";

    /// <summary>Renders a toggle.</summary>
    public static string OnOff(bool v) => v ? "on" : "off";
}

/// <summary>What kind of dialog is open.</summary>
public enum ModalKind
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>A confirmation before a control goes out.</summary>
    Confirm,

    /// <summary>The control choices for one binary output.</summary>
    Control,

    /// <summary>Cold or warm restart.</summary>
    Restart,
}

/// <summary>One line of a dialog's choice list.</summary>
public readonly record struct ModalChoice(string Key, string Label);

/// <summary>The open dialog.</summary>
public sealed class ModalState
{
    /// <summary>What kind of dialog it is.</summary>
    public ModalKind Kind { get; init; }

    /// <summary>Its title, drawn in the frame.</summary>
    public string Title { get; init; } = "";

    /// <summary>What it says.</summary>
    public List<string> Lines { get; init; } = [];

    /// <summary>What it offers.</summary>
    public List<ModalChoice> Choices { get; init; } = [];

    /// <summary>The point it acts on, when it acts on one.</summary>
    public PointKey Target { get; init; }

    /// <summary>What a confirmation would run.</summary>
    public Cmd? Pending { get; init; }

    /// <summary>How that would be described in the log.</summary>
    public string Desc { get; init; } = "";
}

/// <summary>The transient banner that reports what an action did.</summary>
/// <remarks>
/// It duplicates the log line on purpose: the log is where an operator looks
/// afterwards, and the banner is what tells them now, without making them change
/// screens to find out whether a control went through.
/// </remarks>
public sealed class ToastState
{
    /// <summary>What it says.</summary>
    public string Text { get; private set; } = "";

    /// <summary>How loudly it says it.</summary>
    public string Level { get; private set; } = "";

    /// <summary>When it stops saying it.</summary>
    public DateTimeOffset Until { get; private set; }

    /// <summary>Whether anything is showing.</summary>
    public bool Active => Text.Length > 0;

    /// <summary>Shows a message for the next few seconds.</summary>
    public void Show(string level, string text, DateTimeOffset now)
    {
        if (now == default)
        {
            now = DateTimeOffset.Now;
        }

        Text = text;
        Level = level;
        Until = now + TimeSpan.FromSeconds(6);
    }

    /// <summary>Clears the message once its time is up.</summary>
    public void Expire(DateTimeOffset now)
    {
        if (Until != default && now > Until)
        {
            Text = "";
            Level = "";
            Until = default;
        }
    }
}
