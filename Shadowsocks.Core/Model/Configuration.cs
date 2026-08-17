using System;
using System.Collections.Generic;
using System.IO;
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

        public string version;

        public List<Server> configs;

        public List<string> onlineConfigSource;

        // when strategy is set, index is ignored
        public string strategy;
        public int index;
        public bool global;
        public bool enabled;
        public bool shareOverLan;
        public bool firstRun;
        public int localPort;
        public bool portableMode;
        public bool showPluginOutput;
        public string pacUrl;

        public bool useOnlinePac;
        public bool secureLocalPac; // enable secret for PAC server
        public bool regeneratePacOnUpdate; // regenerate pac.txt on version update
        public bool autoCheckUpdate;
        public bool checkPreRelease;
        public string skippedUpdateVersion; // skip the update with this version number
        public bool isVerboseLogging;
        public string uiTheme; // System, Light, or Dark. Presentation preference shared by UI shells.

        // hidden options
        public bool isIPv6Enabled; // for experimental ipv6 support
        public bool generateLegacyUrl; // for pre-sip002 url compatibility
        // GeoSite sources are used only by Local PAC. Every source is cached independently
        // and all successfully loaded databases are merged. The checksum URL is derived as
        // <source>.sha256sum; checksum absence is non-fatal.
        public List<string> geositeUrls;

        // Legacy single-source settings kept only for gui-config.json migration.
        public string geositeUrl;
        public string geositeSha256sumUrl;

        public List<string> geositeDirectGroups;  // groups of domains that we connect without the proxy
        public List<string> geositeProxiedGroups; // groups of domains that we connect via the proxy
        public bool geositePreferDirect; // a.k.a blacklist mode
        public string userAgent;

        public LogViewerConfig logViewer;
        public ForwardProxyConfig proxy;
        public HotkeyConfig hotkey;

        // Traffic capture/routing. User Mode requires no elevation and can identify
        // applications that connect to the managed local HTTP proxy. Admin Mode is
        // implemented by the optional elevated WinDivert helper.
        public TrafficCaptureMode trafficCaptureMode;
        public List<ApplicationRouteRule> applicationRules;
        public DnsPolicyConfig dnsPolicy;
        public List<string> gameModeApplications;

        [JsonIgnore]
        public bool firstRunOnNewVersion;

        public Configuration()
        {
            version = ApplicationInfo.Version;
            strategy = "";
            index = 0;
            global = false;
            enabled = false;
            shareOverLan = false;
            firstRun = true;
            localPort = 1080;
            portableMode = false;
            showPluginOutput = false;
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
            generateLegacyUrl = false;
            geositeUrls = new List<string>()
            {
                GeositeUpdater.DefaultSourceUrl,
            };
            geositeUrl = "";
            geositeSha256sumUrl = "";
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
            userAgent = "ShadowsocksWindows/$version";

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
        public string userAgentString; // $version substituted with numeral version in it

        public const string SettingsValueName = "Configuration";
        public const string SettingsBackupValueName = "ConfigurationBackup";
        public const string SettingsSchemaVersionName = "SchemaVersion";
        public const int SettingsSchemaVersion = 1;
        private const string LEGACY_CONFIG_FILE = "gui-config.json";
        private static ISettingsStore settingsStore;

        [JsonIgnore]
        public static string LegacyConfigFilePath => Path.Combine(Shadowsocks.Core.RuntimeEnvironment.WorkingDirectory, LEGACY_CONFIG_FILE);

        public static void ConfigureSettingsStore(ISettingsStore store)
        {
            settingsStore = store ?? throw new ArgumentNullException(nameof(store));
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

        public WebProxy WebProxy => enabled
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
            CheckPort(server.server_port);
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
            config.dnsPolicy ??= new DnsPolicyConfig();
            config.gameModeApplications ??= new List<string>();
            config.uiTheme = NormalizeUiThemePreference(config.uiTheme);
            // Kept only for deserializing old gui-config.json files. Storage is no longer portable.
            config.portableMode = false;

            // Mark the first run of a new version.
            var appVersion = new Version(ApplicationInfo.Version);
            var configVersion = new Version(config.version);
            if (appVersion.CompareTo(configVersion) > 0)
            {
                config.firstRunOnNewVersion = true;
            }
            // Add an empty server configuration
            if (config.configs.Count == 0)
                config.configs.Add(GetDefaultServer());

            // XChaCha20-Poly1305 depended on the removed native libsodium backend.
            // Keep old gui-config.json files loadable, but the remote server must also
            // be changed to the replacement method before the connection can succeed.
            foreach (Server server in config.configs)
            {
                if (string.Equals(server.method, "xchacha20-ietf-poly1305", StringComparison.OrdinalIgnoreCase))
                {
                    logger.Warn(
                        $"Encryption method 'xchacha20-ietf-poly1305' is no longer supported for {server.server}:{server.server_port}; " +
                        $"using '{Server.DefaultMethod}' locally. The remote server must use the same method.");
                    server.method = Server.DefaultMethod;
                }
            }

            EnsureServerNames(config.configs);

            // Selected server
            if (config.index == -1 && string.IsNullOrEmpty(config.strategy))
                config.index = 0;
            if (config.index >= config.configs.Count)
                config.index = config.configs.Count - 1;
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
            EnsureServerNames(config.configs);
            config.configs = SortByOnlineConfig(config.configs);
            string jsonString = JsonConvert.SerializeObject(config, Formatting.Indented);

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

            var sources = config.geositeUrls ?? new List<string>();

            // fix28 and older supported one custom URL that replaced the built-in source.
            // Preserve that behavior during migration rather than silently adding v2fly as
            // a second database. Checksum URLs are now discovered automatically.
            if (!string.IsNullOrWhiteSpace(config.geositeUrl))
                sources = new List<string> { config.geositeUrl };

            config.geositeUrls = NormalizeGeositeSourceList(sources);
            config.geositeUrl = "";
            config.geositeSha256sumUrl = "";
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
            config.userAgent = "ShadowsocksWindows/$version";
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
