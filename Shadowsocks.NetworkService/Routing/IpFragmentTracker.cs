#nullable enable
using System.Buffers.Binary;
using System.Net;
using Shadowsocks.NetworkService.Ipc;

namespace Shadowsocks.NetworkService.Routing;

internal enum FragmentDisposition
{
    Direct = 0,
    Drop = 1,
}

internal readonly record struct IpFragmentKey(
    byte Protocol,
    IPAddress SourceAddress,
    IPAddress DestinationAddress,
    uint Identification);

internal readonly record struct IpFragmentInfo(
    IpFragmentKey Key,
    bool IsFirstFragment,
    bool MoreFragments);

/// <summary>
/// Keeps all fragments of an outbound IP datagram on the same security path.
/// Fragmented datagrams that would require transparent address/port rewriting
/// are dropped as a whole; rewriting only the first fragment would otherwise
/// leak or corrupt the remaining plaintext fragments.
/// </summary>
internal sealed class IpFragmentTracker
{
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromSeconds(30);
    private readonly Dictionary<IpFragmentKey, Entry> _entries = new();
    private DateTime _lastSweepUtc;

    public void Record(IpFragmentInfo fragment, FragmentDisposition disposition, DateTime nowUtc)
    {
        _entries[fragment.Key] = new Entry(disposition, nowUtc);
        MaybeSweep(nowUtc);
    }

    public bool TryGetDisposition(
        IpFragmentInfo fragment,
        DateTime nowUtc,
        out FragmentDisposition disposition)
    {
        MaybeSweep(nowUtc);
        if (!_entries.TryGetValue(fragment.Key, out Entry entry))
        {
            disposition = FragmentDisposition.Drop;
            return false;
        }

        disposition = entry.Disposition;
        _entries[fragment.Key] = entry with { LastSeenUtc = nowUtc };
        if (!fragment.MoreFragments)
        {
            _entries.Remove(fragment.Key);
        }
        return true;
    }

    internal static FragmentDisposition ForDnsDecision(DnsRouteDecision decision)
        => decision == DnsRouteDecision.Direct ? FragmentDisposition.Direct : FragmentDisposition.Drop;

    internal static FragmentDisposition ForRoute(RouteAction route)
        => route is RouteAction.Direct or RouteAction.Default
            ? FragmentDisposition.Direct
            : FragmentDisposition.Drop;

    internal static bool TryParse(ReadOnlySpan<byte> packet, out IpFragmentInfo fragment)
    {
        fragment = default;
        if (packet.Length < 1)
            return false;

        return (packet[0] >> 4) switch
        {
            4 => TryParseIpv4(packet, out fragment),
            6 => TryParseIpv6(packet, out fragment),
            _ => false,
        };
    }

    private static bool TryParseIpv4(ReadOnlySpan<byte> packet, out IpFragmentInfo fragment)
    {
        fragment = default;
        if (packet.Length < 20)
            return false;

        int headerLength = (packet[0] & 0x0F) * 4;
        if (headerLength < 20 || packet.Length < headerLength)
            return false;

        ushort flagsAndOffset = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2));
        int fragmentOffset = flagsAndOffset & 0x1FFF;
        bool moreFragments = (flagsAndOffset & 0x2000) != 0;
        if (fragmentOffset == 0 && !moreFragments)
            return false;

        byte protocol = packet[9];
        if (protocol is not 6 and not 17)
            return false;

        uint identification = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(4, 2));
        fragment = new IpFragmentInfo(
            new IpFragmentKey(
                protocol,
                new IPAddress(packet.Slice(12, 4)),
                new IPAddress(packet.Slice(16, 4)),
                identification),
            fragmentOffset == 0,
            moreFragments);
        return true;
    }

    private static bool TryParseIpv6(ReadOnlySpan<byte> packet, out IpFragmentInfo fragment)
    {
        fragment = default;
        if (packet.Length < 40)
            return false;

        byte nextHeader = packet[6];
        int offset = 40;
        for (int extensionCount = 0; extensionCount < 8; extensionCount++)
        {
            if (nextHeader == 44)
            {
                if (packet.Length < offset + 8)
                    return false;

                byte fragmentProtocol = packet[offset];
                if (!IsTrackedIpv6FragmentProtocol(fragmentProtocol))
                    return false;

                ushort offsetAndFlags = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 2, 2));
                int fragmentOffset = (offsetAndFlags & 0xFFF8) >> 3;
                bool moreFragments = (offsetAndFlags & 0x0001) != 0;
                if (fragmentOffset == 0 && !moreFragments)
                    return false; // IPv6 atomic fragment: no cross-packet state is required.

                uint identification = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(offset + 4, 4));
                fragment = new IpFragmentInfo(
                    new IpFragmentKey(
                        fragmentProtocol,
                        new IPAddress(packet.Slice(8, 16)),
                        new IPAddress(packet.Slice(24, 16)),
                        identification),
                    fragmentOffset == 0,
                    moreFragments);
                return true;
            }

            switch (nextHeader)
            {
                case 0:  // Hop-by-Hop Options
                case 43: // Routing
                case 60: // Destination Options
                    if (packet.Length < offset + 2)
                        return false;
                    nextHeader = packet[offset];
                    int extensionLength = checked((packet[offset + 1] + 1) * 8);
                    if (extensionLength < 8 || packet.Length < offset + extensionLength)
                        return false;
                    offset += extensionLength;
                    break;

                case 51: // Authentication Header
                    if (packet.Length < offset + 2)
                        return false;
                    nextHeader = packet[offset];
                    int authenticationLength = checked((packet[offset + 1] + 2) * 4);
                    if (authenticationLength < 8 || packet.Length < offset + authenticationLength)
                        return false;
                    offset += authenticationLength;
                    break;

                default:
                    return false;
            }
        }

        return false;
    }


    private static bool IsTrackedIpv6FragmentProtocol(byte nextHeader)
    {
        // The Fragment header can point directly to TCP/UDP or to another extension
        // header in the fragmentable part (for example Destination Options -> UDP).
        // Keep the immediate Next Header value in the fragment key because every
        // fragment carries the same Fragment header, including continuations that no
        // longer contain the later extension/transport headers.
        return nextHeader is 6 or 17 or 0 or 43 or 51 or 60;
    }

    private void MaybeSweep(DateTime nowUtc)
    {
        if (nowUtc - _lastSweepUtc < TimeSpan.FromSeconds(5))
            return;

        _lastSweepUtc = nowUtc;
        foreach ((IpFragmentKey key, Entry entry) in _entries.ToArray())
        {
            if (nowUtc - entry.LastSeenUtc > EntryLifetime)
            {
                _entries.Remove(key);
            }
        }
    }

    private readonly record struct Entry(FragmentDisposition Disposition, DateTime LastSeenUtc);
}
