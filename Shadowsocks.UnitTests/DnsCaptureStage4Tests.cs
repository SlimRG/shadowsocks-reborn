using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Shadowsocks.Controller.Traffic;
using Shadowsocks.Model;
using Shadowsocks.NetworkService;
using Shadowsocks.NetworkService.Ipc;
using Shadowsocks.NetworkService.Routing;

namespace Shadowsocks.UnitTests;

[TestClass]
public class DnsCaptureStage4Tests
{
    [TestMethod]
    public void DnsCaptureRuntimeState_ReadyOnlyWithPortAndPid()
    {
        Assert.IsTrue(new DnsCaptureRuntimeState(5300, 42).IsReady);
        Assert.IsFalse(new DnsCaptureRuntimeState(0, 42).IsReady);
        Assert.IsFalse(new DnsCaptureRuntimeState(5300, 0).IsReady);
    }

    [TestMethod]
    public void DnsRoutingPolicy_SystemDns_IsDirect()
    {
        DnsPolicyDto policy = new() { Mode = Shadowsocks.NetworkService.Ipc.DnsPolicyMode.System };
        Assert.AreEqual(DnsRouteDecision.Direct, DnsRoutingPolicy.Evaluate(policy, dnsInterceptionExempt: false));
    }

    [TestMethod]
    public void DnsRoutingPolicy_DnsCryptExemptProcess_IsDirect()
    {
        DnsPolicyDto policy = ReadyDnsCryptPolicy();
        Assert.AreEqual(DnsRouteDecision.Direct, DnsRoutingPolicy.Evaluate(policy, dnsInterceptionExempt: true));
    }

    [TestMethod]
    public void DnsRoutingPolicy_ReadyDnsCrypt_Redirects()
    {
        DnsPolicyDto policy = ReadyDnsCryptPolicy();
        Assert.AreEqual(DnsRouteDecision.Redirect, DnsRoutingPolicy.Evaluate(policy, dnsInterceptionExempt: false));
    }

    [TestMethod]
    public void DnsRoutingPolicy_UnavailableFailClosed_Blocks()
    {
        DnsPolicyDto policy = new()
        {
            Mode = Shadowsocks.NetworkService.Ipc.DnsPolicyMode.DnsCrypt,
            FailClosed = true,
        };
        Assert.AreEqual(DnsRouteDecision.Block, DnsRoutingPolicy.Evaluate(policy, dnsInterceptionExempt: false));
    }

    [TestMethod]
    public void DnsRoutingPolicy_DnsCryptNeverFallsBackToPlaintext()
    {
        DnsPolicyDto policy = new()
        {
            Mode = Shadowsocks.NetworkService.Ipc.DnsPolicyMode.DnsCrypt,
            FailClosed = false, // Legacy/malformed IPC must not weaken DNSCrypt.
        };
        Assert.AreEqual(DnsRouteDecision.Block, DnsRoutingPolicy.Evaluate(policy, dnsInterceptionExempt: false));
    }

    [TestMethod]
    public void NetworkClassifier_DhcpMdnsAndLlmnrRemainInfrastructureTraffic()
    {
        foreach (ushort port in new ushort[] { 67, 68, 5353, 5355 })
        {
            FlowKey flow = new(17, IPAddress.Parse("192.0.2.10"), 50000, IPAddress.Parse("224.0.0.251"), port);
            Assert.IsTrue(NetworkClassifier.IsInfrastructureTraffic(flow), $"UDP/{port} must remain direct infrastructure traffic.");
        }
    }


    [TestMethod]
    public void DnsRoutingPolicy_DirectAndProxyModesAreActionable()
    {
        Assert.AreEqual(
            DnsRouteDecision.Direct,
            DnsRoutingPolicy.Evaluate(new DnsPolicyDto { Mode = Shadowsocks.NetworkService.Ipc.DnsPolicyMode.Direct }, false));
        Assert.AreEqual(
            DnsRouteDecision.Proxy,
            DnsRoutingPolicy.Evaluate(new DnsPolicyDto { Mode = Shadowsocks.NetworkService.Ipc.DnsPolicyMode.Proxy }, false));
    }

