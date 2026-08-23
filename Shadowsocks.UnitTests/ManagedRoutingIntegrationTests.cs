using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller;
using Shadowsocks.NetworkService.Ipc;
using Shadowsocks.NetworkService.Routing;

namespace Shadowsocks.UnitTests;

[TestClass]
public class ManagedRoutingIntegrationTests
{
    [TestMethod]
    public void LocalPacManagedFunnelContainsNoEmbeddedRuleRuntime()
    {
        string pac = PACDaemon.GetManagedFunnelPac();

        StringAssert.Contains(pac, "FindProxyForURL");
        StringAssert.Contains(pac, "__PROXY__");
        Assert.IsFalse(pac.Contains("__RULES__", StringComparison.Ordinal));
        Assert.IsFalse(pac.Contains("RegExpFilter", StringComparison.Ordinal));
        Assert.IsFalse(pac.Contains("CombinedMatcher", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ManagedAdminPolicyDefersOnlyUnmatchedOrdinaryTcp()
    {
        StartRequest request = new()
        {
            MainProcessId = 111,
            DefaultRoute = RouteAction.Direct,
            ManagedRouting = new ManagedRoutingDto
            {
                Enabled = true,
                DefaultRules = ["||proxy.example^"],
            },
            ApplicationRules =
            [
                new ApplicationRuleDto
                {
                    Enabled = true,
                    Application = "forced.exe",
                    Action = RouteAction.Proxy,
                },
                new ApplicationRuleDto
                {
                    Enabled = true,
                    Application = "defaulted.exe",
                    Action = RouteAction.Default,
                },
            ],
        };
        ApplicationPolicy policy = new(request);

        Assert.AreEqual(RouteAction.Deferred, policy.Evaluate(6, 443, 222, null, "browser"));
        Assert.AreEqual(RouteAction.Direct, policy.Evaluate(17, 443, 222, null, "browser"));
        Assert.AreEqual(RouteAction.Direct, policy.Evaluate(6, 53, 222, null, "browser"));
        Assert.AreEqual(RouteAction.Proxy, policy.Evaluate(6, 443, 222, null, "forced"));
        Assert.AreEqual(RouteAction.Deferred, policy.Evaluate(6, 443, 222, null, "defaulted"));
    }

    [TestMethod]
    public void NetworkServiceLinkedFilterEngineUsesSameAbpSemantics()
    {
        Shadowsocks.NetworkService.ManagedRouting.FilterEngine engine =
            Shadowsocks.NetworkService.ManagedRouting.FilterEngine.Compile(
                new[] { "||proxy.example^", "@@||direct.example^" },
                Array.Empty<string>(),
                out _);

        Assert.AreEqual(
            Shadowsocks.NetworkService.ManagedRouting.FilterRoutingAction.Proxy,
            engine.Evaluate("https://proxy.example/", "proxy.example").Action);
        Assert.AreEqual(
            Shadowsocks.NetworkService.ManagedRouting.FilterRoutingAction.Direct,
            engine.Evaluate("https://direct.example/", "direct.example").Action);
    }

    [TestMethod]
    public void NetworkServiceStartRequestRoundTripsManagedRuleSet()
    {
        StartRequest request = new()
        {
            ManagedRouting = new ManagedRoutingDto
            {
                Enabled = true,
                Source = "test snapshot",
                DefaultRules = ["||proxy.example^", "@@||direct.example^"],
                UserRules = ["||user.example^"],
            },
        };

        string json = JsonSerializer.Serialize(request, NetworkServiceJsonContext.Default.StartRequest);
        StartRequest parsed = JsonSerializer.Deserialize(json, NetworkServiceJsonContext.Default.StartRequest);

        Assert.IsNotNull(parsed);
        Assert.IsTrue(parsed.ManagedRouting.Enabled);
        Assert.AreEqual("test snapshot", parsed.ManagedRouting.Source);
        CollectionAssert.AreEqual(
            new[] { "||proxy.example^", "@@||direct.example^" },
            parsed.ManagedRouting.DefaultRules);
        CollectionAssert.AreEqual(new[] { "||user.example^" }, parsed.ManagedRouting.UserRules);
    }

    [TestMethod]
    public void HttpHostInspectionBuildsRoutingUrlWithPath()
    {
        byte[] request = Encoding.ASCII.GetBytes(
            "GET /ads/banner.js?x=1 HTTP/1.1\r\nHost: cdn.example.com\r\nConnection: close\r\n\r\n");

        TcpDestinationInspectionStatus status = InitialTcpProtocolInspector.Inspect(
            request,
            80,
            out TcpDestinationIdentity identity);

        Assert.AreEqual(TcpDestinationInspectionStatus.Identified, status);
        Assert.AreEqual("cdn.example.com", identity.Host);
        Assert.AreEqual("http://cdn.example.com/ads/banner.js?x=1", identity.Url);
        Assert.AreEqual("http", identity.Protocol);
    }

    [TestMethod]
    public void PartialHttpHeadersRequestMoreData()
    {
        byte[] request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: partial.example");

        TcpDestinationInspectionStatus status = InitialTcpProtocolInspector.Inspect(
            request,
            80,
            out _);

        Assert.AreEqual(TcpDestinationInspectionStatus.NeedMoreData, status);
    }

    [TestMethod]
    public void TlsClientHelloSniIsExtractedWithoutDecryption()
    {
        byte[] hello = BuildClientHello("secure.example.com");

        TcpDestinationInspectionStatus status = InitialTcpProtocolInspector.Inspect(
            hello,
            443,
            out TcpDestinationIdentity identity);

        Assert.AreEqual(TcpDestinationInspectionStatus.Identified, status);
        Assert.AreEqual("secure.example.com", identity.Host);
        Assert.AreEqual("https://secure.example.com/", identity.Url);
        Assert.AreEqual("tls-sni", identity.Protocol);
    }

    [TestMethod]
    public void PartialTlsClientHelloRequestsMoreData()
    {
        byte[] hello = BuildClientHello("secure.example.com");
        byte[] partial = hello[..Math.Min(12, hello.Length)];

        TcpDestinationInspectionStatus status = InitialTcpProtocolInspector.Inspect(
            partial,
            443,
            out _);

        Assert.AreEqual(TcpDestinationInspectionStatus.NeedMoreData, status);
    }

    private static byte[] BuildClientHello(string host)
    {
        byte[] hostBytes = Encoding.ASCII.GetBytes(host);
        using var extensions = new System.IO.MemoryStream();
        WriteUInt16(extensions, 0x0000);
        int serverNameDataLength = 2 + 1 + 2 + hostBytes.Length;
        WriteUInt16(extensions, serverNameDataLength);
        WriteUInt16(extensions, 1 + 2 + hostBytes.Length);
        extensions.WriteByte(0);
        WriteUInt16(extensions, hostBytes.Length);
        extensions.Write(hostBytes, 0, hostBytes.Length);

        using var body = new System.IO.MemoryStream();
        body.WriteByte(0x03);
        body.WriteByte(0x03);
        body.Write(new byte[32], 0, 32);
        body.WriteByte(0); // session id
        WriteUInt16(body, 2);
        body.WriteByte(0x13);
        body.WriteByte(0x01);
        body.WriteByte(1); // compression methods length
        body.WriteByte(0);
        byte[] extensionBytes = extensions.ToArray();
        WriteUInt16(body, extensionBytes.Length);
        body.Write(extensionBytes, 0, extensionBytes.Length);

        byte[] helloBody = body.ToArray();
        using var handshake = new System.IO.MemoryStream();
        handshake.WriteByte(0x01);
        handshake.WriteByte((byte)((helloBody.Length >> 16) & 0xff));
        handshake.WriteByte((byte)((helloBody.Length >> 8) & 0xff));
        handshake.WriteByte((byte)(helloBody.Length & 0xff));
        handshake.Write(helloBody, 0, helloBody.Length);
        byte[] handshakeBytes = handshake.ToArray();

        using var record = new System.IO.MemoryStream();
        record.WriteByte(0x16);
        record.WriteByte(0x03);
        record.WriteByte(0x01);
        WriteUInt16(record, handshakeBytes.Length);
        record.Write(handshakeBytes, 0, handshakeBytes.Length);
        return record.ToArray();
    }

    private static void WriteUInt16(System.IO.MemoryStream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)value));
        stream.Write(bytes);
    }
}
