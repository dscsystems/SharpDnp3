// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SharpDnp3.Security;

internal static class NeighborTable
{
    public static SecurityNeighbor? Windows(IPAddress address, CancellationToken token)
    {
        const int headerSize = 8;
        const int rowSize = 88;
        const int addressOffset = 0;
        const int interfaceOffset = 28;
        const int physicalOffset = 40;
        const int physicalLengthOffset = 72;
        const int stateOffset = 76;
        var ipv4 = address.AddressFamily == AddressFamily.InterNetwork;
        var family = (ushort)(ipv4 ? 2 : 23);
        if (GetIpNetTable(family, out var table) != 0 || table == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var count = Marshal.ReadInt32(table);
            if (count < 0 || count > (int.MaxValue - headerSize) / rowSize)
            {
                return null;
            }

            var row = new byte[rowSize];
            var wanted = address.GetAddressBytes();
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                Marshal.Copy(table + headerSize + i * rowSize, row, 0, rowSize);
                var state = BitConverter.ToInt32(row, stateOffset);
                var index = BitConverter.ToInt32(row, interfaceOffset);
                var length = BitConverter.ToInt32(row, physicalLengthOffset);
                if (state is < 2 or > 6 || length is < 1 or > 32 ||
                    BitConverter.ToUInt16(row, addressOffset) != family ||
                    !row.AsSpan(ipv4 ? 4 : 8, wanted.Length).SequenceEqual(wanted) ||
                    !Ipv6ScopeMatches(address, index))
                {
                    continue;
                }

                return new(new PhysicalAddress(row.AsSpan(physicalOffset, length).ToArray()),
                    "neighbor", InterfaceName(index, ipv4));
            }
        }
        finally
        {
            FreeMibTable(table);
        }

        return null;
    }

    public static SecurityNeighbor? Linux(IPAddress address, CancellationToken token)
    {
        const int netlink = 16;
        const int rawSocketWithCloseOnExec = 3 | 0x80000;
        const ushort getNeighbor = 30;
        const ushort requestDump = 0x301;
        const int header = 16;
        const int neighborHeader = 12;
        const uint sequence = 1;

        var socket = OpenSocket(netlink, rawSocketWithCloseOnExec, 0); // NETLINK_ROUTE
        if (socket < 0)
        {
            return null;
        }

        try
        {
            var wanted = address.GetAddressBytes();
            var message = new byte[header + neighborHeader];
            BitConverter.TryWriteBytes(message, message.Length);
            BitConverter.TryWriteBytes(message.AsSpan(4), getNeighbor);
            BitConverter.TryWriteBytes(message.AsSpan(6), requestDump);
            BitConverter.TryWriteBytes(message.AsSpan(8), sequence);
            var family = (byte)(wanted.Length == 4 ? 2 : 10);
            message[header] = family;

            // A zero netlink port and group send the query to the kernel.
            var destination = new byte[12];
            BitConverter.TryWriteBytes(destination, (ushort)netlink);
            if (SendTo(socket, message, (nuint)message.Length, 0, destination, (uint)destination.Length) < 0)
            {
                return null;
            }

            var poll = new PollDescriptor { Socket = socket, Events = 1 }; // POLLIN
            var buffer = new byte[64 * 1024];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var ready = Poll(ref poll, 1, 20);
                if (ready < 0 || ready > 0 && (poll.Returned & 1) == 0)
                {
                    return null;
                }

                if (ready == 0)
                {
                    continue;
                }

                var count = Receive(socket, buffer, (nuint)buffer.Length, 0x40); // MSG_DONTWAIT
                if (count <= 0)
                {
                    return null;
                }

                for (var remaining = buffer.AsSpan(0, (int)count); remaining.Length >= header;)
                {
                    var length = BitConverter.ToInt32(remaining);
                    if (length < header || length > remaining.Length)
                    {
                        break;
                    }

                    var type = BitConverter.ToUInt16(remaining[4..]);
                    if (BitConverter.ToUInt32(remaining[8..]) == sequence)
                    {
                        if (type is 2 or 3) // NLMSG_ERROR, NLMSG_DONE
                        {
                            return null;
                        }

                        if (type == 28 && length >= header + neighborHeader && remaining[header] == family &&
                            ParseLinuxNeighbor(remaining[header..length], wanted, address) is { } neighbor)
                        {
                            return neighbor;
                        }
                    }

                    var aligned = (length + 3) & ~3;
                    if (aligned > remaining.Length)
                    {
                        break;
                    }

                    remaining = remaining[aligned..];
                }
            }
        }
        finally
        {
            CloseSocket(socket);
        }
    }

    private static SecurityNeighbor? ParseLinuxNeighbor(
        ReadOnlySpan<byte> message, ReadOnlySpan<byte> wanted, IPAddress address)
    {
        var index = BitConverter.ToInt32(message[4..]);
        var state = BitConverter.ToUInt16(message[8..]);
        if ((state & 0x21) != 0 || (state & 0xde) == 0 || !Ipv6ScopeMatches(address, index))
        {
            return null;
        }

        ReadOnlySpan<byte> mac = default;
        var matches = false;
        for (var attributes = message[12..]; attributes.Length >= 4;)
        {
            var length = BitConverter.ToUInt16(attributes);
            if (length < 4 || length > attributes.Length)
            {
                break;
            }

            var type = BitConverter.ToUInt16(attributes[2..]) & 0x3fff;
            var data = attributes[4..length];
            if (type == 1)
            {
                matches = data.SequenceEqual(wanted);
            }
            else if (type == 2 && data.Length is 6 or 8 or 20)
            {
                mac = data;
            }

            var aligned = (length + 3) & ~3;
            if (aligned > attributes.Length)
            {
                break;
            }

            attributes = attributes[aligned..];
        }

        return matches && !mac.IsEmpty
            ? new(new PhysicalAddress(mac.ToArray()), "neighbor", InterfaceName(index, wanted.Length == 4))
            : null;
    }

    private static bool Ipv6ScopeMatches(IPAddress address, int index) =>
        address.AddressFamily == AddressFamily.InterNetwork || address.ScopeId == 0 || address.ScopeId == index;

    private static string? InterfaceName(int index, bool ipv4)
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = adapter.GetIPProperties();
            if ((ipv4 ? properties.GetIPv4Properties()?.Index : properties.GetIPv6Properties()?.Index) == index)
            {
                return adapter.Name;
            }
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int Socket;
        public short Events;
        public short Returned;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true, EntryPoint = "GetIpNetTable2")]
    private static extern uint GetIpNetTable(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern void FreeMibTable(IntPtr table);

    [DllImport("libc", EntryPoint = "socket", ExactSpelling = true)]
    private static extern int OpenSocket(int domain, int type, int protocol);

    [DllImport("libc", EntryPoint = "sendto", ExactSpelling = true)]
    private static extern nint SendTo(int socket, byte[] buffer, nuint length, int flags, byte[] address, uint addressLength);

    [DllImport("libc", EntryPoint = "poll", ExactSpelling = true)]
    private static extern int Poll(ref PollDescriptor descriptor, nuint count, int timeout);

    [DllImport("libc", EntryPoint = "recv", ExactSpelling = true)]
    private static extern nint Receive(int socket, [Out] byte[] buffer, nuint length, int flags);

    [DllImport("libc", EntryPoint = "close", ExactSpelling = true)]
    private static extern int CloseSocket(int socket);
}