    [TestMethod]
    public void DnsRoutingPolicy_DirectCustomServerRedirects()
    {
        Assert.AreEqual(
            DnsRouteDecision.Redirect,
            DnsRoutingPolicy.Evaluate(new DnsPolicyDto
            {
                Mode = Shadowsocks.NetworkService.Ipc.DnsPolicyMode.Direct,
                DirectDnsServer = "1.1.1.1",
            }, false));
        Assert.AreEqual(
            DnsRouteDecision.Direct,
            DnsRoutingPolicy.Evaluate(new DnsPolicyDto
            {
                Mode = Shadowsocks.NetworkService.Ipc.DnsPolicyMode.Direct,
                DirectDnsServer = "not-an-ip",
            }, false));
    }

    [TestMethod]
    public void DnsRoutingPolicy_DirectDnsCanRouteOriginalDestinationThroughShadowsocks()
    {
        Assert.AreEqual(
            DnsRouteDecision.Proxy,
            DnsRoutingPolicy.Evaluate(new DnsPolicyDto
            {
                Mode = Shadowsocks.NetworkService.Ipc.DnsPolicyMode.Direct,
                DirectDnsRouteThroughShadowsocks = true,
            }, false));
    }

    [TestMethod]
    public void TransparentDnsRelay_UsesWildcardBindForRemoteDnsServer()
    {
        Assert.AreEqual(IPAddress.Any, TransparentDnsRelay.SelectUdpBindAddress(IPAddress.Parse("1.1.1.1")));
        Assert.AreEqual(IPAddress.IPv6Any, TransparentDnsRelay.SelectUdpBindAddress(IPAddress.Parse("2606:4700:4700::1111")));
        Assert.AreEqual(IPAddress.Loopback, TransparentDnsRelay.SelectUdpBindAddress(IPAddress.Loopback));
        Assert.AreEqual(IPAddress.IPv6Loopback, TransparentDnsRelay.SelectUdpBindAddress(IPAddress.IPv6Loopback));
    }

    [TestMethod]
    public void DnsRoutingPolicy_CustomDohRequiresValidHttpsEndpoint()
    {
        Assert.AreEqual(
            DnsRouteDecision.Redirect,
            DnsRoutingPolicy.Evaluate(new DnsPolicyDto
            {
                Mode = Shadowsocks.NetworkService.Ipc.DnsPolicyMode.CustomDoh,
                CustomDohUrl = "https://dns.example/dns-query",
                FailClosed = true,
            }, false));
        Assert.AreEqual(
            DnsRouteDecision.Block,
            DnsRoutingPolicy.Evaluate(new DnsPolicyDto
            {
                Mode = Shadowsocks.NetworkService.Ipc.DnsPolicyMode.CustomDoh,
                CustomDohUrl = "http://dns.example/dns-query",
                FailClosed = true,
            }, false));
    }

    [TestMethod]
    public void NetworkClassifier_Dns53_IsHandledByDnsPolicyInsteadOfInfrastructureBypass()
    {
        FlowKey flow = new(17, IPAddress.Parse("192.0.2.10"), 50000, IPAddress.Parse("9.9.9.9"), 53);
        Assert.IsFalse(NetworkClassifier.IsInfrastructureTraffic(flow));
    }

