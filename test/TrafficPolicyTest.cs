using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller.Traffic;
using Shadowsocks.Model;
using System.Collections.Generic;

namespace Shadowsocks.Test
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
        }
    }
}
