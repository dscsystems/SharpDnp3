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
using System.Text.Json.Serialization;
using SharpDnp3.Master;

namespace SharpDnp3.Tools.Master;

// Recording is deliberately CSV rather than a database.
//
// A recorder that needs a schema migration before it can start is a recorder
// that does not get used during an outage, and CSV is the format every engineer
// already has a tool for. Anyone who wants a database can read these files into
// one.

/// <summary>Writes measurements and events to CSV.</summary>
public sealed class Recorder : IDisposable
{
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fffffffK";

    private readonly Lock _gate = new();
    private readonly CsvFile _values;
    private readonly CsvFile _events;
    private bool _disposed;

    /// <summary>Opens the recording files under <paramref name="directory"/>.</summary>
    public Recorder(string directory)
    {
        Directory.CreateDirectory(directory);

        _values = new CsvFile(
            Path.Combine(directory, "values.csv"),
            ["received", "site", "type", "index", "value", "quality", "timestamp", "source"]);

        try
        {
            _events = new CsvFile(
                Path.Combine(directory, "events.csv"),
                ["received", "site", "type", "index", "value", "quality", "timestamp"]);
        }
        catch
        {
            _values.Dispose();
            throw;
        }
    }

    /// <summary>Writes one measurement.</summary>
    /// <remarks>
    /// Events go to both files: values.csv is the current picture, events.csv
    /// is the sequence of record. Keeping them separate matters because a
    /// sequence-of-events file with static poll data mixed in is no longer a
    /// sequence of events.
    /// </remarks>
    public void Record(string site, Update u)
    {
        var (value, flags, stamp) = Describe(u);
        var now = DateTimeOffset.Now.ToString(TimeFormat, CultureInfo.InvariantCulture);
        var source = u.Info.IsEvent ? "event" : "static";

        string[] row =
        [
            now, site, u.Type.ToString(),
            u.Index.ToString(CultureInfo.InvariantCulture),
            value, QualityText(flags, u.Type), StampText(stamp), source,
        ];

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _values.Write(row);
            if (u.Info.IsEvent)
            {
                _events.Write(row[..7]);
            }
        }
    }

    /// <summary>Flushes and closes the recording.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _events.Dispose();
            _values.Dispose();
        }
    }

    /// <summary>
    /// Renders the quality flags, dropping the state bit for binary points
    /// because the value column already says ON or OFF.
    /// </summary>
    /// <remarks>
    /// Repeating it makes every binary row look like it carries a flag it does
    /// not.
    /// </remarks>
    public static string QualityText(Flags f, PointType t)
    {
        if (t is PointType.Binary or PointType.BinaryOutputStatus)
        {
            f = f.Clear(Flags.StateBit);
        }

        return f.StringFor(t);
    }

    /// <summary>Renders a measurement timestamp, or nothing when it has none.</summary>
    public static string StampText(Timestamp t) =>
        t.IsValid ? t.Time.ToString(TimeFormat, CultureInfo.InvariantCulture) : "";

    /// <summary>Renders the moment a row was received.</summary>
    public static string ReceivedText(DateTimeOffset t) =>
        t.ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>Pulls the value, quality and timestamp out of an update.</summary>
    public static (string Value, Flags Flags, Timestamp Stamp) Describe(Update u) => u.Type switch
    {
        PointType.Binary => (BoolText(u.Binary.Value), u.Binary.Flags, u.Binary.Time),
        PointType.DoubleBitBinary =>
            (u.DoubleBit.Value.ToDisplayString(), u.DoubleBit.Flags, u.DoubleBit.Time),
        PointType.Counter => (
            u.Counter.Value.ToString(CultureInfo.InvariantCulture),
            u.Counter.Flags, u.Counter.Time),
        PointType.FrozenCounter => (
            u.FrozenCounter.Value.ToString(CultureInfo.InvariantCulture),
            u.FrozenCounter.Flags, u.FrozenCounter.Time),
        PointType.Analog => (FormatFloat(u.Analog.Value), u.Analog.Flags, u.Analog.Time),
        PointType.BinaryOutputStatus =>
            (BoolText(u.BinaryOutput.Value), u.BinaryOutput.Flags, u.BinaryOutput.Time),
        PointType.AnalogOutputStatus =>
            (FormatFloat(u.AnalogOutput.Value), u.AnalogOutput.Flags, u.AnalogOutput.Time),
        PointType.OctetString => (Printable(u.OctetString), Flags.Online, default),
        _ => ("", Flags.None, default),
    };

    /// <summary>Renders a binary state the way a station log does.</summary>
    public static string BoolText(bool v) => v ? "ON" : "OFF";

    /// <summary>Renders a number without trailing noise.</summary>
    public static string FormatFloat(double v) =>
        v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Renders an octet string, showing the bytes of anything that is not text
    /// rather than letting control characters loose in a CSV cell.
    /// </summary>
    public static string Printable(byte[]? b)
    {
        if (b is null || b.Length == 0)
        {
            return "";
        }

        foreach (var c in b)
        {
            if (c is < 0x20 or > 0x7e)
            {
                return Convert.ToHexString(b).ToLowerInvariant();
            }
        }

        return Encoding.ASCII.GetString(b);
    }

    /// <summary>One CSV file, flushed as it is written.</summary>
    private sealed class CsvFile : IDisposable
    {
        private readonly StreamWriter _writer;

        public CsvFile(string path, string[] header)
        {
            // Append rather than truncate: a restarted recorder must not erase
            // the history it was started to keep.
            var fresh = !File.Exists(path) || new FileInfo(path).Length == 0;

            _writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false));

            if (fresh)
            {
                Write(header);
            }
        }

        public void Write(string[] row)
        {
            var line = new StringBuilder();
            for (var i = 0; i < row.Length; i++)
            {
                if (i > 0)
                {
                    line.Append(',');
                }

                line.Append(Escape(row[i]));
            }

            _writer.WriteLine(line.ToString());

            // Flushed per row: a recorder that loses the last minute of an
            // outage to a buffer is worse than one that writes a little more
            // slowly.
            _writer.Flush();
        }

        public void Dispose()
        {
            _writer.Flush();
            _writer.Dispose();
        }

        private static string Escape(string field)
        {
            if (field.AsSpan().IndexOfAny(",\"\r\n") < 0)
            {
                return field;
            }

            return "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }
    }
}

