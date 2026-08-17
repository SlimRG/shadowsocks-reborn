using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;
using Shadowsocks.Model;
using Shadowsocks.Core.Storage;
using Shadowsocks.Core;

namespace Shadowsocks.Controller.Service
{
    public class GeositeResultEventArgs(bool success) : EventArgs
    {
        public bool Success = success;
    }

    public static class GeositeUpdater
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        private static readonly Lock databaseLock = new();
        private static readonly SemaphoreSlim updateLock = new(1, 1);

        public static event EventHandler<GeositeResultEventArgs> UpdateCompleted;
        public static event ErrorEventHandler Error;

        public const string DefaultSourceUrl = "https://github.com/v2fly/domain-list-community/releases/latest/download/dlc.dat";

        // fix28 used one shared dlc.dat cache. fix29 migrates it when there is exactly
        // one configured source, then keeps a separate cache per URL.
        private static readonly string LegacyDatabasePath = Path.Combine(Shadowsocks.Core.RuntimeEnvironment.WorkingDirectory, "dlc.dat");
        private static readonly string CacheDirectory = AppStoragePaths.GeositeCacheDirectory;
        private static readonly string AppliedSourcesPath = Path.Combine(CacheDirectory, "active-sources.sha256");

        private static List<string> configuredSources = [DefaultSourceUrl];
        private static string configuredSourcesFingerprint = GetSourcesFingerprint([DefaultSourceUrl]);
        private static HashSet<string> sourcesNeedingRefresh = new(StringComparer.OrdinalIgnoreCase);

        public static readonly Dictionary<string, IList<DomainObject>> Geosites =
            new(StringComparer.OrdinalIgnoreCase);

        public static bool IsDatabaseAvailable
        {
            get
            {
                lock (databaseLock)
                {
                    return Geosites.Count > 0;
                }
            }
        }

        public static bool NeedsRefresh
        {
            get
            {
                lock (databaseLock)
                {
                    return configuredSources.Count == 0 || sourcesNeedingRefresh.Count > 0;
                }
            }
        }