    [TestMethod]
    public void AdminCaptureRequest_ContainsRuntimeDnsPolicyAndExclusion()
    {
        Configuration configuration = new()
        {
            dnsPolicy = new DnsPolicyConfig
            {
                mode = Shadowsocks.Controller.Traffic.DnsPolicyMode.DnsCrypt,
                directDnsServer = "9.9.9.9",
                directDnsFallbackServer = "149.112.112.112",
                directDnsRouteThroughShadowsocks = true,
                customDohUrl = "https://dns.example/dns-query",
                customDohRouteThroughShadowsocks = true,
                dnsCrypt = new DnsCryptConfig { failClosed = true },
            },
        };

        string json = AdminCaptureManager.BuildStartRequest(
            configuration,
            @"C:\\WinDivert",
            new[] { 11, 22 },
            new DnsCaptureRuntimeState(38471, 12345));
        JObject request = JObject.Parse(json);

        Assert.AreEqual(4, request["dnsPolicy"]!["mode"]!.Value<int>());
        Assert.AreEqual("9.9.9.9", request["dnsPolicy"]!["directDnsServer"]!.Value<string>());
        Assert.AreEqual("149.112.112.112", request["dnsPolicy"]!["directDnsFallbackServer"]!.Value<string>());
        Assert.IsTrue(request["dnsPolicy"]!["directDnsRouteThroughShadowsocks"]!.Value<bool>());
        Assert.AreEqual("https://dns.example/dns-query", request["dnsPolicy"]!["customDohUrl"]!.Value<string>());
        Assert.IsTrue(request["dnsPolicy"]!["customDohRouteThroughShadowsocks"]!.Value<bool>());
        Assert.AreEqual(38471, request["dnsPolicy"]!["dnsCryptPort"]!.Value<int>());
        Assert.AreEqual(12345, request["dnsPolicy"]!["dnsCryptProcessId"]!.Value<int>());
        Assert.IsTrue(request["dnsPolicy"]!["failClosed"]!.Value<bool>());
        StringAssert.Contains(request["dnsPolicy"]!["dnsCryptComponentRoot"]!.Value<string>()!, "DNSCryptProxy");
        CollectionAssert.Contains(request["excludedProcessIds"]!.Values<int>().ToList(), 12345);

        StartRequest parsed = JsonSerializer.Deserialize(json, NetworkServiceJsonContext.Default.StartRequest);
        Assert.IsNotNull(parsed);
        Assert.AreEqual("9.9.9.9", parsed.DnsPolicy.DirectDnsServer);
        Assert.AreEqual("149.112.112.112", parsed.DnsPolicy.DirectDnsFallbackServer);
        Assert.IsTrue(parsed.DnsPolicy.DirectDnsRouteThroughShadowsocks);
        Assert.AreEqual("https://dns.example/dns-query", parsed.DnsPolicy.CustomDohUrl);
        Assert.IsTrue(parsed.DnsPolicy.CustomDohRouteThroughShadowsocks);
        Assert.AreEqual(38471, parsed.DnsPolicy.DnsCryptPort);
        Assert.AreEqual(12345, parsed.DnsPolicy.DnsCryptProcessId);
        StringAssert.Contains(parsed.DnsPolicy.DnsCryptComponentRoot, "DNSCryptProxy");
        Assert.IsTrue(parsed.DnsPolicy.FailClosed);
    }

    [TestMethod]
    public void AdminCaptureRequest_DnsCryptForcesFailClosedEvenForLegacyFalseSetting()
    {
        Configuration configuration = new()
        {
            dnsPolicy = new DnsPolicyConfig
            {
                mode = Shadowsocks.Controller.Traffic.DnsPolicyMode.DnsCrypt,
                dnsCrypt = new DnsCryptConfig { failClosed = false },
            },
        };

        JObject request = JObject.Parse(AdminCaptureManager.BuildStartRequest(
            configuration,
            @"C:\\WinDivert",
            System.Array.Empty<int>(),
            DnsCaptureRuntimeState.Unavailable));

        Assert.IsTrue(request["dnsPolicy"]!["failClosed"]!.Value<bool>());
    }

    [TestMethod]
    public void AdminCaptureRequest_UnavailableRuntimeSendsZeroEndpointForFailClosed()
    {
        Configuration configuration = new()
        {
            dnsPolicy = new DnsPolicyConfig
            {
                mode = Shadowsocks.Controller.Traffic.DnsPolicyMode.DnsCrypt,
                dnsCrypt = new DnsCryptConfig { failClosed = true },
            },
        };

        JObject request = JObject.Parse(AdminCaptureManager.BuildStartRequest(
            configuration,
            @"C:\\WinDivert",
            System.Array.Empty<int>(),
            DnsCaptureRuntimeState.Unavailable));

        Assert.AreEqual(0, request["dnsPolicy"]!["dnsCryptPort"]!.Value<int>());
        Assert.AreEqual(0, request["dnsPolicy"]!["dnsCryptProcessId"]!.Value<int>());
        Assert.IsTrue(request["dnsPolicy"]!["failClosed"]!.Value<bool>());
    }

