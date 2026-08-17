using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Controller.Service
{
    internal static class Socks5Connector
    {
        public static async Task<Socket> ConnectAsync(
            string proxyHost,
            int proxyPort,
            string destinationHost,
            int destinationPort,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(proxyHost);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationHost);

            string normalizedProxyHost = proxyHost.Trim().TrimStart('[').TrimEnd(']');
            Socket socket = new(
                normalizedProxyHost.Contains(':') ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork,
                SocketType.Stream,
                ProtocolType.Tcp)
            {
                NoDelay = true,
            };

            try
            {
                await socket.ConnectAsync(normalizedProxyHost, proxyPort, cancellationToken).ConfigureAwait(false);
                using NetworkStream stream = new(socket, ownsSocket: false);

                await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken).ConfigureAwait(false);
                byte[] greeting = new byte[2];
                await ReadExactlyAsync(stream, greeting, cancellationToken).ConfigureAwait(false);
                if (greeting[0] != 0x05 || greeting[1] != 0x00)
                {
                    throw new IOException("Local SOCKS5 listener rejected no-authentication negotiation.");
                }

                byte[] request = BuildConnectRequest(destinationHost, destinationPort);
                await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);

                byte[] responseHeader = new byte[4];
                await ReadExactlyAsync(stream, responseHeader, cancellationToken).ConfigureAwait(false);
                if (responseHeader[0] != 0x05 || responseHeader[1] != 0x00)
                {
                    throw new IOException($"Local SOCKS5 CONNECT failed with reply 0x{responseHeader[1]:X2}.");
                }

                int remainingAddressBytes = responseHeader[3] switch
                {
                    0x01 => 4,
                    0x04 => 16,
                    0x03 => await ReadDomainLengthAsync(stream, cancellationToken).ConfigureAwait(false),
                    _ => throw new IOException($"Local SOCKS5 listener returned unsupported address type 0x{responseHeader[3]:X2}."),
                };

                if (remainingAddressBytes > 0)
                {
                    byte[] discardAddress = new byte[remainingAddressBytes];
                    await ReadExactlyAsync(stream, discardAddress, cancellationToken).ConfigureAwait(false);
                }

                byte[] discardPort = new byte[2];
                await ReadExactlyAsync(stream, discardPort, cancellationToken).ConfigureAwait(false);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private static byte[] BuildConnectRequest(string host, int port)
        {
            if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }

            using MemoryStream buffer = new();
            buffer.WriteByte(0x05);
            buffer.WriteByte(0x01);
            buffer.WriteByte(0x00);

            if (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress address))
            {
                byte[] addressBytes = address.GetAddressBytes();
                buffer.WriteByte(address.AddressFamily == AddressFamily.InterNetwork ? (byte)0x01 : (byte)0x04);
                buffer.Write(addressBytes, 0, addressBytes.Length);
            }
            else
            {
                byte[] hostBytes = Encoding.UTF8.GetBytes(host);
                if (hostBytes.Length is 0 or > 255)
                {
                    throw new ArgumentException("SOCKS5 destination hostname must be between 1 and 255 bytes.", nameof(host));
                }

                buffer.WriteByte(0x03);
                buffer.WriteByte((byte)hostBytes.Length);
                buffer.Write(hostBytes, 0, hostBytes.Length);
            }

            Span<byte> portBytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(portBytes, checked((ushort)port));
            buffer.Write(portBytes);
            return buffer.ToArray();
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
                    throw new EndOfStreamException("Unexpected EOF while negotiating SOCKS5 connection.");
                }

                offset += read;
            }
        }
    }
}
