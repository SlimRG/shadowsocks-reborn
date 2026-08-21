using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Shadowsocks.NetworkService.Routing;

/// <summary>
/// Local transparent DNS bridge. WinDivert reflects application DNS packets into
/// the two listeners below. The bridge forwards the DNS payload/stream directly
/// to the DNSCrypt loopback listener, then emits replies through the same reflected
/// flow mechanism used by the Shadowsocks transparent relays.
/// </summary>
internal sealed class TransparentDnsRelay : IAsyncDisposable
{
    private static readonly TimeSpan DefaultUdpIdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultUdpSweepInterval = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan UdpFallbackDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan UdpPendingLifetime = TimeSpan.FromSeconds(3);

    private readonly FlowRegistry _flows;
    private readonly IReadOnlyList<IPEndPoint> _upstreamEndpoints;
    private readonly IPEndPoint? _localSocksEndpoint;
    private readonly TimeSpan _udpIdleTimeout;
    private readonly TimeSpan _udpSweepInterval;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Socket _udpListener;
    private readonly Socket _tcpListener;
    private readonly ConcurrentDictionary<FlowKey, UdpDnsFlow> _udpFlows = new();
    private readonly ConcurrentDictionary<long, Task> _tcpConnections = new();
    private Task? _udpLoop;
    private Task? _udpSweepLoop;
    private Task? _tcpLoop;
    private long _nextTcpConnectionId;
    private int _started;
    private int _disposed;

    public TransparentDnsRelay(
        FlowRegistry flows,
        int dnsCryptPort,
        TimeSpan? udpIdleTimeout = null,
        TimeSpan? udpSweepInterval = null)
        : this(flows, [new IPEndPoint(IPAddress.Loopback, dnsCryptPort)], udpIdleTimeout, udpSweepInterval)
    {
    }

    public TransparentDnsRelay(
        FlowRegistry flows,
        IPEndPoint upstreamEndpoint,
        TimeSpan? udpIdleTimeout = null,
        TimeSpan? udpSweepInterval = null)
        : this(flows, [upstreamEndpoint], udpIdleTimeout, udpSweepInterval)
    {
    }

    public TransparentDnsRelay(
        FlowRegistry flows,
        IReadOnlyList<IPEndPoint> upstreamEndpoints,
        TimeSpan? udpIdleTimeout = null,
        TimeSpan? udpSweepInterval = null,
        string? proxyHost = null,
        int proxyPort = 0)
    {
        ArgumentNullException.ThrowIfNull(upstreamEndpoints);
        IPEndPoint[] normalizedEndpoints = upstreamEndpoints
            .Where(endpoint => endpoint is not null)
            .DistinctBy(endpoint => $"{endpoint.Address}|{endpoint.Port}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedEndpoints.Length == 0)
            throw new ArgumentException("At least one DNS upstream endpoint is required.", nameof(upstreamEndpoints));
        if (normalizedEndpoints.Any(endpoint => endpoint.Port is < 1 or > 65535))
            throw new ArgumentOutOfRangeException(nameof(upstreamEndpoints));

        _flows = flows ?? throw new ArgumentNullException(nameof(flows));
        _upstreamEndpoints = normalizedEndpoints;
        if (!string.IsNullOrWhiteSpace(proxyHost))
        {
            if (proxyPort is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(proxyPort));
            IPAddress proxyAddress = IPAddress.TryParse(proxyHost, out IPAddress? parsed)
                ? parsed
                : Dns.GetHostAddresses(proxyHost).First(static ip => ip.AddressFamily == AddressFamily.InterNetwork);
            _localSocksEndpoint = new IPEndPoint(proxyAddress, proxyPort);
        }
        _udpIdleTimeout = udpIdleTimeout ?? DefaultUdpIdleTimeout;
        _udpSweepInterval = udpSweepInterval ?? DefaultUdpSweepInterval;
        if (_udpIdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(udpIdleTimeout));
        }
        if (_udpSweepInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(udpSweepInterval));
        }

