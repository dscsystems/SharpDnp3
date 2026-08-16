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

namespace SharpDnp3.Tools.Explorer;

// A form is several labelled fields edited together, which the single-line
// prompt cannot express: the connection parameters only make sense as a set,
// because an address and the link addresses have to change together to reach a
// different device and applying them one at a time would reconnect three times
// on the way to somewhere the operator never meant to go.

/// <summary>One labelled field of an editor.</summary>
public sealed class FormField
{
    /// <summary>What the field is called.</summary>
    public string Label { get; init; } = "";

    /// <summary>What it holds.</summary>
    public string Value { get; set; } = "";

    /// <summary>What it means, shown while it has the cursor.</summary>
    public string Hint { get; init; } = "";
}

/// <summary>The open editor.</summary>
public sealed class FormState
{
    /// <summary>
    /// Names the connection fields by what they are rather than by position, so
    /// reading a value back cannot silently pick up its neighbour.
    /// </summary>
    public const int FieldAddress = 0;

    /// <summary>The master's link address.</summary>
    public const int FieldLocal = 1;

    /// <summary>The outstation's link address.</summary>
    public const int FieldRemote = 2;

    /// <summary>The response timeout.</summary>
    public const int FieldTimeout = 3;

    /// <summary>The poll interval.</summary>
    public const int FieldPoll = 4;

    /// <summary>How many fields the connection editor has.</summary>
    public const int NumFields = 5;

    /// <summary>Whether an editor is open.</summary>
    public bool Active { get; init; }

    /// <summary>Its title, drawn in the frame.</summary>
    public string Title { get; init; } = "";

    /// <summary>Its fields, in order.</summary>
    public List<FormField> Fields { get; init; } = [];

    /// <summary>Which field has the cursor.</summary>
    public int Cursor { get; set; }

    /// <summary>Scrolls the field list on a terminal too short to show it whole.</summary>
    public int Offset { get; set; }

    /// <summary>
    /// The last rejection, kept on screen so the operator can see what was wrong
    /// with what they typed while they retype it.
    /// </summary>
    public string Error { get; set; } = "";

    /// <summary>The field with the cursor, or null when there is none.</summary>
    public FormField? Focused =>
        Cursor >= 0 && Cursor < Fields.Count ? Fields[Cursor] : null;

    /// <summary>Moves the cursor between fields.</summary>
    public void Move(int delta)
    {
        if (Fields.Count == 0)
        {
            return;
        }

        // Wrapping, because a five-field form is a loop in the operator's head
        // and stopping dead at the last field reads as the key having failed.
        Cursor = (Cursor + delta + Fields.Count) % Fields.Count;
    }
}

public sealed partial class Model
{
    /// <summary>Fills the editor from the live connection.</summary>
    public void OpenConnectionForm()
    {
        var l = Conn.Current;
        Form = new FormState
        {
            Active = true,
            Title = "Connection",
            Fields =
            [
                new FormField
                {
                    Label = "Outstation",
                    Value = l.Address,
                    Hint = "host:port, /dev/ttyUSB0@9600, or demo",
                },
                new FormField
                {
                    Label = "Local address",
                    Value = l.Local.ToString(CultureInfo.InvariantCulture),
                    Hint = "this master's link address",
                },
                new FormField
                {
                    Label = "Remote address",
                    Value = l.Remote.ToString(CultureInfo.InvariantCulture),
                    Hint = "the outstation's link address",
                },
                new FormField
                {
                    Label = "Response timeout",
                    Value = Duration.ToText(l.Timeout),
                    Hint = "how long to wait for an answer",
                },
                new FormField
                {
                    Label = "Poll interval",
                    Value = Duration.ToText(l.Poll),
                    Hint = "0 disables the periodic class poll",
                },
            ],
        };
    }

    private Cmd? HandleFormKey(string key)
    {
        var f = Form;

        switch (key)
        {
            case "esc":
                Form = new FormState();
                return null;

            case "enter":
                return SubmitForm();

            // Only the arrows and tab move between fields. j and k are letters
            // here, and a form that ate them could not be given a serial device
            // to talk to.
            case "up" or "shift+tab":
                f.Move(-1);
                return null;
            case "down" or "tab":
                f.Move(1);
                return null;

            case "backspace":
                if (f.Focused is { } back && back.Value.Length > 0)
                {
                    back.Value = back.Value[..^1];
                }

                return null;

            case "ctrl+u":
                if (f.Focused is { } clear)
                {
                    clear.Value = "";
                }

                return null;

            case "space":
                if (f.Focused is { } spaced)
                {
                    spaced.Value += " ";
                }

                return null;

            default:
                if (key.Length == 1 && f.Focused is { } typed)
                {
                    typed.Value += key;
                }

                return null;
        }
    }

    /// <summary>Validates every field before changing anything.</summary>
    /// <remarks>
    /// Nothing is applied until all of it parses, because a reconnect that took
    /// the new address and kept the old link address would be a third device
    /// that the operator never asked to talk to.
    /// </remarks>
    private Cmd? SubmitForm()
    {
        LinkParams p;
        try
        {
            p = ParseForm(Conn.Current, Form.Fields);
        }
        catch (FormatException ex)
        {
            Form.Error = ex.Message;
            return null;
        }

        var was = Conn.Current;
        Form = new FormState();

        if (was.SameDevice(p))
        {
            // Only the timing changed, so what is on screen still describes the
            // device in front of the operator and is worth keeping.
            AddLog("info", "reconnecting to " + p.Target);
        }
        else
        {
            // A different device, so the old measurements are not this device's.
            // Keeping them would leave an operator reading values that were
            // never polled from the thing they are now connected to.
            ForgetDevice();
            AddLog("info", string.Format(
                CultureInfo.InvariantCulture,
                "connecting to {0} as master {1} → outstation {2}", p.Target, p.Local, p.Remote));
        }

        Status = "connecting";
        Connected = false;
        LastError = "";
        LinkSince = default;
        Toast.Show("info", "connecting to " + p.Target, Now);
        return Conn.Reconnect(p);
    }

    /// <summary>Drops everything that described the previous outstation.</summary>
    private void ForgetDevice()
    {
        Points = [];
        PointsOrder = [];
        Events = [];
        Iin = "";
        Stats = default;
        for (var i = 0; i < _cursor.Length; i++)
        {
            _cursor[i] = 0;
            _offset[i] = 0;
        }
    }

    /// <summary>Reads the editor back into connection parameters.</summary>
    public static LinkParams ParseForm(LinkParams basis, IReadOnlyList<FormField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (fields.Count < FormState.NumFields)
        {
            throw new FormatException("connection: the form is incomplete");
        }

        var outp = basis.WithAddress(fields[FormState.FieldAddress].Value);

        var local = LinkParams.ParseLinkAddr("local address", fields[FormState.FieldLocal].Value);
        var remote = LinkParams.ParseLinkAddr(
            "remote address", fields[FormState.FieldRemote].Value);
        var timeout = LinkParams.ParseInterval("timeout", fields[FormState.FieldTimeout].Value);
        var poll = LinkParams.ParseInterval("poll", fields[FormState.FieldPoll].Value);

        outp = outp with { Local = local, Remote = remote, Timeout = timeout, Poll = poll };
        outp.Validate();
        return outp;
    }
}