        public static bool IsSourceSetDirty
        {
            get
            {
                string expected;
                lock (databaseLock)
                {
                    expected = configuredSourcesFingerprint;
                }

                try
                {
                    return !File.Exists(AppliedSourcesPath) ||
                           !string.Equals(File.ReadAllText(AppliedSourcesPath).Trim(), expected, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return true;
                }
            }
        }

        /// <summary>
        /// Switch the in-memory GeoSite view to the caches belonging to the configured URLs.
        /// No network traffic is performed here. All databases are merged by group name.
        /// </summary>
        public static void ConfigureSources(IEnumerable<string> sources)
        {
            List<string> normalized = NormalizeSources(sources);
            Directory.CreateDirectory(CacheDirectory);
            TryMigrateLegacyCache(normalized);

            List<Dictionary<string, IList<DomainObject>>> databases = [];
            HashSet<string> needsRefresh = new(StringComparer.OrdinalIgnoreCase);
            foreach (string source in normalized)
            {
                string cachePath = GetCachePath(source);
                if (!File.Exists(cachePath))
                {
                    needsRefresh.Add(source);
                    continue;
                }

                try
                {
                    databases.Add(ParseGeositeList(File.ReadAllBytes(cachePath)));
                    logger.Info($"Loaded cached GeoSite source: {source}");
                }
                catch (Exception ex)
                {
                    needsRefresh.Add(source);
                    logger.Warn(ex, $"Cached GeoSite source is invalid and will be refreshed when Local PAC needs it: {source}");
                }
            }

            lock (databaseLock)
            {
                configuredSources = normalized;
                configuredSourcesFingerprint = GetSourcesFingerprint(normalized);
                sourcesNeedingRefresh = needsRefresh;
                ReplaceGeositesNoLock(MergeDatabases(databases));
            }
        }

        private static List<string> NormalizeSources(IEnumerable<string> sources)
        {
            List<string> normalized = (sources ?? [])
                .Select(source => source?.Trim())
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Where(source => Uri.TryCreate(source, UriKind.Absolute, out Uri uri) &&
                                 (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalized.Count == 0)
            {
                normalized.Add(DefaultSourceUrl);
            }

            return normalized;
        }

        private static void TryMigrateLegacyCache(IReadOnlyList<string> sources)
        {
            if (AppStoragePaths.IsCleanMode)
            {
                return;
            }

            if (sources.Count != 1 || !File.Exists(LegacyDatabasePath))
            {
                return;
            }

            string target = GetCachePath(sources[0]);
            if (File.Exists(target))
            {
                return;
            }

            try
            {
                byte[] legacy = File.ReadAllBytes(LegacyDatabasePath);
                ParseGeositeList(legacy); // validate before associating it with the source URL
                File.WriteAllBytes(target, legacy);
                File.Delete(LegacyDatabasePath);
                logger.Info($"Migrated legacy GeoSite cache to {target}.");
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Could not migrate the legacy dlc.dat cache; it will be ignored.");
            }
        }

        private static Dictionary<string, IList<DomainObject>> ParseGeositeList(byte[] database)
        {
            if (database == null || database.Length == 0)
            {
                throw new InvalidDataException("GeoSite database is empty.");
            }

            GeositeList list = GeositeList.Parser.ParseFrom(database);
            Dictionary<string, IList<DomainObject>> parsed = new(StringComparer.OrdinalIgnoreCase);
            foreach (Geosite item in list.Entries)
            {
                if (string.IsNullOrWhiteSpace(item.GroupName))
                {
                    continue;
                }

                parsed[item.GroupName.ToLowerInvariant()] = item.Domains.ToList();
            }

            if (parsed.Count == 0)
            {
                throw new InvalidDataException("GeoSite database contains no groups.");
            }

            return parsed;
        }

        private static Dictionary<string, IList<DomainObject>> MergeDatabases(
            IEnumerable<Dictionary<string, IList<DomainObject>>> databases)
        {
            Dictionary<string, IList<DomainObject>> merged = new(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, IList<DomainObject>> database in databases)
            {
                foreach (KeyValuePair<string, IList<DomainObject>> group in database)
                {
                    if (!merged.TryGetValue(group.Key, out IList<DomainObject> domains))
                    {
                        domains = [];
                        merged[group.Key] = domains;
                    }

                    foreach (DomainObject domain in group.Value)
                    {
                        domains.Add(domain);
                    }
                }
            }
            return merged;
        }

        private static void ReplaceGeositesNoLock(Dictionary<string, IList<DomainObject>> database)
        {
            Geosites.Clear();
            foreach (KeyValuePair<string, IList<DomainObject>> item in database)
            {
                Geosites[item.Key] = item.Value;
            }
        }

        private static string GetCachePath(string sourceUrl)
        {
            byte[] sourceHash = SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl));
            return Path.Combine(CacheDirectory, Convert.ToHexString(sourceHash) + ".dat");
        }

        private static string GetSourcesFingerprint(IEnumerable<string> sources)
        {
            string canonical = string.Join("\n", (sources ?? [])
                .Select(source => source?.Trim() ?? ""));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }

        private static void MarkCurrentSourceSetApplied()
        {
            string fingerprint;
            lock (databaseLock)
            {
                fingerprint = configuredSourcesFingerprint;
            }

            try
            {
                Directory.CreateDirectory(CacheDirectory);
                File.WriteAllText(AppliedSourcesPath, fingerprint, Encoding.ASCII);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Could not persist the active GeoSite source fingerprint.");
            }
        }

        public static string GetChecksumUrl(string sourceUrl)
        {
            Uri uri = new(sourceUrl, UriKind.Absolute);
            UriBuilder builder = new(uri)
            {
                Path = uri.AbsolutePath + ".sha256sum"
            };
            return builder.Uri.AbsoluteUri;
        }

        public static void ResetEvent()
        {
            UpdateCompleted = null;
            Error = null;
        }

        /// <summary>
        /// Download/update every configured GeoSite source through the already running
        /// local Shadowsocks connection, merge them, then regenerate Local PAC.
        /// A missing/unreachable .sha256sum is deliberately non-fatal.
        /// </summary>
        public static async Task<bool> UpdatePACFromGeosite(Configuration config, bool raiseEvents = true)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (config.useOnlinePac)
            {
                logger.Debug("Skipping GeoSite update because Online PAC is enabled.");
                return false;
            }

