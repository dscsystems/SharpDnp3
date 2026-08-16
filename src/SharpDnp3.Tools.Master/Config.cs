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
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SharpDnp3.Tools.Master;

/// <summary>A set of outstations to poll.</summary>
public sealed class SitesConfig
{
    /// <summary>
    /// This master's link address, shared by every session unless a site
    /// overrides it.
    /// </summary>
    public ushort Local { get; set; }

    /// <summary>The settings a site inherits when it does not say otherwise.</summary>
    public SiteDefaults Defaults { get; set; } = new();

    /// <summary>The outstations.</summary>
    public List<Site> Sites { get; set; } = [];

    /// <summary>
    /// A single outstation on localhost, which is what someone trying the tool
    /// against SharpDnp3.Tools.Outstation wants.
    /// </summary>
    public static SitesConfig Default() => new()
    {
        Local = 1,
        Defaults = new SiteDefaults
        {
            Poll = TimeSpan.FromSeconds(5),
            Integrity = TimeSpan.FromMinutes(5),
            Timeout = TimeSpan.FromSeconds(5),
            KeepAlive = TimeSpan.FromSeconds(30),
            SyncClock = true,
        },
    };

    /// <summary>Reads a configuration file.</summary>
    public static SitesConfig Load(string path)
    {
        var text = File.ReadAllText(path);

        // Unmatched properties are an error rather than a shrug: a typo must
        // fail here rather than be silently ignored and discovered as a poll
        // that was never actually configured.
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new GoDurationConverter())
            .Build();

        SitesConfig? cfg;
        try
        {
            cfg = deserializer.Deserialize<SitesConfig>(text);
        }
        catch (YamlException ex)
        {
            throw new FormatException($"{path}: {ex.Message}", ex);
        }

        if (cfg is null)
        {
            throw new FormatException($"{path}: empty configuration");
        }

        // A file that names no master address means the default one rather than
        // link address zero, which is a broadcast.
        if (cfg.Local == 0)
        {
            cfg.Local = 1;
        }

        return cfg;
    }

    /// <summary>
    /// Reports configuration problems before any connection is attempted.
    /// </summary>
    /// <remarks>
    /// Failing at startup beats failing at three in the morning when a poll
    /// that was never actually configured turns out to be missing.
    /// </remarks>
    public void Validate()
    {
        if (Sites.Count == 0)
        {
            throw new FormatException("no sites configured");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Sites.Count; i++)
        {
            var s = Sites[i];

            if (string.IsNullOrEmpty(s.Name))
            {
                throw new FormatException($"site {i} has no name");
            }

            if (!names.Add(s.Name))
            {
                throw new FormatException(
                    $"two sites are both named \"{s.Name}\"; names identify rows in the recording");
            }

            if (string.IsNullOrEmpty(s.Host) && string.IsNullOrEmpty(s.Serial))
            {
                throw new FormatException($"site \"{s.Name}\" has neither a host nor a serial port");
            }

            if (!string.IsNullOrEmpty(s.Host) && !string.IsNullOrEmpty(s.Serial))
            {
                throw new FormatException(
                    $"site \"{s.Name}\" has both a host and a serial port; pick one");
            }

            if (s.Address == 0)
            {
                throw new FormatException($"site \"{s.Name}\" has no outstation address");
            }

            if (s.Tls is not null)
            {
                if (string.IsNullOrEmpty(s.Tls.Cert) ||
                    string.IsNullOrEmpty(s.Tls.Key) ||
                    string.IsNullOrEmpty(s.Tls.Ca))
                {
                    throw new FormatException(
                        $"site \"{s.Name}\": TLS needs a cert, a key and a CA — a channel that " +
                        "does not verify its peer lets anyone who can reach the port operate plant");
                }

                if (!string.IsNullOrEmpty(s.Serial))
                {
                    throw new FormatException(
                        $"site \"{s.Name}\": TLS over a serial port is not a thing");
                }
            }

            var local = s.Local == 0 ? Local : s.Local;
            if (local == s.Address)
            {
                throw new FormatException(string.Format(
                    CultureInfo.InvariantCulture,
                    "site \"{0}\": the master and outstation addresses are both {1}",
                    s.Name, local));
            }
        }
    }

    /// <summary>Returns a copy of a site with the defaults folded in.</summary>
    public Site Resolved(Site site)
    {
        ArgumentNullException.ThrowIfNull(site);

        var s = site.Clone();

        if (s.Local == 0)
        {
            s.Local = Local;
        }

        if (s.Poll == TimeSpan.Zero)
        {
            s.Poll = Defaults.Poll;
        }

        if (s.Integrity == TimeSpan.Zero)
        {
            s.Integrity = Defaults.Integrity;
        }

        if (s.Timeout == TimeSpan.Zero)
        {
            s.Timeout = Defaults.Timeout;
        }

        if (s.KeepAlive == TimeSpan.Zero)
        {
            s.KeepAlive = Defaults.KeepAlive;
        }

        if (s.Baud == 0)
        {
            s.Baud = 9600;
        }

        s.Unsolicited ??= Defaults.Unsolicited;
        s.LinkConfirms ??= Defaults.LinkConfirms;
        s.SyncClock ??= Defaults.SyncClock;

        // A serial link has no transport-layer delivery guarantee, so
        // link-layer confirmation is what makes it reliable. Defaulting it on
        // there rather than making every serial configuration remember it.
        if (!string.IsNullOrEmpty(s.Serial) && s.LinkConfirms != true)
        {
            s.LinkConfirms = true;
        }

        return s;
    }
}

