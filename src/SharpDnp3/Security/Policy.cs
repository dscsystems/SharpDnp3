// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Net;
using System.Net.NetworkInformation;
using SharpDnp3.App;

namespace SharpDnp3.Security;

/// <summary>A token-bucket rate. A negative count disables the check.</summary>
public readonly record struct SecurityRate(int Count, TimeSpan Period);

/// <summary>How to interpret frames addressed to another station.</summary>
public enum SecurityLinkTopology
{
    /// <summary>Uses the topology implied by the channel.</summary>
    Auto,
    /// <summary>Expects only two stations on the link.</summary>
    PointToPoint,
    /// <summary>Expects traffic for other stations.</summary>
    SharedBus,
}

/// <summary>Controls security reporting and opt-in enforcement for a session.</summary>
public sealed record SecurityPolicy
{
    /// <summary>Receives security observations when no shared dispatcher is supplied.</summary>
    public SecurityEventSink? Sink { get; init; }
    /// <summary>Shared dispatcher owned by the caller.</summary>
    public SecurityDispatcher? Dispatcher { get; init; }
    /// <summary>Options for a session-owned dispatcher.</summary>
    public SecurityDispatcherOptions? DispatcherOptions { get; init; }
    /// <summary>Maximum bytes retained in an event sample. Zero uses 64 bytes. A negative value disables samples.</summary>
    public int MaxRaw { get; init; } = 64;
    /// <summary>Suppresses routine audit events.</summary>
    public bool DisableAudit { get; init; }

    /// <summary>Disables neighbor-table lookups.</summary>
    public bool DisableMacLookup { get; init; }
    /// <summary>Optional application-supplied neighbor resolver.</summary>
    public SecurityNeighborResolver? Resolver { get; init; }
    /// <summary>History shared across connections.</summary>
    public SecurityPeerCache? MacCache { get; init; }
    /// <summary>Maximum time allowed for a neighbor lookup.</summary>
    public TimeSpan MacLookupTimeout { get; init; } = TimeSpan.FromMilliseconds(200);
    /// <summary>How often to check the active peer again. Zero disables rechecks.</summary>
    public TimeSpan MacRecheckInterval { get; init; }
    /// <summary>Closes a connection if its peer changes MAC during the session.</summary>
    public bool DisconnectOnMacChange { get; init; }
    /// <summary>Expected MAC addresses for selected peer IPs.</summary>
    public IReadOnlyDictionary<IPAddress, PhysicalAddress>? PeerBindings { get; init; }
    /// <summary>Rejects peers whose pinned MAC is missing or differs.</summary>
    public bool RejectBindingMismatch { get; init; }

    /// <summary>CIDR networks expected to connect to this server.</summary>
    public IReadOnlyList<IPNetwork>? AllowedPeers { get; init; }
    /// <summary>Rejects peers outside the expected networks.</summary>
    public bool RejectUnlisted { get; init; }
    /// <summary>Connection rate threshold for each source IP.</summary>
    public SecurityRate AcceptRate { get; init; } = new(10, TimeSpan.FromSeconds(10));
    /// <summary>Connection rate threshold across all sources.</summary>
    public SecurityRate GlobalAcceptRate { get; init; } = new(50, TimeSpan.FromSeconds(10));
    /// <summary>Rejects connections beyond the configured thresholds.</summary>
    public bool RejectOverRate { get; init; }

