using System.Buffers.Binary;
using System.Net;

namespace Shadowsocks.NetworkService.WinDivert;

internal unsafe readonly struct PacketView
{
    private readonly byte* _packet;
    private readonly uint _packetLength;
    private readonly byte* _ip;
    private readonly byte* _ipv6;
    private readonly byte* _transport;
    private readonly byte* _payload;

    private PacketView(
        byte* packet,
        uint packetLength,
        byte protocol,
        byte* ip,
        byte* ipv6,
        byte* transport,
        byte* payload,
        uint payloadLength)
    {
        _packet = packet;
        _packetLength = packetLength;
        Protocol = protocol;
        _ip = ip;
        _ipv6 = ipv6;
        _transport = transport;
        _payload = payload;
        PayloadLength = payloadLength;
    }

    public byte Protocol { get; }
    public uint PayloadLength { get; }
    public bool IsTcp => Protocol == 6 && _transport != null;
    public bool IsUdp => Protocol == 17 && _transport != null;
    public bool IsIPv6 => _ipv6 != null;

    public IPAddress SourceAddress => ReadAddress(source: true);
    public IPAddress DestinationAddress => ReadAddress(source: false);

    public ushort SourcePort => ReadPort(0);
    public ushort DestinationPort => ReadPort(2);

    public ReadOnlySpan<byte> Payload => _payload is null || PayloadLength == 0
        ? ReadOnlySpan<byte>.Empty
        : new ReadOnlySpan<byte>(_payload, checked((int)PayloadLength));

    public static bool TryParse(byte* packet, uint packetLength, out PacketView view)
    {
        view = default;
        if (!WinDivertNative.ParsePacket(
                packet,
                packetLength,
                out nint ipHeader,
                out nint ipv6Header,
                out byte protocol,
                0,
                0,
                out nint tcpHeader,
                out nint udpHeader,
                out nint data,
                out uint dataLength,
                0,
                0))
        {
            return false;
        }

        nint transport = tcpHeader != 0 ? tcpHeader : udpHeader;
        if (transport == 0 || (protocol != 6 && protocol != 17))
        {
            return false;
        }

        view = new PacketView(
            packet,
            packetLength,
            protocol,
            (byte*)ipHeader,
            (byte*)ipv6Header,
            (byte*)transport,
            (byte*)data,
            dataLength);
        return true;
    }

    public void ReflectToLocal(ushort localProxyPort)
    {
        SwapAddresses();
        WritePort(2, localProxyPort);
    }

    public void ReflectFromLocal(ushort originalRemotePort)
    {
        SwapAddresses();
        WritePort(0, originalRemotePort);
    }

    private IPAddress ReadAddress(bool source)
    {
        if (_ip != null)
        {
            int offset = source ? 12 : 16;
            return new IPAddress(new ReadOnlySpan<byte>(_ip + offset, 4));
        }

        if (_ipv6 != null)
        {
            int offset = source ? 8 : 24;
            return new IPAddress(new ReadOnlySpan<byte>(_ipv6 + offset, 16));
        }

        throw new InvalidOperationException("Packet has no IP header.");
    }

    private void SwapAddresses()
    {
        if (_ip != null)
        {
            SwapBytes(_ip + 12, _ip + 16, 4);
            return;
        }

        if (_ipv6 != null)
        {
            SwapBytes(_ipv6 + 8, _ipv6 + 24, 16);
            return;
        }

        throw new InvalidOperationException("Packet has no IP header.");
    }

    private ushort ReadPort(int offset)
    {
        if (_transport == null)
        {
            return 0;
        }

        return BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(_transport + offset, 2));
    }

    private void WritePort(int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(_transport + offset, 2), value);
    }

    private static void SwapBytes(byte* left, byte* right, int length)
    {
        for (int i = 0; i < length; i++)
        {
            byte value = left[i];
            left[i] = right[i];
            right[i] = value;
        }
    }
}
