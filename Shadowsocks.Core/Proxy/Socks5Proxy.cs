using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shadowsocks.Controller;
using Shadowsocks.Util.Sockets;

namespace Shadowsocks.Proxy
{
    public class Socks5Proxy : IProxy
    {
        private const byte SocksVersion = 0x05;
        private const byte AuthVersion = 0x01;
        private const byte MethodNoAuthentication = 0x00;
        private const byte MethodUsernamePassword = 0x02;
        private const byte MethodNoAcceptableMethods = 0xFF;
        private const byte CommandConnect = 0x01;
        private const byte AddressTypeIpv4 = 0x01;
        private const byte AddressTypeDomain = 0x03;
        private const byte AddressTypeIpv6 = 0x04;

        private readonly WrappedSocket _remote = new WrappedSocket();

        public EndPoint LocalEndPoint => _remote.LocalEndPoint;
        public EndPoint ProxyEndPoint { get; private set; }
        public EndPoint DestEndPoint { get; private set; }

        public void BeginConnectProxy(EndPoint remoteEP, AsyncCallback callback, object state)
        {
            ProxyEndPoint = remoteEP ?? throw new ArgumentNullException(nameof(remoteEP));
            _remote.BeginConnect(remoteEP, callback, state);
        }

        public void EndConnectProxy(IAsyncResult asyncResult)
        {
            _remote.EndConnect(asyncResult);
            _remote.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
        }