    /// <summary>Determines whether frames for other link addresses are expected.</summary>
    public SecurityLinkTopology LinkTopology { get; init; }
    /// <summary>Expected link addresses of masters at an outstation.</summary>
    public IReadOnlySet<ushort>? AllowedMasters { get; init; }
    /// <summary>Discards primary frames from unlisted master link addresses.</summary>
    public bool RejectUnexpectedMaster { get; init; }
    /// <summary>Application operations to refuse.</summary>
    public FunctionPolicy? Functions { get; init; }
    /// <summary>Functions expected in broadcasts. Null uses standard defaults.</summary>
    public IReadOnlySet<FuncCode>? BroadcastAllowed { get; init; }
    /// <summary>Discards broadcasts outside the permitted functions.</summary>
    public bool RejectBroadcastMisuse { get; init; }
    /// <summary>Application callback for authorizing individual controls.</summary>
    public SecurityCommandGuard? CommandGuard { get; init; }
    /// <summary>Distinct unknown functions, objects or points needed for a scan event.</summary>
    public int ScanThreshold { get; init; } = 20;
    /// <summary>Time window for scan detection.</summary>
    public TimeSpan ScanWindow { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>How long to recognize a request repeated across connections.</summary>
    public TimeSpan DuplicateWindow { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Minimum clock correction reported as a clock step.</summary>
    public TimeSpan ClockStepThreshold { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>Announced write size above which a file is reported.</summary>
    public long MaxFileSize { get; init; }
    /// <summary>How far ahead to warn about certificate expiry.</summary>
    public TimeSpan CertExpiryWarning { get; init; } = TimeSpan.FromDays(30);
    /// <summary>Consecutive missed confirmations that trigger an event.</summary>
    public int ConfirmWithheldAfter { get; init; } = 3;

    /// <summary>Per-connection thresholds for traffic and protocol observations.</summary>
    public SecurityLimits Limits { get; init; } = new();
    /// <summary>Whether to observe, throttle or disconnect on excessive traffic.</summary>
    public SecurityAction FloodAction { get; init; }
    /// <summary>Status returned for a throttled control.</summary>
    public CommandStatus ThrottleStatus { get; init; } = CommandStatus.TooManyOps;

    internal bool Allows(IPAddress address)
    {
        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return AllowedPeers is null || AllowedPeers.Any(network => network.Contains(ip));
    }

    internal bool AllowsBroadcast(Fragment frag)
    {
        var func = frag.Header.Func;
        return func switch
        {
            FuncCode.Read or FuncCode.Confirm => true,
            FuncCode.Write => (BroadcastAllowed?.Contains(func) ?? true) &&
                frag.Objects.All(h => h.Group is 50 or 80),
            _ => BroadcastAllowed?.Contains(func) ?? (func is
                FuncCode.RecordCurrentTime or FuncCode.DelayMeasure or
                FuncCode.ImmedFreeze or FuncCode.ImmedFreezeNR or
                FuncCode.FreezeClear or FuncCode.FreezeClearNR or
                FuncCode.FreezeAtTime or FuncCode.FreezeAtTimeNR),
        };
    }

    internal void Validate()
    {
        if (!Enum.IsDefined(LinkTopology))
        {
            throw new BadConfigException("Unknown security link topology.");
        }

        if (FloodAction is not (SecurityAction.Observed or SecurityAction.Throttled or SecurityAction.Disconnected))
        {
            throw new BadConfigException("FloodAction must be Observed, Throttled or Disconnected.");
        }

        if (RejectBindingMismatch && (PeerBindings is null || DisableMacLookup) ||
            DisconnectOnMacChange && (DisableMacLookup || MacRecheckInterval <= TimeSpan.Zero))
        {
            throw new BadConfigException("MAC enforcement requires a binding or a positive recheck interval and MAC lookup.");
        }

        if (RejectUnlisted && AllowedPeers is null || RejectUnexpectedMaster && AllowedMasters is null)
        {
            throw new BadConfigException("Rejecting unlisted peers or masters requires an allowlist.");
        }
    }
}

/// <summary>Checks a control before the outstation's command handler runs.</summary>
public delegate CommandStatus SecurityCommandGuard(
    SecurityPeer peer, FuncCode function, byte group, ushort index, ControlCode code);

/// <summary>Refuses selected application operations before they change outstation state.</summary>
public sealed record FunctionPolicy
{
    /// <summary>Application function codes to refuse outright.</summary>
    public IReadOnlySet<FuncCode>? Deny { get; init; }
    /// <summary>Refuses state-changing requests.</summary>
    public bool ReadOnly { get; init; }
    /// <summary>Allows clock writes in read-only mode.</summary>
    public bool AllowTimeWrite { get; init; }
    /// <summary>Refuses cold and warm restarts.</summary>
    public bool DenyRestart { get; init; }
    /// <summary>Refuses application and configuration operations.</summary>
    public bool DenyApplicationControl { get; init; }
    /// <summary>Refuses file opens for writing and appending.</summary>
    public bool DenyFileWrite { get; init; }
    /// <summary>Refuses file deletion.</summary>
    public bool DenyFileDelete { get; init; }
    /// <summary>Refuses event class assignments.</summary>
    public bool DenyAssignClass { get; init; }
    /// <summary>Refuses group 34 writes.</summary>
    public bool DenyDeadbandWrite { get; init; }
    /// <summary>Refuses group 112 writes.</summary>
    public bool DenyTerminal { get; init; }
    /// <summary>Refuses requests to disable unsolicited responses.</summary>
    public bool DenyUnsolDisable { get; init; }
    /// <summary>Status returned for a blocked control.</summary>
    public CommandStatus DenyStatus { get; init; } = CommandStatus.NotAuthorized;

    internal bool Blocks(FuncCode func) => Deny?.Contains(func) == true || func switch
    {
        FuncCode.ColdRestart or FuncCode.WarmRestart => DenyRestart || ReadOnly,
        FuncCode.InitializeData or FuncCode.InitializeAppl or FuncCode.StartAppl or FuncCode.StopAppl or
            FuncCode.SaveConfig or FuncCode.ActivateConfig => DenyApplicationControl || ReadOnly,
        FuncCode.DeleteFile => DenyFileDelete || ReadOnly,
        FuncCode.AssignClass => DenyAssignClass || ReadOnly,
        FuncCode.DisableUnsolicited => DenyUnsolDisable,
        FuncCode.Select or FuncCode.Operate or FuncCode.DirectOperate or FuncCode.DirectOperateNR or
            FuncCode.ImmedFreeze or FuncCode.ImmedFreezeNR or
            FuncCode.FreezeClear or FuncCode.FreezeClearNR or
            FuncCode.FreezeAtTime or FuncCode.FreezeAtTimeNR => ReadOnly,
        _ => false,
    };

    internal bool BlocksWrite(byte group) => group switch
    {
        34 => DenyDeadbandWrite || ReadOnly,
        112 => DenyTerminal || ReadOnly,
        80 => false,
        50 => ReadOnly && !AllowTimeWrite,
        _ => ReadOnly,
    };
}

/// <summary>Default flood thresholds for each connection.</summary>
public sealed record SecurityLimits
{
    /// <summary>Control objects.</summary>
    public SecurityRate Commands { get; init; } = new(20, TimeSpan.FromSeconds(2));
    /// <summary>Class 0 requests.</summary>
    public SecurityRate Integrity { get; init; } = new(6, TimeSpan.FromMinutes(1));
    /// <summary>Event class requests.</summary>
    public SecurityRate EventPolls { get; init; } = new(20, TimeSpan.FromSeconds(1));
    /// <summary>Cold and warm restart requests.</summary>
    public SecurityRate Restart { get; init; } = new(2, TimeSpan.FromMinutes(10));
    /// <summary>Requests that set the clock.</summary>
    public SecurityRate TimeWrite { get; init; } = new(6, TimeSpan.FromMinutes(1));
    /// <summary>Application and configuration operations.</summary>
    public SecurityRate AppControl { get; init; } = new(2, TimeSpan.FromMinutes(1));
    /// <summary>Link status requests.</summary>
    public SecurityRate LinkStatus { get; init; } = new(5, TimeSpan.FromSeconds(1));
    /// <summary>Link reset requests.</summary>
    public SecurityRate LinkReset { get; init; } = new(5, TimeSpan.FromSeconds(10));
    /// <summary>Secure Authentication challenges.</summary>
    public SecurityRate AuthChallenges { get; init; } = new(20, TimeSpan.FromSeconds(1));
    /// <summary>File open and delete requests.</summary>
    public SecurityRate FileOps { get; init; } = new(10, TimeSpan.FromSeconds(1));
    /// <summary>Decoded link frames.</summary>
    public SecurityRate Frames { get; init; } = new(2000, TimeSpan.FromSeconds(1));
    /// <summary>Received octets, including data the frame parser discards.</summary>
    public SecurityRate Octets { get; init; } = new(1 << 20, TimeSpan.FromSeconds(1));
    /// <summary>Unsolicited responses received at the master.</summary>
    public SecurityRate Unsolicited { get; init; } = new(20, TimeSpan.FromSeconds(1));
    /// <summary>Threshold for escalating frame-count-bit repeats.</summary>
    public SecurityRate FcbRepeats { get; init; } = new(5, TimeSpan.FromMinutes(1));
    /// <summary>Threshold for escalating repeated requests.</summary>
    public SecurityRate Repeats { get; init; } = new(5, TimeSpan.FromMinutes(1));
    /// <summary>Rejected controls.</summary>
    public SecurityRate CommandRejects { get; init; } = new(10, TimeSpan.FromMinutes(1));
    /// <summary>Secure Authentication failures.</summary>
    public SecurityRate AuthFailures { get; init; } = new(5, TimeSpan.FromMinutes(1));
}
