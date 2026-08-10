using System.Net;
using System.Net.Sockets;

namespace VirtualLANPlatform.Core.Networking;

/// <summary>
/// Minimal STUN client (RFC 5389) — discovers the external IP and UDP port
/// for a given local port by querying public STUN servers.
/// </summary>
public static class StunClient
{
    private static readonly (string Host, int Port)[] Servers =
    [
        ("stun.l.google.com",   19302),
        ("stun1.l.google.com",  19302),
        ("stun.cloudflare.com", 3478),
        ("stun.ekiga.net",      3478),
    ];

    /// <summary>
    /// Returns the external (public) IP and UDP port for <paramref name="localPort"/>.
    /// Returns (null, 0) if all servers fail.
    /// </summary>
    public static async Task<(IPAddress? Ip, ushort Port)> DiscoverAsync(
        ushort localPort = 0, CancellationToken ct = default)
    {
        foreach (var (host, port) in Servers)
        {
            try
            {
                var r = await QueryOneAsync(localPort, host, port, ct).ConfigureAwait(false);
                if (r.Ip != null) return r;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return (null, 0);
    }

    private static async Task<(IPAddress? Ip, ushort Port)> QueryOneAsync(
        ushort localPort, string stunHost, int stunPort, CancellationToken ct)
    {
        IPAddress[] addrs = await Dns
            .GetHostAddressesAsync(stunHost, AddressFamily.InterNetwork, ct)
            .ConfigureAwait(false);
        if (addrs.Length == 0) return (null, 0);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        if (localPort > 0)
        {
            try { udp.Client.Bind(new IPEndPoint(IPAddress.Any, localPort)); }
            catch { /* port in use; OS assigns random port */ }
        }

        var serverEp = new IPEndPoint(addrs[0], stunPort);

        // Build STUN Binding Request (RFC 5389)
        var txId = new byte[12];
        Random.Shared.NextBytes(txId);
        var req = new byte[20];
        req[0] = 0x00; req[1] = 0x01;                          // Binding Request
        // bytes 2–3: message length = 0
        req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42; // Magic Cookie
        txId.CopyTo(req, 8);

        await udp.SendAsync(req, serverEp, ct).ConfigureAwait(false);

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(3500);
        UdpReceiveResult result = await udp.ReceiveAsync(readCts.Token).ConfigureAwait(false);

        return ParseAttributes(result.Buffer);
    }

    private static (IPAddress? Ip, ushort Port) ParseAttributes(byte[] data)
    {
        if (data.Length < 20) return (null, 0);

        int i = 20;
        while (i + 4 <= data.Length)
        {
            ushort attrType = (ushort)((data[i] << 8) | data[i + 1]);
            ushort attrLen  = (ushort)((data[i + 2] << 8) | data[i + 3]);
            i += 4;

            if (i + attrLen > data.Length) break;

            // XOR-MAPPED-ADDRESS (0x0020) or MAPPED-ADDRESS (0x0001), IPv4 only
            if ((attrType == 0x0020 || attrType == 0x0001)
                && attrLen >= 8 && i + 7 < data.Length && data[i + 1] == 0x01)
            {
                bool   xor  = attrType == 0x0020;
                ushort port = (ushort)((data[i + 2] << 8) | data[i + 3]);
                byte[] addr = [data[i + 4], data[i + 5], data[i + 6], data[i + 7]];

                if (xor)
                {
                    port   ^= 0x2112;
                    addr[0] ^= 0x21; addr[1] ^= 0x12;
                    addr[2] ^= 0xA4; addr[3] ^= 0x42;
                }

                return (new IPAddress(addr), port);
            }

            // Advance with 4-byte alignment
            int padded = attrLen + (attrLen % 4 != 0 ? 4 - attrLen % 4 : 0);
            i += padded;
        }
        return (null, 0);
    }
}