    [TestMethod]
    public void ApplicationPolicy_ManagedDnsCryptExecutable_IsAlwaysDirect()
    {
        string root = Path.Combine(Path.GetTempPath(), "Shadowsocks", "Components", "DNSCryptProxy");
        StartRequest request = new()
        {
            MainProcessId = 1,
            DefaultRoute = RouteAction.Proxy,
            DnsPolicy = new DnsPolicyDto { DnsCryptComponentRoot = root },
        };
        ApplicationPolicy policy = new(request);
        string executable = Path.Combine(root, ".prepared-test", "dnscrypt-proxy.exe");

        Assert.IsTrue(policy.IsExcludedProcess(4321, executable));
        Assert.IsTrue(policy.IsDnsInterceptionExempt(4321, executable));
        Assert.AreEqual(RouteAction.Direct, policy.Evaluate(6, 443, 4321, executable, "dnscrypt-proxy"));
    }

    [TestMethod]
    public void ApplicationPolicy_DnsCryptNamedExecutableOutsideManagedRoot_IsNotExcluded()
    {
        string root = Path.Combine(Path.GetTempPath(), "Shadowsocks", "Components", "DNSCryptProxy");
        StartRequest request = new()
        {
            MainProcessId = 1,
            DefaultRoute = RouteAction.Proxy,
            DnsPolicy = new DnsPolicyDto { DnsCryptComponentRoot = root },
        };
        ApplicationPolicy policy = new(request);
        string executable = Path.Combine(Path.GetTempPath(), "Other", "dnscrypt-proxy.exe");

        Assert.IsFalse(policy.IsExcludedProcess(4321, executable));
        Assert.IsFalse(policy.IsDnsInterceptionExempt(4321, executable));
        Assert.AreEqual(RouteAction.Proxy, policy.Evaluate(6, 443, 4321, executable, "dnscrypt-proxy"));
    }

    [TestMethod]
    public void ApplicationPolicy_DnsCryptInterceptsMainAndPluginDnsButExemptsDnsCryptItself()
    {
        StartRequest request = new()
        {
            MainProcessId = 100,
            ExcludedProcessIds = [200, 300],
            DefaultRoute = RouteAction.Proxy,
            DnsPolicy = new DnsPolicyDto
            {
                Mode = Shadowsocks.NetworkService.Ipc.DnsPolicyMode.DnsCrypt,
                DnsCryptProcessId = 300,
            },
        };
        ApplicationPolicy policy = new(request);

        // They still bypass generic application capture to prevent transport recursion.
        Assert.IsTrue(policy.IsExcludedProcess(100, @"C:\Shadowsocks\Shadowsocks.exe"));
        Assert.IsTrue(policy.IsExcludedProcess(200, @"C:\Shadowsocks\Plugins\v2ray-plugin.exe"));

        // But UDP/TCP 53 from them must go through DNSCrypt; otherwise they can leak.
        Assert.IsFalse(policy.IsDnsInterceptionExempt(100, @"C:\Shadowsocks\Shadowsocks.exe"));
        Assert.IsFalse(policy.IsDnsInterceptionExempt(200, @"C:\Shadowsocks\Plugins\v2ray-plugin.exe"));
        Assert.IsTrue(policy.IsDnsInterceptionExempt(300, null));
    }

    [TestMethod]
    public void DnsRoutingPolicy_FailClosedStillBlocksGenericTrafficExcludedProcess()
    {
        DnsPolicyDto policy = new()
        {
            Mode = Shadowsocks.NetworkService.Ipc.DnsPolicyMode.DnsCrypt,
            FailClosed = true,
        };

        // A Shadowsocks/plugin traffic exclusion is intentionally NOT passed as a DNS exemption.
        Assert.AreEqual(DnsRouteDecision.Block, DnsRoutingPolicy.Evaluate(policy, dnsInterceptionExempt: false));
    }

