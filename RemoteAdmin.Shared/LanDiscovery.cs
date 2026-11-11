using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RemoteAdmin.Shared;

public static class LanDiscovery
{
    public static bool IsLocalAddress(string host)
    {
        if (!IPAddress.TryParse(host, out var address)) return true;
        if (IPAddress.IsLoopback(address)) return true;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(network =>
                network.GetIPProperties().UnicastAddresses.Any(local => local.Address.Equals(address)));
        }
        catch (NetworkInformationException) { return true; }
    }

    public static IEnumerable<IPAddress> BroadcastAddresses()
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up ||
                network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            foreach (var address in network.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask is null)
                    continue;
                var broadcast = BroadcastAddress(address.Address, address.IPv4Mask);
                if (broadcast is not null) yield return broadcast;
            }
        }
    }

    public static IPAddress? BroadcastAddress(IPAddress address, IPAddress mask)
    {
        var ip = address.GetAddressBytes();
        var bits = mask.GetAddressBytes();
        // /31 and /32 point-to-point adapters have no broadcast address.
        if (ip.Length != 4 || bits.Length != 4 ||
            (bits[0] == 255 && bits[1] == 255 && bits[2] == 255 && bits[3] >= 254) ||
            ip[0] == 127 || (ip[0] == 169 && ip[1] == 254))
            return null;
        return new IPAddress(ip.Select((b, i) => (byte)(b | (byte)~bits[i])).ToArray());
    }

    public static async Task<List<DiscoveryAnnouncement>> FindAsync(Action<string> log, CancellationToken token,
        IEnumerable<IPEndPoint>? destinations = null, TimeSpan? responseWindow = null)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        if (OperatingSystem.IsWindows())
        {
            // Windows reports ICMP Port Unreachable from an unrelated destination as
            // ConnectionReset on this shared UDP socket. Do not discard valid LAN replies.
            udp.Client.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        }
        var request = Encoding.UTF8.GetBytes(Protocol.DiscoveryRequest);
        var targets = destinations ?? BroadcastAddresses().Append(IPAddress.Broadcast).Distinct()
            .Select(ip => new IPEndPoint(ip, Protocol.DiscoveryPort));
        foreach (var target in targets)
        {
            try
            {
                await udp.SendAsync(request, target, token);
                log($"Discovery request sent to {target}.");
            }
            catch (SocketException ex) { log($"Discovery send to {target} failed ({ex.SocketErrorCode}): {ex.Message}"); }
        }

        var result = new List<DiscoveryAnnouncement>();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
        window.CancelAfter(responseWindow ?? TimeSpan.FromMilliseconds(1200));
        while (!window.IsCancellationRequested)
        {
            try
            {
                var packet = await udp.ReceiveAsync(window.Token);
                var server = JsonSerializer.Deserialize<DiscoveryAnnouncement>(packet.Buffer, Protocol.Json);
                if (server is null || string.IsNullOrWhiteSpace(server.InstanceId) || server.Port is < 1 or > 65535)
                    continue;
                server = server with { Host = packet.RemoteEndPoint.Address.ToString() };
                // Keep alternate interfaces for the same Admin as connection fallbacks.
                if (!result.Contains(server)) result.Add(server);
            }
            catch (JsonException) { log("Ignored an invalid discovery response."); }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.NetworkReset)
            { log($"Ignoring discovery error from an unreachable destination ({ex.SocketErrorCode})."); }
            catch (OperationCanceledException) when (window.IsCancellationRequested) { break; }
        }
        token.ThrowIfCancellationRequested();
        log($"Discovery found {result.Select(x => x.InstanceId).Distinct().Count()} Admin instance(s), {result.Count} endpoint(s).");
        return result;
    }
}
