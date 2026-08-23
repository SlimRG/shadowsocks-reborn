using System.Net;
using System.Net.Sockets;
using Shadowsocks.NetworkService.Ipc;
using Shadowsocks.NetworkService.ManagedRouting;

namespace Shadowsocks.NetworkService.Routing;

internal sealed class TransparentTcpRelay : IAsyncDisposable
{
    private const int MaxInspectionBytes = 64 * 1024;
    private static readonly TimeSpan InspectionTimeout = TimeSpan.FromMilliseconds(500);

    private readonly FlowRegistry _flows;
    private readonly string _proxyHost;
    private readonly int _proxyPort;
    private readonly FilterEngine? _managedEngine;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Socket _listener;
    private Task? _acceptLoop;

    public TransparentTcpRelay(FlowRegistry flows, StartRequest request)
    {
        ArgumentNullException.ThrowIfNull(flows);
        ArgumentNullException.ThrowIfNull(request);

        _flows = flows;
        _proxyHost = request.LocalProxyHost;
        _proxyPort = request.LocalProxyPort;
        ManagedRoutingDto managed = request.ManagedRouting ?? new ManagedRoutingDto();
        if (managed.Enabled)
        {
            _managedEngine = FilterEngine.Compile(
                managed.DefaultRules ?? [],
                managed.UserRules ?? [],
                out FilterCompilationReport report);
            ManagedRoutingRuleCount = report.DefaultRuleCount + report.UserRuleCount;
        }

        _listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
        {
            DualMode = true,
            NoDelay = true,
        };
        _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        _listener.Listen(1024);
        Port = ((IPEndPoint)_listener.LocalEndPoint!).Port;
    }

    public int Port { get; }
    public bool ManagedRoutingActive => _managedEngine is not null;
    public int ManagedRoutingRuleCount { get; private set; }

    public void Start() => _acceptLoop = AcceptLoopAsync();

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = HandleConnectionAsync(client);
        }
    }

    private async Task HandleConnectionAsync(Socket client)
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
                || state is null)
            {
                return;
            }

            RouteAction route = state.Route;
            byte[] bufferedPrefix = [];
            if (route == RouteAction.Deferred)
            {
                (route, bufferedPrefix) = await ResolveDeferredRouteAsync(client, state).ConfigureAwait(false);
            }

            if (route == RouteAction.Block)
            {
                return;
            }

            using Socket upstream = route == RouteAction.Proxy
                ? await Socks5Connector.ConnectAsync(
                    _proxyHost,
                    _proxyPort,
                    state.Key.RemoteAddress,
                    state.Key.RemotePort,
                    _shutdown.Token).ConfigureAwait(false)
                : await ConnectDirectAsync(
                    state.Key.RemoteAddress,
                    state.Key.RemotePort,
                    _shutdown.Token).ConfigureAwait(false);

            using NetworkStream clientStream = new(client, ownsSocket: false);
            using NetworkStream upstreamStream = new(upstream, ownsSocket: false);
            if (bufferedPrefix.Length > 0)
            {
                await upstreamStream.WriteAsync(bufferedPrefix, _shutdown.Token).ConfigureAwait(false);
            }

            Task a = PumpAsync(clientStream, upstreamStream, _shutdown.Token);
            Task b = PumpAsync(upstreamStream, clientStream, _shutdown.Token);
            await Task.WhenAny(a, b).ConfigureAwait(false);
        }
    }

    private async Task<(RouteAction Route, byte[] Prefix)> ResolveDeferredRouteAsync(Socket client, FlowState state)
    {
        if (_managedEngine is null)
        {
            return (NormalizeFallback(state.FallbackRoute), []);
        }

        using MemoryStream prefix = new();
        byte[] buffer = new byte[4096];
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(InspectionTimeout);

        while (prefix.Length < MaxInspectionBytes)
        {
            byte[] current = prefix.ToArray();
            TcpDestinationInspectionStatus status = InitialTcpProtocolInspector.Inspect(
                current,
                state.Key.RemotePort,
                out TcpDestinationIdentity identity);
            if (status == TcpDestinationInspectionStatus.Identified)
            {
                FilterRoutingDecision decision = _managedEngine.Evaluate(identity.Url, identity.Host);
                return (
                    decision.Action == FilterRoutingAction.Proxy ? RouteAction.Proxy : RouteAction.Direct,
                    prefix.ToArray());
            }

            if (status == TcpDestinationInspectionStatus.Unknown)
            {
                return (ResolveManagedFallback(state), prefix.ToArray());
            }

            int remaining = checked(MaxInspectionBytes - (int)prefix.Length);
            int read;
            try
            {
                read = await client.ReceiveAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                    SocketFlags.None,
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
            {
                return (ResolveManagedFallback(state), prefix.ToArray());
            }

            if (read == 0)
            {
                return (ResolveManagedFallback(state), prefix.ToArray());
            }
            prefix.Write(buffer, 0, read);
        }

        return (ResolveManagedFallback(state), prefix.ToArray());
    }

    private RouteAction ResolveManagedFallback(FlowState state)
    {
        if (_managedEngine is null)
        {
            return NormalizeFallback(state.FallbackRoute);
        }

        string host = state.Key.RemoteAddress.ToString();
        string authority = state.Key.RemoteAddress.AddressFamily == AddressFamily.InterNetworkV6
            ? "[" + host + "]"
            : host;
        string scheme = state.Key.RemotePort == 80 ? "http" : "https";
        int defaultPort = scheme == "http" ? 80 : 443;
        if (state.Key.RemotePort != defaultPort)
        {
            authority += ":" + state.Key.RemotePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        FilterRoutingDecision decision = _managedEngine.Evaluate(scheme + "://" + authority + "/", host);
        return decision.Action == FilterRoutingAction.Proxy ? RouteAction.Proxy : RouteAction.Direct;
    }

    private static RouteAction NormalizeFallback(RouteAction route)
        => route switch
        {
            RouteAction.Proxy => RouteAction.Proxy,
            RouteAction.Block => RouteAction.Block,
            _ => RouteAction.Direct,
        };

    private static async Task<Socket> ConnectDirectAsync(
        IPAddress remoteAddress,
        ushort remotePort,
        CancellationToken cancellationToken)
    {
        Socket socket = new(remoteAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(remoteAddress, remotePort), cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task PumpAsync(Stream source, Stream destination, CancellationToken token)
    {
        byte[] buffer = new byte[64 * 1024];
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
    }

    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Dispose();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
            }
        }
        _shutdown.Dispose();
    }
}