    [TestMethod]
    public void ServiceResponse_CaptureHealthRoundTripsThroughSourceGeneratedJson()
    {
        ServiceResponse expected = new()
        {
            Success = true,
            CaptureActive = true,
            TcpRedirectPort = 31001,
            UdpRedirectPort = 31002,
            DnsInterceptionActive = true,
        };

        string json = JsonSerializer.Serialize(expected, NetworkServiceJsonContext.Default.ServiceResponse);
        ServiceResponse actual = JsonSerializer.Deserialize(json, NetworkServiceJsonContext.Default.ServiceResponse);

        Assert.IsNotNull(actual);
        Assert.IsTrue(actual.CaptureActive);
        Assert.AreEqual(31001, actual.TcpRedirectPort);
        Assert.AreEqual(31002, actual.UdpRedirectPort);
        Assert.IsTrue(actual.DnsInterceptionActive);
    }

    [TestMethod]
    public async Task TransparentDnsRelay_Udp_ForwardsPayloadToDnsCryptLoopback()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        using Socket dnsCrypt = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        dnsCrypt.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int dnsCryptPort = ((IPEndPoint)dnsCrypt.LocalEndPoint!).Port;

        Task echoTask = Task.Run(async () =>
        {
            byte[] echoBuffer = new byte[512];
            EndPoint peer = new IPEndPoint(IPAddress.Any, 0);
            SocketReceiveFromResult received = await dnsCrypt.ReceiveFromAsync(
                echoBuffer, SocketFlags.None, peer, timeout.Token).ConfigureAwait(false);
            await dnsCrypt.SendToAsync(
                echoBuffer.AsMemory(0, received.ReceivedBytes),
                SocketFlags.None,
                received.RemoteEndPoint,
                timeout.Token).ConfigureAwait(false);
        }, timeout.Token);

        FlowRegistry flows = CreateDnsRelayFlowRegistry();
        await using TransparentDnsRelay relay = new(flows, dnsCryptPort);
        relay.Start();

        using Socket application = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        application.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        ushort applicationPort = checked((ushort)((IPEndPoint)application.LocalEndPoint!).Port);
        FlowKey key = new(17, IPAddress.Loopback, applicationPort, IPAddress.Loopback, 53);
        flows.MarkReflected(new FlowState(key, 77, null, "test", RouteAction.Direct, RouteAction.Direct, DateTime.UtcNow));

        byte[] payload = [0x12, 0x34, 0x01, 0x00, 0x00, 0x01];
        await application.SendToAsync(
            payload,
            SocketFlags.None,
            new IPEndPoint(IPAddress.Loopback, relay.UdpPort),
            timeout.Token).ConfigureAwait(false);

