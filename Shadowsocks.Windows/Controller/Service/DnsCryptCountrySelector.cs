#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shadowsocks.Controller.Traffic;

namespace Shadowsocks.Controller.Service
{
    internal static class DnsCryptCountrySelector
    {
        public static async Task<IReadOnlyList<string>> SelectAsync(
            IReadOnlyList<DnsCryptResolverInfo> resolvers,
            IReadOnlyList<IpCountryInfo> targetCountries,
            DnsCryptConfig config,
            Func<string, CancellationToken, Task<IpCountryInfo?>> countryLookup,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(resolvers);
            ArgumentNullException.ThrowIfNull(targetCountries);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(countryLookup);

            DnsCryptResolverInfo[] compatible = OrderCompatibleResolvers(resolvers, config).ToArray();
            var selected = new List<string>();

            foreach (IpCountryInfo target in targetCountries
                         .Where(item => !string.IsNullOrWhiteSpace(item.CountryCode))
                         .GroupBy(item => item.CountryCode, StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.First()))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // CountryCode on the resolver catalog is populated exclusively by GeoIP
                // from the resolver endpoint IP. Never use a resolver name, city token,
                // description, provider name, or other textual metadata as geography.
                DnsCryptResolverInfo? match = compatible.FirstOrDefault(resolver =>
                    !selected.Contains(resolver.Name, StringComparer.OrdinalIgnoreCase)
                    && string.Equals(resolver.CountryCode, target.CountryCode, StringComparison.OrdinalIgnoreCase));

                if (match is null)
                {
                    // Catalog enrichment is best-effort. If GeoIP was temporarily unavailable
                    // while the list was loaded, retry unresolved resolver endpoints here.
                    foreach (DnsCryptResolverInfo resolver in compatible.Where(resolver =>
                                 string.IsNullOrWhiteSpace(resolver.CountryCode)
                                 && !selected.Contains(resolver.Name, StringComparer.OrdinalIgnoreCase)))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        IpCountryInfo? located = await ResolveResolverCountryByEndpointAsync(
                            resolver, countryLookup, cancellationToken).ConfigureAwait(false);
                        if (located is null
                            || !string.Equals(located.CountryCode, target.CountryCode, StringComparison.OrdinalIgnoreCase))
                            continue;

                        match = resolver;
                        break;
                    }
                }

                if (match is not null)
                    selected.Add(match.Name);
            }

            return selected;
        }

        internal static bool IsCompatible(DnsCryptResolverInfo resolver, DnsCryptConfig config)
        {
            if (resolver is null || string.IsNullOrWhiteSpace(resolver.Name))
                return false;
            if (!IsSupportedProtocol(resolver.Protocol))
                return false;
            if (resolver.IPv6 && !config.ipv6Servers)
                return false;
            if (!resolver.IPv6 && !config.ipv4Servers)
                return false;
            if (config.requireDnssec && resolver.Dnssec != true)
                return false;
            if (config.requireNoLog && !resolver.NoLog)
                return false;
            if (config.requireNoFilter && !resolver.NoFilter)
                return false;
            return true;
        }

        private static bool IsDnsCryptProtocol(string? protocol)
            => !string.IsNullOrWhiteSpace(protocol) && protocol.Contains("DNSCrypt", StringComparison.OrdinalIgnoreCase);

        private static bool IsSupportedProtocol(string? protocol)
            => IsDnsCryptProtocol(protocol)
                || string.Equals(protocol?.Trim(), "DoH", StringComparison.OrdinalIgnoreCase);

        internal static IReadOnlyList<string> SelectFallback(
            IReadOnlyList<DnsCryptResolverInfo> resolvers,
            DnsCryptConfig config,
            int maxCount = 1)
        {
            ArgumentNullException.ThrowIfNull(resolvers);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);

            return OrderCompatibleResolvers(resolvers, config)
                .Select(item => item.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(maxCount)
                .ToArray();
        }

        private static IEnumerable<DnsCryptResolverInfo> OrderCompatibleResolvers(
            IReadOnlyList<DnsCryptResolverInfo> resolvers,
            DnsCryptConfig config)
        {
            IEnumerable<DnsCryptResolverInfo> compatible = resolvers.Where(item => IsCompatible(item, config));
            return config.routeThroughShadowsocks
                ? compatible
                    .OrderBy(item => IsDnsCryptProtocol(item.Protocol) ? 0 : 1)
                    .ThenBy(item => item.LatencyMs ?? int.MaxValue)
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                : compatible
                    .OrderBy(item => item.LatencyMs ?? int.MaxValue)
                    .ThenBy(item => IsDnsCryptProtocol(item.Protocol) ? 0 : 1)
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
        }

        internal static async Task<IpCountryInfo?> ResolveResolverCountryByEndpointAsync(
            DnsCryptResolverInfo resolver,
            Func<string, CancellationToken, Task<IpCountryInfo?>> countryLookup,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(resolver);
            ArgumentNullException.ThrowIfNull(countryLookup);

            string[] allHosts = (resolver.Addresses ?? Array.Empty<string>())
                .Where(address => !string.IsNullOrWhiteSpace(address))
                .Select(IpCountryService.NormalizeHost)
                .Where(host => !string.IsNullOrWhiteSpace(host))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] literalHosts = allHosts
                .Where(host => System.Net.IPAddress.TryParse(host, out _))
                .ToArray();
            string[] hosts = literalHosts.Length > 0 ? literalHosts : allHosts;

            var located = new List<IpCountryInfo>();
            foreach (string host in hosts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IpCountryInfo? country = await countryLookup(host, cancellationToken).ConfigureAwait(false);
                if (country is not null)
                    located.Add(country);
            }

            string[] countryCodes = located
                .Select(country => country.CountryCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return countryCodes.Length == 1
                ? located.First(country => string.Equals(
                    country.CountryCode, countryCodes[0], StringComparison.OrdinalIgnoreCase))
                : null;
        }
    }
}
