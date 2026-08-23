using System.Collections.Generic;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller.Traffic;
using Shadowsocks.Controller.Service;
using Shadowsocks.Controller.Strategy;
using Shadowsocks.Model;
using Shadowsocks.Routing;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class TrafficPolicyTest
    {
        [TestMethod]
        public void BalancingStrategyHandlesNegativeUdpEndpointHash()
        {
            Server first = new()
            {
                server = "198.51.100.10",
                ServerPort = 443,
                password = "first",
                method = Server.DefaultMethod,
            };
            Server second = new()
            {
                server = "198.51.100.11",
                ServerPort = 443,
                password = "second",
                method = Server.DefaultMethod,
            };
            Configuration configuration = new()
            {
                configs = new List<Server> { first, second },
            };
            BalancingStrategy strategy = new(() => configuration);

            Server selected = strategy.GetAServer(
                IStrategyCallerType.UDP,
                new NegativeHashIpEndPoint(),
                new IPEndPoint(IPAddress.Parse("203.0.113.10"), 53));

            Assert.AreSame(second, selected);
        }

        [TestMethod]
        public void BalancingStrategySkipsIncompleteServers()
        {
            Server incomplete = new()
            {
                server = "198.51.100.20",
                ServerPort = 443,
                password = string.Empty,
                method = Server.DefaultMethod,
            };
            Server configured = new()
            {
                server = "198.51.100.21",
                ServerPort = 443,
                password = "configured",
                method = Server.DefaultMethod,
            };
            Configuration configuration = new()
            {
                configs = new List<Server> { incomplete, configured },
            };
            BalancingStrategy strategy = new(() => configuration);

            Assert.AreSame(
                configured,
                strategy.GetAServer(
                    IStrategyCallerType.UDP,
                    new ZeroHashIpEndPoint(),
                    new IPEndPoint(IPAddress.Parse("203.0.113.20"), 443)));
        }

        [TestMethod]
        public void HighAvailabilitySkipsIncompleteServers()
        {
            Server incomplete = new()
            {
                server = "198.51.100.30",
                ServerPort = 443,
                password = "damaged-timeout",
                method = Server.DefaultMethod,
                timeout = 0,
            };
            Server configured = new()
            {
                server = "198.51.100.31",
                ServerPort = 443,
                password = "configured",
                method = Server.DefaultMethod,
            };
            Configuration configuration = new()
            {
                configs = new List<Server> { incomplete, configured },
            };
            HighAvailabilityStrategy strategy = new(() => configuration);

            strategy.ReloadServers();

            Assert.AreSame(
                configured,
                strategy.GetAServer(
                    IStrategyCallerType.TCP,
                    new IPEndPoint(IPAddress.Loopback, 12345),
                    new IPEndPoint(IPAddress.Parse("203.0.113.30"), 443)));
        }

        [TestMethod]
        public void HighAvailabilityKeepsSameEndpointProfilesDistinct()
        {
            Server first = new()
            {
                server = "198.51.100.40",
                ServerPort = 443,
                password = "first",
                method = Server.DefaultMethod,
            };
            Server second = new()
            {
                server = "198.51.100.40",
                ServerPort = 443,
                password = "second",
                method = Server.DefaultMethod,
            };
            Configuration configuration = new()
            {
                configs = new List<Server> { first, second },
            };
            HighAvailabilityStrategy strategy = new(() => configuration);
            strategy.ReloadServers();

            var field = typeof(HighAvailabilityStrategy).GetField(
                "_serverStatus",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            var statuses = (Dictionary<Server, HighAvailabilityStrategy.ServerStatus>)field.GetValue(strategy);

            Assert.AreEqual(2, statuses.Count);
            Assert.IsTrue(statuses.ContainsKey(first));
            Assert.IsTrue(statuses.ContainsKey(second));
        }

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
                        Application = "game*.exe",
                        Action = TrafficRouteAction.Direct,
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
        public void ApplicationRulesArePublishedAsImmutableSnapshots()
        {
            ApplicationRouteRule configuredRule = new()
            {
                Application = "browser.exe",
                Action = TrafficRouteAction.Direct,
            };
            Configuration configuration = new()
            {
                applicationRules = new List<ApplicationRouteRule> { configuredRule },
            };
            TrafficPolicyEngine engine = new(configuration);

            configuredRule.Application = "other.exe";
            configuredRule.Action = TrafficRouteAction.Block;

            RouteDecision decision = engine.EvaluateUserMode(new TrafficContext
            {
                ProcessName = "browser.exe",
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
        public void ManagedRoutingSnapshotCanDirectOrProxyUserModeRequests()
        {
            Configuration configuration = new();
            TrafficPolicyEngine policy = new(configuration);
            FilterEngine filters = FilterEngine.Compile(
                new[] { "||proxy.example^", "@@||direct.example^" },
                System.Array.Empty<string>(),
                out FilterCompilationReport report);
            policy.UpdateManagedRouting(ManagedRoutingSnapshot.LocalRules(filters, report));

            RouteDecision proxy = policy.EvaluateUserMode(new TrafficContext
            {
                ProcessName = "browser.exe",
                DestinationHost = "proxy.example",
                DestinationUrl = "https://proxy.example/path",
                DestinationPort = 443,
                Protocol = "TCP",
            });
            RouteDecision direct = policy.EvaluateUserMode(new TrafficContext
            {
                ProcessName = "browser.exe",
                DestinationHost = "direct.example",
                DestinationUrl = "https://direct.example/path",
                DestinationPort = 443,
                Protocol = "TCP",
            });

            Assert.AreEqual(TrafficRouteAction.Proxy, proxy.Action);
            Assert.AreEqual(TrafficRouteAction.Direct, direct.Action);
            Assert.AreEqual(1L, policy.ManagedProxyDecisionCount);
            Assert.AreEqual(1L, policy.ManagedDirectDecisionCount);
            StringAssert.Contains(proxy.Reason, "managed routing generation");
        }

        [TestMethod]
        public void ApplicationRuleStillOverridesManagedRoutingSnapshot()
        {
            Configuration configuration = new()
            {
                applicationRules = new List<ApplicationRouteRule>
                {
                    new()
                    {
                        Application = "browser.exe",
                        Action = TrafficRouteAction.Direct,
                    },
                },
            };
            TrafficPolicyEngine policy = new(configuration);
            FilterEngine filters = FilterEngine.Compile(
                new[] { "/.*/" },
                System.Array.Empty<string>(),
                out FilterCompilationReport report);
            policy.UpdateManagedRouting(ManagedRoutingSnapshot.LocalRules(filters, report));

            RouteDecision decision = policy.EvaluateUserMode(new TrafficContext
            {
                ProcessName = "browser.exe",
                DestinationHost = "proxy.example",
                DestinationUrl = "https://proxy.example/",
                DestinationPort = 443,
                Protocol = "TCP",
            });

            Assert.AreEqual(TrafficRouteAction.Direct, decision.Action);
            Assert.AreEqual("application rule", decision.Reason);
        }

        [TestMethod]
        public void ManagedRoutingSnapshotPublicationReplacesWholeRuleSet()
        {
            TrafficPolicyEngine policy = new(new Configuration());
            FilterEngine first = FilterEngine.Compile(
                new[] { "@@||switch.example^" },
                System.Array.Empty<string>(),
                out FilterCompilationReport firstReport);
            FilterEngine second = FilterEngine.Compile(
                new[] { "||switch.example^" },
                System.Array.Empty<string>(),
                out FilterCompilationReport secondReport);
            ManagedRoutingSnapshot firstSnapshot = ManagedRoutingSnapshot.LocalRules(first, firstReport);
            ManagedRoutingSnapshot secondSnapshot = ManagedRoutingSnapshot.LocalRules(second, secondReport);

            policy.UpdateManagedRouting(firstSnapshot);
            RouteDecision before = policy.EvaluateUserMode(new TrafficContext
            {
                DestinationHost = "switch.example",
                DestinationUrl = "https://switch.example/",
                DestinationPort = 443,
                Protocol = "TCP",
            });
            policy.UpdateManagedRouting(secondSnapshot);
            RouteDecision after = policy.EvaluateUserMode(new TrafficContext
            {
                DestinationHost = "switch.example",
                DestinationUrl = "https://switch.example/",
                DestinationPort = 443,
                Protocol = "TCP",
            });

            Assert.IsTrue(secondSnapshot.Generation > firstSnapshot.Generation);
            Assert.AreEqual(TrafficRouteAction.Direct, before.Action);
            Assert.AreEqual(TrafficRouteAction.Proxy, after.Action);
            Assert.AreSame(secondSnapshot, policy.CurrentManagedRoutingSnapshot);
        }

        [TestMethod]
        public void ManagedRoutingWithoutUrlPreservesSystemProxyFallback()
        {
            TrafficPolicyEngine policy = new(new Configuration());
            FilterEngine filters = FilterEngine.Compile(
                new[] { "@@||direct.example^" },
                System.Array.Empty<string>(),
                out FilterCompilationReport report);
            policy.UpdateManagedRouting(ManagedRoutingSnapshot.LocalRules(filters, report));

            RouteDecision decision = policy.EvaluateUserMode(new TrafficContext
            {
                DestinationHost = "direct.example",
                DestinationPort = 443,
                Protocol = "TCP",
            });

            Assert.AreEqual(TrafficRouteAction.Proxy, decision.Action);
        }


        [TestMethod]
        public void AdminModeUsesManagedRoutingWhenAHostnameUrlIsAvailable()
        {
            TrafficPolicyEngine policy = new(new Configuration());
            FilterEngine filters = FilterEngine.Compile(
                new[] { "||proxy.example^", "@@||direct.example^" },
                System.Array.Empty<string>(),
                out FilterCompilationReport report);
            policy.UpdateManagedRouting(ManagedRoutingSnapshot.LocalRules(filters, report));

            RouteDecision decision = policy.EvaluateAdminMode(
                new TrafficContext
                {
                    DestinationHost = "proxy.example",
                    DestinationUrl = "https://proxy.example/",
                    DestinationPort = 443,
                    Protocol = "TCP",
                },
                TrafficRouteAction.Direct);

            Assert.AreEqual(TrafficRouteAction.Proxy, decision.Action);
            StringAssert.Contains(decision.Reason, "managed routing generation");
        }


        [TestMethod]
        public void ManagedRoutingBuilderPreservesGlobalAndOnlinePacCompatibilityModes()
        {
            Configuration global = new()
            {
                Enabled = true,
                global = true,
            };
            Configuration onlinePac = new()
            {
                Enabled = true,
                global = false,
                useOnlinePac = true,
            };

            ManagedRoutingSnapshot globalSnapshot = ManagedRoutingSnapshotBuilder.Build(global);
            ManagedRoutingSnapshot onlineSnapshot = ManagedRoutingSnapshotBuilder.Build(onlinePac);

            Assert.AreEqual(ManagedRoutingSnapshotMode.Disabled, globalSnapshot.Mode);
            Assert.AreEqual(ManagedRoutingSnapshotMode.Disabled, onlineSnapshot.Mode);
            StringAssert.Contains(globalSnapshot.Source, "global mode");
            StringAssert.Contains(onlineSnapshot.Source, "online PAC");
        }


        [TestMethod]
        public void ManagedHttpRoutingUrlPreservesHttpPathAndConnectAuthority()
        {
            Assert.AreEqual(
                "http://example.com/path?q=1",
                ManagedHttpProxyService.BuildRoutingUrlForPolicy(
                    "http://example.com/path?q=1", "example.com", 80, false));
            Assert.AreEqual(
                "http://example.com/origin",
                ManagedHttpProxyService.BuildRoutingUrlForPolicy(
                    "/origin", "example.com", 80, false));
            Assert.AreEqual(
                "https://example.com:8443/",
                ManagedHttpProxyService.BuildRoutingUrlForPolicy(
                    "example.com:8443", "example.com", 8443, true));
            Assert.AreEqual(
                "https://[2001:db8::1]/",
                ManagedHttpProxyService.BuildRoutingUrlForPolicy(
                    "[2001:db8::1]:443", "2001:db8::1", 443, true));
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

        private sealed class ZeroHashIpEndPoint : IPEndPoint
        {
            public ZeroHashIpEndPoint()
                : base(IPAddress.Loopback, 12345)
            {
            }

            public override int GetHashCode() => 0;
        }

        private sealed class NegativeHashIpEndPoint : IPEndPoint
        {
            public NegativeHashIpEndPoint()
                : base(IPAddress.Loopback, 12345)
            {
            }

            public override int GetHashCode() => -1;
        }
    }
}