            List<string> sources = NormalizeSources(config.geositeUrls);
            bool blacklist = config.geositePreferDirect;

            await updateLock.WaitAsync();
            try
            {
                using HttpClient httpClient = LocalProxyHttpClient.Create(config);
                List<Dictionary<string, IList<DomainObject>>> loadedDatabases = [];
                List<string> failedSources = [];

                foreach (string source in sources)
                {
                    try
                    {
                        loadedDatabases.Add(await LoadOrRefreshSourceAsync(httpClient, source));
                    }
                    catch (Exception ex)
                    {
                        failedSources.Add(source);
                        logger.Warn(ex, $"GeoSite source could not be loaded: {source}");
                    }
                }

                if (loadedDatabases.Count == 0)
                {
                    throw new InvalidOperationException(
                        "None of the configured GeoSite sources could be loaded. " +
                        string.Join(", ", failedSources));
                }

                Dictionary<string, IList<DomainObject>> merged = MergeDatabases(loadedDatabases);
                lock (databaseLock)
                {
                    configuredSources = sources;
                    configuredSourcesFingerprint = GetSourcesFingerprint(sources);
                    sourcesNeedingRefresh = new(failedSources, StringComparer.OrdinalIgnoreCase);
                    ReplaceGeositesNoLock(merged);
                }

                LogInvalidConfiguredGroups(config);
                bool pacFileChanged = MergeAndWritePACFile(
                    config.geositeDirectGroups,
                    config.geositeProxiedGroups,
                    blacklist);

                if (failedSources.Count > 0)
                {
                    logger.Warn("Local PAC was regenerated from available GeoSite sources; unavailable sources: " +
                                string.Join(", ", failedSources));
                }

                if (raiseEvents)
                {
                    UpdateCompleted?.Invoke(null, new(pacFileChanged));
                }
                return pacFileChanged;
            }
            catch (Exception ex)
            {
                if (raiseEvents)
                {
                    Error?.Invoke(null, new(ex));
                    return false;
                }
                throw;
            }
            finally
            {
                updateLock.Release();
            }
        }

        private static async Task<Dictionary<string, IList<DomainObject>>> LoadOrRefreshSourceAsync(
            HttpClient httpClient,
            string sourceUrl)
        {
            Directory.CreateDirectory(CacheDirectory);
            string cachePath = GetCachePath(sourceUrl);
            string expectedHash = await TryGetChecksumAsync(httpClient, sourceUrl);

            if (!string.IsNullOrEmpty(expectedHash) && File.Exists(cachePath))
            {
                try
                {
                    byte[] cachedBytes = await File.ReadAllBytesAsync(cachePath);
                    string cachedHash = Convert.ToHexString(SHA256.HashData(cachedBytes));
                    if (string.Equals(expectedHash, cachedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        logger.Info($"GeoSite source is up to date: {sourceUrl}");
                        return ParseGeositeList(cachedBytes);
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, $"Cached GeoSite source could not be reused: {sourceUrl}");
                }
            }

            try
            {
                logger.Info($"Downloading GeoSite through Shadowsocks: {sourceUrl}");
                byte[] downloadedBytes = await httpClient.GetByteArrayAsync(sourceUrl);
                if (downloadedBytes.Length == 0)
                {
                    throw new InvalidDataException("Downloaded GeoSite database is empty.");
                }

                if (!string.IsNullOrEmpty(expectedHash))
                {
                    string downloadedHash = Convert.ToHexString(SHA256.HashData(downloadedBytes));
                    if (!string.Equals(expectedHash, downloadedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"GeoSite SHA256 mismatch for {sourceUrl}.");
                    }
                }

                Dictionary<string, IList<DomainObject>> parsed = ParseGeositeList(downloadedBytes);
                string tempPath = cachePath + ".new";
                await File.WriteAllBytesAsync(tempPath, downloadedBytes);
                File.Move(tempPath, cachePath, true);
                return parsed;
            }
            catch (Exception downloadException)
            {
                // The last known-good source cache is more useful than failing Local PAC
                // merely because a mirror is temporarily offline or its checksum changed.
                if (File.Exists(cachePath))
                {
                    try
                    {
                        byte[] cachedBytes = await File.ReadAllBytesAsync(cachePath);
                        Dictionary<string, IList<DomainObject>> parsed = ParseGeositeList(cachedBytes);
                        logger.Warn(downloadException,
                            $"GeoSite refresh failed; using last known-good cache for {sourceUrl}.");
                        return parsed;
                    }
                    catch (Exception cacheException)
                    {
                        throw new AggregateException(
                            $"GeoSite source and its cache are both unusable: {sourceUrl}",
                            downloadException,
                            cacheException);
                    }
                }

                throw;
            }
        }

        private static async Task<string> TryGetChecksumAsync(HttpClient httpClient, string sourceUrl)
        {
            string checksumUrl = GetChecksumUrl(sourceUrl);
            try
            {
                using HttpResponseMessage response = await httpClient.GetAsync(checksumUrl);
                if (!response.IsSuccessStatusCode)
                {
                    logger.Info($"GeoSite checksum is unavailable ({(int)response.StatusCode}); continuing without it: {checksumUrl}");
                    return null;
                }

                string content = await response.Content.ReadAsStringAsync();
                Match match = Regex.Match(content ?? string.Empty, @"(?i)\b[0-9a-f]{64}\b");
                if (!match.Success)
                {
                    logger.Info($"GeoSite checksum response has no SHA256 value; continuing without it: {checksumUrl}");
                    return null;
                }

                string checksum = match.Value.ToUpperInvariant();
                logger.Info($"GeoSite SHA256 for {sourceUrl}: {checksum}");
                return checksum;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"GeoSite checksum could not be fetched; continuing without verification: {checksumUrl}");
                return null;
            }
        }

        private static void LogInvalidConfiguredGroups(Configuration config)
        {
            foreach (string group in (config.geositeDirectGroups ?? [])
                         .Concat(config.geositeProxiedGroups ?? [])
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!CheckGeositeGroup(group))
                {
                    logger.Warn($"Configured GeoSite group is absent from the currently merged sources and will be skipped: {group}");
                }
            }
        }

        /// <summary>
        /// Merge and write pac.txt from the cached GeoSite database.
        /// </summary>
        public static bool MergeAndWritePACFile(List<string> directGroups, List<string> proxiedGroups, bool blacklist)
        {
            if (!IsDatabaseAvailable)
            {
                throw new InvalidOperationException("GeoSite database is not installed. Enable Local PAC and wait for the database download to complete.");
            }

            string abpContent = MergePACFile(
                directGroups ?? [],
                proxiedGroups ?? [],
                blacklist);
            if (File.Exists(PACDaemon.PAC_FILE))
            {
                string original = FileManager.NonExclusiveReadAllText(PACDaemon.PAC_FILE, Encoding.UTF8);
                if (original == abpContent)
                {
                    MarkCurrentSourceSetApplied();
                    return false;
                }
            }
            File.WriteAllText(PACDaemon.PAC_FILE, abpContent, Encoding.UTF8);
            MarkCurrentSourceSetApplied();
            return true;
        }

        /// <summary>
        /// Checks if the specified group exists in the currently merged GeoSite databases.
        /// </summary>
        public static bool CheckGeositeGroup(string group)
        {
            if (!IsDatabaseAvailable || !SeparateAttributeFromGroupName(group, out string groupName, out _))
            {
                return false;
            }

            lock (databaseLock)
            {
                return Geosites.ContainsKey(groupName);
            }
        }

        private static bool SeparateAttributeFromGroupName(string group, out string groupName, out string attribute)
        {
            string[] splitGroupAttributeList = (group ?? string.Empty).Split('@');
            if (splitGroupAttributeList.Length == 1)
            {
                groupName = splitGroupAttributeList[0];
                attribute = "";
            }
            else if (splitGroupAttributeList.Length == 2)
            {
                groupName = splitGroupAttributeList[0];
                attribute = splitGroupAttributeList[1];
            }
            else
            {
                groupName = "";
                attribute = "";
                return false;
            }
            return !string.IsNullOrWhiteSpace(groupName);
        }

        private static string MergePACFile(List<string> directGroups, List<string> proxiedGroups, bool blacklist)
        {
            string abpContent;
            if (File.Exists(PACDaemon.USER_ABP_FILE))
            {
                abpContent = FileManager.NonExclusiveReadAllText(PACDaemon.USER_ABP_FILE, Encoding.UTF8);
            }
            else
            {
                abpContent = EmbeddedResources.AbpJs;
            }

            List<string> userruleLines = [];
            if (File.Exists(PACDaemon.USER_RULE_FILE))
            {
                string userrulesString = FileManager.NonExclusiveReadAllText(PACDaemon.USER_RULE_FILE, Encoding.UTF8);
                userruleLines = ProcessUserRules(userrulesString);
            }

            List<string> ruleLines = GenerateRules(directGroups, proxiedGroups, blacklist);
            abpContent =
$@"var __USERRULES__ = {JsonConvert.SerializeObject(userruleLines, Formatting.Indented)};
var __RULES__ = {JsonConvert.SerializeObject(ruleLines, Formatting.Indented)};
{abpContent}";
            return abpContent;
        }

        private static List<string> ProcessUserRules(string content)
        {
            List<string> validLines = [];
            using StringReader stringReader = new(content);
            for (string line = stringReader.ReadLine(); line != null; line = stringReader.ReadLine())
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("!") || line.StartsWith("["))
                {
                    continue;
                }
                validLines.Add(line);
            }
            return validLines;
        }

        private static List<string> GenerateRules(List<string> directGroups, List<string> proxiedGroups, bool blacklist)
        {
            List<string> ruleLines;
            if (blacklist)
            {
                ruleLines = GenerateBlockingRules(proxiedGroups);
                ruleLines.AddRange(GenerateExceptionRules(directGroups));
            }
            else
            {
                ruleLines = ["/.*/"];
                ruleLines.AddRange(GenerateExceptionRules(directGroups));
            }
            return ruleLines.Distinct(StringComparer.Ordinal).ToList();
        }

        private static List<string> GenerateBlockingRules(List<string> groups)
        {
            List<string> ruleLines = [];
            foreach (string group in groups ?? [])
            {
                if (!SeparateAttributeFromGroupName(group, out string groupName, out string attribute))
                {
                    continue;
                }

                IList<DomainObject> domainObjects;
                lock (databaseLock)
                {
                    if (!Geosites.TryGetValue(groupName, out domainObjects))
                    {
                        continue;
                    }
                    domainObjects = domainObjects.ToList();
                }

                if (!string.IsNullOrEmpty(attribute))
                {
                    DomainObject.Types.Attribute attributeObject = new()
                    {
                        Key = attribute,
                        BoolValue = true
                    };
                    foreach (DomainObject domainObject in domainObjects)
                    {
                        if (domainObject.Attribute.Contains(attributeObject))
                        {
                            AddDomainRule(ruleLines, domainObject);
                        }
                    }
                }
                else
                {
                    foreach (DomainObject domainObject in domainObjects)
                    {
                        AddDomainRule(ruleLines, domainObject);
                    }
                }
            }
            return ruleLines.Distinct(StringComparer.Ordinal).ToList();
        }

        private static void AddDomainRule(List<string> ruleLines, DomainObject domainObject)
        {
            switch (domainObject.Type)
            {
                case DomainObject.Types.Type.Plain:
                    ruleLines.Add(domainObject.Value);
                    break;
                case DomainObject.Types.Type.Regex:
                    ruleLines.Add($"/{domainObject.Value}/");
                    break;
                case DomainObject.Types.Type.Domain:
                    ruleLines.Add($"||{domainObject.Value}");
                    break;
                case DomainObject.Types.Type.Full:
                    ruleLines.Add($"|http://{domainObject.Value}");
                    ruleLines.Add($"|https://{domainObject.Value}");
                    break;
            }
        }

        private static List<string> GenerateExceptionRules(List<string> groups)
            => GenerateBlockingRules(groups)
                .Select(r => $"@@{r}")
                .ToList();
    }
}