/// <summary>The settings a site inherits.</summary>
public sealed class SiteDefaults
{
    /// <summary>How often to poll the event classes.</summary>
    public TimeSpan Poll { get; set; }

    /// <summary>How often to re-read everything.</summary>
    public TimeSpan Integrity { get; set; }

    /// <summary>How long to wait for a response.</summary>
    public TimeSpan Timeout { get; set; }

    /// <summary>How long an idle link may stay silent before it is probed.</summary>
    public TimeSpan KeepAlive { get; set; }

    /// <summary>Whether to enable unsolicited reporting.</summary>
    public bool Unsolicited { get; set; }

    /// <summary>Whether the link layer confirms every frame.</summary>
    public bool LinkConfirms { get; set; }

    /// <summary>Whether to set the outstation's clock after connecting.</summary>
    public bool SyncClock { get; set; }
}

/// <summary>One outstation.</summary>
public sealed class Site
{
    /// <summary>Identifies the site in logs, CSV rows and the HTTP API.</summary>
    public string Name { get; set; } = "";

    /// <summary>host:port for TCP or TLS. Exactly one of this and <see cref="Serial"/> is required.</summary>
    public string Host { get; set; } = "";

    /// <summary>A serial port name.</summary>
    public string Serial { get; set; } = "";

    /// <summary>The serial line rate.</summary>
    public int Baud { get; set; }

    /// <summary>Overrides the master's link address for this site.</summary>
    public ushort Local { get; set; }

    /// <summary>The outstation's link address.</summary>
    public ushort Address { get; set; }

    /// <summary>Enables and configures a mutually authenticated channel.</summary>
    public TlsFiles? Tls { get; set; }

    /// <summary>The event class poll interval; zero inherits the default.</summary>
    public TimeSpan Poll { get; set; }

    /// <summary>The integrity poll interval; zero inherits the default.</summary>
    public TimeSpan Integrity { get; set; }

    /// <summary>The response timeout; zero inherits the default.</summary>
    public TimeSpan Timeout { get; set; }

    /// <summary>The keep-alive interval; zero inherits the default.</summary>
    public TimeSpan KeepAlive { get; set; }

    /// <summary>Whether to enable unsolicited reporting; null inherits.</summary>
    public bool? Unsolicited { get; set; }

    /// <summary>Whether to confirm link frames; null inherits.</summary>
    public bool? LinkConfirms { get; set; }

    /// <summary>Whether to set the outstation's clock; null inherits.</summary>
    public bool? SyncClock { get; set; }

    /// <summary>The classes to enable for unsolicited reporting.</summary>
    public Class UnsolMask => Unsolicited == true ? Class.Class123 : Class.None;

    /// <summary>Names the transport for the console and the HTTP API.</summary>
    public string Describe()
    {
        if (!string.IsNullOrEmpty(Serial))
        {
            return string.Format(
                CultureInfo.InvariantCulture, "serial {0} at {1} baud", Serial, Baud);
        }

        return Tls is not null ? "TLS " + Host : "TCP " + Host;
    }

