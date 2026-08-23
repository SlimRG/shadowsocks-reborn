using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Newtonsoft.Json;
using NLog;
using Shadowsocks.Controller;
using Shadowsocks.Controller.Service;
using Shadowsocks.Core;
using Shadowsocks.Controller.Traffic;
using Shadowsocks.Core.Storage;

namespace Shadowsocks.Model
{
    [Serializable]
    public class Configuration
    {
        [JsonIgnore]
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public string version { get; set; }

        public List<Server> configs { get; set; }

        public List<string> onlineConfigSource { get; set; }

        // when strategy is set, index is ignored
        public string strategy { get; set; }
        public int index { get; set; }
        public bool global { get; set; }
        [JsonProperty("enabled")]
        public bool Enabled { get; set; }
        public bool shareOverLan { get; set; }
        public bool firstRun { get; set; }
        public int localPort { get; set; }
        public bool showPluginOutput { get; set; }
        public bool showDnsLogs { get; set; }
        public string pacUrl { get; set; }

        public bool useOnlinePac { get; set; }
        public bool secureLocalPac { get; set; } // enable secret for PAC server
        public bool regeneratePacOnUpdate { get; set; } // regenerate pac.txt on version update
        public bool autoCheckUpdate { get; set; }
        public bool checkPreRelease { get; set; }
        public string skippedUpdateVersion { get; set; } // skip the update with this version number
        public bool isVerboseLogging { get; set; }
        public string uiTheme { get; set; } // System, Light, or Dark. Presentation preference shared by UI shells.

        // hidden options
        public bool isIPv6Enabled { get; set; } // for experimental ipv6 support
        // GeoSite sources are used only by Local PAC. Every source is cached independently
        // and all successfully loaded databases are merged. The checksum URL is derived as
        // <source>.sha256sum; checksum absence is non-fatal.
        public List<string> geositeUrls { get; set; }

        public List<string> geositeDirectGroups { get; set; }  // groups of domains that we connect without the proxy
        public List<string> geositeProxiedGroups { get; set; } // groups of domains that we connect via the proxy
        public bool geositePreferDirect { get; set; } // a.k.a blacklist mode
        public string userAgent { get; set; }

        public LogViewerConfig logViewer { get; set; }
        public ForwardProxyConfig proxy { get; set; }
        public HotkeyConfig hotkey { get; set; }

        // Traffic capture/routing. User Mode requires no elevation and can identify
        // applications that connect to the managed local HTTP proxy. Admin Mode is
        // implemented by the optional elevated WinDivert helper.
        public TrafficCaptureMode trafficCaptureMode { get; set; }
        public List<ApplicationRouteRule> applicationRules { get; set; }
        public DnsPolicyConfig dnsPolicy { get; set; }
        public List<string> gameModeApplications { get; set; }

        [JsonIgnore]
        public bool firstRunOnNewVersion { get; set; }

        public Configuration()
        {
            version = ApplicationInfo.Version;
            strategy = "";
            index = -1;
            global = false;
            Enabled = false;
            shareOverLan = false;
            firstRun = true;
            localPort = 1080;
            showPluginOutput = false;
            showDnsLogs = false;
            pacUrl = "";
            useOnlinePac = false;
            secureLocalPac = true;
            regeneratePacOnUpdate = true;
            autoCheckUpdate = true;
            checkPreRelease = false;
            skippedUpdateVersion = "";
            isVerboseLogging = false;
            uiTheme = "System";

            // hidden options
            isIPv6Enabled = false;
            geositeUrls = new List<string>()
            {
                GeositeUpdater.DefaultSourceUrl,
            };
            geositeDirectGroups = new List<string>()
            {
                "private",
                "cn",
                "geolocation-!cn@cn",
            };
            geositeProxiedGroups = new List<string>()
            {
                "geolocation-!cn",
            };
            geositePreferDirect = false;
            userAgent = "Shadowsocks-Reborn/$version";

            logViewer = new LogViewerConfig();
            proxy = new ForwardProxyConfig();
            hotkey = new HotkeyConfig();

            trafficCaptureMode = TrafficCaptureMode.User;
            applicationRules = new List<ApplicationRouteRule>();
            dnsPolicy = new DnsPolicyConfig();
            gameModeApplications = new List<string>();

            firstRunOnNewVersion = false;

            configs = new List<Server>();
            onlineConfigSource = new List<string>();
        }

