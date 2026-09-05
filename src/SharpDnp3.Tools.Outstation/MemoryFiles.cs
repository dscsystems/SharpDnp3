// Copyright (C) 2026 Ricardo Olsen / DSC Systems.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option)
// any later version. It is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details, in the LICENSE file at
// the root of this repository or at <https://www.gnu.org/licenses/>.
//
// A simulated RTU has files on it: the configuration it was commissioned with,
// an event log, a firmware version. A master being developed against this tool
// needs somewhere to point its file transfer, and asking whoever runs it to
// prepare a directory first would mean the feature goes untested — the same
// reason the plant simulation ships with a default substation.
//
// So the device serves an in-memory filesystem unless told to serve a real
// directory. Nothing here touches the host: reading is synthesised, and what a
// master writes stays in this process and disappears with it.

using System.Globalization;
using System.Text;
using SharpDnp3.Outstation;

namespace SharpDnp3.Tools.Outstation;

/// <summary>A filesystem in memory.</summary>
/// <remarks>
/// It is deliberately flat apart from one directory, because the point is to
/// exercise a master's file transfer rather than to be a filesystem: a listing
/// with a directory in it, files of different sizes, and one that is not there.
/// </remarks>
public sealed class MemoryFileHandler : IFileHandler
{
    /// <summary>
    /// Caps what one written file may hold, so a master with a bug — or a test
    /// of what happens when a transfer runs away — cannot exhaust a simulator
    /// that is meant to be left running.
    /// </summary>
    public const int FileLimit = 4 << 20;

    private sealed class Entry
    {
        public required string Name { get; init; }

        public byte[] Data { get; set; } = [];

        public bool IsDirectory { get; init; }

        public DateTimeOffset Created { get; init; }

        public FilePermissions Permissions { get; init; }
    }

    private const FilePermissions ReadWrite =
        FilePermissions.OwnerRead | FilePermissions.OwnerWrite | FilePermissions.GroupRead;

    private const FilePermissions ReadOnlyPerms =
        FilePermissions.OwnerRead | FilePermissions.GroupRead | FilePermissions.WorldRead;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _files = [];
    private readonly bool _readOnly;

    /// <summary>
    /// Builds the device's filesystem from the simulated plant, so the
    /// configuration a master reads back describes the device it is talking to.
    /// </summary>
    public MemoryFileHandler(PlantConfig plant, DeviceConfig device, bool readOnly)
    {
        ArgumentNullException.ThrowIfNull(plant);
        ArgumentNullException.ThrowIfNull(device);

        _readOnly = readOnly;
        var now = DateTimeOffset.UtcNow;

        _files["/logs"] = new Entry
        {
            Name = "/logs",
            IsDirectory = true,
            Created = now,
            Permissions = FilePermissions.OwnerRead | FilePermissions.OwnerExecute,
        };

        Add("/device.txt", ReadOnlyPerms, DeviceFile(plant, device), now);
        Add("/points.txt", ReadOnlyPerms, PointsFile(plant), now);
        Add("/config.yaml", ReadWrite, ConfigFile(plant), now);
        Add("/logs/events.log", ReadOnlyPerms, EventLogFile(now), now);
    }

    private void Add(string name, FilePermissions perms, string content, DateTimeOffset now) =>
        _files[name] = new Entry
        {
            Name = name,
            Data = Encoding.UTF8.GetBytes(content),
            Created = now,
            Permissions = perms,
        };

    /// <summary>
    /// Normalises a DNP3 path the way the device thinks of it: rooted, with no
    /// trailing separator.
    /// </summary>
    private static string Normalise(string name)
    {
        var s = (name ?? string.Empty).Replace('\\', '/').Trim();
        if (!s.StartsWith('/'))
        {
            s = "/" + s;
        }

        while (s.Length > 1 && s.EndsWith('/'))
        {
            s = s[..^1];
        }

        return s;
    }

    /// <inheritdoc/>
    public FileStatus Info(string name, out FileEntry info)
    {
        var path = Normalise(name);
        info = default;

        if (path == "/")
        {
            info = new FileEntry
            {
                Name = "/",
                Type = FileType.Directory,
                Permissions = FilePermissions.OwnerRead | FilePermissions.OwnerExecute,
            };
            return FileStatus.Success;
        }

        lock (_gate)
        {
            if (!_files.TryGetValue(path, out var e))
            {
                return FileStatus.NotFound;
            }

            info = InfoFor(e);
            return FileStatus.Success;
        }
    }

