#nullable enable
using System;
using System.Buffers.Binary;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.NetworkService.Ipc;
using Shadowsocks.NetworkService.Routing;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class IpFragmentTrackerTests
    {
        private static readonly DateTime Now = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void ParsesIpv4FirstAndContinuationFragments()
        {
            byte[] first = CreateIpv4Fragment(id: 0x1234, offset: 0, moreFragments: true);
            byte[] last = CreateIpv4Fragment(id: 0x1234, offset: 185, moreFragments: false);

            Assert.IsTrue(IpFragmentTracker.TryParse(first, out IpFragmentInfo firstInfo));
            Assert.IsTrue(firstInfo.IsFirstFragment);
            Assert.IsTrue(firstInfo.MoreFragments);
            Assert.AreEqual((byte)17, firstInfo.Key.Protocol);
            Assert.AreEqual(IPAddress.Parse("192.0.2.10"), firstInfo.Key.SourceAddress);
            Assert.AreEqual(IPAddress.Parse("198.51.100.53"), firstInfo.Key.DestinationAddress);
            Assert.AreEqual((uint)0x1234, firstInfo.Key.Identification);

            Assert.IsTrue(IpFragmentTracker.TryParse(last, out IpFragmentInfo lastInfo));
            Assert.IsFalse(lastInfo.IsFirstFragment);
            Assert.IsFalse(lastInfo.MoreFragments);
            Assert.AreEqual(firstInfo.Key, lastInfo.Key);
        }

        [TestMethod]
        public void ParsesIpv6FragmentHeaderAfterExtensionHeader()
        {
            byte[] packet = CreateIpv6Fragment(id: 0x10203040, offset: 0, moreFragments: true);

            Assert.IsTrue(IpFragmentTracker.TryParse(packet, out IpFragmentInfo info));
            Assert.IsTrue(info.IsFirstFragment);
            Assert.AreEqual((byte)17, info.Key.Protocol);
            Assert.AreEqual((uint)0x10203040, info.Key.Identification);
            Assert.AreEqual(IPAddress.Parse("2001:db8::1"), info.Key.SourceAddress);
            Assert.AreEqual(IPAddress.Parse("2001:db8::53"), info.Key.DestinationAddress);
        }

        [TestMethod]
        public void Ipv6FragmentCanPointToPostFragmentExtensionHeader()
        {
            byte[] packet = new byte[64];
            packet[0] = 0x60;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 24);
            packet[6] = 44; // Fragment
            packet[7] = 64;
            IPAddress.Parse("2001:db8::1").GetAddressBytes().CopyTo(packet, 8);
            IPAddress.Parse("2001:db8::53").GetAddressBytes().CopyTo(packet, 24);
            packet[40] = 60; // Destination Options follows Fragment
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(42, 2), 0x0001);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(44, 4), 0x11223344);
            packet[48] = 17; // UDP follows Destination Options
            packet[49] = 0; // 8-byte Destination Options header

            Assert.IsTrue(IpFragmentTracker.TryParse(packet, out IpFragmentInfo fragment));
            Assert.IsTrue(fragment.IsFirstFragment);
            Assert.IsTrue(fragment.MoreFragments);
            Assert.AreEqual((byte)60, fragment.Key.Protocol);
            Assert.AreEqual(0x11223344u, fragment.Key.Identification);
        }

        [TestMethod]
        public void AtomicIpv6FragmentDoesNotRequireFragmentTracking()
        {
            byte[] packet = CreateIpv6Fragment(id: 7, offset: 0, moreFragments: false);
            Assert.IsFalse(IpFragmentTracker.TryParse(packet, out _));
        }

        [TestMethod]
        public void TrackerCarriesDirectDecisionAcrossFragmentsAndRemovesOnLast()
        {
            IpFragmentInfo first = Parse(CreateIpv4Fragment(9, 0, moreFragments: true));
            IpFragmentInfo last = Parse(CreateIpv4Fragment(9, 100, moreFragments: false));
            var tracker = new IpFragmentTracker();

            tracker.Record(first, FragmentDisposition.Direct, Now);
            Assert.IsTrue(tracker.TryGetDisposition(last, Now.AddMilliseconds(1), out FragmentDisposition disposition));
            Assert.AreEqual(FragmentDisposition.Direct, disposition);
            Assert.IsFalse(tracker.TryGetDisposition(last, Now.AddMilliseconds(2), out _));
        }

        [TestMethod]
        public void UnknownContinuationFailsSecure()
        {
            IpFragmentInfo continuation = Parse(CreateIpv4Fragment(17, 100, moreFragments: false));
            var tracker = new IpFragmentTracker();

            Assert.IsFalse(tracker.TryGetDisposition(continuation, Now, out FragmentDisposition disposition));
            Assert.AreEqual(FragmentDisposition.Drop, disposition);
        }

        [TestMethod]
        public void TransparentRoutesDropFragmentedDatagrams()
        {
            Assert.AreEqual(
                FragmentDisposition.Drop,
                IpFragmentTracker.ForDnsDecision(DnsRouteDecision.Redirect));
            Assert.AreEqual(
                FragmentDisposition.Drop,
                IpFragmentTracker.ForDnsDecision(DnsRouteDecision.Block));
            Assert.AreEqual(
                FragmentDisposition.Direct,
                IpFragmentTracker.ForDnsDecision(DnsRouteDecision.Direct));
            Assert.AreEqual(FragmentDisposition.Drop, IpFragmentTracker.ForRoute(RouteAction.Proxy));
            Assert.AreEqual(FragmentDisposition.Drop, IpFragmentTracker.ForRoute(RouteAction.Block));
            Assert.AreEqual(FragmentDisposition.Direct, IpFragmentTracker.ForRoute(RouteAction.Direct));
        }

        private static IpFragmentInfo Parse(byte[] packet)
        {
            Assert.IsTrue(IpFragmentTracker.TryParse(packet, out IpFragmentInfo info));
            return info;
        }

        private static byte[] CreateIpv4Fragment(ushort id, int offset, bool moreFragments)
        {
            byte[] packet = new byte[28];
            packet[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), checked((ushort)packet.Length));
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), id);
            ushort flagsAndOffset = checked((ushort)(offset & 0x1FFF));
            if (moreFragments)
                flagsAndOffset |= 0x2000;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), flagsAndOffset);
            packet[8] = 64;
            packet[9] = 17;
            IPAddress.Parse("192.0.2.10").GetAddressBytes().CopyTo(packet, 12);
            IPAddress.Parse("198.51.100.53").GetAddressBytes().CopyTo(packet, 16);
            if (offset == 0)
            {
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), 53000);
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 53);
            }
            return packet;
        }

        private static byte[] CreateIpv6Fragment(uint id, int offset, bool moreFragments)
        {
            // IPv6 base header + one Hop-by-Hop header + Fragment header + UDP header.
            byte[] packet = new byte[64];
            packet[0] = 0x60;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 24);
            packet[6] = 0; // Hop-by-Hop
            packet[7] = 64;
            IPAddress.Parse("2001:db8::1").GetAddressBytes().CopyTo(packet, 8);
            IPAddress.Parse("2001:db8::53").GetAddressBytes().CopyTo(packet, 24);

            packet[40] = 44; // next = Fragment
            packet[41] = 0;  // 8-byte extension header

            packet[48] = 17; // Fragment next = UDP
            ushort offsetAndFlags = checked((ushort)((offset << 3) & 0xFFF8));
            if (moreFragments)
                offsetAndFlags |= 0x0001;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(50, 2), offsetAndFlags);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(52, 4), id);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(56, 2), 53000);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(58, 2), 53);
            return packet;
        }
    }
}
