using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shadowsocks.Controller;
using Shadowsocks.Core;
using Shadowsocks.Util.Sockets;

namespace Shadowsocks.Proxy
{
    public sealed class HttpProxy : IProxy, IDisposable
    {
        private const int MaxProxyResponseHeaderBytes = 64 * 1024;
        private const string HttpCrlf = "\r\n";

        private static readonly Regex HttpStatusLineRegex = new(
            @"^HTTP/1\.[01] (?<status>\d{3})(?:\s|$)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly byte[] HeaderTerminator = Encoding.ASCII.GetBytes("\r\n\r\n");
        private static readonly byte[] LineTerminator = Encoding.ASCII.GetBytes(HttpCrlf);

        private readonly WrappedSocket _remote = new();
        private readonly object _pendingReceiveLock = new();
        private byte[] _pendingReceiveBuffer = Array.Empty<byte>();
        private int _pendingReceiveOffset;

        public EndPoint LocalEndPoint => _remote.LocalEndPoint;
        public EndPoint ProxyEndPoint { get; private set; }
        public EndPoint DestEndPoint { get; private set; }

        public void BeginConnectProxy(EndPoint remoteEP, AsyncCallback callback, object state)
        {
            ArgumentNullException.ThrowIfNull(remoteEP);
            ProxyEndPoint = remoteEP;
            _remote.BeginConnect(remoteEP, callback, state);
        }

        public void EndConnectProxy(IAsyncResult asyncResult)
        {
            _remote.EndConnect(asyncResult);
            _remote.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
        }

        public void BeginConnectDest(EndPoint destEndPoint, AsyncCallback callback, object state, NetworkCredential auth = null)
        {
            ArgumentNullException.ThrowIfNull(destEndPoint);
            DestEndPoint = destEndPoint;
            ClearPendingReceiveBuffer();

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
            if (TryReadPending(buffer, offset, size, out int copied))
            {
                var completion = new TaskCompletionSource<int>(state, TaskCreationOptions.RunContinuationsAsynchronously);
                completion.TrySetResult(copied);
                if (callback != null)
                {
                    ThreadPool.QueueUserWorkItem(_ => callback(completion.Task));
                }
                return;
            }

            _remote.BeginReceive(buffer, offset, size, socketFlags, callback, state);
        }

        public int EndReceive(IAsyncResult asyncResult)
        {
            if (asyncResult is Task<int> bufferedReceive)
            {
                return bufferedReceive.GetAwaiter().GetResult();
            }
            return _remote.EndReceive(asyncResult);
        }

        public void Shutdown(SocketShutdown how)
        {
            _remote.Shutdown(how);
        }

        public void Close()
        {
            ClearPendingReceiveBuffer();
            _remote.Dispose();
        }

        public void Dispose()
        {
            Close();
            GC.SuppressFinalize(this);
        }

        private async Task CompleteConnectDestAsync(
            EndPoint destination,
            NetworkCredential auth,
            TaskCompletionSource<object> completion)
        {
            try
            {
                string authority = FormatAuthority(destination);
                string authInfo = string.Empty;
                if (auth != null)
                {
                    string authKey = Convert.ToBase64String(
                        Encoding.UTF8.GetBytes((auth.UserName ?? string.Empty) + ":" + (auth.Password ?? string.Empty)));
                    authInfo = $"Proxy-Authorization: Basic {authKey}{HttpCrlf}";
                }

                string requestText =
                    $"CONNECT {authority} HTTP/1.1{HttpCrlf}" +
                    $"Host: {authority}{HttpCrlf}" +
                    $"Proxy-Connection: keep-alive{HttpCrlf}" +
                    $"User-Agent: Shadowsocks-Reborn/{ApplicationInfo.Version}{HttpCrlf}" +
                    authInfo + HttpCrlf;
                byte[] request = Encoding.ASCII.GetBytes(requestText);
                await SendAllAsync(request).ConfigureAwait(false);
                await ReadProxyResponseAsync().ConfigureAwait(false);
                completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }

        private async Task ReadProxyResponseAsync()
        {
            using var response = new MemoryStream();
            var chunk = new byte[4096];

            while (response.Length < MaxProxyResponseHeaderBytes)
            {
                int allowed = (int)Math.Min(chunk.Length, MaxProxyResponseHeaderBytes - response.Length);
                int received = await ReceiveAsync(chunk, 0, allowed).ConfigureAwait(false);
                if (received <= 0)
                {
                    throw ProxyRequestFailed();
                }

                response.Write(chunk, 0, received);
                byte[] bytes = response.GetBuffer();
                int length = checked((int)response.Length);
                int terminatorIndex = IndexOf(bytes, length, HeaderTerminator);
                if (terminatorIndex < 0)
                {
                    continue;
                }

                int statusLineEnd = IndexOf(bytes, terminatorIndex + LineTerminator.Length, LineTerminator);
                if (statusLineEnd <= 0)
                {
                    throw ProxyRequestFailed();
                }

                string statusLine = Encoding.ASCII.GetString(bytes, 0, statusLineEnd);
                Match match = HttpStatusLineRegex.Match(statusLine);
                if (!match.Success || !string.Equals(match.Groups["status"].Value, "200", StringComparison.Ordinal))
                {
                    throw ProxyRequestFailed();
                }

                int payloadOffset = terminatorIndex + HeaderTerminator.Length;
                int payloadLength = length - payloadOffset;
                if (payloadLength > 0)
                {
                    StorePending(bytes, payloadOffset, payloadLength);
                }
                return;
            }

            throw new InvalidDataException("HTTP proxy response headers exceeded the 64 KiB limit.");
        }

        private static string FormatAuthority(EndPoint endpoint)
        {
            return endpoint switch
            {
                DnsEndPoint dns => $"{new IdnMapping().GetAscii(dns.Host)}:{dns.Port}",
                IPEndPoint ip when ip.AddressFamily == AddressFamily.InterNetworkV6 => $"[{ip.Address}]:{ip.Port}",
                IPEndPoint ip => $"{ip.Address}:{ip.Port}",
                _ => throw new ArgumentException(I18N.GetString("Proxy request failed"), nameof(endpoint)),
            };
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

        private void StorePending(byte[] source, int offset, int count)
        {
            lock (_pendingReceiveLock)
            {
                _pendingReceiveBuffer = new byte[count];
                Buffer.BlockCopy(source, offset, _pendingReceiveBuffer, 0, count);
                _pendingReceiveOffset = 0;
            }
        }

        private bool TryReadPending(byte[] destination, int offset, int count, out int copied)
        {
            copied = 0;
            if (count <= 0)
            {
                return false;
            }

            lock (_pendingReceiveLock)
            {
                int remaining = _pendingReceiveBuffer.Length - _pendingReceiveOffset;
                if (remaining <= 0)
                {
                    return false;
                }

                copied = Math.Min(count, remaining);
                Buffer.BlockCopy(_pendingReceiveBuffer, _pendingReceiveOffset, destination, offset, copied);
                _pendingReceiveOffset += copied;
                if (_pendingReceiveOffset >= _pendingReceiveBuffer.Length)
                {
                    _pendingReceiveBuffer = Array.Empty<byte>();
                    _pendingReceiveOffset = 0;
                }
                return true;
            }
        }

        private void ClearPendingReceiveBuffer()
        {
            lock (_pendingReceiveLock)
            {
                _pendingReceiveBuffer = Array.Empty<byte>();
                _pendingReceiveOffset = 0;
            }
        }

        private static int IndexOf(byte[] haystack, int haystackLength, byte[] needle)
        {
            if (needle.Length == 0 || haystackLength < needle.Length)
            {
                return -1;
            }

            int lastStart = haystackLength - needle.Length;
            for (int start = 0; start <= lastStart; start++)
            {
                int index = 0;
                while (index < needle.Length && haystack[start + index] == needle[index])
                {
                    index++;
                }
                if (index == needle.Length)
                {
                    return start;
                }
            }
            return -1;
        }

        private static InvalidOperationException ProxyRequestFailed()
        {
            return new InvalidOperationException(I18N.GetString("Proxy request failed"));
        }
    }
}