        byte[] response = new byte[512];
        int responseLength = await application.ReceiveAsync(response, SocketFlags.None, timeout.Token).ConfigureAwait(false);
        CollectionAssert.AreEqual(payload, response.AsSpan(0, responseLength).ToArray());
        await echoTask.ConfigureAwait(false);
    }

    [TestMethod]
    public async Task TransparentDnsRelay_Udp_IdleFlowsAreRemovedAndDisposed()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        using Socket dnsCrypt = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        dnsCrypt.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int dnsCryptPort = ((IPEndPoint)dnsCrypt.LocalEndPoint!).Port;

        Task echoTask = Task.Run(async () =>
        {
            byte[] echoBuffer = new byte[512];
            EndPoint peer = new IPEndPoint(IPAddress.Any, 0);
            SocketReceiveFromResult received = await dnsCrypt.ReceiveFromAsync(
                echoBuffer, SocketFlags.None, peer, timeout.Token).ConfigureAwait(false);
            await dnsCrypt.SendToAsync(
                echoBuffer.AsMemory(0, received.ReceivedBytes),
                SocketFlags.None,
                received.RemoteEndPoint,
                timeout.Token).ConfigureAwait(false);
        }, timeout.Token);

        FlowRegistry flows = CreateDnsRelayFlowRegistry();
        await using TransparentDnsRelay relay = new(
            flows,
            dnsCryptPort,
            udpIdleTimeout: TimeSpan.FromMilliseconds(500),
            udpSweepInterval: TimeSpan.FromMilliseconds(50));
        relay.Start();

        using Socket application = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        application.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        ushort applicationPort = checked((ushort)((IPEndPoint)application.LocalEndPoint!).Port);
        FlowKey key = new(17, IPAddress.Loopback, applicationPort, IPAddress.Loopback, 53);
        flows.MarkReflected(new FlowState(key, 77, null, "test", RouteAction.Direct, RouteAction.Direct, DateTime.UtcNow));

        byte[] payload = [0x12, 0x34, 0x01, 0x00, 0x00, 0x01];
        await application.SendToAsync(
            payload,
            SocketFlags.None,
            new IPEndPoint(IPAddress.Loopback, relay.UdpPort),
            timeout.Token).ConfigureAwait(false);
        byte[] response = new byte[512];
        _ = await application.ReceiveAsync(response, SocketFlags.None, timeout.Token).ConfigureAwait(false);
        await echoTask.ConfigureAwait(false);
        Assert.AreEqual(1, relay.UdpFlowCount);

        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (relay.UdpFlowCount != 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, timeout.Token).ConfigureAwait(false);
        }

        Assert.AreEqual(0, relay.UdpFlowCount, "Idle UDP DNS flow must be removed instead of living until Admin Mode stops.");
    }

    [TestMethod]
    public async Task TransparentDnsRelay_Tcp_ForwardsDnsStreamToDnsCryptLoopback()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        using Socket dnsCryptListener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        dnsCryptListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        dnsCryptListener.Listen(1);
        int dnsCryptPort = ((IPEndPoint)dnsCryptListener.LocalEndPoint!).Port;

        Task echoTask = Task.Run(async () =>
        {
            using Socket accepted = await dnsCryptListener.AcceptAsync(timeout.Token).ConfigureAwait(false);
            byte[] echoBuffer = new byte[6];
            int offset = 0;
            while (offset < echoBuffer.Length)
            {
                int received = await accepted.ReceiveAsync(echoBuffer.AsMemory(offset), SocketFlags.None, timeout.Token).ConfigureAwait(false);
                if (received == 0)
                    throw new IOException("Test DNSCrypt TCP peer closed before receiving the framed DNS message.");
                offset += received;
            }
            await accepted.SendAsync(echoBuffer, SocketFlags.None, timeout.Token).ConfigureAwait(false);
        }, timeout.Token);

        FlowRegistry flows = CreateDnsRelayFlowRegistry();
        await using TransparentDnsRelay relay = new(flows, dnsCryptPort);
        relay.Start();

        using Socket application = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        application.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        ushort applicationPort = checked((ushort)((IPEndPoint)application.LocalEndPoint!).Port);
        FlowKey key = new(6, IPAddress.Loopback, applicationPort, IPAddress.Loopback, 53);
        flows.MarkReflected(new FlowState(key, 77, null, "test", RouteAction.Direct, RouteAction.Direct, DateTime.UtcNow));

        await application.ConnectAsync(new IPEndPoint(IPAddress.Loopback, relay.TcpPort), timeout.Token).ConfigureAwait(false);
        byte[] payload = [0x00, 0x04, 0x12, 0x34, 0x01, 0x00];
        await application.SendAsync(payload, SocketFlags.None, timeout.Token).ConfigureAwait(false);
        byte[] response = new byte[payload.Length];
        int offset = 0;
        while (offset < response.Length)
        {
            int received = await application.ReceiveAsync(response.AsMemory(offset), SocketFlags.None, timeout.Token).ConfigureAwait(false);
            Assert.AreNotEqual(0, received);
            offset += received;
        }

        CollectionAssert.AreEqual(payload, response);
        await echoTask.ConfigureAwait(false);
    }

    [TestMethod]
    public void WinDivertReceiveTerminationIsOnlyExpectedDuringShutdown()
    {
        Assert.IsTrue(WinDivertTransparentRouter.IsExpectedReceiveTermination(shutdownRequested: true, errorCode: 6));
        Assert.IsTrue(WinDivertTransparentRouter.IsExpectedReceiveTermination(shutdownRequested: true, errorCode: 232));
        Assert.IsTrue(WinDivertTransparentRouter.IsExpectedReceiveTermination(shutdownRequested: true, errorCode: 995));
        Assert.IsFalse(WinDivertTransparentRouter.IsExpectedReceiveTermination(shutdownRequested: false, errorCode: 995));
        Assert.IsFalse(WinDivertTransparentRouter.IsExpectedReceiveTermination(shutdownRequested: true, errorCode: 5));
    }

    [TestMethod]
    public void WinDivertSendValidationRejectsNativeFailureAndShortWrite()
    {
        System.ComponentModel.Win32Exception nativeFailure = Assert.ThrowsExactly<System.ComponentModel.Win32Exception>(() =>
            WinDivertTransparentRouter.ValidateSendResult(
                succeeded: false,
                sentLength: 0,
                expectedLength: 128,
                errorCode: 5));
        Assert.AreEqual(5, nativeFailure.NativeErrorCode);

        Assert.ThrowsExactly<IOException>(() =>
            WinDivertTransparentRouter.ValidateSendResult(
                succeeded: true,
                sentLength: 64,
                expectedLength: 128,
                errorCode: 0));
    }


    [TestMethod]
    public async Task TransparentDnsRelay_StartTwiceIsRejectedAndDisposeIsIdempotent()
    {
        using Socket dnsCrypt = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        dnsCrypt.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int dnsCryptPort = ((IPEndPoint)dnsCrypt.LocalEndPoint!).Port;

        FlowRegistry flows = CreateDnsRelayFlowRegistry();
        TransparentDnsRelay relay = new(flows, dnsCryptPort);
        relay.Start();
        Assert.ThrowsExactly<InvalidOperationException>(() => relay.Start());

        await relay.DisposeAsync();
        await relay.DisposeAsync();
    }

    [TestMethod]
    public void FlowRegistry_PeerIndexPreservesMultiHomedFallbackWithoutLinearScan()
    {
        FlowRegistry flows = CreateDnsRelayFlowRegistry();
        DateTime now = DateTime.UtcNow;
        FlowKey firstKey = new(
            17,
            IPAddress.Parse("192.0.2.10"),
            50123,
            IPAddress.Parse("9.9.9.9"),
            53);
        FlowKey secondKey = firstKey with { LocalAddress = IPAddress.Parse("192.0.2.11") };
        FlowState first = new(firstKey, 77, null, "test-1", RouteAction.Direct, RouteAction.Direct, now.AddSeconds(-1));
        FlowState second = new(secondKey, 78, null, "test-2", RouteAction.Direct, RouteAction.Direct, now);
        flows.MarkReflected(first);
        flows.MarkReflected(second);

        Assert.IsTrue(flows.TryGetReflected(
            17,
            firstKey.LocalAddress,
            50123,
            IPAddress.Parse("9.9.9.9"),
            out FlowState exactFirst));
        Assert.AreSame(first, exactFirst);

        Assert.IsTrue(flows.TryGetReflected(
            17,
            secondKey.LocalAddress,
            50123,
            IPAddress.Parse("9.9.9.9"),
            out FlowState exactSecond));
        Assert.AreSame(second, exactSecond);

        Assert.IsTrue(flows.TryGetByPeer(17, IPAddress.Parse("9.9.9.9"), 50123, out FlowState byPeer));
        Assert.AreSame(second, byPeer);

        Assert.IsTrue(flows.TryGetReflected(
            17,
            IPAddress.Parse("198.51.100.20"),
            50123,
            IPAddress.Parse("9.9.9.9"),
            out FlowState fallback));
        Assert.AreSame(second, fallback);
    }

    [TestMethod]
    public async Task CaptureChildSupervisor_InitialPingIsImmediateAndVersioned()
    {
        await using CaptureChildSupervisor supervisor = new();
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        ServiceResponse response = await supervisor.PingAsync();

        stopwatch.Stop();
        Assert.IsTrue(response.Success);
        Assert.IsFalse(response.CaptureActive);
        Assert.AreEqual("pong", response.Message);
        Assert.IsFalse(string.IsNullOrWhiteSpace(response.Version));
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    private static FlowRegistry CreateDnsRelayFlowRegistry()
    {
        StartRequest request = new()
        {
            MainProcessId = -1,
            DefaultRoute = RouteAction.Direct,
        };
        return new FlowRegistry(new ApplicationPolicy(request));
    }

    private static DnsPolicyDto ReadyDnsCryptPolicy() => new()
    {
        Mode = Shadowsocks.NetworkService.Ipc.DnsPolicyMode.DnsCrypt,
        DnsCryptPort = 5300,
        DnsCryptProcessId = 42,
        FailClosed = true,
    };
}
