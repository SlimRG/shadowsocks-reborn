using System.Net;
using System.Net.Sockets;

namespace Shadowsocks.NetworkService.Routing;

internal sealed class TransparentTcpRelay : IAsyncDisposable
{
    private readonly FlowRegistry _flows;
    private readonly string _proxyHost;
    private readonly int _proxyPort;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Socket _listener;
    private Task? _acceptLoop;

    public TransparentTcpRelay(FlowRegistry flows, string proxyHost, int proxyPort)
    {
        _flows = flows;
        _proxyHost = proxyHost;
        _proxyPort = proxyPort;
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

            using Socket upstream = await Socks5Connector.ConnectAsync(
                _proxyHost,
                _proxyPort,
                state.Key.RemoteAddress,
                state.Key.RemotePort,
                _shutdown.Token).ConfigureAwait(false);
            using NetworkStream clientStream = new(client, ownsSocket: false);
            using NetworkStream upstreamStream = new(upstream, ownsSocket: false);

            Task a = PumpAsync(clientStream, upstreamStream, _shutdown.Token);
            Task b = PumpAsync(upstreamStream, clientStream, _shutdown.Token);
            await Task.WhenAny(a, b).ConfigureAwait(false);
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
