// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Net.Sockets;
using SharpDnp3.Security;

namespace SharpDnp3.Channels;

internal sealed class TlsHandshakePrefix(Socket socket) : NetworkStream(socket, ownsSocket: false)
{
    private readonly byte[] _prefix = new byte[5];
    private int _count;

    public string? NonTlsKind
    {
        get
        {
            if (_count < 2)
            {
                return null;
            }

            if (_prefix[0] == 0x05 && _prefix[1] == 0x64)
            {
                return SecurityEventKind.TLSPlaintextOnTLSPort;
            }

            return _count == _prefix.Length && (_prefix[0] is < 20 or > 24 || _prefix[1] != 3)
                ? SecurityEventKind.TLSNonTLSProbe
                : null;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var received = await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Capture(buffer.Span[..received]);
        return received;
    }

    private void Capture(ReadOnlySpan<byte> data)
    {
        var length = Math.Min(data.Length, _prefix.Length - _count);
        if (length > 0)
        {
            data[..length].CopyTo(_prefix.AsSpan(_count));
            _count += length;
        }
    }
}
