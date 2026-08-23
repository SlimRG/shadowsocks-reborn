using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Core;
using Shadowsocks.Proxy;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class HttpProxyTests
    {
        [TestMethod]
        public async Task ConnectUsesProductUserAgentBasicAuthAndPreservesTunnelBytes()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var listenerEndPoint = (IPEndPoint)listener.LocalEndpoint;

            Task serverTask = Task.Run(async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                NetworkStream stream = client.GetStream();
                string request = await ReadHeadersAsync(stream);

                StringAssert.Contains(request, "CONNECT example.com:443 HTTP/1.1\r\n");
                StringAssert.Contains(request, $"User-Agent: Shadowsocks-Reborn/{ApplicationInfo.Version}\r\n");
                string expectedAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes("user:secret"));
                StringAssert.Contains(request, $"Proxy-Authorization: Basic {expectedAuth}\r\n");

                byte[] responsePrefix = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 Connection established\r\nProxy-Agent: test\r\n");
                foreach (byte value in responsePrefix)
                {
                    await stream.WriteAsync(new byte[] { value });
                    await Task.Yield();
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\nHELLO"));
            });

            var proxy = new HttpProxy();
            try
            {
                await ConnectProxyAsync(proxy, listenerEndPoint);
                await ConnectDestinationAsync(
                    proxy,
                    new DnsEndPoint("example.com", 443),
                    new NetworkCredential("user", "secret"));

                var payload = new byte[5];
                int received = await ReceiveAsync(proxy, payload);
                Assert.AreEqual(5, received);
                Assert.AreEqual("HELLO", Encoding.ASCII.GetString(payload));
                await serverTask;
            }
            finally
            {
                proxy.Close();
                listener.Stop();
            }
        }

        [TestMethod]
        public async Task Non200ConnectResponseFails()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var listenerEndPoint = (IPEndPoint)listener.LocalEndpoint;

            Task serverTask = Task.Run(async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                NetworkStream stream = client.GetStream();
                await ReadHeadersAsync(stream);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\n\r\n"));
            });

            var proxy = new HttpProxy();
            try
            {
                await ConnectProxyAsync(proxy, listenerEndPoint);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    ConnectDestinationAsync(proxy, new DnsEndPoint("example.com", 443), null));
                await serverTask;
            }
            finally
            {
                proxy.Close();
                listener.Stop();
            }
        }

        private static Task ConnectProxyAsync(HttpProxy proxy, EndPoint endpoint)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.BeginConnectProxy(endpoint, ar =>
            {
                try
                {
                    proxy.EndConnectProxy(ar);
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }, null);
            return completion.Task;
        }

        private static Task ConnectDestinationAsync(HttpProxy proxy, EndPoint endpoint, NetworkCredential credential)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.BeginConnectDest(endpoint, ar =>
            {
                try
                {
                    proxy.EndConnectDest(ar);
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }, null, credential);
            return completion.Task;
        }

        private static Task<int> ReceiveAsync(HttpProxy proxy, byte[] buffer)
        {
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.BeginReceive(buffer, 0, buffer.Length, SocketFlags.None, ar =>
            {
                try
                {
                    completion.TrySetResult(proxy.EndReceive(ar));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }, null);
            return completion.Task;
        }

        private static async Task<string> ReadHeadersAsync(NetworkStream stream)
        {
            var bytes = new System.Collections.Generic.List<byte>();
            var one = new byte[1];
            while (bytes.Count < 64 * 1024)
            {
                int read = await stream.ReadAsync(one);
                if (read == 0)
                {
                    throw new InvalidOperationException("Unexpected EOF while reading HTTP CONNECT request.");
                }
                bytes.Add(one[0]);
                int count = bytes.Count;
                if (count >= 4
                    && bytes[count - 4] == '\r'
                    && bytes[count - 3] == '\n'
                    && bytes[count - 2] == '\r'
                    && bytes[count - 1] == '\n')
                {
                    return Encoding.ASCII.GetString(bytes.ToArray());
                }
            }

            throw new InvalidOperationException("HTTP CONNECT request headers exceeded the test limit.");
        }
    }
}