        [JsonIgnore]
        public string userAgentString { get; set; } // $version substituted with numeral version in it

        public const string SettingsValueName = "Configuration";
        public const string SettingsBackupValueName = "ConfigurationBackup";
        public const string SettingsSchemaVersionName = "SchemaVersion";
        public const int SettingsSchemaVersion = 3;
        private static ISettingsStore settingsStore;

        public static void ConfigureSettingsStore(ISettingsStore store)
        {
            ArgumentNullException.ThrowIfNull(store);
            settingsStore = store;
        }
        [JsonIgnore]
        public string LocalHost => isIPv6Enabled ? "[::1]" : "127.0.0.1";

        public Server GetCurrentServer()
        {
            if (index >= 0 && index < configs.Count)
                return configs[index];
            else
                return GetDefaultServer();
        }

        public WebProxy WebProxy => Enabled
            ? new WebProxy(
                    isIPv6Enabled
                    ? $"[{IPAddress.IPv6Loopback}]"
                    : IPAddress.Loopback.ToString(),
                    localPort)
            : null;

        [JsonIgnore]
        public bool HasConfiguredServer
        {
            get
            {
                if (configs == null || configs.Count == 0)
                    return false;

                if (!string.IsNullOrWhiteSpace(strategy))
                    return configs.Any(server => server?.IsConfigured == true);

                return GetCurrentServer()?.IsConfigured == true;
            }
        }

        /// <summary>
        /// Used by multiple forms to validate a server.
        /// Communication is done by throwing exceptions.
        /// </summary>
        /// <param name="server"></param>
        public static void CheckServer(Server server)
        {
            CheckServer(server.server);
            CheckPort(server.ServerPort);
            CheckPassword(server.password);
            CheckTimeout(server.timeout, Server.MaxServerTimeoutSec);
        }

        /// <summary>
        /// Loads the user configuration from the configured settings store. Product builds
        /// use a JSON file under the active storage root (LocalAppData in normal mode,
        /// a disposable Temp session in Clean Mode).
        /// </summary>
        public static Configuration Load()
        {
            if (settingsStore == null)
            {
                logger.Warn("No settings store is configured; using default configuration in memory.");
                return new Configuration();
            }

            if (!settingsStore.TryGetString(SettingsValueName, out string configContent) || string.IsNullOrWhiteSpace(configContent))
            {
                return new Configuration();
            }

            if (TryDeserialize(configContent, out Configuration config))
            {
                MigrateSettingsSchemaIfNeeded(config);
                return config;
            }

            logger.Error("Primary configuration is invalid; attempting the rollback snapshot.");
            if (settingsStore.TryGetString(SettingsBackupValueName, out string backupContent)
                && TryDeserialize(backupContent, out config))
            {
                try
                {
                    settingsStore.SetString(SettingsValueName, backupContent);
                }
                catch (Exception restoreException)
                {
                    logger.LogUsefulException(restoreException);
                }
                MigrateSettingsSchemaIfNeeded(config);
                return config;
            }

            logger.Error("No valid configuration snapshot is available; using defaults.");
            return new Configuration();
        }

        public static bool TryDeserialize(string json, out Configuration config)
        {
            config = null;
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                config = JsonConvert.DeserializeObject<Configuration>(json, new JsonSerializerSettings()
                {
                    ObjectCreationHandling = ObjectCreationHandling.Replace
                }) ?? new Configuration();
                return true;
            }
            catch (Exception exception)
            {
                logger.LogUsefulException(exception);
                return false;
            }
        }

