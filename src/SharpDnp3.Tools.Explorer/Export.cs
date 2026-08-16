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

// Export exists because the answer to "what is this device reporting" usually
// has to leave the terminal: it goes into a commissioning report, an email to
// the vendor, or a diff against yesterday. Writing what is on screen — after the
// filter and the sort, not before — means the operator exports the view they
// were looking at rather than something they have to reconstruct.

public sealed partial class Model
{
    private const string CsvTimeFormat = "yyyy-MM-ddTHH:mm:ss.fffffffK";

    /// <summary>Writes the current list to a CSV file beside the tool.</summary>
    public Cmd Export()
    {
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string name;
        var rows = new List<string[]>();

        switch (Screen)
        {
            case Screen.Points or Screen.Overview:
                name = "dnp3-points-" + stamp + ".csv";
                rows.Add([
                    "type", "index", "value", "quality", "timestamp", "timestamp_quality",
                    "received", "updates", "events", "source",
                ]);
                foreach (var p in VisiblePoints())
                {
                    rows.Add([
                        p.Key.Type.ToString(),
                        p.Key.Index.ToString(CultureInfo.InvariantCulture),
                        p.Value,
                        View.QualityText(p.Flags, p.Key.Type),
                        StampCsv(p.Time),
                        p.Time.Quality.ToDisplayString(),
                        p.Updated.ToString(CsvTimeFormat, CultureInfo.InvariantCulture),
                        p.Updates.ToString(CultureInfo.InvariantCulture),
                        p.Events.ToString(CultureInfo.InvariantCulture),
                        p.GV.ToString(),
                    ]);
                }

                break;

            case Screen.Events:
                name = "dnp3-events-" + stamp + ".csv";
                rows.Add([
                    "received", "type", "index", "value", "quality", "timestamp", "class", "source",
                ]);
                foreach (var e in VisibleEvents())
                {
                    rows.Add([
                        e.At.ToString(CsvTimeFormat, CultureInfo.InvariantCulture),
                        e.Key.Type.ToString(),
                        e.Key.Index.ToString(CultureInfo.InvariantCulture),
                        e.Value,
                        View.QualityText(e.Flags, e.Key.Type),
                        StampCsv(e.Stamp),
                        View.ClassText(e.Class),
                        e.GV.ToString(),
                    ]);
                }

                break;

            case Screen.Log:
                name = "dnp3-log-" + stamp + ".csv";
                rows.Add(["time", "level", "message"]);
                foreach (var l in VisibleLogs())
                {
                    rows.Add([
                        l.At.ToString(CsvTimeFormat, CultureInfo.InvariantCulture),
                        l.Level,
                        l.Text,
                    ]);
                }

                break;

            default:
                return static () => Task.FromResult<IMsg?>(
                    new CommandResultMsg("nothing to export from this screen"));
        }

        if (rows.Count == 1)
        {
            return static () => Task.FromResult<IMsg?>(
                new CommandResultMsg("nothing to export — the list is empty"));
        }

        return () =>
        {
            try
            {
                var b = new StringBuilder();
                foreach (var row in rows)
                {
                    for (var i = 0; i < row.Length; i++)
                    {
                        if (i > 0)
                        {
                            b.Append(',');
                        }

                        b.Append(Escape(row[i]));
                    }

                    b.Append('\n');
                }

                File.WriteAllText(name, b.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult<IMsg?>(
                    new CommandResultMsg("export failed: " + ex.Message));
            }

            return Task.FromResult<IMsg?>(new CommandResultMsg(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "wrote {0} rows to {1}", rows.Count - 1, name),
                true));
        };
    }

    private static string StampCsv(Timestamp t) =>
        t.IsValid ? t.Time.ToString(CsvTimeFormat, CultureInfo.InvariantCulture) : "";

    private static string Escape(string field)
    {
        if (field.AsSpan().IndexOfAny(",\"\r\n") < 0)
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
