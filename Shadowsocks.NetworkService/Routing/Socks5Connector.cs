using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Shadowsocks.NetworkService.Routing;

internal static class Socks5Connector
{
    public static async Task<Socket> ConnectAsync(
        string proxyHost,
        int proxyPort,
        IPAddress destinationAddress,
        int destinationPort,
        CancellationToken cancellationToken)
    {
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(proxyHost, proxyPort, cancellationToken).ConfigureAwait(false);
            using NetworkStream stream = new(socket, ownsSocket: false);
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, cancellationToken).ConfigureAwait(false);
            byte[] response = new byte[2];
            await ReadExactlyAsync(stream, response, cancellationToken).ConfigureAwait(false);
            if (response[0] != 5 || response[1] != 0)
            {
                throw new IOException("Local Shadowsocks SOCKS5 listener rejected authentication negotiation.");
            }

            byte[] request = BuildConnectRequest(destinationAddress, destinationPort);
            await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            byte[] header = new byte[4];
            await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
            if (header[0] != 5 || header[1] != 0)
            {
                throw new IOException($"SOCKS5 CONNECT failed with reply 0x{header[1]:X2}.");
            }

            int addressLength = header[3] switch
            {
                1 => 4,
                4 => 16,
                3 => await ReadDomainLengthAsync(stream, cancellationToken).ConfigureAwait(false),
                _ => throw new IOException("Invalid SOCKS5 response address type."),
            };
            byte[] discard = new byte[addressLength + 2];
            await ReadExactlyAsync(stream, discard, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static byte[] BuildUdpDatagram(IPEndPoint destination, ReadOnlySpan<byte> payload)
    {
        byte[] address = destination.Address.GetAddressBytes();
        int headerLength = 3 + 1 + address.Length + 2;
        byte[] packet = new byte[headerLength + payload.Length];
        packet[0] = 0;
        packet[1] = 0;
        packet[2] = 0;
        packet[3] = destination.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
        address.CopyTo(packet.AsSpan(4));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4 + address.Length, 2), checked((ushort)destination.Port));
        payload.CopyTo(packet.AsSpan(headerLength));
        return packet;
    }

    public static bool TryParseUdpDatagram(ReadOnlySpan<byte> packet, out IPEndPoint? source, out byte[] payload)
    {
        source = null;
        payload = [];
        if (packet.Length < 7 || packet[0] != 0 || packet[1] != 0 || packet[2] != 0)
        {
            return false;
        }

        int offset = 4;
        IPAddress address;
        switch (packet[3])
        {
            case 1:
                if (packet.Length < offset + 4 + 2) return false;
                address = new IPAddress(packet.Slice(offset, 4));
                offset += 4;
                break;
            case 4:
                if (packet.Length < offset + 16 + 2) return false;
                address = new IPAddress(packet.Slice(offset, 16));
                offset += 16;
                break;
            case 3:
                // Shadowsocks UDP responses are normally IP based. DNS names cannot be
                // injected as an IP packet until the DNS layer is implemented.
                return false;
            default:
                return false;
        }

        ushort port = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset, 2));
        offset += 2;
        source = new IPEndPoint(address, port);
        payload = packet[offset..].ToArray();
        return true;
    }

    private static byte[] BuildConnectRequest(IPAddress destinationAddress, int destinationPort)
    {
        byte[] address = destinationAddress.GetAddressBytes();
        byte[] request = new byte[4 + address.Length + 2];
        request[0] = 5;
        request[1] = 1;
        request[2] = 0;
        request[3] = destinationAddress.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
        address.CopyTo(request.AsSpan(4));
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4 + address.Length, 2), checked((ushort)destinationPort));
        return request;
    }

    private static async Task<int> ReadDomainLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] length = new byte[1];
        await ReadExactlyAsync(stream, length, cancellationToken).ConfigureAwait(false);
        return length[0];
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }
            offset += read;
        }
    }
}
