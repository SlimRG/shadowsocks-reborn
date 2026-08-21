#nullable enable
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.NetworkService.Routing;
using Shadowsocks.NetworkService.WinDivert;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class ProcessAttributionCacheTests
    {
        private static readonly DateTime Now = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

        [TestInitialize]
        public void Initialize()
        {
            ProcessAttributionCache.ClearForTests();
        }

        [TestMethod]
        public void FlowObservationResolvesHostAndNetworkOrderPortForms()
        {
            const ushort port = 53000;
            ProcessAttributionCache.Observe(17, port, 4242, Now);

            Assert.IsTrue(ProcessAttributionCache.TryGetProcessId(17, port, Now, out int direct));
            Assert.AreEqual(4242, direct);

            ushort reversed = BinaryPrimitives.ReverseEndianness(port);
            Assert.IsTrue(ProcessAttributionCache.TryGetProcessId(17, reversed, Now, out int reverse));
            Assert.AreEqual(4242, reverse);
        }

        [TestMethod]
        public void AmbiguousSharedPortFallsBackInsteadOfGuessingPid()
        {
            ProcessAttributionCache.Observe(17, 54000, 100, Now);
            ProcessAttributionCache.Observe(17, 54000, 200, Now);

            Assert.IsFalse(ProcessAttributionCache.TryGetProcessId(17, 54000, Now, out int processId));
            Assert.AreEqual(0, processId);
        }

        [TestMethod]
        public void FlowDeletionRemovesOnlyMatchingPid()
        {
            ProcessAttributionCache.Observe(6, 55000, 100, Now);
            ProcessAttributionCache.Observe(6, 55000, 200, Now);
            ProcessAttributionCache.Remove(6, 55000, 100);

            Assert.IsTrue(ProcessAttributionCache.TryGetProcessId(6, 55000, Now, out int processId));
            Assert.AreEqual(200, processId);
        }

        [TestMethod]
        public void StaleFlowOwnershipExpires()
        {
            ProcessAttributionCache.Observe(17, 56000, 4242, Now);

            Assert.IsFalse(ProcessAttributionCache.TryGetProcessId(
                17,
                56000,
                Now.AddMinutes(6),
                out _));
        }

        [TestMethod]
        public void NativeFlowLayoutFitsWinDivertAddressUnionAndEventBitsDecode()
        {
            Assert.AreEqual(64, Marshal.SizeOf<WinDivertDataFlow>());
            Assert.AreEqual(80, Marshal.SizeOf<WinDivertAddress>());

            WinDivertAddress address = new()
            {
                BitFields = (uint)WinDivertEvent.FlowEstablished << 8,
            };
            Assert.AreEqual(WinDivertEvent.FlowEstablished, address.Event);
        }
    }
}
