// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpDnp3.Security;

/// <summary>Severity in order of increasing urgency.</summary>
public enum SecuritySeverity
{
    /// <summary>Routine information.</summary>
    Info = 1,
    /// <summary>An observation that may merit attention.</summary>
    Notice,
    /// <summary>A protocol or security warning.</summary>
    Warning,
    /// <summary>An event that merits prompt attention.</summary>
    Alert,
    /// <summary>A critical failure or observation.</summary>
    Critical,
}

/// <summary>The stack's response to the observation.</summary>
public enum SecurityAction
{
    /// <summary>Reported while processing continued.</summary>
    Observed,
    /// <summary>Input discarded.</summary>
    Ignored,
    /// <summary>Request or connection refused.</summary>
    Rejected,
    /// <summary>Request refused due to rate limiting.</summary>
    Throttled,
    /// <summary>Connection ended.</summary>
    Disconnected,
    /// <summary>Input refused by an explicit policy.</summary>
    Blocked,
}

/// <summary>Context for one security-relevant observation.</summary>
public sealed record SecurityEvent(string Kind)
{
    /// <summary>UTC time of the observation.</summary>
    public DateTimeOffset Time { get; init; }
    /// <summary>Protocol name used by event consumers.</summary>
    public string Protocol { get; init; } = "dnp3";
    /// <summary>Event category from the event catalog.</summary>
    public string? Category { get; init; }
    /// <summary>Urgency of the observation.</summary>
    public SecuritySeverity Severity { get; init; }
    /// <summary>How the stack handled the observation.</summary>
    public SecurityAction Action { get; init; }
    /// <summary>Role of the session that observed the event.</summary>
    public string? Role { get; init; }
    /// <summary>Transport carrying the connection.</summary>
    public string? Transport { get; init; }
    /// <summary>Process-wide connection correlation identifier.</summary>
    public long? ConnId { get; init; }
    /// <summary>Remote endpoint and its observed identity.</summary>
    public SecurityPeer? Peer { get; init; }
    /// <summary>Source link address.</summary>
    public ushort? LinkSrc { get; init; }
    /// <summary>Destination link address.</summary>
    public ushort? LinkDest { get; init; }
    /// <summary>Numeric link function.</summary>
    public byte? LinkFunc { get; init; }
    /// <summary>Received transport sequence number.</summary>
    public byte? TransportSeq { get; init; }
    /// <summary>Application function name.</summary>
    public string? AppFunc { get; init; }
    /// <summary>Received application sequence number.</summary>
    public byte? AppSeq { get; init; }
    /// <summary>Application sequence number the session expected.</summary>
    public byte? ExpectedSeq { get; init; }
    /// <summary>Indications carried by the response.</summary>
    public ushort? Iin { get; init; }
    /// <summary>DNP3 object group.</summary>
    public byte? Group { get; init; }
    /// <summary>DNP3 object variation.</summary>
    public byte? Variation { get; init; }
    /// <summary>Object qualifier octet.</summary>
    public byte? Qualifier { get; init; }
    /// <summary>Point index.</summary>
    public uint? Index { get; init; }
    /// <summary>Status returned for a control.</summary>
    public string? CommandStatus { get; init; }
    /// <summary>Control code octet.</summary>
    public byte? ControlCode { get; init; }
    /// <summary>Secure Authentication user number.</summary>
    public ushort? User { get; init; }
    /// <summary>File transfer status.</summary>
    public string? FileStatus { get; init; }
    /// <summary>Name supplied by a file operation.</summary>
    public string? FileName { get; init; }
    /// <summary>Non-secret context about the value observed.</summary>
    public string? Value { get; init; }
    /// <summary>Number of occurrences represented by this event.</summary>
    public int Count { get; init; } = 1;
    /// <summary>Time of the first occurrence in a repeat window.</summary>
    public DateTimeOffset? FirstSeen { get; init; }
    /// <summary>Human-readable explanation.</summary>
    public string? Detail { get; init; }
    /// <summary>Optional bounded sample with sensitive material omitted.</summary>
    public byte[]? Raw { get; init; }
    /// <summary>MITRE ATT&amp;CK for ICS technique hint.</summary>
    public string? Technique { get; init; }