        /// <summary>
        /// Process the loaded configurations and set up things.
        /// </summary>
        /// <param name="config">A reference of Configuration object.</param>
        public static void Process(ref Configuration config)
        {
            config.configs ??= new List<Server>();
            config.onlineConfigSource ??= new List<string>();
            NormalizeGeositeSources(config);
            config.applicationRules ??= new List<ApplicationRouteRule>();
            config.logViewer ??= new LogViewerConfig();
            config.proxy ??= new ForwardProxyConfig();
            config.hotkey ??= new HotkeyConfig();
            config.userAgent ??= "Shadowsocks-Reborn/$version";
            config.version ??= "0.0.0.0";
            config.dnsPolicy ??= new DnsPolicyConfig();
            config.dnsPolicy.dnsCrypt ??= new DnsCryptConfig();
            config.dnsPolicy.dnsCrypt.serverNames ??= new List<string>();
            // Persisted DNSCrypt preferences are user state. Do not rewrite legacy/custom
            // values here; effective Administrator-mode DNSCrypt interception remains
            // fail-closed independently in the NetworkService request contract.
            if (config.dnsPolicy.dnsCrypt.automaticResolvers)
                config.dnsPolicy.dnsCrypt.serverNames.Clear();
            config.gameModeApplications ??= new List<string>();
            config.uiTheme = NormalizeUiThemePreference(config.uiTheme);
            // Mark the first run of a newer product version. Malformed/missing version
            // metadata must never prevent the application from starting.
            var appVersion = new Version(ApplicationInfo.Version);
            if (!Version.TryParse(config.version, out Version configVersion))
            {
                configVersion = new Version(0, 0, 0, 0);
            }
            if (appVersion.CompareTo(configVersion) > 0)
            {
                config.firstRunOnNewVersion = true;
            }
            // Empty placeholder servers were historically persisted to keep a synthetic
            // "Server 1" row alive. Zero configured servers is now a first-class state.
            RemovePersistedEmptyServers(config);
            EnsureServerNames(config.configs);

            // Selected server. With no servers there is deliberately no selection and the
            // system proxy is disabled so Windows is never pointed at an inactive relay.
            if (config.configs.Count == 0)
            {
                config.index = -1;
                config.strategy = string.Empty;
                config.Enabled = false;
            }
            else
            {
                if (config.index < 0 && string.IsNullOrEmpty(config.strategy))
                    config.index = 0;
                if (config.index >= config.configs.Count)
                    config.index = config.configs.Count - 1;

                if (!config.HasConfiguredServer)
                {
                    // Preserve the user's DNS preference. Dependent runtime/UI paths are
                    // gated by HasConfiguredServer and remain inactive until a server exists.
                    config.Enabled = false;
                }
            }
            // Check OS IPv6 support
            if (!System.Net.Sockets.Socket.OSSupportsIPv6)
                config.isIPv6Enabled = false;
            config.proxy.CheckConfig();
            // Replace $version with the version number.
            config.userAgentString = config.userAgent.Replace("$version", config.version);

        }

        /// <summary>
        /// Saves the configuration to the configured settings store. The previous valid
        /// value is preserved as a rollback snapshot before the primary value is replaced.
        /// </summary>
        public static void Save(Configuration config)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (settingsStore == null)
                throw new InvalidOperationException("The settings store must be configured before saving configuration.");

            config.configs ??= new List<Server>();
            RemovePersistedEmptyServers(config);
            EnsureServerNames(config.configs);

            Server selectedServer = null;
            if (string.IsNullOrEmpty(config.strategy)
                && config.index >= 0
                && config.index < config.configs.Count)
            {
                selectedServer = config.configs[config.index];
            }

            config.configs = SortByOnlineConfig(config.configs);
            if (config.configs.Count == 0)
            {
                config.index = -1;
                config.strategy = string.Empty;
            }
            else if (string.IsNullOrEmpty(config.strategy))
            {
                int sortedIndex = selectedServer is null ? -1 : config.configs.FindIndex(server => ReferenceEquals(server, selectedServer));
                config.index = sortedIndex >= 0
                    ? sortedIndex
                    : Math.Clamp(config.index, 0, config.configs.Count - 1);
            }
            if (!config.HasConfiguredServer)
            {
                // Persist the selected DNS policy even while no server is available. The
                // controller applies inactive runtime behavior until a server exists.
                config.Enabled = false;
            }
            string jsonString = SerializeConfiguration(config);

