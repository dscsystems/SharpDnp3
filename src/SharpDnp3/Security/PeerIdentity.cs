// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace SharpDnp3.Security;

/// <summary>A MAC address supplied by a neighbor table or an application.</summary>
public sealed record SecurityNeighbor(PhysicalAddress Address, string Source = "custom", string? Interface = null);

/// <summary>Resolves a peer address using the host or application network inventory.</summary>
public delegate ValueTask<SecurityNeighbor?> SecurityNeighborResolver(IPAddress address, CancellationToken cancellationToken);

/// <summary>Bounded history of peer identities. Share one cache across sessions, or persist it as JSON.</summary>
public sealed class SecurityPeerCache
{
    internal static SecurityPeerCache Default { get; } = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly int _capacity;
    private readonly TimeSpan _retention;

    /// <summary>Creates an empty history with the given capacity and retention.</summary>
    public SecurityPeerCache(int capacity = 1024, TimeSpan? retention = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _retention = retention ?? TimeSpan.FromHours(24);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_retention, TimeSpan.Zero);
    }

    internal IReadOnlyList<string> Observe(IPAddress address, string? mac, string? certificate, ushort? link)
    {
        var ip = address.ToString();
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var cutoff = now - _retention;
            List<string> changes = [];
            _entries.TryGetValue(ip, out var previous);
            if (previous is not null && previous.Seen < cutoff)
            {
                previous = null;
            }

            if (Different(previous?.Mac, mac))
            {
                changes.Add(SecurityEventKind.PeerMACChanged);
            }

            if (Different(previous?.Certificate, certificate))
            {
                changes.Add(SecurityEventKind.TLSCertChanged);
            }

            if (previous?.Link is { } priorLink && link is { } currentLink && priorLink != currentLink)
            {
                changes.Add(SecurityEventKind.PeerLinkAddrChanged);
            }

            if (mac is not null && previous?.Mac != mac)
            {
                var sameMac = _entries.Values.FirstOrDefault(other =>
                    other.Ip != ip && other.Mac == mac && other.Seen >= cutoff);
                if (sameMac is not null)
                {
                    changes.Add(previous is null ? SecurityEventKind.PeerIPChanged : SecurityEventKind.PeerMACShared);
                }
            }

            Put(new Entry(ip, mac ?? previous?.Mac, certificate ?? previous?.Certificate, link ?? previous?.Link, now));
            return changes;
        }
    }

    /// <summary>Saves current history. The caller owns the destination stream.</summary>
    public void Save(Stream destination)
    {
        Entry[] entries;
        lock (_gate)
        {
            var cutoff = DateTimeOffset.UtcNow - _retention;
            entries = _entries.Values.Where(entry => entry.Seen >= cutoff).ToArray();
        }

        JsonSerializer.Serialize(destination, entries);
    }

    /// <summary>Loads a history snapshot after decoding it successfully.</summary>
    public async Task LoadAsync(Stream source, CancellationToken cancellationToken = default)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var cutoff = DateTimeOffset.UtcNow - _retention;
        var stream = JsonSerializer.DeserializeAsyncEnumerable<Entry>(source, cancellationToken: cancellationToken);
        await foreach (var entry in stream.ConfigureAwait(false))
        {
            if (entry is null || entry.Seen < cutoff || !IPAddress.TryParse(entry.Ip, out _))
            {
                continue;
            }

            if (entries.Count == _capacity && !entries.ContainsKey(entry.Ip))
            {
                entries.Remove(entries.MinBy(pair => pair.Value.Seen).Key);
            }

            entries[entry.Ip] = entry;
        }

        lock (_gate)
        {
            _entries.Clear();
            foreach (var entry in entries.Values)
            {
                Put(entry);
            }
        }
    }

    private void Put(Entry entry)
    {
        if (_entries.Count >= _capacity && !_entries.ContainsKey(entry.Ip))
        {
            _entries.Remove(_entries.MinBy(pair => pair.Value.Seen).Key);
        }

        _entries[entry.Ip] = entry;
    }

    private static bool Different(string? previous, string? current) =>
        previous is not null && current is not null && previous != current;

    private sealed record Entry(string Ip, string? Mac, string? Certificate, ushort? Link, DateTimeOffset Seen);
}