    internal SecurityEvent ApplyDefaults()
    {
        var known = SecurityEventKind.Catalog.TryGetValue(Kind, out var info);
        return this with
        {
            Time = Time == default ? DateTimeOffset.UtcNow : Time,
            Category = Category ?? (known ? info.Category : "meta"),
            Severity = Severity == 0 ? (known ? info.Severity : SecuritySeverity.Notice) : Severity,
            Technique = Technique ?? info.Technique,
        };
    }
}

/// <summary>Identifies the remote endpoint of a connection.</summary>
public sealed record SecurityPeer
{
    /// <summary>Network or transport type.</summary>
    public string? Network { get; init; }
    /// <summary>Remote endpoint.</summary>
    [JsonPropertyName("remote_addr")]
    public string? Remote { get; init; }
    /// <summary>Local endpoint.</summary>
    [JsonPropertyName("local_addr")]
    public string? Local { get; init; }
    /// <summary>Peer MAC address when available.</summary>
    public string? Mac { get; init; }
    /// <summary>Source of the MAC address.</summary>
    public string? MacSource { get; init; }
    /// <summary>Whether a local interface can directly reach the peer.</summary>
    public bool? OnLink { get; init; }
    /// <summary>Name of the local interface.</summary>
    public string? Interface { get; init; }
    /// <summary>Serial device for a local serial connection.</summary>
    public string? SerialPort { get; init; }

    /// <summary>Time the observer was bound to this connection.</summary>
    public DateTimeOffset? ConnectedAt { get; init; }

    /// <summary>Last link source address seen on the connection.</summary>
    public ushort? LinkAddr { get; init; }

    /// <summary>Metadata for the TLS peer certificate.</summary>
    public SecurityTlsPeer? Tls { get; init; }
}

/// <summary>Certificate metadata associated with a TLS peer.</summary>
public sealed record SecurityTlsPeer(
    string Subject, string Issuer, string Serial,
    [property: JsonPropertyName("fingerprint_sha256")] string Fingerprint,
    DateTimeOffset NotAfter)
{
    /// <summary>Negotiated protocol version using its TLS wire number.</summary>
    public ushort? Version { get; init; }

    /// <summary>Negotiated cipher suite using its TLS wire number.</summary>
    public ushort? CipherSuite { get; init; }

    /// <summary>Server name associated with the handshake.</summary>
    public string? ServerName { get; init; }

    /// <summary>Whether the TLS connection verified this certificate.</summary>
    public bool Verified { get; init; }
}

/// <summary>Receives events on the dispatcher's delivery task.</summary>
public delegate void SecurityEventSink(SecurityEvent observation);

/// <summary>Composes event sinks.</summary>
public static class SecurityEventSinks
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter<SecuritySeverity>(JsonNamingPolicy.SnakeCaseLower),
            new JsonStringEnumConverter<SecurityAction>(JsonNamingPolicy.SnakeCaseLower),
        },
    };

    /// <summary>Writes each event as one JSON line. The caller owns the writer.</summary>
    public static SecurityEventSink JsonLines(TextWriter writer) => observation =>
    {
        writer.WriteLine(JsonSerializer.Serialize(observation, Json));
        writer.Flush();
    };

    /// <summary>Forwards each event to all the supplied sinks.</summary>
    public static SecurityEventSink FanOut(params SecurityEventSink[] sinks) => observation =>
    {
        foreach (var sink in sinks)
        {
            sink(observation);
        }
    };

    /// <summary>Forwards events meeting the severity and category filters.</summary>
    public static SecurityEventSink Filter(
        SecuritySeverity minimum, IReadOnlySet<string>? categories, SecurityEventSink sink) => observation =>
    {
        if (observation.Severity >= minimum &&
            (categories is null || observation.Category is { } category && categories.Contains(category)))
        {
            sink(observation);
        }
    };
}