        public void BeginConnectDest(EndPoint destEndPoint, AsyncCallback callback, object state, NetworkCredential auth = null)
        {
            DestEndPoint = destEndPoint ?? throw new ArgumentNullException(nameof(destEndPoint));

            var completion = new TaskCompletionSource<object>(state, TaskCreationOptions.RunContinuationsAsynchronously);
            if (callback != null)
            {
                completion.Task.ContinueWith(
                    task => callback(task),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            _ = CompleteConnectDestAsync(destEndPoint, auth, completion);
        }

        public void EndConnectDest(IAsyncResult asyncResult)
        {
            if (asyncResult is not Task task)
            {
                throw new ArgumentException("Invalid asyncResult.", nameof(asyncResult));
            }

            task.GetAwaiter().GetResult();
        }

        public void BeginSend(byte[] buffer, int offset, int size, SocketFlags socketFlags, AsyncCallback callback,
            object state)
        {
            _remote.BeginSend(buffer, offset, size, socketFlags, callback, state);
        }

        public int EndSend(IAsyncResult asyncResult)
        {
            return _remote.EndSend(asyncResult);
        }

        public void BeginReceive(byte[] buffer, int offset, int size, SocketFlags socketFlags, AsyncCallback callback,
            object state)
        {
            _remote.BeginReceive(buffer, offset, size, socketFlags, callback, state);
        }

        public int EndReceive(IAsyncResult asyncResult)
        {
            return _remote.EndReceive(asyncResult);
        }

        public void Shutdown(SocketShutdown how)
        {
            _remote.Shutdown(how);
        }

        public void Close()
        {
            _remote.Dispose();
        }

        private async Task CompleteConnectDestAsync(
            EndPoint destEndPoint,
            NetworkCredential auth,
            TaskCompletionSource<object> completion)
        {
            try
            {
                await NegotiateAuthenticationAsync(auth).ConfigureAwait(false);
                await SendConnectRequestAsync(destEndPoint).ConfigureAwait(false);
                await ReadConnectReplyAsync().ConfigureAwait(false);
                completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }

        private async Task NegotiateAuthenticationAsync(NetworkCredential auth)
        {
            byte[] methods = auth == null
                ? new byte[] { SocksVersion, 0x01, MethodNoAuthentication }
                : new byte[] { SocksVersion, 0x01, MethodUsernamePassword };

            await SendAllAsync(methods).ConfigureAwait(false);
            byte[] response = await ReceiveExactAsync(2).ConfigureAwait(false);

            if (response[0] != SocksVersion || response[1] == MethodNoAcceptableMethods)
            {
                throw ProxyHandshakeFailed();
            }

            switch (response[1])
            {
                case MethodNoAuthentication when auth == null:
                    return;
                case MethodUsernamePassword when auth != null:
                    await AuthenticateUsernamePasswordAsync(auth).ConfigureAwait(false);
                    return;
                default:
                    throw ProxyHandshakeFailed();
            }
        }

        private async Task AuthenticateUsernamePasswordAsync(NetworkCredential auth)
        {
            byte[] username = Encoding.UTF8.GetBytes(auth.UserName ?? string.Empty);
            byte[] password = Encoding.UTF8.GetBytes(auth.Password ?? string.Empty);
            if (username.Length is < 1 or > 255 || password.Length is < 1 or > 255)
            {
                throw new ArgumentException("SOCKS5 username and password must each encode to 1..255 bytes.", nameof(auth));
            }

            var request = new byte[3 + username.Length + password.Length];
            int offset = 0;
            request[offset++] = AuthVersion;
            request[offset++] = (byte)username.Length;
            Buffer.BlockCopy(username, 0, request, offset, username.Length);
            offset += username.Length;
            request[offset++] = (byte)password.Length;
            Buffer.BlockCopy(password, 0, request, offset, password.Length);

            await SendAllAsync(request).ConfigureAwait(false);
            byte[] response = await ReceiveExactAsync(2).ConfigureAwait(false);
            if (response[0] != AuthVersion || response[1] != 0x00)
            {
                throw ProxyHandshakeFailed();
            }
        }

        private async Task SendConnectRequestAsync(EndPoint destination)
        {
            byte[] address;
            byte addressType;
            int port;

            if (destination is DnsEndPoint dnsEndPoint)
            {
                string asciiHost;
                try
                {
                    asciiHost = new IdnMapping().GetAscii(dnsEndPoint.Host);
                }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException("Invalid SOCKS5 destination host name.", nameof(destination), ex);
                }

                address = Encoding.ASCII.GetBytes(asciiHost);
                if (address.Length is < 1 or > 255)
                {
                    throw new ArgumentException("SOCKS5 destination host name must encode to 1..255 bytes.", nameof(destination));
                }

                addressType = AddressTypeDomain;
                port = dnsEndPoint.Port;
            }
            else if (destination is IPEndPoint ipEndPoint)
            {
                address = ipEndPoint.Address.GetAddressBytes();
                addressType = ipEndPoint.AddressFamily switch
                {
                    AddressFamily.InterNetwork => AddressTypeIpv4,
                    AddressFamily.InterNetworkV6 => AddressTypeIpv6,
                    _ => throw new ArgumentException(I18N.GetString("Proxy request failed"), nameof(destination))
                };
                port = ipEndPoint.Port;
            }
            else
            {
                throw new ArgumentException(I18N.GetString("Proxy request failed"), nameof(destination));
            }

            int addressPrefixLength = addressType == AddressTypeDomain ? 1 : 0;
            var request = new byte[4 + addressPrefixLength + address.Length + 2];
            int offset = 0;
            request[offset++] = SocksVersion;
            request[offset++] = CommandConnect;
            request[offset++] = 0x00;
            request[offset++] = addressType;
            if (addressType == AddressTypeDomain)
            {
                request[offset++] = (byte)address.Length;
            }

            Buffer.BlockCopy(address, 0, request, offset, address.Length);
            offset += address.Length;
            request[offset++] = (byte)(port >> 8);
            request[offset] = (byte)port;

            await SendAllAsync(request).ConfigureAwait(false);
        }

        private async Task ReadConnectReplyAsync()
        {
            byte[] header = await ReceiveExactAsync(4).ConfigureAwait(false);
            if (header[0] != SocksVersion || header[1] != 0x00 || header[2] != 0x00)
            {
                throw ProxyRequestFailed();
            }

            switch (header[3])
            {
                case AddressTypeIpv4:
                    await ReceiveExactAsync(4 + 2).ConfigureAwait(false);
                    break;
                case AddressTypeIpv6:
                    await ReceiveExactAsync(16 + 2).ConfigureAwait(false);
                    break;
                case AddressTypeDomain:
                    byte[] length = await ReceiveExactAsync(1).ConfigureAwait(false);
                    if (length[0] == 0)
                    {
                        throw ProxyRequestFailed();
                    }
                    await ReceiveExactAsync(length[0] + 2).ConfigureAwait(false);
                    break;
                default:
                    throw ProxyRequestFailed();
            }
        }

        private async Task SendAllAsync(byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int sent = await SendAsync(buffer, offset, buffer.Length - offset).ConfigureAwait(false);
                if (sent <= 0)
                {
                    throw new SocketException((int)SocketError.ConnectionReset);
                }
                offset += sent;
            }
        }

        private Task<int> SendAsync(byte[] buffer, int offset, int count)
        {
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _remote.BeginSend(buffer, offset, count, SocketFlags.None, ar =>
                {
                    try
                    {
                        completion.TrySetResult(_remote.EndSend(ar));
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                }, null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            return completion.Task;
        }

        private async Task<byte[]> ReceiveExactAsync(int count)
        {
            var buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int received = await ReceiveAsync(buffer, offset, count - offset).ConfigureAwait(false);
                if (received <= 0)
                {
                    throw new SocketException((int)SocketError.ConnectionReset);
                }
                offset += received;
            }
            return buffer;
        }

        private Task<int> ReceiveAsync(byte[] buffer, int offset, int count)
        {
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _remote.BeginReceive(buffer, offset, count, SocketFlags.None, ar =>
                {
                    try
                    {
                        completion.TrySetResult(_remote.EndReceive(ar));
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                }, null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            return completion.Task;
        }

        private static Exception ProxyHandshakeFailed()
        {
            return new InvalidOperationException(I18N.GetString("Proxy handshake failed"));
        }

        private static Exception ProxyRequestFailed()
        {
            return new InvalidOperationException(I18N.GetString("Proxy request failed"));
        }
    }
}
