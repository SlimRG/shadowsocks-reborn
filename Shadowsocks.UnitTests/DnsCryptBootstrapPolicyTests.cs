#nullable enable
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller.Service;
using Shadowsocks.Controller.Traffic;
using Shadowsocks.Model;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class DnsCryptBootstrapPolicyTests
    {
        [TestMethod]
        public void DirectDnsCryptDoesNotRestrictShadowsocksHostnames()
        {
            Configuration configuration = CreateConfiguration("example.org");
            configuration.dnsPolicy.dnsCrypt.routeThroughShadowsocks = false;

            Assert.AreEqual(0, DnsCryptBootstrapPolicy.GetUnsafeEndpoints(configuration).Count);
        }

        [TestMethod]
        public void RoutedDnsCryptRequiresIpLiteralShadowsocksEndpoints()
        {
            Configuration configuration = CreateConfiguration("ss.example.org");

            IReadOnlyList<string> unsafeEndpoints = DnsCryptBootstrapPolicy.GetUnsafeEndpoints(configuration);

            CollectionAssert.AreEqual(new[] { "ss.example.org" }, unsafeEndpoints.ToArray());
            Assert.ThrowsExactly<System.InvalidOperationException>(() => DnsCryptBootstrapPolicy.Validate(configuration));
        }

        [TestMethod]
        public void RoutedDnsCryptAcceptsIpv4AndIpv6ServerEndpoints()
        {
            Configuration configuration = CreateConfiguration("203.0.113.7");
            configuration.configs.Add(new Server { server = "2001:db8::7", ServerPort = 8388, password = "test" });

            DnsCryptBootstrapPolicy.Validate(configuration);
            Assert.AreEqual(0, DnsCryptBootstrapPolicy.GetUnsafeEndpoints(configuration).Count);
        }

        [TestMethod]
        public void RoutedDnsCryptAlsoRequiresIpLiteralForwardProxyEndpoint()
        {
            Configuration configuration = CreateConfiguration("203.0.113.7");
            configuration.proxy.useProxy = true;
            configuration.proxy.proxyServer = "proxy.example.org";
            configuration.proxy.proxyPort = 1080;

            IReadOnlyList<string> unsafeEndpoints = DnsCryptBootstrapPolicy.GetUnsafeEndpoints(configuration);

            CollectionAssert.Contains(unsafeEndpoints.ToArray(), "proxy.example.org");
        }

        private static Configuration CreateConfiguration(string serverHost)
        {
            Configuration configuration = new()
            {
                configs = [new Server { server = serverHost, ServerPort = 8388, password = "test" }],
                dnsPolicy = new DnsPolicyConfig
                {
                    mode = DnsPolicyMode.DnsCrypt,
                    dnsCrypt = new DnsCryptConfig { routeThroughShadowsocks = true },
                },
            };
            return configuration;
        }
    }
}