internal static class SystemNeighbors
{
    public static string? OnLink(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return null;
        }

        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != address.AddressFamily || unicast.Address.Equals(address))
                    {
                        continue;
                    }

                    var local = unicast.Address.GetAddressBytes();
                    var remote = address.GetAddressBytes();
                    var bytes = unicast.PrefixLength / 8;
                    var bits = unicast.PrefixLength % 8;
                    if (local.AsSpan(0, bytes).SequenceEqual(remote.AsSpan(0, bytes)) &&
                        (bits == 0 || (local[bytes] ^ remote[bytes]) >> (8 - bits) == 0))
                    {
                        return adapter.Name;
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No reliable on-link evidence is available.
        }

        return null;
    }

    // With several distinct default gateways the actual route can depend
    // on routing policy. Skip the attribution when it is ambiguous.
    public static IPAddress? DefaultGateway(IPAddress peer)
    {
        if (peer.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(peer))
        {
            return null;
        }

        try
        {
            IPAddress? candidate = null;
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var gateway in adapter.GetIPProperties().GatewayAddresses)
                {
                    var address = gateway.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork || address.Equals(IPAddress.Any))
                    {
                        continue;
                    }

                    if (candidate is not null && !candidate.Equals(address))
                    {
                        return null;
                    }

                    candidate = address;
                }
            }

            return candidate;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    public static async ValueTask<SecurityNeighbor?> ResolveAsync(IPAddress address, CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            return await Task.Run(() => NeighborTable.Windows(address, token), token).ConfigureAwait(false);
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            return await ReadBsdAsync(address, token).ConfigureAwait(false);
        }

        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var neighbor = await Task.Run(() => NeighborTable.Linux(address, token), token).ConfigureAwait(false);
        if (neighbor is not null || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return neighbor;
        }

        using var arp = File.OpenText("/proc/net/arp");
        while (await arp.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 6 && IPAddress.TryParse(fields[0], out var candidate) && candidate.Equals(address) &&
                uint.TryParse(fields[2].AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var flags) &&
                (flags & 2) != 0 && PhysicalAddress.TryParse(fields[3], out var mac))
            {
                return new SecurityNeighbor(mac, "neighbor", fields[5]);
            }
        }

        return null;
    }
    private static async Task<SecurityNeighbor?> ReadBsdAsync(IPAddress address, CancellationToken token)
    {
        var ipv4 = address.AddressFamily == AddressFamily.InterNetwork;
        var utility = ipv4 ? "arp" : "ndp";
        var path = File.Exists("/usr/sbin/" + utility) ? "/usr/sbin/" + utility : "/sbin/" + utility;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                ArgumentList = { "-an" },
            },
        };
        if (!process.Start())
        {
            return null;
        }

        try
        {
            while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var offset = ipv4 ? 1 : 0;
                if (fields.Length < (ipv4 ? 6 : 3) ||
                    !IPAddress.TryParse(fields[offset].Trim('(', ')').Split('%', 2)[0], out var candidate) ||
                    !candidate.GetAddressBytes().AsSpan().SequenceEqual(address.GetAddressBytes()))
                {
                    continue;
                }

                var octets = fields[ipv4 ? 3 : 1].Split(':');
                var mac = string.Join(':', octets.Select(octet => octet.PadLeft(2, '0')));
                if (PhysicalAddress.TryParse(mac, out var parsed))
                {
                    return new SecurityNeighbor(parsed, "neighbor", fields[ipv4 ? 5 : 2]);
                }
            }

            return null;
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // It exited between the check and termination.
                }
            }
        }
    }

}