        _udpListener = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)
        {
            DualMode = true,
        };
        _udpListener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpListener.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        UdpPort = ((IPEndPoint)_udpListener.LocalEndPoint!).Port;

        _tcpListener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
        {
            DualMode = true,
            NoDelay = true,
        };
        _tcpListener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _tcpListener.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        _tcpListener.Listen(256);
        TcpPort = ((IPEndPoint)_tcpListener.LocalEndPoint!).Port;
    }

    public int TcpPort { get; }
    public int UdpPort { get; }
    internal int UdpFlowCount => _udpFlows.Count;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("Transparent DNS relay has already been started.");
        }

        _udpLoop = UdpReceiveLoopAsync();
        _udpSweepLoop = UdpSweepLoopAsync();
        _tcpLoop = TcpAcceptLoopAsync();
    }

    private async Task UdpReceiveLoopAsync()
    {
        byte[] buffer = new byte[65535];
        EndPoint peerTemplate = new IPEndPoint(IPAddress.IPv6Any, 0);
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                SocketReceiveFromResult result = await _udpListener
                    .ReceiveFromAsync(buffer, SocketFlags.None, peerTemplate, _shutdown.Token)
                    .ConfigureAwait(false);
                if (result.RemoteEndPoint is not IPEndPoint peer)
                {
                    continue;
                }

                IPAddress remoteAddress = Normalize(peer.Address);
                if (!_flows.TryGetByPeer(17, remoteAddress, checked((ushort)peer.Port), out FlowState? state)
                    || state is null
                    || state.Key.RemotePort != 53)
                {
                    continue;
                }

                UdpDnsFlow flow = _udpFlows.GetOrAdd(
                    state.Key,
                    key => new UdpDnsFlow(
                        key,
                        _udpListener,
                        _upstreamEndpoints,
                        _localSocksEndpoint,
                        _shutdown.Token,
                        RemoveFaultedUdpFlow));
                try
                {
                    await flow.SendAsync(buffer.AsMemory(0, result.ReceivedBytes)).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    RemoveUdpFlow(state.Key, flow);
                }
                catch (ObjectDisposedException)
                {
                    RemoveUdpFlow(state.Key, flow);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                // A single malformed/broken UDP flow must never terminate the listener.
                try
                {
                    await Task.Delay(25, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task UdpSweepLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_udpSweepInterval, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            DateTime now = DateTime.UtcNow;
            foreach ((FlowKey key, UdpDnsFlow flow) in _udpFlows)
            {
                if (now - flow.LastSeenUtc > _udpIdleTimeout)
                {
                    RemoveUdpFlow(key, flow);
                }
            }
        }
    }

    private void RemoveFaultedUdpFlow(FlowKey key, UdpDnsFlow flow)
        => RemoveUdpFlow(key, flow);

    private void RemoveUdpFlow(FlowKey key, UdpDnsFlow flow)
    {
        if (_udpFlows.TryRemove(new KeyValuePair<FlowKey, UdpDnsFlow>(key, flow)))
        {
            _ = DisposeUdpFlowAsync(flow);
        }
    }

    private static async Task DisposeUdpFlowAsync(UdpDnsFlow flow)
    {
        try
        {
            await flow.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task TcpAcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _tcpListener.AcceptAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            TrackTcpConnection(client);
        }
    }


    private void TrackTcpConnection(Socket client)
    {
        long id = Interlocked.Increment(ref _nextTcpConnectionId);
        Task task = HandleTcpConnectionAsync(client);
        _tcpConnections[id] = task;

        if (task.IsCompleted)
        {
            _tcpConnections.TryRemove(id, out _);
            return;
        }

        _ = task.ContinueWith(
            completed =>
            {
                _tcpConnections.TryRemove(id, out _);
                _ = completed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleTcpConnectionAsync(Socket client)
    {
        using (client)
        {
            client.NoDelay = true;
            if (client.RemoteEndPoint is not IPEndPoint peer || client.LocalEndPoint is not IPEndPoint local)
            {
                return;
            }

            IPAddress remoteAddress = Normalize(peer.Address);
            IPAddress localAddress = Normalize(local.Address);
            if (!_flows.TryGetReflected(6, localAddress, checked((ushort)peer.Port), remoteAddress, out FlowState? state)
                || state is null
                || state.Key.RemotePort != 53)
            {
                return;
            }

            using Socket? upstream = await ConnectTcpUpstreamAsync(_shutdown.Token).ConfigureAwait(false);
            if (upstream is null)
                return;

            using NetworkStream clientStream = new(client, ownsSocket: false);
            using NetworkStream upstreamStream = new(upstream, ownsSocket: false);
            Task clientToUpstream = PumpAsync(clientStream, upstreamStream, _shutdown.Token);
            Task upstreamToClient = PumpAsync(upstreamStream, clientStream, _shutdown.Token);
            await Task.WhenAny(clientToUpstream, upstreamToClient).ConfigureAwait(false);

            TryShutdown(client);
            TryShutdown(upstream);
            await Task.WhenAll(clientToUpstream, upstreamToClient).ConfigureAwait(false);
        }
    }


    private async Task<Socket?> ConnectTcpUpstreamAsync(CancellationToken cancellationToken)
    {
        foreach (IPEndPoint endpoint in _upstreamEndpoints)
        {
            Socket? socket = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                if (_localSocksEndpoint is not null)
                {
                    socket = await Socks5Connector.ConnectAsync(
                        _localSocksEndpoint.Address.ToString(),
                        _localSocksEndpoint.Port,
                        endpoint.Address,
                        endpoint.Port,
                        timeout.Token).ConfigureAwait(false);
                }
                else
                {
                    socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true,
                    };
                    await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
                }
                return socket;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket?.Dispose();
            }
            catch (SocketException)
            {
                socket?.Dispose();
            }
            catch (IOException)
            {
                socket?.Dispose();
            }
        }
        return null;
    }


    private static void TryShutdown(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task PumpAsync(Stream source, Stream destination, CancellationToken token)
    {
        byte[] buffer = new byte[32 * 1024];
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
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
        catch (ObjectDisposedException)
        {
        }
    }

    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        _udpListener.Dispose();
        _tcpListener.Dispose();

        UdpDnsFlow[] udpFlows = _udpFlows.Values.ToArray();
        _udpFlows.Clear();
        foreach (UdpDnsFlow flow in udpFlows)
        {
            flow.Dispose();
        }

        await AwaitIgnoringShutdownAsync(_udpLoop).ConfigureAwait(false);
        await AwaitIgnoringShutdownAsync(_udpSweepLoop).ConfigureAwait(false);
        await AwaitIgnoringShutdownAsync(_tcpLoop).ConfigureAwait(false);

        Task[] tcpConnections = _tcpConnections.Values.ToArray();
        if (tcpConnections.Length > 0)
        {
            try
            {
                await Task.WhenAll(tcpConnections).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        _tcpConnections.Clear();

        foreach (UdpDnsFlow flow in udpFlows)
        {
            await flow.DisposeAsync().ConfigureAwait(false);
        }

        _shutdown.Dispose();
    }

    private static async Task AwaitIgnoringShutdownAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    internal static IPAddress SelectUdpBindAddress(IPAddress upstreamAddress)
    {
        ArgumentNullException.ThrowIfNull(upstreamAddress);
        if (IPAddress.IsLoopback(upstreamAddress))
            return upstreamAddress.AddressFamily == AddressFamily.InterNetworkV6
                ? IPAddress.IPv6Loopback
                : IPAddress.Loopback;
        return upstreamAddress.AddressFamily == AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Any
            : IPAddress.Any;
    }

    private sealed class UdpDnsFlow : IDisposable, IAsyncDisposable
    {
        private sealed class PendingQuery
        {
            public PendingQuery(long generation, byte[] payload)
            {
                Generation = generation;
                Payload = payload;
            }

            public long Generation { get; }
            public byte[] Payload { get; }
            public int Completed;
        }

        private readonly FlowKey _flow;
        private readonly Socket _transparentListener;
        private readonly CancellationToken _token;
        private readonly Action<FlowKey, UdpDnsFlow> _onFault;
        private readonly IReadOnlyList<Socket> _dnsSockets;
        private readonly IReadOnlyList<IPEndPoint> _upstreamEndpoints;
        private readonly IPEndPoint? _localSocksEndpoint;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<ushort, PendingQuery> _pending = new();
        private readonly Task[] _receiveLoops;
        private long _lastSeenTicks;
        private long _generation;
        private int _disposed;
        private int _cleanupStarted;

        public UdpDnsFlow(
            FlowKey flow,
            Socket transparentListener,
            IReadOnlyList<IPEndPoint> upstreamEndpoints,
            IPEndPoint? localSocksEndpoint,
            CancellationToken token,
            Action<FlowKey, UdpDnsFlow> onFault)
        {
            _flow = flow;
            _transparentListener = transparentListener;
            _token = token;
            _onFault = onFault;
            _upstreamEndpoints = upstreamEndpoints;
            _localSocksEndpoint = localSocksEndpoint;
            _lastSeenTicks = DateTime.UtcNow.Ticks;

            var sockets = new List<Socket>(upstreamEndpoints.Count);
            try
            {
                foreach (IPEndPoint endpoint in upstreamEndpoints)
                {
                    AddressFamily family = localSocksEndpoint?.AddressFamily ?? endpoint.AddressFamily;
                    var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
                    if (localSocksEndpoint is null)
                    {
                        IPAddress bindAddress = SelectUdpBindAddress(endpoint.Address);
                        socket.Bind(new IPEndPoint(bindAddress, 0));
                        socket.Connect(endpoint);
                    }
                    else
                    {
                        IPAddress bindAddress = family == AddressFamily.InterNetworkV6
                            ? IPAddress.IPv6Loopback
                            : IPAddress.Loopback;
                        socket.Bind(new IPEndPoint(bindAddress, 0));
                    }
                    sockets.Add(socket);
                }
            }
            catch
            {
                foreach (Socket socket in sockets)
                    socket.Dispose();
                throw;
            }

            _dnsSockets = sockets;
            _receiveLoops = sockets.Select((socket, index) => ReceiveFromUpstreamAsync(socket, index)).ToArray();
        }

        public DateTime LastSeenUtc => new(Interlocked.Read(ref _lastSeenTicks), DateTimeKind.Utc);

        public async Task SendAsync(ReadOnlyMemory<byte> payload)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (payload.Length < 2)
                return;

            Touch();
            ushort transactionId = checked((ushort)((payload.Span[0] << 8) | payload.Span[1]));
            var pending = new PendingQuery(Interlocked.Increment(ref _generation), payload.ToArray());
            _pending[transactionId] = pending;

            await _sendLock.WaitAsync(_token).ConfigureAwait(false);
            try
            {
                await SendToUpstreamAsync(0, payload).ConfigureAwait(false);
                Touch();
            }
            finally
            {
                _sendLock.Release();
            }

            _ = ScheduleFallbackAndCleanupAsync(transactionId, pending);
        }

        private async Task ScheduleFallbackAndCleanupAsync(ushort transactionId, PendingQuery pending)
        {
            try
            {
                if (_dnsSockets.Count > 1)
                {
                    await Task.Delay(UdpFallbackDelay, _token).ConfigureAwait(false);
                    if (!IsPending(transactionId, pending))
                        return;

                    await _sendLock.WaitAsync(_token).ConfigureAwait(false);
                    try
                    {
                        // A backup is a fallback, not a fan-out: it receives the query only
                        // after the primary had a chance to answer first.
                        await SendToUpstreamAsync(1, pending.Payload).ConfigureAwait(false);
                        Touch();
                    }
                    finally
                    {
                        _sendLock.Release();
                    }
                }

                TimeSpan remaining = UdpPendingLifetime - (_dnsSockets.Count > 1 ? UdpFallbackDelay : TimeSpan.Zero);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, _token).ConfigureAwait(false);
                if (IsPending(transactionId, pending))
                    _pending.TryRemove(new KeyValuePair<ushort, PendingQuery>(transactionId, pending));
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
                if (_dnsSockets.Count == 1)
                    _onFault(_flow, this);
            }
        }

        private async Task SendToUpstreamAsync(int index, ReadOnlyMemory<byte> payload)
        {
            Socket socket = _dnsSockets[index];
            if (_localSocksEndpoint is null)
            {
                await socket.SendAsync(payload, SocketFlags.None, _token).ConfigureAwait(false);
                return;
            }

            byte[] packet = Socks5Connector.BuildUdpDatagram(_upstreamEndpoints[index], payload.Span);
            await socket.SendToAsync(packet, SocketFlags.None, _localSocksEndpoint, _token).ConfigureAwait(false);
        }

        private bool IsPending(ushort transactionId, PendingQuery pending)
            => Volatile.Read(ref pending.Completed) == 0
                && _pending.TryGetValue(transactionId, out PendingQuery? current)
                && ReferenceEquals(current, pending);

        private async Task ReceiveFromUpstreamAsync(Socket dnsSocket, int upstreamIndex)
        {
            byte[] buffer = new byte[65535];
            while (!_token.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    ReadOnlyMemory<byte> responsePayload;
                    if (_localSocksEndpoint is null)
                    {
                        int received = await dnsSocket.ReceiveAsync(buffer, SocketFlags.None, _token).ConfigureAwait(false);
                        responsePayload = buffer.AsMemory(0, received);
                    }
                    else
                    {
                        EndPoint sender = new IPEndPoint(
                            _localSocksEndpoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
                            0);
                        SocketReceiveFromResult received = await dnsSocket
                            .ReceiveFromAsync(buffer, SocketFlags.None, sender, _token)
                            .ConfigureAwait(false);
                        if (!Socks5Connector.TryParseUdpDatagram(
                                buffer.AsSpan(0, received.ReceivedBytes),
                                out IPEndPoint? source,
                                out byte[] payload)
                            || source is null
                            || !source.Address.Equals(_upstreamEndpoints[upstreamIndex].Address)
                            || source.Port != _upstreamEndpoints[upstreamIndex].Port)
                        {
                            continue;
                        }
                        responsePayload = payload;
                    }
                    if (responsePayload.Length < 2)
                        continue;

                    ushort transactionId = checked((ushort)((responsePayload.Span[0] << 8) | responsePayload.Span[1]));
                    if (!_pending.TryGetValue(transactionId, out PendingQuery? pending)
                        || Interlocked.Exchange(ref pending.Completed, 1) != 0
                        || !_pending.TryRemove(new KeyValuePair<ushort, PendingQuery>(transactionId, pending)))
                    {
                        continue;
                    }

                    Touch();
                    IPEndPoint fakePeer = new(_flow.RemoteAddress, _flow.LocalPort);
                    await _transparentListener
                        .SendToAsync(responsePayload, SocketFlags.None, fakePeer, _token)
                        .ConfigureAwait(false);
                    Touch();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException) when (_token.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
                {
                    break;
                }
                catch (SocketException)
                {
                    // One upstream socket can fail while the backup remains useful. Let the
                    // pending fallback path try the other resolver before faulting the flow.
                    if (_dnsSockets.Count == 1)
                        _onFault(_flow, this);
                    break;
                }
            }
        }

        private void Touch()
            => Interlocked.Exchange(ref _lastSeenTicks, DateTime.UtcNow.Ticks);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            foreach (Socket socket in _dnsSockets)
                socket.Dispose();
            _pending.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            Dispose();
            if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0)
                return;

            try
            {
                await Task.WhenAll(_receiveLoops).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
            finally
            {
                _sendLock.Dispose();
            }
        }
    }
}
