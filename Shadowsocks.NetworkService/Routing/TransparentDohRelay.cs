#nullable enable
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Shadowsocks.NetworkService.Routing;

/// <summary>
/// Transparent DNS-over-HTTPS bridge used by Admin Mode. WinDivert reflects
/// application UDP/TCP DNS requests into local listeners; each DNS wire message
/// is sent to the configured HTTPS endpoint and reflected back to the original
/// application flow.
/// </summary>
internal sealed class TransparentDohRelay : IAsyncDisposable
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    private readonly FlowRegistry _flows;
    private readonly Uri _endpoint;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Socket _udpListener;
    private readonly Socket _tcpListener;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<long, Task> _operations = new();
    private Task? _udpLoop;
    private Task? _tcpLoop;
    private long _nextOperationId;
    private int _started;
    private int _disposed;

    public TransparentDohRelay(
        FlowRegistry flows,
        string endpoint,
        bool routeThroughShadowsocks = false,
        string localProxyHost = "127.0.0.1",
        int localProxyPort = 1080)
    {
        _flows = flows ?? throw new ArgumentNullException(nameof(flows));
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Custom DoH endpoint must be an absolute HTTPS URL.", nameof(endpoint));
        }
        _endpoint = uri;

        if (routeThroughShadowsocks && (localProxyPort is < 1 or > 65535))
            throw new ArgumentOutOfRangeException(nameof(localProxyPort));
        string normalizedProxyHost = string.IsNullOrWhiteSpace(localProxyHost)
            ? "127.0.0.1"
            : localProxyHost.Trim().TrimStart('[').TrimEnd(']');
        string proxyHostForUri = normalizedProxyHost.Contains(':', StringComparison.Ordinal)
            ? $"[{normalizedProxyHost}]"
            : normalizedProxyHost;

        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = routeThroughShadowsocks,
            Proxy = routeThroughShadowsocks
                ? new WebProxy($"socks5://{proxyHostForUri}:{localProxyPort}")
                : null,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));

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

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("Transparent DoH relay has already been started.");

        _udpLoop = UdpReceiveLoopAsync();
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
                    continue;

                IPAddress remoteAddress = Normalize(peer.Address);
                if (!_flows.TryGetByPeer(17, remoteAddress, checked((ushort)peer.Port), out FlowState? state)
                    || state is null
                    || state.Key.RemotePort != 53)
                {
                    continue;
                }

                byte[] query = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
                TrackOperation(HandleUdpQueryAsync(state, query));
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
                try { await Task.Delay(25, _shutdown.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleUdpQueryAsync(FlowState state, byte[] query)
    {
        try
        {
            byte[] response = await QueryDohAsync(query, _shutdown.Token).ConfigureAwait(false);
            IPEndPoint fakePeer = new(state.Key.RemoteAddress, state.Key.LocalPort);
            await _udpListener
                .SendToAsync(response, SocketFlags.None, fakePeer, _shutdown.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (HttpRequestException)
        {
            // Per-query upstream failure: fail closed by not emitting a plaintext reply.
        }
        catch (SocketException)
        {
        }
        catch (InvalidDataException)
        {
        }
    }

    private async Task TcpAcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                Socket client = await _tcpListener.AcceptAsync(_shutdown.Token).ConfigureAwait(false);
                TrackOperation(HandleTcpConnectionAsync(client));
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
            }
        }
    }

    private async Task HandleTcpConnectionAsync(Socket client)
    {
        using (client)
        {
            client.NoDelay = true;
            if (client.RemoteEndPoint is not IPEndPoint peer || client.LocalEndPoint is not IPEndPoint local)
                return;

            IPAddress remoteAddress = Normalize(peer.Address);
            IPAddress localAddress = Normalize(local.Address);
            if (!_flows.TryGetReflected(6, localAddress, checked((ushort)peer.Port), remoteAddress, out FlowState? state)
                || state is null
                || state.Key.RemotePort != 53)
            {
                return;
            }

            using var stream = new NetworkStream(client, ownsSocket: false);
            byte[] lengthPrefix = new byte[2];
            while (!_shutdown.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, lengthPrefix, _shutdown.Token).ConfigureAwait(false))
                    return;

                int length = BinaryPrimitives.ReadUInt16BigEndian(lengthPrefix);
                if (length is < 12 or > 65535)
                    return;

                byte[] query = new byte[length];
                if (!await ReadExactAsync(stream, query, _shutdown.Token).ConfigureAwait(false))
                    return;

                byte[] response;
                try
                {
                    response = await QueryDohAsync(query, _shutdown.Token).ConfigureAwait(false);
                }
                catch (HttpRequestException)
                {
                    return;
                }
                catch (InvalidDataException)
                {
                    return;
                }

                if (response.Length > ushort.MaxValue)
                    return;
                BinaryPrimitives.WriteUInt16BigEndian(lengthPrefix, checked((ushort)response.Length));
                await stream.WriteAsync(lengthPrefix, _shutdown.Token).ConfigureAwait(false);
                await stream.WriteAsync(response, _shutdown.Token).ConfigureAwait(false);
                await stream.FlushAsync(_shutdown.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task<byte[]> QueryDohAsync(byte[] query, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(QueryTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new ByteArrayContent(query),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "application/dns-message", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"DoH endpoint returned unsupported content type '{mediaType ?? "<missing>"}'.");

        byte[] payload = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
        if (payload.Length is < 12 or > 65535)
            throw new InvalidDataException("DoH endpoint returned an invalid DNS wire message length.");
        return payload;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer[offset..], token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return false;
            }
            catch (SocketException)
            {
                return false;
            }
            if (read == 0)
                return false;
            offset += read;
        }
        return true;
    }

    private void TrackOperation(Task task)
    {
        long id = Interlocked.Increment(ref _nextOperationId);
        _operations[id] = task;
        _ = task.ContinueWith(
            completed =>
            {
                _operations.TryRemove(id, out _);
                _ = completed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdown.Cancel();
        _udpListener.Dispose();
        _tcpListener.Dispose();

        await AwaitIgnoringShutdownAsync(_udpLoop).ConfigureAwait(false);
        await AwaitIgnoringShutdownAsync(_tcpLoop).ConfigureAwait(false);

        Task[] operations = _operations.Values.ToArray();
        if (operations.Length > 0)
        {
            try { await Task.WhenAll(operations).ConfigureAwait(false); }
            catch { }
        }
        _operations.Clear();
        _httpClient.Dispose();
        _shutdown.Dispose();
    }

    private static async Task AwaitIgnoringShutdownAsync(Task? task)
    {
        if (task is null)
            return;
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }
}
