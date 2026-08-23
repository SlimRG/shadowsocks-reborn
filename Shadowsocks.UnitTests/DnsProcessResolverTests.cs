using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.NetworkService.Routing;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class DnsProcessResolverTests
    {
        [TestMethod]
        public void DnsOwnerLookupRetriesShortLivedSocketRegistration()
        {
            var delays = new List<int>();
            int calls = 0;
            int pid = ProcessResolver.ResolvePidWithRetry(
                () => ++calls == 4 ? 4242 : 0,
                dnsFlow: true,
                delays.Add);

            Assert.AreEqual(4242, pid);
            Assert.AreEqual(4, calls);
            CollectionAssert.AreEqual(new[] { 1, 4, 10 }, delays);
        }

        [TestMethod]
        public void NonDnsOwnerLookupDoesNotRetry()
        {
            int calls = 0;
            int pid = ProcessResolver.ResolvePidWithRetry(
                () =>
                {
                    calls++;
                    return 0;
                },
                dnsFlow: false,
                _ => Assert.Fail("Non-DNS owner lookup must not delay."));

            Assert.AreEqual(0, pid);
            Assert.AreEqual(1, calls);
        }
    }
}