            try
            {
                if (settingsStore.TryGetString(SettingsValueName, out string previous)
                    && !string.IsNullOrWhiteSpace(previous)
                    && TryDeserialize(previous, out _))
                {
                    settingsStore.SetString(SettingsBackupValueName, previous);
                }

                settingsStore.SetString(SettingsValueName, jsonString);
                settingsStore.SetInt32(SettingsSchemaVersionName, SettingsSchemaVersion);
            }
            catch (Exception exception)
            {
                logger.LogUsefulException(exception);
                throw;
            }
        }

        private static void MigrateSettingsSchemaIfNeeded(Configuration config)
        {
            if (settingsStore is null)
            {
                return;
            }

            int schemaVersion = settingsStore.TryGetInt32(SettingsSchemaVersionName, out int storedVersion)
                ? storedVersion
                : 0;
            if (schemaVersion >= SettingsSchemaVersion)
            {
                return;
            }

            try
            {
                // Schema v3 removes the historical synthetic empty server without changing
                // any persisted DNSCrypt preferences. New DNSCrypt defaults come only from
                // constructing a new DnsCryptConfig.
                RemovePersistedEmptyServers(config);

                // Re-serializing the typed model is the schema migration boundary: properties
                // no longer represented by Configuration are removed without carrying a list
                // of historical field names forever. Keep a valid rollback snapshot canonical too.
                settingsStore.SetString(SettingsValueName, SerializeConfiguration(config));

                if (settingsStore.TryGetString(SettingsBackupValueName, out string backupJson)
                    && TryDeserialize(backupJson, out Configuration backupConfig))
                {
                    RemovePersistedEmptyServers(backupConfig);
                    settingsStore.SetString(SettingsBackupValueName, SerializeConfiguration(backupConfig));
                }

                settingsStore.SetInt32(SettingsSchemaVersionName, SettingsSchemaVersion);
            }
            catch (Exception exception)
            {
                // Migration failure must not prevent startup; the in-memory configuration is
                // already valid and a later successful save will persist the current schema.
                logger.LogUsefulException(exception);
            }
        }

        private static void RemovePersistedEmptyServers(Configuration config)
        {
            config.configs ??= new List<Server>();
            int selectedIndex = config.index;
            int removedBeforeSelection = 0;
            bool selectedWasRemoved = false;

            for (int index = config.configs.Count - 1; index >= 0; index--)
            {
                if (!IsPersistedEmptyServer(config.configs[index]))
                    continue;

                if (selectedIndex >= 0)
                {
                    if (index < selectedIndex)
                        removedBeforeSelection++;
                    else if (index == selectedIndex)
                        selectedWasRemoved = true;
                }

                config.configs.RemoveAt(index);
            }

            if (config.configs.Count == 0)
            {
                config.index = -1;
                return;
            }

            if (!string.IsNullOrEmpty(config.strategy))
                return;

            if (selectedIndex < 0)
            {
                config.index = 0;
                return;
            }

            int adjustedIndex = selectedIndex - removedBeforeSelection;
            if (selectedWasRemoved && adjustedIndex >= config.configs.Count)
                adjustedIndex = config.configs.Count - 1;
            config.index = Math.Clamp(adjustedIndex, 0, config.configs.Count - 1);
        }

        private static bool IsPersistedEmptyServer(Server server)
            => server is null
               || (string.IsNullOrWhiteSpace(server.server)
                   && string.IsNullOrWhiteSpace(server.password)
                   && string.IsNullOrWhiteSpace(server.remarks)
                   && string.IsNullOrWhiteSpace(server.plugin)
                   && string.IsNullOrWhiteSpace(server.PluginOptions)
                   && string.IsNullOrWhiteSpace(server.PluginArguments)
                   && string.IsNullOrWhiteSpace(server.group));

        private static string SerializeConfiguration(Configuration config)
            => JsonConvert.SerializeObject(config, Formatting.Indented);

        /// <summary>
        /// Gives every configured server a stable display name when the source did not
        /// provide one. Explicit names from ss:// fragments, subscriptions, or the user
        /// are preserved. Generated names are unique within the current configuration.
        /// </summary>
        public static void EnsureServerNames(IList<Server> servers)
        {
            ArgumentNullException.ThrowIfNull(servers);

            var usedNames = new HashSet<string>(
                servers
                    .Where(server => server is not null && !string.IsNullOrWhiteSpace(server.remarks))
                    .Select(server => server.remarks.Trim()),
                StringComparer.OrdinalIgnoreCase);

            int sequence = 1;
            foreach (Server server in servers)
            {
                if (server is null || !server.IsConfigured || !string.IsNullOrWhiteSpace(server.remarks))
                {
                    continue;
                }

                string candidate;
                do
                {
                    candidate = $"Server {sequence++}";
                }
                while (!usedNames.Add(candidate));

                server.remarks = candidate;
            }
        }

        public static List<Server> SortByOnlineConfig(IEnumerable<Server> servers)
        {
            var groups = servers.GroupBy(s => s.group);
            List<Server> ret = new List<Server>();
            ret.AddRange(groups.Where(g => string.IsNullOrEmpty(g.Key)).SelectMany(g => g));
            ret.AddRange(groups.Where(g => !string.IsNullOrEmpty(g.Key)).SelectMany(g => g));
            return ret;
        }

        public static List<string> NormalizeGeositeSourceList(IEnumerable<string> sources)
        {
            var normalized = (sources ?? Enumerable.Empty<string>())
                .Select(source => source?.Trim())
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Where(source => Uri.TryCreate(source, UriKind.Absolute, out Uri uri) &&
                                 (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalized.Count == 0)
                normalized.Add(GeositeUpdater.DefaultSourceUrl);

            return normalized;
        }

        public static string NormalizeUiThemePreference(string theme)
        {
            return theme?.Trim().ToUpperInvariant() switch
            {
                "LIGHT" => "Light",
                "DARK" => "Dark",
                _ => "System",
            };
        }

        private static void NormalizeGeositeSources(Configuration config)
        {
            if (config == null)
                return;

            config.geositeUrls = NormalizeGeositeSourceList(config.geositeUrls ?? new List<string>());
        }

        /// <summary>
        /// Validates if the groups in the list are all valid.
        /// </summary>
        /// <param name="groups">The list of groups to validate.</param>
        /// <returns>
        /// True if all groups are valid.
        /// False if any one of them is invalid.
        /// </returns>
        public static bool ValidateGeositeGroupList(List<string> groups)
        {
            foreach (var geositeGroup in groups)
                if (!GeositeUpdater.CheckGeositeGroup(geositeGroup)) // found invalid group
                {
#if DEBUG
                    logger.Debug($"Available groups:");
                    foreach (var group in GeositeUpdater.Geosites.Keys)
                        logger.Debug($"{group}");
#endif
                    logger.Warn($"The Geosite group {geositeGroup} doesn't exist. Resetting to default groups.");
                    return false;
                }
            return true;
        }

        public static void ResetGeositeDirectGroup(ref List<string> geositeDirectGroups)
        {
            geositeDirectGroups.Clear();
            geositeDirectGroups.Add("private");
            geositeDirectGroups.Add("cn");
            geositeDirectGroups.Add("geolocation-!cn@cn");
        }

        public static void ResetGeositeProxiedGroup(ref List<string> geositeProxiedGroups)
        {
            geositeProxiedGroups.Clear();
            geositeProxiedGroups.Add("geolocation-!cn");
        }

        public static void ResetUserAgent(Configuration config)
        {
            config.userAgent = "Shadowsocks-Reborn/$version";
            config.userAgentString = config.userAgent.Replace("$version", config.version);
        }

        public static Server AddDefaultServerOrServer(Configuration config, Server server = null, int? index = null)
        {
            if (config?.configs != null)
            {
                server = (server ?? GetDefaultServer());

                config.configs.Insert(index.GetValueOrDefault(config.configs.Count), server);
            }
            return server;
        }

        public static Server GetDefaultServer()
        {
            return new Server();
        }

        public static void CheckPort(int port)
        {
            if (port <= 0 || port > 65535)
                throw new ArgumentException(I18N.GetString("Port out of range"));
        }

        public static void CheckLocalPort(int port)
        {
            CheckPort(port);
            if (port == 8123)
                throw new ArgumentException(I18N.GetString("Port can't be 8123"));
        }

        private static void CheckPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException(I18N.GetString("Password can not be blank"));
        }

        public static void CheckServer(string server)
        {
            if (string.IsNullOrEmpty(server))
                throw new ArgumentException(I18N.GetString("Server IP can not be blank"));
        }

        public static void CheckTimeout(int timeout, int maxTimeout)
        {
            if (timeout <= 0 || timeout > maxTimeout)
                throw new ArgumentException(
                    I18N.GetString("Timeout is invalid, it should not exceed {0}", maxTimeout));
        }
    }
}
