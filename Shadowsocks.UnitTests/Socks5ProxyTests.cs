using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Proxy;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class Socks5ProxyTests
    {
        [TestMethod]
        public async Task ConnectSupportsUsernamePasswordAndDomainReplyWithFragmentedFrames()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var listenerEndPoint = (IPEndPoint)listener.LocalEndpoint;

            Task serverTask = Task.Run(async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                NetworkStream stream = client.GetStream();

                byte[] greeting = await ReadExactAsync(stream, 3);
                CollectionAssert.AreEqual(new byte[] { 0x05, 0x01, 0x02 }, greeting);
                await WriteFragmentedAsync(stream, new byte[] { 0x05, 0x02 });

                byte[] authHeader = await ReadExactAsync(stream, 2);
                Assert.AreEqual(0x01, authHeader[0]);
                byte[] username = await ReadExactAsync(stream, authHeader[1]);
                byte[] passwordLength = await ReadExactAsync(stream, 1);
                byte[] password = await ReadExactAsync(stream, passwordLength[0]);
                Assert.AreEqual("user", Encoding.UTF8.GetString(username));
                Assert.AreEqual("secret", Encoding.UTF8.GetString(password));
                await WriteFragmentedAsync(stream, new byte[] { 0x01, 0x00 });

                byte[] requestHeader = await ReadExactAsync(stream, 5);
                CollectionAssert.AreEqual(new byte[] { 0x05, 0x01, 0x00, 0x03 }, requestHeader[..4]);
                int hostLength = requestHeader[4];
                byte[] host = await ReadExactAsync(stream, hostLength);
                byte[] port = await ReadExactAsync(stream, 2);
                Assert.AreEqual("example.com", Encoding.ASCII.GetString(host));
                Assert.AreEqual(443, (port[0] << 8) | port[1]);

                byte[] domain = Encoding.ASCII.GetBytes("proxy.local");
                var reply = new List<byte> { 0x05, 0x00, 0x00, 0x03, (byte)domain.Length };
                reply.AddRange(domain);
                reply.Add(0x1F);
                reply.Add(0x90);
                await WriteFragmentedAsync(stream, reply.ToArray());
            });

            var proxy = new Socks5Proxy();
            try
            {
                await ConnectProxyAsync(proxy, listenerEndPoint);
                await ConnectDestinationAsync(
                    proxy,
                    new DnsEndPoint("example.com", 443),
                    new NetworkCredential("user", "secret"));
                await serverTask;
            }
            finally
            {
                proxy.Close();
                listener.Stop();
            }
        }

        [TestMethod]
        public async Task ConnectSupportsNoAuthenticationAndIpv6Reply()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var listenerEndPoint = (IPEndPoint)listener.LocalEndpoint;

            Task serverTask = Task.Run(async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                NetworkStream stream = client.GetStream();

                CollectionAssert.AreEqual(
                    new byte[] { 0x05, 0x01, 0x00 },
                    await ReadExactAsync(stream, 3));
                await WriteFragmentedAsync(stream, new byte[] { 0x05, 0x00 });

                byte[] request = await ReadExactAsync(stream, 10);
                CollectionAssert.AreEqual(new byte[] { 0x05, 0x01, 0x00, 0x01 }, request[..4]);
                Assert.AreEqual(53, (request[8] << 8) | request[9]);

                var reply = new byte[4 + 16 + 2];
                reply[0] = 0x05;
                reply[1] = 0x00;
                reply[2] = 0x00;
                reply[3] = 0x04;
                IPAddress.IPv6Loopback.GetAddressBytes().CopyTo(reply, 4);
                reply[^2] = 0x00;
                reply[^1] = 0x35;
                await WriteFragmentedAsync(stream, reply);
            });

            var proxy = new Socks5Proxy();
            try
            {
                await ConnectProxyAsync(proxy, listenerEndPoint);
                await ConnectDestinationAsync(proxy, new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53), null);
                await serverTask;
            }
            finally
            {
                proxy.Close();
                listener.Stop();
            }
        }

        [TestMethod]
        public async Task CredentialsRejectUnexpectedNoAuthenticationDowngrade()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var listenerEndPoint = (IPEndPoint)listener.LocalEndpoint;

            Task serverTask = Task.Run(async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                NetworkStream stream = client.GetStream();
                CollectionAssert.AreEqual(new byte[] { 0x05, 0x01, 0x02 }, await ReadExactAsync(stream, 3));
                await stream.WriteAsync(new byte[] { 0x05, 0x00 });
            });

            var proxy = new Socks5Proxy();
            try
            {
                await ConnectProxyAsync(proxy, listenerEndPoint);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    ConnectDestinationAsync(
                        proxy,
                        new DnsEndPoint("example.com", 443),
                        new NetworkCredential("user", "secret")));
                await serverTask;
            }
            finally
            {
                proxy.Close();
                listener.Stop();
            }
        }

        [TestMethod]
        public async Task UsernamePasswordFailureIsReported()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var listenerEndPoint = (IPEndPoint)listener.LocalEndpoint;

            Task serverTask = Task.Run(async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                NetworkStream stream = client.GetStream();
                await ReadExactAsync(stream, 3);
                await stream.WriteAsync(new byte[] { 0x05, 0x02 });

                byte[] authHeader = await ReadExactAsync(stream, 2);
                await ReadExactAsync(stream, authHeader[1]);
                byte[] passwordLength = await ReadExactAsync(stream, 1);
                await ReadExactAsync(stream, passwordLength[0]);
                await stream.WriteAsync(new byte[] { 0x01, 0x01 });
            });

            var proxy = new Socks5Proxy();
            try
            {
                await ConnectProxyAsync(proxy, listenerEndPoint);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    ConnectDestinationAsync(
                        proxy,
                        new DnsEndPoint("example.com", 443),
                        new NetworkCredential("user", "wrong")));
                await serverTask;
            }
            finally
            {
                proxy.Close();
                listener.Stop();
            }
        }

        private static Task ConnectProxyAsync(Socks5Proxy proxy, EndPoint endpoint)
        {
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.BeginConnectProxy(endpoint, ar =>
            {
                try
                {
                    proxy.EndConnectProxy(ar);
                    completion.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }, null);
            return completion.Task;
        }

        private static Task ConnectDestinationAsync(Socks5Proxy proxy, EndPoint endpoint, NetworkCredential credential)
        {
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.BeginConnectDest(endpoint, ar =>
            {
                try
                {
                    proxy.EndConnectDest(ar);
                    completion.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }, null, credential);
            return completion.Task;
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
        {
            var buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
                if (read == 0)
                {
                    throw new InvalidOperationException("Unexpected EOF in test SOCKS5 server.");
                }
                offset += read;
            }
            return buffer;
        }

        private static async Task WriteFragmentedAsync(NetworkStream stream, byte[] buffer)
        {
            foreach (byte value in buffer)
            {
                await stream.WriteAsync(new byte[] { value });
                await Task.Yield();
            }
        }
    }
}
