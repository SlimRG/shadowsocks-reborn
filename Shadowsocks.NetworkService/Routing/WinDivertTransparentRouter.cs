using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using Shadowsocks.NetworkService.Ipc;
using Shadowsocks.NetworkService.WinDivert;

namespace Shadowsocks.NetworkService.Routing;

internal sealed class WinDivertTransparentRouter : IAsyncDisposable
{
    private const uint MaxPacket = 40 + 65535;
    private readonly FlowRegistry _flows;
    private readonly WinDivertFlowObserver _flowObserver = new();
    private readonly TransparentTcpRelay _tcpRelay;
    private readonly TransparentUdpRelay _udpRelay;
    private readonly TransparentDnsRelay? _dnsRelay;
    private readonly TransparentDohRelay? _dohRelay;
    private readonly DnsPolicyDto _dnsPolicy;
    private readonly IpFragmentTracker _fragments = new();
    private readonly CancellationTokenSource _shutdown = new();
    private nint _handle;
    private Task? _packetLoop;
    private Task? _completion;

    public WinDivertTransparentRouter(StartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        WinDivertNative.ConfigureDirectory(request.WinDivertDirectory);
        ApplicationPolicy policy = new(request);
        _flows = new FlowRegistry(policy);
        _tcpRelay = new TransparentTcpRelay(_flows, request);
        _udpRelay = new TransparentUdpRelay(_flows, request.LocalProxyHost, request.LocalProxyPort);
        _dnsPolicy = request.DnsPolicy ?? new DnsPolicyDto();

        if (_dnsPolicy.DnsCryptReady)
        {
            if (request.ExcludedProcessIds is null || !request.ExcludedProcessIds.Contains(_dnsPolicy.DnsCryptProcessId))
            {
                throw new InvalidOperationException(
                    "DNSCrypt process must be excluded from WinDivert capture before DNS interception is enabled.");
            }
            _dnsRelay = new TransparentDnsRelay(_flows, _dnsPolicy.DnsCryptPort);
        }
        else if (_dnsPolicy.DirectDnsReady
            && IPAddress.TryParse(_dnsPolicy.DirectDnsServer.Trim(), out IPAddress? directDnsAddress))
        {
            var directDnsEndpoints = new List<IPEndPoint>
            {
                new(directDnsAddress, 53),
            };
            if (IPAddress.TryParse(_dnsPolicy.DirectDnsFallbackServer?.Trim(), out IPAddress? fallbackDnsAddress)
                && !fallbackDnsAddress.Equals(directDnsAddress))
            {
                directDnsEndpoints.Add(new IPEndPoint(fallbackDnsAddress, 53));
            }
            _dnsRelay = _dnsPolicy.DirectDnsRouteThroughShadowsocks
                ? new TransparentDnsRelay(
                    _flows,
                    directDnsEndpoints,
                    proxyHost: request.LocalProxyHost,
                    proxyPort: request.LocalProxyPort)
                : new TransparentDnsRelay(_flows, directDnsEndpoints);
        }
        else if (_dnsPolicy.CustomDohReady)
        {
            _dohRelay = new TransparentDohRelay(
                _flows,
                _dnsPolicy.CustomDohUrl,
                _dnsPolicy.CustomDohRouteThroughShadowsocks,
                request.LocalProxyHost,
                request.LocalProxyPort);
        }
    }

    public int TcpRedirectPort => _tcpRelay.Port;
    public int UdpRedirectPort => _udpRelay.Port;
    public bool ManagedRoutingActive => _tcpRelay.ManagedRoutingActive;
    public int ManagedRoutingRuleCount => _tcpRelay.ManagedRoutingRuleCount;
    public bool DnsInterceptionActive => _dnsPolicy.Mode switch
    {
        DnsPolicyMode.Direct => _dnsRelay is not null || _dnsPolicy.DirectDnsRouteThroughShadowsocks,
        DnsPolicyMode.Proxy => true,
        DnsPolicyMode.CustomDoh => _dohRelay is not null,
        DnsPolicyMode.DnsCrypt => _dnsRelay is not null,
        _ => false,
    };
    public bool DnsFailClosedActive => _dnsPolicy.Mode switch
    {
        DnsPolicyMode.CustomDoh => _dohRelay is null && _dnsPolicy.FailClosed,
        DnsPolicyMode.DnsCrypt => _dnsRelay is null && _dnsPolicy.FailClosed,
        _ => false,
    };
    public Task Completion => _completion ?? _packetLoop ?? Task.CompletedTask;

