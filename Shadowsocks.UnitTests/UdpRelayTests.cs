using System;
using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class UdpRelayTests
    {
        [TestMethod]
        public void Socks5UdpDestinationParsesIpv4DomainAndIpv6()
        {
            byte[] ipv4 = { 0, 0, 0, 1, 1, 1, 1, 1, 0, 53 };
            var ipv4Endpoint = UDPRelay.TryParseDestination(ipv4, ipv4.Length) as IPEndPoint;
            Assert.IsNotNull(ipv4Endpoint);
            Assert.AreEqual(IPAddress.Parse("1.1.1.1"), ipv4Endpoint.Address);
            Assert.AreEqual(53, ipv4Endpoint.Port);

            byte[] host = Encoding.ASCII.GetBytes("example.com");
            var domain = new byte[3 + 1 + 1 + host.Length + 2];
            domain[3] = 3;
            domain[4] = (byte)host.Length;
            Buffer.BlockCopy(host, 0, domain, 5, host.Length);
            domain[^2] = 0x01;
            domain[^1] = 0xBB;
            var domainEndpoint = UDPRelay.TryParseDestination(domain, domain.Length) as DnsEndPoint;
            Assert.IsNotNull(domainEndpoint);
            Assert.AreEqual("example.com", domainEndpoint.Host);
            Assert.AreEqual(443, domainEndpoint.Port);

            byte[] ipv6Bytes = IPAddress.IPv6Loopback.GetAddressBytes();
            var ipv6 = new byte[3 + 1 + 16 + 2];
            ipv6[3] = 4;
            Buffer.BlockCopy(ipv6Bytes, 0, ipv6, 4, ipv6Bytes.Length);
            ipv6[^2] = 0x14;
            ipv6[^1] = 0xE9;
            var ipv6Endpoint = UDPRelay.TryParseDestination(ipv6, ipv6.Length) as IPEndPoint;
            Assert.IsNotNull(ipv6Endpoint);
            Assert.AreEqual(IPAddress.IPv6Loopback, ipv6Endpoint.Address);
            Assert.AreEqual(5353, ipv6Endpoint.Port);
        }

        [TestMethod]
        public void Socks5UdpDestinationRejectsMalformedPackets()
        {
            Assert.IsNull(UDPRelay.TryParseDestination(new byte[] { 0, 0, 0, 3, 10, 1, 2 }, 7));
            Assert.IsNull(UDPRelay.TryParseDestination(new byte[] { 0, 0, 0, 9, 1, 2, 3, 4, 0, 53 }, 10));
            Assert.IsNull(UDPRelay.TryParseDestination(new byte[] { 0, 0, 0, 1, 1, 1, 1, 1, 0, 0 }, 10));
        }
    }
}