// ---------- Live snapshot ----------

/// <summary>Identifies a point within a site.</summary>
public readonly record struct PointKey(PointType Type, ushort Index);

/// <summary>One point as the snapshot holds it.</summary>
public sealed class PointValue
{
    /// <summary>The site the point belongs to.</summary>
    [JsonPropertyName("site")]
    public string Site { get; init; } = "";

    /// <summary>The measurement type.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    /// <summary>The point index.</summary>
    [JsonPropertyName("index")]
    public ushort Index { get; init; }

    /// <summary>The latest value, rendered.</summary>
    [JsonPropertyName("value")]
    public string Value { get; init; } = "";

    /// <summary>The quality flags, named.</summary>
    [JsonPropertyName("quality")]
    public string Quality { get; init; } = "";

    /// <summary>Whether the quality is one a control room would trust.</summary>
    [JsonPropertyName("good")]
    public bool Good { get; init; }

    /// <summary>The outstation's timestamp, when it sent one.</summary>
    [JsonPropertyName("timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Stamp { get; init; } = "";

    /// <summary>When this master received it.</summary>
    [JsonPropertyName("received")]
    public string Received { get; init; } = "";

    /// <summary>Whether it arrived as an event or as static data.</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = "";
}

/// <summary>What the API reports about one session.</summary>
public sealed class SiteStatus
{
    /// <summary>The site's name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>What it is being polled over.</summary>
    [JsonPropertyName("target")]
    public string Target { get; init; } = "";

    /// <summary>Whether the link is up.</summary>
    [JsonPropertyName("connected")]
    public bool Connected { get; init; }

    /// <summary>The internal indications last reported.</summary>
    [JsonPropertyName("indications")]
    public string Indications { get; init; } = "";

    /// <summary>How many tasks the session has run.</summary>
    [JsonPropertyName("tasks_run")]
    public ulong TasksRun { get; init; }

    /// <summary>How many of them succeeded.</summary>
    [JsonPropertyName("tasks_succeeded")]
    public ulong TasksSucceeded { get; init; }

    /// <summary>How many of them failed.</summary>
    [JsonPropertyName("tasks_failed")]
    public ulong TasksFailed { get; init; }

    /// <summary>How many responses never arrived.</summary>
    [JsonPropertyName("response_timeouts")]
    public ulong Timeouts { get; init; }

    /// <summary>How many unsolicited fragments arrived.</summary>
    [JsonPropertyName("unsolicited")]
    public ulong Unsolicited { get; init; }

    /// <summary>How many outstation restarts were seen.</summary>
    [JsonPropertyName("restarts_seen")]
    public ulong Restarts { get; init; }

    /// <summary>How many times the channel connected.</summary>
    [JsonPropertyName("connections")]
    public ulong Connections { get; init; }
}

/// <summary>Holds the latest value of every point, for the HTTP API.</summary>
/// <remarks>
/// It is separate from the recorder because they answer different questions:
/// the recording is what happened, the snapshot is what is true now.
/// </remarks>
public sealed class Snapshot
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<PointKey, PointValue>> _values = [];
    private readonly Dictionary<string, SiteStatus> _status = [];

    /// <summary>Folds one measurement into the current picture.</summary>
    public void Apply(string site, Update u)
    {
        var (value, flags, stamp) = Recorder.Describe(u);

        lock (_gate)
        {
            if (!_values.TryGetValue(site, out var points))
            {
                points = [];
                _values[site] = points;
            }

            points[new PointKey(u.Type, u.Index)] = new PointValue
            {
                Site = site,
                Type = u.Type.ToString(),
                Index = u.Index,
                Value = value,
                Quality = Recorder.QualityText(flags, u.Type),
                Good = flags.IsGood(),
                Stamp = Recorder.StampText(stamp),
                Received = Recorder.ReceivedText(DateTimeOffset.Now),
                Source = u.Info.IsEvent ? "event" : "static",
            };
        }
    }

    /// <summary>Records what one session is doing.</summary>
    public void SetStatus(SiteStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        lock (_gate)
        {
            _status[status.Name] = status;
        }
    }

    /// <summary>Returns every known value, optionally for one site only.</summary>
    /// <remarks>
    /// Sorted, so the output is stable between requests: a diff of two API
    /// responses should show what changed, not the dictionary order.
    /// </remarks>
    public List<PointValue> Points(string? site)
    {
        lock (_gate)
        {
            var outp = new List<PointValue>();
            foreach (var (name, points) in _values)
            {
                if (!string.IsNullOrEmpty(site) && !string.Equals(name, site, StringComparison.Ordinal))
                {
                    continue;
                }

                outp.AddRange(points.Values);
            }

            outp.Sort(static (a, b) =>
            {
                var c = string.CompareOrdinal(a.Site, b.Site);
                if (c != 0)
                {
                    return c;
                }

                c = string.CompareOrdinal(a.Type, b.Type);
                return c != 0 ? c : a.Index.CompareTo(b.Index);
            });
            return outp;
        }
    }

    /// <summary>Returns what every session is doing, by name.</summary>
    public List<SiteStatus> Status()
    {
        lock (_gate)
        {
            var outp = new List<SiteStatus>(_status.Values);
            outp.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            return outp;
        }
    }

    /// <summary>Renders a one-line-per-site overview for the console.</summary>
    public string Summary()
    {
        var b = new StringBuilder();
        foreach (var st in Status())
        {
            b.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "  {0,-16} {1,-4} {2,-24} tasks {3}/{4}  timeouts {5}",
                st.Name, st.Connected ? "up" : "down", st.Indications,
                st.TasksSucceeded, st.TasksRun, st.Timeouts));
        }

        return b.ToString();
    }
}