    public void Start()
    {
        // Open the FLOW observer before the NETWORK handle so new connections can be
        // attributed to a PID before their packets reach the routing loop.
        _flowObserver.Start();
        _tcpRelay.Start();
        _udpRelay.Start();
        _dnsRelay?.Start();
        _dohRelay?.Start();

        // Normal loopback traffic is excluded so the local Shadowsocks and DNSCrypt
        // endpoints cannot recurse. In DNSCrypt mode we still capture explicit loopback
        // port-53 requests (for example a system DNS stub on 127.0.0.1). When redirecting,
        // the DNS relay source ports are admitted as well so their spoofed replies can be
        // reflected back with source port 53.
        bool interceptLoopbackDns = _dnsRelay is not null
            || _dnsPolicy.Mode is DnsPolicyMode.Proxy or DnsPolicyMode.CustomDoh or DnsPolicyMode.DnsCrypt
            || (_dnsPolicy.Mode == DnsPolicyMode.Direct && _dnsPolicy.DirectDnsRouteThroughShadowsocks);
        int dnsTcpRelayPort = _dnsRelay?.TcpPort ?? _dohRelay?.TcpPort ?? 0;
        int dnsUdpRelayPort = _dnsRelay?.UdpPort ?? _dohRelay?.UdpPort ?? 0;
        string filter = interceptLoopbackDns
            ? dnsTcpRelayPort == 0 || dnsUdpRelayPort == 0
                ? "outbound and (tcp or udp or fragment) and (!loopback or fragment or tcp.DstPort == 53 or udp.DstPort == 53)"
                : $"outbound and (tcp or udp or fragment) and (!loopback or fragment or tcp.DstPort == 53 or udp.DstPort == 53 " +
                  $"or tcp.SrcPort == {dnsTcpRelayPort} or udp.SrcPort == {dnsUdpRelayPort})"
            : "outbound and !loopback and (tcp or udp or fragment)";
        _handle = WinDivertNative.Open(
            filter,
            WinDivertLayer.Network,
            100,
            WinDivertFlags.None);
        if (WinDivertNative.IsInvalidHandle(_handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "WinDivertOpen failed.");
        }

        SetParamOrThrow(WinDivertParam.QueueLength, 8192);
        SetParamOrThrow(WinDivertParam.QueueSize, 16 * 1024 * 1024);
        SetParamOrThrow(WinDivertParam.QueueTime, 1000);
        _packetLoop = Task.Run(PacketLoop);
        _completion = Task.WhenAll(_packetLoop, _flowObserver.Completion);
    }

