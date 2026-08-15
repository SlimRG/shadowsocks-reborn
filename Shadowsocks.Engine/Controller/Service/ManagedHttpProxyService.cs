using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Shadowsocks.Controller.Traffic;
using Shadowsocks.Controller.Traffic.Applications;
using Shadowsocks.Model;

namespace Shadowsocks.Controller.Service
{
    /// <summary>
    /// Managed HTTP/1.1 proxy replacing the bundled Privoxy executable.
    /// Supports CONNECT tunnelling and ordinary absolute-form HTTP requests.
    /// Routing decisions are application-aware and shared with Admin Mode.
    /// </summary>
    internal sealed class ManagedHttpProxyService(TrafficPolicyEngine policyEngine, Configuration initialConfiguration) : Listener.Service
    {
        private const int MaxHeaderBytes = 64 * 1024;
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly TrafficPolicyEngine _policyEngine = policyEngine ?? throw new ArgumentNullException(nameof(policyEngine));
        private readonly TcpProcessResolver _processResolver = new();
        private readonly ConcurrentDictionary<long, Connection> _connections = new();
        private Configuration _configuration = initialConfiguration ?? throw new ArgumentNullException(nameof(initialConfiguration));
        private long _nextConnectionId;
        private volatile bool _stopping;

        public void UpdateConfiguration(Configuration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public override bool Handle(byte[] firstPacket, int length, Socket socket, object state)
        {
            if (socket.ProtocolType != ProtocolType.Tcp || !LooksLikeHttp(firstPacket, length))
            {
                return false;
            }

            if (_stopping)
            {
                socket.Dispose();
                return true;
            }

            long id = Interlocked.Increment(ref _nextConnectionId);
            byte[] initial = firstPacket.AsSpan(0, length).ToArray();
            ProcessIdentity identity = _processResolver.Resolve(socket);
            Connection connection = new(
                id,
                socket,
                initial,
                identity,
                _policyEngine,
                _configuration,
                RemoveConnection);
            _connections[id] = connection;
            connection.Start();
            return true;
        }

        public override void Stop()
        {
            _stopping = true;
            foreach (Connection connection in _connections.Values)
            {
                connection.Dispose();
            }

            _connections.Clear();
        }

        private void RemoveConnection(long id)
        {
            _connections.TryRemove(id, out _);
        }

        private static bool LooksLikeHttp(byte[] packet, int length)
        {
            if (length < 4)
            {
                return false;
            }

            ReadOnlySpan<byte> span = packet.AsSpan(0, Math.Min(length, 16));
            return StartsWithAscii(span, "CONNECT ")
                || StartsWithAscii(span, "GET ")
                || StartsWithAscii(span, "POST ")
                || StartsWithAscii(span, "PUT ")
                || StartsWithAscii(span, "DELETE ")
                || StartsWithAscii(span, "HEAD ")
                || StartsWithAscii(span, "OPTIONS ")
                || StartsWithAscii(span, "PATCH ")
                || StartsWithAscii(span, "TRACE ");
        }

        private static bool StartsWithAscii(ReadOnlySpan<byte> value, string prefix)
        {
            if (value.Length < prefix.Length)
            {
                return false;
            }

            for (int i = 0; i < prefix.Length; i++)
            {
                if (value[i] != (byte)prefix[i])
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class Connection(
            long id,
            Socket client,
            byte[] initialPacket,
            ProcessIdentity process,
            TrafficPolicyEngine policyEngine,
            Configuration configuration,
            Action<long> onClosed) : IDisposable
        {
            private readonly long _id = id;
            private readonly Socket _client = client;
            private readonly byte[] _initialPacket = initialPacket;
            private readonly ProcessIdentity _process = process;
            private readonly TrafficPolicyEngine _policyEngine = policyEngine;
            private readonly Configuration _configuration = configuration;
            private readonly Action<long> _onClosed = onClosed;
            private readonly CancellationTokenSource _shutdown = new();
            private Socket _upstream;
            private int _disposed;

            public void Start()
            {
                _ = RunAsync();
            }

            private async Task RunAsync()
            {
                try
                {
                    using NetworkStream clientStream = new(_client, ownsSocket: false);
                    byte[] requestBytes = await ReadHeadersAsync(clientStream, _initialPacket, _shutdown.Token).ConfigureAwait(false);
                    HttpProxyRequest request = HttpProxyRequest.Parse(requestBytes);

                    TrafficContext context = new()
                    {
                        ProcessId = _process.ProcessId,
                        ProcessPath = _process.ProcessPath,
                        ProcessName = _process.ProcessName,
                        Protocol = "TCP",
                        DestinationHost = request.Host,
                        DestinationPort = request.Port,
                        IsInternal = _process.IsCurrentProcess,
                    };
                    RouteDecision decision = _policyEngine.EvaluateUserMode(context);

                    if (decision.Action == TrafficRouteAction.Block)
                    {
                        await WriteErrorAsync(clientStream, 403, "Blocked by application routing policy", _shutdown.Token).ConfigureAwait(false);
                        return;
                    }

                    _upstream = decision.Action == TrafficRouteAction.Direct
                        ? await ConnectDirectAsync(request.Host, request.Port, _shutdown.Token).ConfigureAwait(false)
                        : await Socks5Connector.ConnectAsync(
                            _configuration.LocalHost,
                            _configuration.localPort,
                            request.Host,
                            request.Port,
                            _shutdown.Token).ConfigureAwait(false);

                    using NetworkStream upstreamStream = new(_upstream, ownsSocket: false);
                    if (request.IsConnect)
                    {
                        byte[] established = "HTTP/1.1 200 Connection Established\r\nProxy-Agent: Shadowsocks.NET\r\n\r\n"u8.ToArray();
                        await clientStream.WriteAsync(established, _shutdown.Token).ConfigureAwait(false);

                        // A client may optimistically send tunnel bytes in the same TCP
                        // packet as the CONNECT headers. Preserve them instead of dropping
                        // the bytes already consumed by ReadHeadersAsync.
                        if (requestBytes.Length > request.HeaderEnd)
                        {
                            await upstreamStream.WriteAsync(
                                requestBytes.AsMemory(request.HeaderEnd),
                                _shutdown.Token).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        byte[] rewritten = request.RewriteForOriginServer(requestBytes);
                        await upstreamStream.WriteAsync(rewritten, _shutdown.Token).ConfigureAwait(false);
                    }

                    Task clientToUpstream = PumpAsync(clientStream, upstreamStream, _shutdown.Token);
                    Task upstreamToClient = PumpAsync(upstreamStream, clientStream, _shutdown.Token);
                    await Task.WhenAny(clientToUpstream, upstreamToClient).ConfigureAwait(false);
                    _shutdown.Cancel();
                    await IgnoreShutdownAsync(clientToUpstream).ConfigureAwait(false);
                    await IgnoreShutdownAsync(upstreamToClient).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    Logger.Debug(exception, "Managed HTTP proxy connection failed");
                    try
                    {
                        using NetworkStream clientStream = new(_client, ownsSocket: false);
                        await WriteErrorAsync(clientStream, 502, "Proxy connection failed", CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
                finally
                {
                    Dispose();
                }
            }

            private static async Task<Socket> ConnectDirectAsync(string host, int port, CancellationToken cancellationToken)
            {
                Socket socket = new(SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                try
                {
                    await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
                    return socket;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            private static async Task<byte[]> ReadHeadersAsync(NetworkStream stream, byte[] initial, CancellationToken cancellationToken)
            {
                using MemoryStream buffer = new(Math.Min(MaxHeaderBytes, Math.Max(4096, initial.Length + 1024)));
                buffer.Write(initial, 0, initial.Length);
                if (FindHeaderEnd(initial) >= 0)
                {
                    return buffer.ToArray();
                }

                byte[] chunk = new byte[4096];
                while (buffer.Length < MaxHeaderBytes)
                {
                    int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("Client disconnected before completing HTTP proxy headers.");
                    }

                    buffer.Write(chunk, 0, read);
                    byte[] current = buffer.GetBuffer();
                    if (FindHeaderEnd(current.AsSpan(0, checked((int)buffer.Length))) >= 0)
                    {
                        return buffer.ToArray();
                    }
                }

                throw new InvalidDataException($"HTTP proxy headers exceed {MaxHeaderBytes} bytes.");
            }

            private static int FindHeaderEnd(ReadOnlySpan<byte> bytes)
            {
                return bytes.IndexOf("\r\n\r\n"u8);
            }

            private static async Task PumpAsync(NetworkStream source, NetworkStream destination, CancellationToken cancellationToken)
            {
                byte[] buffer = new byte[32 * 1024];
                while (true)
                {
                    int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            private static async Task IgnoreShutdownAsync(Task task)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
                catch (SocketException)
                {
                }
            }

            private static async Task WriteErrorAsync(NetworkStream stream, int status, string message, CancellationToken cancellationToken)
            {
                string body = message + "\r\n";
                string response = $"HTTP/1.1 {status} {message}\r\nConnection: close\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response), cancellationToken).ConfigureAwait(false);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                _shutdown.Cancel();
                _upstream?.Dispose();
                _client.Dispose();
                _shutdown.Dispose();
                _onClosed(_id);
            }
        }

        private sealed class HttpProxyRequest(
            string initialMethod,
            string initialTarget,
            string initialHost,
            int initialPort,
            bool initialIsConnect,
            int initialHeaderEnd)
        {
            public string Method { get; } = initialMethod;
            public string Target { get; } = initialTarget;
            public string Host { get; } = initialHost;
            public int Port { get; } = initialPort;
            public bool IsConnect { get; } = initialIsConnect;
            public int HeaderEnd { get; } = initialHeaderEnd;

            public static HttpProxyRequest Parse(byte[] requestBytes)
            {
                int headerEnd = requestBytes.AsSpan().IndexOf("\r\n\r\n"u8);
                if (headerEnd < 0)
                {
                    throw new InvalidDataException("Incomplete HTTP proxy request.");
                }

                string headers = Encoding.Latin1.GetString(requestBytes, 0, headerEnd + 4);
                int firstLineEnd = headers.IndexOf("\r\n", StringComparison.Ordinal);
                if (firstLineEnd <= 0)
                {
                    throw new InvalidDataException("Invalid HTTP request line.");
                }

                string firstLine = headers[..firstLineEnd];
                string[] parts = firstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3)
                {
                    throw new InvalidDataException("Invalid HTTP proxy request line.");
                }

                string method = parts[0];
                string target = parts[1];
                bool connect = method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase);
                if (connect)
                {
                    (string host, int port) = ParseAuthority(target, 443);
                    return new HttpProxyRequest(method, target, host, port, true, headerEnd + 4);
                }

                if (Uri.TryCreate(target, UriKind.Absolute, out Uri absoluteUri))
                {
                    if (!absoluteUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Unsupported HTTP proxy URI scheme '{absoluteUri.Scheme}'. HTTPS must use CONNECT.");
                    }

                    int port = absoluteUri.IsDefaultPort ? 80 : absoluteUri.Port;
                    return new HttpProxyRequest(method, target, absoluteUri.Host, port, false, headerEnd + 4);
                }

                string hostHeader = FindHeader(headers, "Host")
                    ?? throw new InvalidDataException("HTTP proxy request does not contain a Host header.");
                (string fallbackHost, int fallbackPort) = ParseAuthority(hostHeader, 80);
                return new HttpProxyRequest(method, target, fallbackHost, fallbackPort, false, headerEnd + 4);
            }

            public byte[] RewriteForOriginServer(byte[] requestBytes)
            {
                if (IsConnect)
                {
                    return requestBytes;
                }

                string headers = Encoding.Latin1.GetString(requestBytes, 0, HeaderEnd);
                int firstLineEnd = headers.IndexOf("\r\n", StringComparison.Ordinal);
                if (firstLineEnd <= 0)
                {
                    throw new InvalidDataException("Invalid HTTP request line.");
                }

                string version = headers[..firstLineEnd].Split(' ', 3)[2];
                string originTarget = Target;
                if (Uri.TryCreate(Target, UriKind.Absolute, out Uri uri))
                {
                    originTarget = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
                }

                string[] lines = headers[(firstLineEnd + 2)..].Split("\r\n", StringSplitOptions.None);
                bool isUpgrade = false;
                StringBuilder rewritten = new();
                rewritten.Append(Method).Append(' ').Append(originTarget).Append(' ').Append(version).Append("\r\n");

                foreach (string line in lines)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    int separator = line.IndexOf(':');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    string name = line[..separator].Trim();
                    if (name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
                    {
                        isUpgrade = true;
                    }

                    if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    rewritten.Append(line).Append("\r\n");
                }

                rewritten.Append(isUpgrade ? "Connection: Upgrade\r\n" : "Connection: close\r\n");
                rewritten.Append("\r\n");
                byte[] headerBytes = Encoding.Latin1.GetBytes(rewritten.ToString());

                if (requestBytes.Length == HeaderEnd)
                {
                    return headerBytes;
                }

                byte[] result = new byte[headerBytes.Length + requestBytes.Length - HeaderEnd];
                Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
                Buffer.BlockCopy(requestBytes, HeaderEnd, result, headerBytes.Length, requestBytes.Length - HeaderEnd);
                return result;
            }

            private static string FindHeader(string headers, string name)
            {
                string prefix = name + ":";
                string[] lines = headers.Split("\r\n", StringSplitOptions.None);
                foreach (string line in lines)
                {
                    if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return line[prefix.Length..].Trim();
                    }
                }

                return null;
            }

            private static (string Host, int Port) ParseAuthority(string authority, int defaultPort)
            {
                string value = authority.Trim();
                if (value.StartsWith('['))
                {
                    int closing = value.IndexOf(']');
                    if (closing <= 0)
                    {
                        throw new InvalidDataException("Invalid IPv6 authority.");
                    }

                    string host = value[1..closing];
                    if (closing + 1 < value.Length && value[closing + 1] == ':')
                    {
                        return (host, ParsePort(value[(closing + 2)..]));
                    }

                    return (host, defaultPort);
                }

                int colon = value.LastIndexOf(':');
                if (colon > 0 && value.IndexOf(':') == colon)
                {
                    return (value[..colon], ParsePort(value[(colon + 1)..]));
                }

                return (value, defaultPort);
            }

            private static int ParsePort(string text)
            {
                if (!int.TryParse(text, out int port) || port is < 1 or > 65535)
                {
                    throw new InvalidDataException("Invalid destination port in HTTP proxy request.");
                }

                return port;
            }
        }
    }
}