    /// <inheritdoc/>
    public FileStatus List(string name, out IReadOnlyList<FileEntry> entries)
    {
        var path = Normalise(name);
        var prefix = path == "/" ? "/" : path + "/";

        lock (_gate)
        {
            if (path != "/" &&
                (!_files.TryGetValue(path, out var dir) || !dir.IsDirectory))
            {
                entries = [];
                return _files.ContainsKey(path) ? FileStatus.InvalidMode : FileStatus.NotFound;
            }

            var output = new List<FileEntry>();
            foreach (var (key, e) in _files)
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                // Direct children only: a listing is one level, not a tree.
                if (key[prefix.Length..].Contains('/', StringComparison.Ordinal))
                {
                    continue;
                }

                output.Add(InfoFor(e) with { Name = key[prefix.Length..] });
            }

            output.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            entries = output;
            return FileStatus.Success;
        }
    }

    /// <inheritdoc/>
    public FileStatus OpenRead(string name, out Stream? stream, out FileEntry info)
    {
        var path = Normalise(name);
        stream = null;
        info = default;

        lock (_gate)
        {
            if (!_files.TryGetValue(path, out var e))
            {
                return FileStatus.NotFound;
            }

            if (e.IsDirectory)
            {
                // A directory is read as a listing, which the session builds.
                return FileStatus.InvalidMode;
            }

            info = InfoFor(e);
            stream = new MemoryStream(e.Data, writable: false);
            return FileStatus.Success;
        }
    }

    /// <inheritdoc/>
    public FileStatus OpenWrite(string name, FileOpenMode mode, uint size, out Stream? stream)
    {
        var path = Normalise(name);
        stream = null;

        if (_readOnly)
        {
            return FileStatus.PermissionDenied;
        }

        if (size > FileLimit)
        {
            return FileStatus.BufferOverrun;
        }

        lock (_gate)
        {
            if (_files.TryGetValue(path, out var existing) && existing.IsDirectory)
            {
                return FileStatus.InvalidMode;
            }

            var entry = existing ?? new Entry
            {
                Name = path,
                Created = DateTimeOffset.UtcNow,
                Permissions = ReadWrite,
            };

            _files[path] = entry;

            var initial = mode == FileOpenMode.Append ? entry.Data : [];
            stream = new EntryStream(this, entry, initial);
            return FileStatus.Success;
        }
    }

    /// <inheritdoc/>
    public FileStatus Delete(string name)
    {
        var path = Normalise(name);

        if (_readOnly)
        {
            return FileStatus.PermissionDenied;
        }

        lock (_gate)
        {
            if (!_files.TryGetValue(path, out var e))
            {
                return FileStatus.NotFound;
            }

            if (e.IsDirectory)
            {
                return FileStatus.InvalidMode;
            }

            _files.Remove(path);
            return FileStatus.Success;
        }
    }

    private static FileEntry InfoFor(Entry e) => new()
    {
        Name = e.Name,
        Type = e.IsDirectory ? FileType.Directory : FileType.Simple,
        Size = (uint)e.Data.Length,
        Created = e.Created,
        Permissions = e.Permissions,
    };

    /// <summary>
    /// A write stream that publishes what it collected when it is closed.
    /// </summary>
    /// <remarks>
    /// Publishing on close rather than per block is what makes a failed
    /// transfer leave the previous content alone: a master that gives up half
    /// way has not replaced the file.
    /// </remarks>
    private sealed class EntryStream : MemoryStream
    {
        private readonly MemoryFileHandler _owner;
        private readonly Entry _entry;

        public EntryStream(MemoryFileHandler owner, Entry entry, byte[] initial)
        {
            _owner = owner;
            _entry = entry;
            if (initial.Length > 0)
            {
                Write(initial, 0, initial.Length);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Guard(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Guard(buffer.Length);
            base.Write(buffer);
        }

        private void Guard(int count)
        {
            if (Length + count > FileLimit)
            {
                throw new IOException(string.Format(
                    CultureInfo.InvariantCulture,
                    "the simulated device holds at most {0} octets per file", FileLimit));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_owner._gate)
                {
                    _entry.Data = ToArray();
                }
            }

            base.Dispose(disposing);
        }
    }

    // ---------- the synthesised content ----------

    /// <summary>
    /// The identification a real RTU exposes: what it is, what it runs, and the
    /// addresses it answers on.
    /// </summary>
    private static string DeviceFile(PlantConfig plant, DeviceConfig device)
    {
        var b = new StringBuilder();

        // The same identity the device answers a group 0 read with. A device
        // whose file and whose attributes disagree about its own serial number
        // is a device nobody can commission.
        b.AppendFormat(CultureInfo.InvariantCulture, "vendor:        {0}\n", device.Vendor);
        b.AppendFormat(CultureInfo.InvariantCulture, "model:         {0}\n", device.Model);
        b.AppendFormat(CultureInfo.InvariantCulture, "version:       {0}\n", device.Version);
        b.AppendFormat(CultureInfo.InvariantCulture, "serial:        {0}\n", device.Serial);
        b.AppendFormat(CultureInfo.InvariantCulture, "address:       {0}\n", plant.Address);
        b.AppendFormat(CultureInfo.InvariantCulture, "master:        {0}\n", plant.Master);
        b.AppendFormat(
            CultureInfo.InvariantCulture, "breakers:      {0}\n", plant.Breakers.Count);
        b.AppendFormat(
            CultureInfo.InvariantCulture, "analog_inputs: {0}\n", plant.Analogs.Count);
        b.AppendFormat(
            CultureInfo.InvariantCulture, "counters:      {0}\n", plant.Counters.Count);

        return b.ToString();
    }

    /// <summary>The point list, which is what a commissioning engineer wants.</summary>
    private static string PointsFile(PlantConfig plant)
    {
        var b = new StringBuilder();
        b.Append("index  type            name\n");

        foreach (var br in plant.Breakers)
        {
            b.AppendFormat(
                CultureInfo.InvariantCulture,
                "{0,5}  binary          {1}\n", br.StatusIndex, br.Name);
        }

        foreach (var a in plant.Analogs)
        {
            b.AppendFormat(
                CultureInfo.InvariantCulture,
                "{0,5}  analog          {1}{2}\n",
                a.Index, a.Name, string.IsNullOrEmpty(a.Units) ? "" : " (" + a.Units + ")");
        }

        foreach (var c in plant.Counters)
        {
            b.AppendFormat(
                CultureInfo.InvariantCulture,
                "{0,5}  counter         {1}\n", c.Index, c.Name);
        }

        return b.ToString();
    }

    /// <summary>
    /// A YAML rendering of the running configuration, which is the file an
    /// engineer would expect to pull off a device and edit.
    /// </summary>
    private static string ConfigFile(PlantConfig plant)
    {
        var b = new StringBuilder();
        b.Append("# The running configuration of this simulated outstation.\n");
        b.Append("# Writing it back changes nothing: the simulator keeps what\n");
        b.Append("# a master sends so the transfer can be verified, and goes on\n");
        b.Append("# running the plant it started with.\n");
        b.AppendFormat(
            CultureInfo.InvariantCulture,
            "address: {0}\nmaster: {1}\n", plant.Address, plant.Master);

        if (plant.Breakers.Count > 0)
        {
            b.Append("breakers:\n");
            foreach (var br in plant.Breakers)
            {
                b.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "  - {{status_index: {0}, control_index: {1}, name: \"{2}\"}}\n",
                    br.StatusIndex, br.ControlIndex, br.Name);
            }
        }

        if (plant.Analogs.Count > 0)
        {
            b.Append("analogs:\n");
            foreach (var a in plant.Analogs)
            {
                b.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "  - {{index: {0}, name: \"{1}\", units: \"{2}\"}}\n",
                    a.Index, a.Name, a.Units);
            }
        }

        return b.ToString();
    }

    /// <summary>A plausible event log, so a read returns something to look at.</summary>
    private static string EventLogFile(DateTimeOffset now)
    {
        var b = new StringBuilder();
        (TimeSpan Ago, string Text)[] lines =
        [
            (TimeSpan.FromMinutes(37), "outstation started"),
            (TimeSpan.FromMinutes(31), "master 1 connected"),
            (TimeSpan.FromMinutes(30), "integrity poll served"),
            (TimeSpan.FromMinutes(12), "breaker 0 opened by control"),
            (TimeSpan.FromMinutes(11), "breaker 0 closed by control"),
            (TimeSpan.FromMinutes(2), "clock set by master"),
        ];

        foreach (var (ago, text) in lines)
        {
            b.AppendFormat(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss}  {1}\n", (now - ago).UtcDateTime, text);
        }

        return b.ToString();
    }
}
