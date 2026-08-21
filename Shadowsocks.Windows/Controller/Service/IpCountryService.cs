#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Shadowsocks.Core;

namespace Shadowsocks.Controller.Service
{
    public sealed record IpCountryInfo(string CountryCode, string CountryName, string FlagEmoji, string Address);

    internal static class IpCountryService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly ConcurrentDictionary<string, IpCountryInfo> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTimeOffset> NegativeCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim LookupGate = new(4, 4);
        private static readonly HttpClient DirectClient = CreateClient(useProxy: false);
        private static readonly HttpClient ProxyFallbackClient = CreateClient(useProxy: true);
        private static readonly TimeSpan NegativeCacheLifetime = TimeSpan.FromMinutes(10);

        public static async Task<IpCountryInfo?> ResolveHostAsync(string host, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(host))
                return null;

            string normalized = NormalizeHost(host);
            if (string.IsNullOrWhiteSpace(normalized))
                return null;
            if (IPAddress.TryParse(normalized, out IPAddress? parsed))
                return await LookupAddressAsync(parsed, cancellationToken).ConfigureAwait(false);

            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(normalized).WaitAsync(cancellationToken).ConfigureAwait(false);
                IPAddress? address = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
                    ?? addresses.FirstOrDefault();
                return address is null ? null : await LookupAddressAsync(address, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                Logger.Debug(exception, $"IP country lookup could not resolve host '{normalized}'.");
                return null;
            }
        }

        public static async Task<IpCountryInfo?> LookupAddressAsync(IPAddress address, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(address);
            if (!IsPublicAddress(address))
                return null;

            string key = address.ToString();
            if (Cache.TryGetValue(key, out IpCountryInfo? cached))
                return cached;
            if (NegativeCache.TryGetValue(key, out DateTimeOffset failedAt)
                && DateTimeOffset.UtcNow - failedAt < NegativeCacheLifetime)
                return null;

            await LookupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Cache.TryGetValue(key, out cached))
                    return cached;

                IpCountryInfo? info = await LookupCountryIsAsync(key, cancellationToken).ConfigureAwait(false)
                    ?? await LookupIpWhoAsync(key, cancellationToken).ConfigureAwait(false);
                if (info is not null)
                {
                    Cache[key] = info;
                    NegativeCache.TryRemove(key, out _);
                    return info;
                }

                NegativeCache[key] = DateTimeOffset.UtcNow;
                Logger.Debug($"IP country lookup returned no result for {key}.");
                return null;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                NegativeCache[key] = DateTimeOffset.UtcNow;
                Logger.Debug(exception, $"IP country lookup failed for {key}.");
                return null;
            }
            finally
            {
                LookupGate.Release();
            }
        }

        public static async Task<IReadOnlyDictionary<string, IpCountryInfo>> LookupAddressesAsync(
            IEnumerable<IPAddress> addresses,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(addresses);
            string[] keys = addresses
                .Where(address => address is not null && IsPublicAddress(address))
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var result = new Dictionary<string, IpCountryInfo>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            foreach (string key in keys)
            {
                if (Cache.TryGetValue(key, out IpCountryInfo? cached))
                {
                    result[key] = cached;
                    continue;
                }
                if (NegativeCache.TryGetValue(key, out DateTimeOffset failedAt)
                    && DateTimeOffset.UtcNow - failedAt < NegativeCacheLifetime)
                    continue;
                missing.Add(key);
            }

            string[][] chunks = missing.Chunk(100).ToArray();
            Task<IReadOnlyDictionary<string, IpCountryInfo>>[] batchTasks = chunks
                .Select(chunk => LookupCountryIsBatchAsync(chunk, cancellationToken))
                .ToArray();
            IReadOnlyDictionary<string, IpCountryInfo>[] batches = await Task.WhenAll(batchTasks).ConfigureAwait(false);

            for (int index = 0; index < chunks.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] chunk = chunks[index];
                IReadOnlyDictionary<string, IpCountryInfo> batch = batches[index];
                foreach ((string key, IpCountryInfo info) in batch)
                {
                    Cache[key] = info;
                    NegativeCache.TryRemove(key, out _);
                    result[key] = info;
                }

                // If the primary batch endpoint failed completely, do not fan out into
                // up to 100 secondary HTTP requests. Leave the entries uncached so a later
                // catalog refresh or the targeted Automatic-selection fallback can retry.
                if (batch.Count == 0)
                    continue;

                // For a partial batch response, fall back only for the missing IPs.
                // Country classification must never be inferred from resolver text.
                foreach (string key in chunk.Where(key => !batch.ContainsKey(key)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IpCountryInfo? info;
                    try
                    {
                        info = await LookupIpWhoAsync(key, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        Logger.Debug(exception, $"Secondary IP country lookup failed for {key}.");
                        info = null;
                    }

                    if (info is not null)
                    {
                        Cache[key] = info;
                        NegativeCache.TryRemove(key, out _);
                        result[key] = info;
                    }
                    else
                    {
                        NegativeCache[key] = DateTimeOffset.UtcNow;
                    }
                }
            }

            return result;
        }

        private static async Task<IpCountryInfo?> LookupCountryIsAsync(string address, CancellationToken cancellationToken)
        {
            string? json = await GetStringWithFallbackAsync(
                $"https://api.country.is/{Uri.EscapeDataString(address)}",
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return null;
            CountryIsResponse? dto = JsonSerializer.Deserialize<CountryIsResponse>(json);
            return CreateInfo(dto?.Country, address);
        }

        private static async Task<IReadOnlyDictionary<string, IpCountryInfo>> LookupCountryIsBatchAsync(
            IReadOnlyList<string> addresses,
            CancellationToken cancellationToken)
        {
            if (addresses.Count == 0)
                return new Dictionary<string, IpCountryInfo>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string payload = JsonSerializer.Serialize(addresses);
                string? json = await PostStringWithFallbackAsync(
                    "https://api.country.is/", payload, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                    return new Dictionary<string, IpCountryInfo>(StringComparer.OrdinalIgnoreCase);
                List<CountryIsResponse>? rows = JsonSerializer.Deserialize<List<CountryIsResponse>>(json);
                var result = new Dictionary<string, IpCountryInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (CountryIsResponse row in rows ?? [])
                {
                    if (string.IsNullOrWhiteSpace(row.Ip))
                        continue;
                    IpCountryInfo? info = CreateInfo(row.Country, row.Ip);
                    if (info is not null)
                        result[row.Ip] = info;
                }
                return result;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                Logger.Debug(exception, "Batch IP country lookup failed.");
                return new Dictionary<string, IpCountryInfo>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static async Task<IpCountryInfo?> LookupIpWhoAsync(string address, CancellationToken cancellationToken)
        {
            string? json = await GetStringWithFallbackAsync(
                $"https://ipwho.is/{Uri.EscapeDataString(address)}?fields=success,country,country_code",
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return null;
            IpWhoResponse? dto = JsonSerializer.Deserialize<IpWhoResponse>(json);
            if (dto?.Success != true || string.IsNullOrWhiteSpace(dto.CountryCode))
                return null;

            string code = dto.CountryCode.Trim().ToUpperInvariant();
            return new IpCountryInfo(code, dto.Country?.Trim() ?? CountryCodeToName(code), CountryCodeToFlag(code), address);
        }

        private static IpCountryInfo? CreateInfo(string? countryCode, string address)
        {
            if (string.IsNullOrWhiteSpace(countryCode))
                return null;
            string code = countryCode.Trim().ToUpperInvariant();
            string flag = CountryCodeToFlag(code);
            if (flag.Length == 0)
                return null;
            return new IpCountryInfo(code, CountryCodeToName(code), flag, address);
        }

        internal static string NormalizeHost(string? host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return string.Empty;

            string value = host.Trim();
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                int closingBracket = value.IndexOf(']');
                if (closingBracket > 1)
                    return value[1..closingBracket];
            }

            if (IPAddress.TryParse(value, out _))
                return value;

            if (Uri.TryCreate($"tcp://{value}", UriKind.Absolute, out Uri? endpointUri)
                && !string.IsNullOrWhiteSpace(endpointUri.Host))
                return endpointUri.Host;

            return value;
        }

        internal static string CountryCodeToFlag(string? countryCode)
        {
            if (string.IsNullOrWhiteSpace(countryCode))
                return string.Empty;
            string code = countryCode.Trim().ToUpperInvariant();
            if (code.Length != 2 || code.Any(ch => ch < 'A' || ch > 'Z'))
                return string.Empty;
            return string.Concat(code.Select(ch => char.ConvertFromUtf32(0x1F1E6 + ch - 'A')));
        }

        internal static string CountryCodeToName(string countryCode)
        {
            try
            {
                return new RegionInfo(countryCode).EnglishName;
            }
            catch (ArgumentException)
            {
                return countryCode;
            }
        }

        internal static bool IsPublicAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
                return false;
            if (address.AddressFamily != AddressFamily.InterNetwork)
                return true;

            byte[] bytes = address.GetAddressBytes();
            if (bytes[0] == 10 || bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31))
                return false;
            return true;
        }

        private static async Task<string?> GetStringWithFallbackAsync(string uri, CancellationToken cancellationToken)
        {
            foreach (HttpClient client in new[] { DirectClient, ProxyFallbackClient })
            {
                try
                {
                    using HttpResponseMessage response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    Logger.Debug(exception, $"Country metadata request failed: {uri}");
                }
            }
            return null;
        }

        private static async Task<string?> PostStringWithFallbackAsync(
            string uri,
            string payload,
            CancellationToken cancellationToken)
        {
            foreach (HttpClient client in new[] { DirectClient, ProxyFallbackClient })
            {
                try
                {
                    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = await client.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    Logger.Debug(exception, $"Country metadata batch request failed: {uri}");
                }
            }
            return null;
        }

        private static HttpClient CreateClient(bool useProxy)
        {
            var handler = new SocketsHttpHandler { UseProxy = useProxy };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"Shadowsocks-Reborn/{ApplicationInfo.Version}");
            return client;
        }

        private sealed class CountryIsResponse
        {
            [JsonPropertyName("ip")]
            public string? Ip { get; set; }
            [JsonPropertyName("country")]
            public string? Country { get; set; }
        }

        private sealed class IpWhoResponse
        {
            [JsonPropertyName("success")]
            public bool Success { get; set; }
            [JsonPropertyName("country")]
            public string? Country { get; set; }
            [JsonPropertyName("country_code")]
            public string? CountryCode { get; set; }
        }
    }
}
