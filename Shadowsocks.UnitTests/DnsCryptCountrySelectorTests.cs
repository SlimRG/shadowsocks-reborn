#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller.Service;
using Shadowsocks.Controller.Traffic;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class DnsCryptCountrySelectorTests
    {
        [TestMethod]
        public async Task AutomaticSelectionKeepsResolverInServerCountry()
        {
            var resolvers = new List<DnsCryptResolverInfo>
            {
                new("de-one", "DNSCrypt", false, true, true, true, "Hosted in Germany.", new[] { "203.0.113.10" }),
                new("us-one", "DNSCrypt", false, true, true, true, "Hosted in the United States.", new[] { "198.51.100.10" }),
            };
            var countries = new List<IpCountryInfo> { new("DE", "Germany", "🇩🇪", "192.0.2.1") };
            var config = new DnsCryptConfig();

            IReadOnlyList<string> selected = await DnsCryptCountrySelector.SelectAsync(
                resolvers, countries, config,
                (host, _) => Task.FromResult(host.StartsWith("203.", StringComparison.Ordinal)
                    ? new IpCountryInfo("DE", "Germany", "🇩🇪", host)
                    : new IpCountryInfo("US", "United States", "🇺🇸", host)),
                CancellationToken.None);

            Assert.IsTrue(selected.Contains("de-one", StringComparer.OrdinalIgnoreCase));
            Assert.IsFalse(selected.Contains("us-one", StringComparer.OrdinalIgnoreCase));
        }


        [TestMethod]
        public async Task AutomaticSelectionCoversAllServerCountries()
        {
            var resolvers = new List<DnsCryptResolverInfo>
            {
                new("de-one", "DNSCrypt", false, true, true, true, "Hosted in Germany.", new[] { "203.0.113.10:443" }),
                new("us-one", "DNSCrypt", false, true, true, true, "Hosted in the United States.", new[] { "198.51.100.10:443" }),
                new("fr-one", "DNSCrypt", false, true, true, true, "Hosted in France.", new[] { "192.0.2.20:443" }),
            };
            var countries = new List<IpCountryInfo>
            {
                new("DE", "Germany", "🇩🇪", "192.0.2.1"),
                new("US", "United States", "🇺🇸", "192.0.2.2"),
            };
            var config = new DnsCryptConfig();

            IReadOnlyList<string> selected = await DnsCryptCountrySelector.SelectAsync(
                resolvers, countries, config,
                (host, _) => Task.FromResult<IpCountryInfo?>(host.StartsWith("203.", StringComparison.Ordinal)
                    ? new IpCountryInfo("DE", "Germany", "🇩🇪", host)
                    : host.StartsWith("198.", StringComparison.Ordinal)
                        ? new IpCountryInfo("US", "United States", "🇺🇸", host)
                        : new IpCountryInfo("FR", "France", "🇫🇷", host)),
                CancellationToken.None);

            Assert.IsTrue(selected.Contains("de-one", StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(selected.Contains("us-one", StringComparer.OrdinalIgnoreCase));
            Assert.IsFalse(selected.Contains("fr-one", StringComparer.OrdinalIgnoreCase));
        }

        [TestMethod]
        public async Task CountryCodeDoesNotMatchCommonDescriptionWord()
        {
            var resolvers = new List<DnsCryptResolverInfo>
            {
                new("fr-one", "DNSCrypt", false, true, true, true, "Fast resolver in France.", Array.Empty<string>()),
            };
            var countries = new List<IpCountryInfo> { new("IN", "India", "🇮🇳", "192.0.2.3") };

            IReadOnlyList<string> selected = await DnsCryptCountrySelector.SelectAsync(
                resolvers, countries, new DnsCryptConfig(),
                (_, _) => Task.FromResult<IpCountryInfo?>(null),
                CancellationToken.None);

            Assert.AreEqual(0, selected.Count);
        }

        [TestMethod]
        public async Task AutomaticSelectionUsesEnrichedCountryAndPrefersLowerLatency()
        {
            var resolvers = new List<DnsCryptResolverInfo>
            {
                new("de-slow", "DNSCrypt", false, true, true, true, "", Array.Empty<string>(), "DE", "Germany", "🇩🇪", 90),
                new("de-fast", "DNSCrypt", false, true, true, true, "", Array.Empty<string>(), "DE", "Germany", "🇩🇪", 18),
                new("us-fast", "DNSCrypt", false, true, true, true, "", Array.Empty<string>(), "US", "United States", "🇺🇸", 5),
            };
            var countries = new List<IpCountryInfo> { new("DE", "Germany", "🇩🇪", "192.0.2.1") };

            IReadOnlyList<string> selected = await DnsCryptCountrySelector.SelectAsync(
                resolvers, countries, new DnsCryptConfig(),
                (_, _) => Task.FromResult<IpCountryInfo?>(null),
                CancellationToken.None);

            Assert.AreEqual(1, selected.Count);
            Assert.AreEqual("de-fast", selected[0]);
            Assert.IsFalse(selected.Contains("de-slow", StringComparer.OrdinalIgnoreCase));
            Assert.IsFalse(selected.Contains("us-fast", StringComparer.OrdinalIgnoreCase));
        }

        [TestMethod]
        public async Task AutomaticSelectionNeverUsesDohResolvers()
        {
            var resolvers = new List<DnsCryptResolverInfo>
            {
                new("de-doh-fast", "DoH", false, true, true, true, "Hosted in Germany.", Array.Empty<string>(), "DE", "Germany", "🇩🇪", 5),
                new("de-dnscrypt", "DNSCrypt", false, true, true, true, "Hosted in Germany.", Array.Empty<string>(), "DE", "Germany", "🇩🇪", 35),
            };
            var countries = new List<IpCountryInfo> { new("DE", "Germany", "🇩🇪", "192.0.2.1") };
            var config = new DnsCryptConfig { routeThroughShadowsocks = true };

            IReadOnlyList<string> selected = await DnsCryptCountrySelector.SelectAsync(
                resolvers, countries, config,
                (_, _) => Task.FromResult<IpCountryInfo?>(null),
                CancellationToken.None);

            Assert.AreEqual(1, selected.Count);
            Assert.AreEqual("de-dnscrypt", selected[0]);
        }


        [TestMethod]
        public async Task MoscowResolverCountryComesFromEndpointIpOnly()
        {
            var resolver = new DnsCryptResolverInfo(
                "totally-unrelated-name",
                "DNSCrypt",
                false,
                true,
                true,
                true,
                "Description intentionally contains no location.",
                new[] { "mow01.dnscry.pt", "93.183.106.222:443" });

            IpCountryInfo? located = await DnsCryptCountrySelector.ResolveResolverCountryByEndpointAsync(
                resolver,
                (host, _) => Task.FromResult<IpCountryInfo?>(host == "93.183.106.222"
                    ? new IpCountryInfo("RU", "Russia", "🇷🇺", host)
                    : new IpCountryInfo("US", "United States", "🇺🇸", host)),
                CancellationToken.None);

            Assert.IsNotNull(located);
            Assert.AreEqual("RU", located.CountryCode);
            Assert.AreEqual("Russia", located.CountryName);
            Assert.AreEqual("93.183.106.222", located.Address);
        }

        [TestMethod]
        public async Task ResolverNameAndDescriptionNeverDefineCountry()
        {
            var resolvers = new List<DnsCryptResolverInfo>
            {
                new("dnscry.pt-moscow-ipv4", "DNSCrypt", false, true, true, true,
                    "Moscow Russia resolver.", new[] { "198.51.100.77" }),
            };
            var countries = new List<IpCountryInfo>
            {
                new("RU", "Russia", "🇷🇺", "192.0.2.1"),
            };

            IReadOnlyList<string> selected = await DnsCryptCountrySelector.SelectAsync(
                resolvers,
                countries,
                new DnsCryptConfig(),
                (host, _) => Task.FromResult<IpCountryInfo?>(
                    new IpCountryInfo("US", "United States", "🇺🇸", host)),
                CancellationToken.None);

            Assert.AreEqual(0, selected.Count);
        }


        [TestMethod]
        public async Task ResolverWithEndpointsInDifferentCountriesHasNoSingleCountry()
        {
            var resolver = new DnsCryptResolverInfo(
                "multi-endpoint",
                "DoH",
                false,
                true,
                true,
                true,
                string.Empty,
                new[] { "203.0.113.10", "198.51.100.10" });

            IpCountryInfo? located = await DnsCryptCountrySelector.ResolveResolverCountryByEndpointAsync(
                resolver,
                (host, _) => Task.FromResult<IpCountryInfo?>(host.StartsWith("203.", StringComparison.Ordinal)
                    ? new IpCountryInfo("DE", "Germany", "🇩🇪", host)
                    : new IpCountryInfo("US", "United States", "🇺🇸", host)),
                CancellationToken.None);

            Assert.IsNull(located);
        }

        [TestMethod]
        public void AutomaticFallbackAppliesResolverPrivacyFilters()
        {
            var resolvers = new List<DnsCryptResolverInfo>
            {
                new("fast-logs", "DNSCrypt", false, true, false, true, "", Array.Empty<string>(), LatencyMs: 1),
                new("filtered", "DNSCrypt", false, true, true, false, "", Array.Empty<string>(), LatencyMs: 2),
                new("eligible", "DoH", false, true, true, true, "", Array.Empty<string>(), LatencyMs: 20),
            };
            var config = new DnsCryptConfig
            {
                requireDnssec = true,
                requireNoLog = true,
                requireNoFilter = true,
                ipv4Servers = true,
                ipv6Servers = false,
            };

            IReadOnlyList<string> selected = DnsCryptCountrySelector.SelectFallback(resolvers, config);

            Assert.AreEqual(1, selected.Count);
            Assert.AreEqual("eligible", selected[0]);
        }

        [TestMethod]
        public void ResolverEndpointNormalizationRemovesPortAndIpv6Brackets()
        {
            Assert.AreEqual("1.2.3.4", IpCountryService.NormalizeHost("1.2.3.4:443"));
            Assert.AreEqual("2001:db8::1", IpCountryService.NormalizeHost("[2001:db8::1]:443"));
            Assert.AreEqual("resolver.example", IpCountryService.NormalizeHost("resolver.example:443"));
        }

        [TestMethod]
        public void CountryCodeProducesFlagEmoji()
        {
            Assert.AreEqual("🇩🇪", IpCountryService.CountryCodeToFlag("DE"));
            Assert.AreEqual("🇺🇸", IpCountryService.CountryCodeToFlag("us"));
            Assert.AreEqual(string.Empty, IpCountryService.CountryCodeToFlag("USA"));
        }
    }
}
