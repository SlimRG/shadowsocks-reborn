using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Routing;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class FilterEngineTests
    {
        [TestMethod]
        public void BlockingAndExceptionRulesMapToProxyAndDirect()
        {
            FilterEngine engine = FilterEngine.Compile(
                new[] { "||blocked.example^", "@@||direct.example^" },
                Array.Empty<string>());

            Assert.AreEqual(FilterRoutingAction.Proxy, engine.Evaluate("https://blocked.example/a", "blocked.example").Action);
            Assert.AreEqual(FilterRoutingAction.Direct, engine.Evaluate("https://direct.example/a", "direct.example").Action);
            Assert.AreEqual(FilterRoutingAction.Direct, engine.Evaluate("https://other.example/a", "other.example").Action);
        }

        [TestMethod]
        public void UserRulesHavePriorityOverGeneratedRules()
        {
            FilterEngine proxyOverride = FilterEngine.Compile(
                new[] { "@@||example.com^" },
                new[] { "||example.com^" });
            FilterEngine directOverride = FilterEngine.Compile(
                new[] { "||example.com^" },
                new[] { "@@||example.com^" });

            FilterRoutingDecision proxy = proxyOverride.Evaluate("https://example.com/", "example.com");
            FilterRoutingDecision direct = directOverride.Evaluate("https://example.com/", "example.com");

            Assert.AreEqual(FilterRoutingAction.Proxy, proxy.Action);
            Assert.AreEqual(FilterRuleSource.User, proxy.Source);
            Assert.AreEqual(FilterRoutingAction.Direct, direct.Action);
            Assert.AreEqual(FilterRuleSource.User, direct.Source);
        }

        [TestMethod]
        public void ExceptionWinsWithinSameRuleSet()
        {
            FilterEngine engine = FilterEngine.Compile(
                new[] { "||example.com^", "@@||example.com/safe^" },
                Array.Empty<string>());

            FilterRoutingDecision decision = engine.Evaluate("https://example.com/safe/file.js", "example.com");

            Assert.AreEqual(FilterRoutingAction.Direct, decision.Action);
            Assert.AreEqual("@@||example.com/safe^", decision.MatchedRule);
        }

        [TestMethod]
        public void DomainAnchorMatchesSubdomainsButNotLookalikes()
        {
            FilterEngine engine = FilterEngine.Compile(new[] { "||example.com^" }, Array.Empty<string>());

            Assert.AreEqual(FilterRoutingAction.Proxy, engine.Evaluate("https://example.com/a", "example.com").Action);
            Assert.AreEqual(FilterRoutingAction.Proxy, engine.Evaluate("https://cdn.example.com/a", "cdn.example.com").Action);
            Assert.AreEqual(FilterRoutingAction.Direct, engine.Evaluate("https://notexample.com/a", "notexample.com").Action);
        }

        [TestMethod]
        public void HostCanBeDerivedFromAbsoluteUrl()
        {
            FilterEngine engine = FilterEngine.Compile(
                new[] { "||derived.example^" },
                Array.Empty<string>());

            FilterRoutingDecision decision = engine.Evaluate("https://cdn.derived.example/path");

            Assert.AreEqual(FilterRoutingAction.Proxy, decision.Action);
            Assert.AreEqual(FilterRuleSource.Default, decision.Source);
        }

        [TestMethod]
        public void SimpleDomainRulesUseOptimizedHostSuffixPath()
        {
            _ = FilterEngine.Compile(
                new[] { "||one.example^", "||two.example", "/complex.*/" },
                Array.Empty<string>(),
                out FilterCompilationReport report);

            Assert.AreEqual(3, report.DefaultRuleCount);
            Assert.AreEqual(2, report.OptimizedDomainRuleCount);
            Assert.AreEqual(1, report.UnindexedRuleCount);
        }

        [TestMethod]
        public void FullAnchorAndWildcardFollowAbpNetworkFilterSemantics()
        {
            FilterEngine engine = FilterEngine.Compile(
                new[] { "|https://exact.example/path|", "ads.example/*/banner" },
                Array.Empty<string>());

            Assert.AreEqual(FilterRoutingAction.Proxy, engine.Evaluate("https://exact.example/path", "exact.example").Action);
            Assert.AreEqual(FilterRoutingAction.Direct, engine.Evaluate("https://exact.example/path/more", "exact.example").Action);
            Assert.AreEqual(FilterRoutingAction.Proxy, engine.Evaluate("https://ads.example/a/b/banner", "ads.example").Action);
        }

        [TestMethod]
        public void ConsecutiveWildcardsPreserveAbpNetworkFilterSemantics()
        {
            FilterEngine engine = FilterEngine.Compile(
                new[] { "ads.example/**/banner" },
                Array.Empty<string>());

            Assert.AreEqual(
                FilterRoutingAction.Proxy,
                engine.Evaluate("https://ads.example/a/b/banner", "ads.example").Action);
        }

        [TestMethod]
        public void ExplicitRegexRulesAreSupported()
        {
            FilterEngine engine = FilterEngine.Compile(new[] { @"/^https?:\/\/([^\/]+\.)?regex\.example\/.*$/" }, Array.Empty<string>());

            Assert.AreEqual(FilterRoutingAction.Proxy, engine.Evaluate("https://cdn.regex.example/file", "cdn.regex.example").Action);
            Assert.AreEqual(FilterRoutingAction.Direct, engine.Evaluate("https://regex.invalid/file", "regex.invalid").Action);
        }

        [TestMethod]
        public void DomainOptionSupportsIncludesExcludesAndParentDomains()
        {
            FilterEngine engine = FilterEngine.Compile(
                new[] { "/ads/$domain=example.com|~safe.example.com" },
                Array.Empty<string>());

            Assert.AreEqual(FilterRoutingAction.Proxy, engine.Evaluate("https://cdn.test/ads/x", "www.example.com").Action);
            Assert.AreEqual(FilterRoutingAction.Direct, engine.Evaluate("https://cdn.test/ads/x", "safe.example.com").Action);
            Assert.AreEqual(FilterRoutingAction.Direct, engine.Evaluate("https://cdn.test/ads/x", "child.safe.example.com").Action);
            Assert.AreEqual(FilterRoutingAction.Direct, engine.Evaluate("https://cdn.test/ads/x", "other.test").Action);
        }

        [TestMethod]
        public void ShadowsocksDomainRuleNormalizationKeepsOptionsValid()
        {
            FilterEngine engine = FilterEngine.Compile(
                new[] { "||example.com$domain=example.com" },
                Array.Empty<string>(),
                out FilterCompilationReport report);

            Assert.AreEqual(0, report.InvalidRuleCount);
            Assert.AreEqual(FilterRoutingAction.Proxy, engine.Evaluate("https://example.com/path", "example.com").Action);
            Assert.AreEqual(FilterRoutingAction.Direct, engine.Evaluate("https://example.com.evil/path", "example.com.evil").Action);
        }

        [TestMethod]
        public void MatchCaseOptionIsHonored()
        {
            FilterEngine engine = FilterEngine.Compile(new[] { "CaseSensitive$match-case" }, Array.Empty<string>());

            Assert.AreEqual(FilterRoutingAction.Proxy, engine.Evaluate("https://example/CaseSensitive", "example").Action);
            Assert.AreEqual(FilterRoutingAction.Direct, engine.Evaluate("https://example/casesensitive", "example").Action);
        }

        [TestMethod]
        public void CompatibilityContentOptionsDoNotInvalidateNetworkRule()
        {
            FilterEngine engine = FilterEngine.Compile(new[] { "||example.com^$script,third-party" }, Array.Empty<string>(), out FilterCompilationReport report);

            Assert.AreEqual(0, report.InvalidRuleCount);
            Assert.AreEqual(FilterRoutingAction.Proxy, engine.Evaluate("https://example.com/app.js", "example.com").Action);
        }

        [TestMethod]
        public void SiteKeyRulesRemainInactiveInNetworkOnlyRouting()
        {
            FilterEngine engine = FilterEngine.Compile(new[] { "||example.com^$sitekey=ABC123" }, Array.Empty<string>(), out FilterCompilationReport report);

            Assert.AreEqual(0, report.InvalidRuleCount);
            Assert.AreEqual(FilterRoutingAction.Direct, engine.Evaluate("https://example.com/", "example.com").Action);
        }

        [TestMethod]
        public void UnsupportedOptionsAreReportedAndIgnored()
        {
            FilterEngine engine = FilterEngine.Compile(
                new[] { "||bad.example^$made-up-option", "||good.example^" },
                Array.Empty<string>(),
                out FilterCompilationReport report);

            Assert.AreEqual(1, report.InvalidRuleCount);
            StringAssert.Contains(report.InvalidRules[0].Reason, "unsupported option");
            Assert.AreEqual(FilterRoutingAction.Direct, engine.Evaluate("https://bad.example/", "bad.example").Action);
            Assert.AreEqual(FilterRoutingAction.Proxy, engine.Evaluate("https://good.example/", "good.example").Action);
        }

        [TestMethod]
        public void ParseRuleLinesMatchesUserRuleFileContract()
        {
            string content = "! comment\r\n[Adblock Plus 2.0]\r\n\r\n||proxy.example^\r\n@@||direct.example^\r\n";

            IReadOnlyList<string> rules = FilterEngine.ParseRuleLines(content);

            CollectionAssert.AreEqual(
                new[] { "||proxy.example^", "@@||direct.example^" },
                new List<string>(rules));
        }

        [TestMethod]
        public void GeneratedGeositeRuleShapesAreCompatible()
        {
            FilterEngine blacklist = FilterEngine.Compile(
                new[] { "plain-token", "||domain.example", "|http://full.example", "|https://full.example", @"/regex\d+\.example/" },
                Array.Empty<string>());

            Assert.AreEqual(FilterRoutingAction.Proxy, blacklist.Evaluate("https://site/plain-token/path", "site").Action);
            Assert.AreEqual(FilterRoutingAction.Proxy, blacklist.Evaluate("https://cdn.domain.example/path", "cdn.domain.example").Action);
            Assert.AreEqual(FilterRoutingAction.Direct, blacklist.Evaluate("https://domain.example.evil/path", "domain.example.evil").Action);
            Assert.AreEqual(FilterRoutingAction.Proxy, blacklist.Evaluate("https://full.example/path", "full.example").Action);
            Assert.AreEqual(FilterRoutingAction.Proxy, blacklist.Evaluate("https://regex12.example/path", "regex12.example").Action);

            FilterEngine whitelist = FilterEngine.Compile(new[] { "/.*/", "@@||direct.example" }, Array.Empty<string>());
            Assert.AreEqual(FilterRoutingAction.Direct, whitelist.Evaluate("https://direct.example/", "direct.example").Action);
            Assert.AreEqual(FilterRoutingAction.Proxy, whitelist.Evaluate("https://other.example/", "other.example").Action);
        }

        [TestMethod]
        public void PrivateIpv4RangesBypassRules()
        {
            FilterEngine engine = FilterEngine.Compile(new[] { "/.*/" }, Array.Empty<string>());

            Assert.AreEqual(FilterRuleSource.PrivateNetwork, engine.Evaluate("http://10.0.0.5/", "10.0.0.5").Source);
            Assert.AreEqual(FilterRuleSource.PrivateNetwork, engine.Evaluate("http://127.0.0.1/", "127.0.0.1").Source);
            Assert.AreEqual(FilterRuleSource.PrivateNetwork, engine.Evaluate("http://172.16.5.4/", "172.16.5.4").Source);
            Assert.AreEqual(FilterRuleSource.PrivateNetwork, engine.Evaluate("http://192.168.1.2/", "192.168.1.2").Source);
            Assert.AreEqual(FilterRoutingAction.Proxy, engine.Evaluate("http://8.8.8.8/", "8.8.8.8").Action);
        }

        [TestMethod]
        public void EmbeddedDefaultUserRuleFileCompilesWithoutInvalidRules()
        {
            IReadOnlyList<string> rules = FilterEngine.ParseRuleLines(Shadowsocks.Core.EmbeddedResources.UserRule);
            _ = FilterEngine.Compile(Array.Empty<string>(), rules, out FilterCompilationReport report);

            Assert.AreEqual(0, report.InvalidRuleCount);
            Assert.AreEqual(0, report.UserRuleCount);
        }
    }
}
