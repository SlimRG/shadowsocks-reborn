using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller.Traffic;
using Shadowsocks.Model;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class TrafficPolicyTest
    {
        [TestMethod]
        public void UserModeDefaultsToProxy()
        {
            Configuration configuration = new();
            TrafficPolicyEngine engine = new(configuration);

            RouteDecision decision = engine.EvaluateUserMode(new TrafficContext
            {
                ProcessName = "browser.exe",
                DestinationHost = "example.com",
                DestinationPort = 443,
                Protocol = "TCP",
            });

            Assert.AreEqual(TrafficRouteAction.Proxy, decision.Action);
        }

        [TestMethod]
        public void ApplicationRuleOverridesUserMode()
        {
            Configuration configuration = new()
            {
                applicationRules = new List<ApplicationRouteRule>
                {
                    new()
                    {
                        application = "game*.exe",
                        action = TrafficRouteAction.Direct,
                    },
                },
            };
            TrafficPolicyEngine engine = new(configuration);

            RouteDecision decision = engine.EvaluateUserMode(new TrafficContext
            {
                ProcessName = "game64",
                DestinationHost = "example.com",
                DestinationPort = 443,
                Protocol = "TCP",
            });

            Assert.AreEqual(TrafficRouteAction.Direct, decision.Action);
        }

        [TestMethod]
        public void AdminModeUsesConfiguredFallbackWhenNoApplicationRuleMatches()
        {
            Configuration configuration = new();
            TrafficPolicyEngine engine = new(configuration);

            RouteDecision decision = engine.EvaluateAdminMode(
                new TrafficContext
                {
                    ProcessName = "unknown.exe",
                    DestinationPort = 443,
                    Protocol = "TCP",
                },
                TrafficRouteAction.Direct);

            Assert.AreEqual(TrafficRouteAction.Direct, decision.Action);
        }

        [TestMethod]
        public void DnsPolicyIsPresentButDefaultsToSystem()
        {
            Configuration configuration = new();
            Assert.IsNotNull(configuration.dnsPolicy);
            Assert.AreEqual(DnsPolicyMode.System, configuration.dnsPolicy.mode);
            Assert.IsNotNull(configuration.dnsPolicy.dnsCrypt);
            Assert.IsTrue(configuration.dnsPolicy.dnsCrypt.autoUpdate);
            Assert.IsTrue(configuration.dnsPolicy.dnsCrypt.requireDnssec);
            Assert.IsTrue(configuration.dnsPolicy.dnsCrypt.requireNoLog);
            Assert.IsTrue(configuration.dnsPolicy.dnsCrypt.failClosed);
        }

        [TestMethod]
        public void ConfigurationProcessRepairsMissingDnsCryptSettingsFromOlderJson()
        {
            Configuration configuration = new()
            {
                dnsPolicy = new DnsPolicyConfig { dnsCrypt = null },
            };

            Configuration.Process(ref configuration);

            Assert.IsNotNull(configuration.dnsPolicy.dnsCrypt);
            Assert.IsNotNull(configuration.dnsPolicy.dnsCrypt.serverNames);
        }
    }
}