    /// <summary>Returns a shallow copy, so folding defaults in cannot mutate the file's own view.</summary>
    public Site Clone() => (Site)MemberwiseClone();
}

/// <summary>Names the certificates for a TLS site.</summary>
public sealed class TlsFiles
{
    /// <summary>The client certificate this master presents.</summary>
    public string Cert { get; set; } = "";

    /// <summary>Its private key.</summary>
    public string Key { get; set; } = "";

    /// <summary>The authority used to verify the outstation.</summary>
    public string Ca { get; set; } = "";

    /// <summary>The name to expect in the outstation's certificate.</summary>
    public string ServerName { get; set; } = "";
}

/// <summary>Parses the duration spelling a DNP3 operator already types: 500ms, 30s, 5m, 1h.</summary>
public static class GoDuration
{
    /// <summary>Parses a duration, or throws <see cref="FormatException"/>.</summary>
    public static TimeSpan Parse(string s)
    {
        ArgumentException.ThrowIfNullOrEmpty(s);

        // A bare number is seconds, because that is what someone in a hurry
        // types and refusing it helps nobody.
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var bare))
        {
            return TimeSpan.FromSeconds(bare);
        }

        var total = TimeSpan.Zero;
        var i = 0;

        while (i < s.Length)
        {
            var start = i;
            while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] is '.' or '-' or '+'))
            {
                i++;
            }

            if (start == i)
            {
                throw new FormatException($"\"{s}\" is not a duration");
            }

            var number = double.Parse(s[start..i], CultureInfo.InvariantCulture);

            var unitStart = i;
            while (i < s.Length && char.IsAsciiLetter(s[i]))
            {
                i++;
            }

            var unit = s[unitStart..i];
            total += unit switch
            {
                "ns" => TimeSpan.FromTicks((long)(number / 100)),
                "us" or "µs" => TimeSpan.FromTicks((long)(number * 10)),
                "ms" => TimeSpan.FromMilliseconds(number),
                "s" => TimeSpan.FromSeconds(number),
                "m" => TimeSpan.FromMinutes(number),
                "h" => TimeSpan.FromHours(number),
                _ => throw new FormatException($"\"{s}\" has an unknown time unit \"{unit}\""),
            };
        }

        return total;
    }

    /// <summary>Renders a duration the way it would be typed back in.</summary>
    public static string ToString(TimeSpan d)
    {
        if (d == TimeSpan.Zero)
        {
            return "0s";
        }

        if (d.TotalMilliseconds < 1000)
        {
            return string.Format(
                CultureInfo.InvariantCulture, "{0:0.###}ms", d.TotalMilliseconds);
        }

        if (d.TotalSeconds < 60)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.###}s", d.TotalSeconds);
        }

        if (d.TotalMinutes < 60)
        {
            var seconds = d.Seconds + (d.Milliseconds / 1000.0);
            return seconds == 0
                ? string.Format(CultureInfo.InvariantCulture, "{0}m", (int)d.TotalMinutes)
                : string.Format(
                    CultureInfo.InvariantCulture, "{0}m{1:0.###}s", (int)d.TotalMinutes, seconds);
        }

        return d.Minutes == 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}h", (int)d.TotalHours)
            : string.Format(
                CultureInfo.InvariantCulture, "{0}h{1}m", (int)d.TotalHours, d.Minutes);
    }
}

/// <summary>Reads durations out of YAML in the spelling the flags use.</summary>
internal sealed class GoDurationConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(TimeSpan) || type == typeof(TimeSpan?);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        ArgumentNullException.ThrowIfNull(parser);

        var scalar = parser.Consume<YamlDotNet.Core.Events.Scalar>();
        if (string.IsNullOrEmpty(scalar.Value))
        {
            return type == typeof(TimeSpan?) ? null : TimeSpan.Zero;
        }

        return GoDuration.Parse(scalar.Value);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(emitter);

        var text = value is TimeSpan d ? GoDuration.ToString(d) : "0s";
        emitter.Emit(new YamlDotNet.Core.Events.Scalar(text));
    }
}
