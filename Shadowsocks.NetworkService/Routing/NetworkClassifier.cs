using System.Net;
using System.Net.Sockets;

namespace Shadowsocks.NetworkService.Routing;

internal static class NetworkClassifier
{
    public static bool IsInfrastructureTraffic(FlowKey flow)
    {
        if (flow.Protocol is not (6 or 17))
        {
            return true;
        }

        // Port 53 is handled before this classifier by the explicit DNS policy.
        // Keeping it out of the generic infrastructure rules prevents an enabled
        // DNSCrypt policy from being accidentally bypassed.

        // DHCP, mDNS and LLMNR are local discovery/bootstrap traffic and must never
        // be sent to a remote Shadowsocks server.
        if (flow.Protocol == 17 && flow.RemotePort is 67 or 68 or 5353 or 5355)
        {
            return true;
        }

        IPAddress address = flow.RemoteAddress;
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast)
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            if (bytes[0] == 10
                || bytes[0] == 127
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] >= 224))
            {
                return true;
            }
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] bytes = address.GetAddressBytes();
            // fc00::/7 unique-local range.
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return true;
            }
        }

        return false;
    }
}