    private unsafe void PacketLoop()
    {
        byte[] packet = new byte[checked((int)MaxPacket)];
        while (!_shutdown.IsCancellationRequested)
        {
            fixed (byte* packetPtr = packet)
            {
                if (!WinDivertNative.Receive(_handle, packetPtr, MaxPacket, out uint length, out WinDivertAddress address))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (IsExpectedReceiveTermination(_shutdown.IsCancellationRequested, error))
                        return;

                    throw new Win32Exception(error, "WinDivertRecv failed while capture was active.");
                }

                DateTime nowUtc = DateTime.UtcNow;
                bool isFragment = IpFragmentTracker.TryParse(
                    new ReadOnlySpan<byte>(packetPtr, checked((int)length)),
                    out IpFragmentInfo fragment);

                if (isFragment && !fragment.IsFirstFragment)
                {
                    // The local Windows stack normally emits the first fragment first. If an
                    // orphan/out-of-order continuation arrives, fail secure instead of letting
                    // an unclassified fragment bypass DNS/application routing.
                    if (_fragments.TryGetDisposition(fragment, nowUtc, out FragmentDisposition disposition)
                        && disposition == FragmentDisposition.Direct)
                    {
                        Reinject(packetPtr, length, ref address);
                    }
                    continue;
                }

                if (!PacketView.TryParse(packetPtr, length, out PacketView view))
                {
                    if (isFragment)
                    {
                        // A first fragment without a complete TCP/UDP header cannot be safely
                        // classified or rewritten. Remember Drop for all remaining fragments.
                        _fragments.Record(fragment, FragmentDisposition.Drop, nowUtc);
                    }
                    else
                    {
                        Reinject(packetPtr, length, ref address);
                    }
                    continue;
                }

                ushort sourcePort = view.SourcePort;
                ushort destinationPort = view.DestinationPort;
                ushort proxyRedirectPort = view.IsTcp
                    ? checked((ushort)_tcpRelay.Port)
                    : checked((ushort)_udpRelay.Port);
                ushort dnsRedirectPort = view.IsTcp
                    ? checked((ushort)(_dnsRelay?.TcpPort ?? _dohRelay?.TcpPort ?? 0))
                    : checked((ushort)(_dnsRelay?.UdpPort ?? _dohRelay?.UdpPort ?? 0));

                if (isFragment)
                {
                    FragmentDisposition disposition = EvaluateFirstFragment(
                        view,
                        proxyRedirectPort,
                        dnsRedirectPort);
                    _fragments.Record(fragment, disposition, nowUtc);
                    if (disposition == FragmentDisposition.Direct)
                    {
                        Reinject(packetPtr, length, ref address);
                    }
                    continue;
                }

                // Response generated by one of the local transparent relays. Both the
                // Shadowsocks relay and the DNS bridge use the same reflection mapping.
                if (sourcePort == proxyRedirectPort || (dnsRedirectPort != 0 && sourcePort == dnsRedirectPort))
                {
                    if (_flows.TryGetReflected(
                            view.Protocol,
                            view.SourceAddress,
                            destinationPort,
                            view.DestinationAddress,
                            out FlowState? reflected)
                        && reflected is not null)
                    {
                        view.ReflectFromLocal(reflected.Key.RemotePort);
                        address.Outbound = false;
                        RecalculateChecksumsOrThrow(packetPtr, length, ref address);
                        Reinject(packetPtr, length, ref address);
                    }
                    // Unknown packets from a reserved transparent relay port are not
                    // allowed to escape to the network.
                    continue;
                }

                FlowKey key = new(
                    view.Protocol,
                    view.SourceAddress,
                    sourcePort,
                    view.DestinationAddress,
                    destinationPort);

                if (destinationPort == 53)
                {
                    FlowState dnsState = _flows.GetOrCreate(key);
                    DnsRouteDecision dnsDecision = DnsRoutingPolicy.Evaluate(
                        _dnsPolicy,
                        _flows.IsDnsInterceptionExempt(dnsState));
                    switch (dnsDecision)
                    {
                        case DnsRouteDecision.Direct:
                            Reinject(packetPtr, length, ref address);
                            break;
                        case DnsRouteDecision.Proxy:
                            _flows.MarkReflected(dnsState);
                            view.ReflectToLocal(proxyRedirectPort);
                            address.Outbound = false;
                            RecalculateChecksumsOrThrow(packetPtr, length, ref address);
                            Reinject(packetPtr, length, ref address);
                            break;
                        case DnsRouteDecision.Block:
                            // Fail-closed: deliberately drop plaintext DNS while DNSCrypt
                            // is unavailable. No silent downgrade to the system resolver.
                            break;
                        case DnsRouteDecision.Redirect:
                            if (dnsRedirectPort == 0)
                            {
                                // Defensive fallback. The policy should only return Redirect
                                // when the DNS relay exists, but never leak if state diverges.
                                if (!_dnsPolicy.FailClosed)
                                {
                                    Reinject(packetPtr, length, ref address);
                                }
                                break;
                            }
                            _flows.MarkReflected(dnsState);
                            view.ReflectToLocal(dnsRedirectPort);
                            address.Outbound = false;
                            RecalculateChecksumsOrThrow(packetPtr, length, ref address);
                            Reinject(packetPtr, length, ref address);
                            break;
                    }
                    continue;
                }

                // DHCP, mDNS, LLMNR and local/private network traffic remain direct.
                if (NetworkClassifier.IsInfrastructureTraffic(key))
                {
                    Reinject(packetPtr, length, ref address);
                    continue;
                }

                FlowState state = _flows.GetOrCreate(key);
                switch (state.Route)
                {
                    case RouteAction.Direct:
                    case RouteAction.Default:
                        Reinject(packetPtr, length, ref address);
                        break;
                    case RouteAction.Block:
                        break;
                    case RouteAction.Proxy:
                    case RouteAction.Deferred:
                        view.ReflectToLocal(proxyRedirectPort);
                        address.Outbound = false;
                        RecalculateChecksumsOrThrow(packetPtr, length, ref address);
                        Reinject(packetPtr, length, ref address);
                        break;
                }
            }
        }
    }

    private FragmentDisposition EvaluateFirstFragment(
        PacketView view,
        ushort proxyRedirectPort,
        ushort dnsRedirectPort)
    {
        ushort sourcePort = view.SourcePort;
        ushort destinationPort = view.DestinationPort;

        // Local relay responses require address/port rewriting too. Do not partially
        // rewrite a fragmented relay datagram.
        if (sourcePort == proxyRedirectPort || (dnsRedirectPort != 0 && sourcePort == dnsRedirectPort))
            return FragmentDisposition.Drop;

        FlowKey key = new(
            view.Protocol,
            view.SourceAddress,
            sourcePort,
            view.DestinationAddress,
            destinationPort);

        if (destinationPort == 53)
        {
            FlowState dnsState = _flows.GetOrCreate(key);
            DnsRouteDecision dnsDecision = DnsRoutingPolicy.Evaluate(
                _dnsPolicy,
                _flows.IsDnsInterceptionExempt(dnsState));
            return IpFragmentTracker.ForDnsDecision(dnsDecision);
        }

        if (NetworkClassifier.IsInfrastructureTraffic(key))
            return FragmentDisposition.Direct;

        FlowState state = _flows.GetOrCreate(key);
        return IpFragmentTracker.ForRoute(state.Route);
    }

    private void SetParamOrThrow(WinDivertParam parameter, ulong value)
    {
        if (!WinDivertNative.SetParam(_handle, parameter, value))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"WinDivertSetParam failed for {parameter}.");
        }
    }

    private unsafe static void RecalculateChecksumsOrThrow(
        byte* packet,
        uint length,
        ref WinDivertAddress address)
    {
        if (!WinDivertNative.CalculateChecksums(packet, length, ref address, 0))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "WinDivert failed to recalculate packet checksums.");
        }
    }

    private unsafe void Reinject(byte* packet, uint length, ref WinDivertAddress address)
    {
        bool succeeded = WinDivertNative.Send(_handle, packet, length, out uint sentLength, in address);
        int error = succeeded ? 0 : Marshal.GetLastPInvokeError();
        ValidateSendResult(succeeded, sentLength, length, error);
    }

    internal static bool IsExpectedReceiveTermination(bool shutdownRequested, int errorCode)
        => shutdownRequested && errorCode is 6 or 232 or 995;

    internal static void ValidateSendResult(bool succeeded, uint sentLength, uint expectedLength, int errorCode)
    {
        if (!succeeded)
            throw new Win32Exception(errorCode, "WinDivertSend failed while capture was active.");

        if (sentLength != expectedLength)
            throw new IOException($"WinDivertSend injected {sentLength} of {expectedLength} bytes.");
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (!WinDivertNative.IsInvalidHandle(_handle))
        {
            WinDivertNative.Shutdown(_handle, WinDivertShutdown.Both);
            WinDivertNative.Close(_handle);
            _handle = 0;
        }

        if (_packetLoop is not null)
        {
            try
            {
                await _packetLoop.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        await _flowObserver.DisposeAsync().ConfigureAwait(false);
        if (_dnsRelay is not null)
        {
            await _dnsRelay.DisposeAsync().ConfigureAwait(false);
        }
        if (_dohRelay is not null)
        {
            await _dohRelay.DisposeAsync().ConfigureAwait(false);
        }
        await _tcpRelay.DisposeAsync().ConfigureAwait(false);
        await _udpRelay.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
