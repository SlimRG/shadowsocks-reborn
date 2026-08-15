using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Shadowsocks.NetworkService.Routing;

internal sealed class TransparentUdpRelay : IAsyncDisposable
{
    private readonly FlowRegistry _flows;
    private readonly IPEndPoint _localSocksEndpoint;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Socket _listener;
    private readonly ConcurrentDictionary<FlowKey, UdpFlow> _udpFlows = new();
    private Task? _receiveLoop;

    public TransparentUdpRelay(FlowRegistry flows, string proxyHost, int proxyPort)
    {
        _flows = flows;
        IPAddress proxyAddress = IPAddress.TryParse(proxyHost, out IPAddress? parsed)
            ? parsed
            : Dns.GetHostAddresses(proxyHost).First(static ip => ip.AddressFamily == AddressFamily.InterNetwork);
        _localSocksEndpoint = new IPEndPoint(proxyAddress, proxyPort);

        _listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)
        {
            DualMode = true,
        };
        _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        Port = ((IPEndPoint)_listener.LocalEndPoint!).Port;
    }

    public int Port { get; }

    public void Start() => _receiveLoop = ReceiveLoopAsync();

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[65535];
        EndPoint peerTemplate = new IPEndPoint(IPAddress.IPv6Any, 0);
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                SocketReceiveFromResult result = await _listener.ReceiveFromAsync(buffer, SocketFlags.None, peerTemplate, _shutdown.Token).ConfigureAwait(false);
                if (result.RemoteEndPoint is not IPEndPoint peer)
                {
                    continue;
                }

                IPAddress remoteAddress = Normalize(peer.Address);
                if (!_flows.TryGetByPeer(17, remoteAddress, checked((ushort)peer.Port), out FlowState? state)
                    || state is null)
                {
                    continue;
                }

                UdpFlow flow = _udpFlows.GetOrAdd(state.Key, key => new UdpFlow(
                    key,
                    _listener,
                    _localSocksEndpoint,
                    _shutdown.Token));
                await flow.SendAsync(buffer.AsMemory(0, result.ReceivedBytes)).ConfigureAwait(false);
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
        }
    }

    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Dispose();
        foreach (UdpFlow flow in _udpFlows.Values)
        {
            flow.Dispose();
        }
        _udpFlows.Clear();
        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch
            {
            }
        }
        _shutdown.Dispose();
    }

    private sealed class UdpFlow : IDisposable
    {
        private readonly FlowKey _flow;
        private readonly Socket _transparentListener;
        private readonly IPEndPoint _localSocksEndpoint;
        private readonly CancellationToken _token;
        private readonly Socket _socksSocket;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly Task _receiveLoop;

        public UdpFlow(FlowKey flow, Socket transparentListener, IPEndPoint localSocksEndpoint, CancellationToken token)
        {
            _flow = flow;
            _transparentListener = transparentListener;
            _localSocksEndpoint = localSocksEndpoint;
            _token = token;
            _socksSocket = new Socket(localSocksEndpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            _socksSocket.Bind(new IPEndPoint(
                localSocksEndpoint.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback,
                0));
            _receiveLoop = ReceiveFromSocksAsync();
        }

        public async Task SendAsync(ReadOnlyMemory<byte> payload)
        {
            byte[] packet = Socks5Connector.BuildUdpDatagram(
                new IPEndPoint(_flow.RemoteAddress, _flow.RemotePort),
                payload.Span);
            await _sendLock.WaitAsync(_token).ConfigureAwait(false);
            try
            {
                await _socksSocket.SendToAsync(packet, SocketFlags.None, _localSocksEndpoint, _token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveFromSocksAsync()
        {
            byte[] buffer = new byte[65535];
            EndPoint sender = new IPEndPoint(_localSocksEndpoint.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
            while (!_token.IsCancellationRequested)
            {
                try
                {
                    SocketReceiveFromResult result = await _socksSocket.ReceiveFromAsync(buffer, SocketFlags.None, sender, _token).ConfigureAwait(false);
                    if (!Socks5Connector.TryParseUdpDatagram(
                            buffer.AsSpan(0, result.ReceivedBytes),
                            out IPEndPoint? source,
                            out byte[] payload)
                        || source is null)
                    {
                        continue;
                    }

                    // Deliberately send toward the spoofed remote endpoint. WinDivert
                    // catches this outbound datagram and reflects it back into the local
                    // stack with source address/port restored to the remote peer.
                    IPEndPoint fakePeer = new(source.Address, _flow.LocalPort);
                    await _transparentListener.SendToAsync(payload, SocketFlags.None, fakePeer, _token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException) when (_token.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        public void Dispose()
        {
            _socksSocket.Dispose();
            _sendLock.Dispose();
            _ = _receiveLoop;
        }
    }
}
