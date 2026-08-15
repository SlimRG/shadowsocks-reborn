using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Forms;
using NLog;
using Shadowsocks.Controller.Service;
using Shadowsocks.Controller.Strategy;
using Shadowsocks.Controller.Traffic;
using Shadowsocks.Model;
using Shadowsocks.Util;
using WPFLocalizeExtension.Engine;

namespace Shadowsocks.Controller
{
    public class ShadowsocksController
    {
        private readonly Logger logger;
        private readonly HttpClient httpClient;

        // controller:
        // handle user actions
        // manipulates UI
        // interacts with low level logic
        #region Members definition
        private Thread _trafficThread;

        private Listener _listener;
        private PACDaemon _pacDaemon;
        private PACServer _pacServer;
        private Configuration _config;
        private StrategyManager _strategyManager;
        private readonly TrafficPolicyEngine _trafficPolicyEngine;
        private readonly AdminCaptureManager _adminCaptureManager;
        private readonly GameModeManager _gameModeManager;
        private ManagedHttpProxyService _managedHttpProxy;
        private readonly ConcurrentDictionary<Server, Sip003Plugin> _pluginsByServer;

        private long _inboundCounter = 0;
        private long _outboundCounter = 0;
        public long InboundCounter => Interlocked.Read(ref _inboundCounter);
        public long OutboundCounter => Interlocked.Read(ref _outboundCounter);
        public Queue<TrafficPerSecond> trafficPerSecondQueue;

        private bool stopped = false;

        public class PathEventArgs : EventArgs
        {
            public string Path;
        }

        public class UpdatedEventArgs : EventArgs
        {
            public string OldVersion;
            public string NewVersion;
        }

        public class TrafficPerSecond
        {
            public long inboundCounter;
            public long outboundCounter;
            public long inboundIncreasement;
            public long outboundIncreasement;
        }

        public event EventHandler ConfigChanged;
        public event EventHandler EnableStatusChanged;
        public event EventHandler EnableGlobalChanged;
        public event EventHandler ShareOverLANStatusChanged;
        public event EventHandler VerboseLoggingStatusChanged;
        public event EventHandler ShowPluginOutputChanged;
        public event EventHandler TrafficChanged;
        public event EventHandler TrafficModeChanged;

        // when user clicked Edit PAC, and PAC file has already created
        public event EventHandler<PathEventArgs> PACFileReadyToOpen;
        public event EventHandler<PathEventArgs> UserRuleFileReadyToOpen;

        public event EventHandler<GeositeResultEventArgs> UpdatePACFromGeositeCompleted;

        public event ErrorEventHandler UpdatePACFromGeositeError;

        public event ErrorEventHandler Errored;

        // Invoked when controller.Start();
        public event EventHandler<UpdatedEventArgs> ProgramUpdated;
        #endregion

        public ShadowsocksController()
        {
            logger = LogManager.GetCurrentClassLogger();
            httpClient = new HttpClient();
            _config = Configuration.Load();
            Configuration.Process(ref _config);
            _strategyManager = new StrategyManager(this);
            _trafficPolicyEngine = new TrafficPolicyEngine(_config);
            _adminCaptureManager = new AdminCaptureManager();
            _gameModeManager = new GameModeManager(_adminCaptureManager);
            _gameModeManager.StatusChanged += (_, _) => TrafficModeChanged?.Invoke(this, EventArgs.Empty);
            _pluginsByServer = new ConcurrentDictionary<Server, Sip003Plugin>();
            StartTrafficStatistics(61);

            ProgramUpdated += (o, e) =>
            {
                // version update precedures
                if (e.OldVersion == "4.3.0.0" || e.OldVersion == "4.3.1.0")
                    _config.geositeDirectGroups.Add("private");

                logger.Info($"Updated from {e.OldVersion} to {e.NewVersion}");
            };
        }

        #region Basic

        public void Start(bool systemWakeUp = false)
        {
            stopped = false;
            if (_config.firstRunOnNewVersion && !systemWakeUp)
            {
                ProgramUpdated.Invoke(this, new UpdatedEventArgs()
                {
                    OldVersion = _config.version,
                    NewVersion = UpdateChecker.Version,
                });
                // delete pac.txt when regeneratePacOnUpdate is true
                if (_config.regeneratePacOnUpdate)
                    try
                    {
                        File.Delete(PACDaemon.PAC_FILE);
                        logger.Info("Deleted pac.txt from previous version.");
                    }
                    catch (Exception e)
                    {
                        logger.LogUsefulException(e);
                    }
                // finish up first run of new version
                _config.firstRunOnNewVersion = false;
                _config.version = UpdateChecker.Version;
                Configuration.Save(_config);
            }
            Reload();
            if (!systemWakeUp)
                HotkeyReg.RegAllHotkeys();
        }

        public void Stop()
        {
            if (stopped)
            {
                return;
            }
            stopped = true;
            try
            {
                _gameModeManager.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                logger.LogUsefulException(exception);
            }
            if (_listener != null)
            {
                _listener.Stop();
            }
            StopPlugins();
            if (_config.enabled)
            {
                SystemProxy.Update(_config, true, null);
            }
            Encryption.RNG.Close();
        }

        protected void Reload()
        {
            Encryption.RNG.Reload();
            // some logic in configuration updated the config when saving, we need to read it again
            _config = Configuration.Load();
            Configuration.Process(ref _config);
            if (!_config.useOnlinePac)
                GeositeUpdater.ConfigureSources(_config.geositeUrls);

            NLogConfig.LoadConfiguration();

            logger.Info($"WPF Localization Extension|Current culture: {LocalizeDictionary.CurrentCulture}");

            // set User-Agent for httpClient
            try
            {
                if (!string.IsNullOrWhiteSpace(_config.userAgentString))
                    httpClient.DefaultRequestHeaders.Add("User-Agent", _config.userAgentString);
            }
            catch
            {
                // reset userAgent to default and reapply
                Configuration.ResetUserAgent(_config);
                httpClient.DefaultRequestHeaders.Add("User-Agent", _config.userAgentString);
            }

            if (_pacDaemon == null)
            {
                _pacDaemon = new PACDaemon(_config);
                _pacDaemon.PACFileChanged += PacDaemon_PACFileChanged;
                _pacDaemon.UserRuleFileChanged += PacDaemon_UserRuleFileChanged;
            }
            else
            {
                _pacDaemon.UpdateConfiguration(_config);
            }

            _pacServer = _pacServer ?? new PACServer(_pacDaemon);
            _pacServer.UpdatePACURL(_config); // Both Local and cached Online PAC are served from localhost.

            // Do not even initialize GeoSite while Online PAC is selected.
            if (!_config.useOnlinePac)
            {
                GeositeUpdater.ResetEvent();
                GeositeUpdater.UpdateCompleted += PacServer_PACUpdateCompleted;
                GeositeUpdater.Error += PacServer_PACUpdateError;
            }

            _listener?.Stop();
            StopPlugins();

            try
            {
                var strategy = GetCurrentStrategy();
                strategy?.ReloadServers();

                StartPlugin();
                _trafficPolicyEngine.UpdateConfiguration(_config);
                _managedHttpProxy = new ManagedHttpProxyService(_trafficPolicyEngine, _config);

                TCPRelay tcpRelay = new TCPRelay(this, _config);
                tcpRelay.OnInbound += UpdateInboundCounter;
                tcpRelay.OnOutbound += UpdateOutboundCounter;
                tcpRelay.OnFailed += (o, e) => GetCurrentStrategy()?.SetFailure(e.server);

                UDPRelay udpRelay = new UDPRelay(this);
                List<Listener.IService> services = new List<Listener.IService>
                {
                    tcpRelay,
                    udpRelay,
                    _pacServer,
                    _managedHttpProxy
                };
                _listener = new Listener(services);
                _listener.Start(_config);
                _ = ApplyTrafficCaptureAsync(_config);
            }
            catch (Exception e)
            {
                // translate Microsoft language into human language
                // i.e. An attempt was made to access a socket in a way forbidden by its access permissions => Port already in use
                if (e is SocketException se)
                {
                    if (se.SocketErrorCode == SocketError.AddressAlreadyInUse)
                    {
                        e = new Exception(I18N.GetString("Port {0} already in use", _config.localPort), e);
                    }
                    else if (se.SocketErrorCode == SocketError.AccessDenied)
                    {
                        e = new Exception(I18N.GetString("Port {0} is reserved by system", _config.localPort), e);
                    }
                }
                logger.LogUsefulException(e);
                ReportError(e);
            }

            // Apply the Windows proxy first, then notify the UI. Both PAC modes now point
            // WinINet at localhost; remote data is refreshed only after the SS listener is up.
            UpdateSystemProxy();
            ConfigChanged?.Invoke(this, new EventArgs());

            _ = RefreshActivePacDataAsync(_config);
        }

        protected void SaveConfig(Configuration newConfig)
        {
            Configuration.Save(newConfig);
            Reload();
        }

        protected void ReportError(Exception e)
        {
            Errored?.Invoke(this, new ErrorEventArgs(e));
        }

        public HttpClient GetHttpClient() => httpClient;
        public Server GetCurrentServer() => _config.GetCurrentServer();
        public Configuration GetCurrentConfiguration() => _config;
        internal SystemProxyMode GetSystemProxyMode() => SystemProxy.GetCurrentMode(_config, _pacServer);

        public Server GetAServer(IStrategyCallerType type, IPEndPoint localIPEndPoint, EndPoint destEndPoint)
        {
            IStrategy strategy = GetCurrentStrategy();
            if (strategy != null)
            {
                return strategy.GetAServer(type, localIPEndPoint, destEndPoint);
            }
            if (_config.index < 0)
            {
                _config.index = 0;
            }
            return GetCurrentServer();
        }

        public void SaveServers(List<Server> servers, int localPort, bool portableMode)
        {
            _config.configs = servers;
            _config.localPort = localPort;
            _config.portableMode = portableMode;
            Configuration.Save(_config);
        }

        public void SelectServerIndex(int index)
        {
            _config.index = index;
            _config.strategy = null;
            SaveConfig(_config);
        }

        public void ToggleShareOverLAN(bool enabled)
        {
            _config.shareOverLan = enabled;
            SaveConfig(_config);

            ShareOverLANStatusChanged?.Invoke(this, new EventArgs());
        }

        #endregion


        #region Traffic routing

        public TrafficRuntimeMode GetTrafficRuntimeMode() => _gameModeManager.RuntimeMode;

        public async Task<bool> SetTrafficCaptureModeAsync(TrafficCaptureMode mode)
        {
            TrafficCaptureMode previousMode = _config.trafficCaptureMode;
            _config.trafficCaptureMode = mode;
            _trafficPolicyEngine.UpdateConfiguration(_config);

            try
            {
                await _gameModeManager.ApplyConfigurationAsync(_config, GetCaptureExclusionProcessIds()).ConfigureAwait(false);
                Configuration.Save(_config);
                TrafficModeChanged?.Invoke(this, EventArgs.Empty);
                ConfigChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (AdminElevationCanceledException exception)
            {
                logger.Info(exception, "Administrator elevation for Admin Mode was cancelled.");
            }
            catch (Exception exception)
            {
                logger.LogUsefulException(exception);
                ReportError(exception);
            }

            _config.trafficCaptureMode = previousMode == TrafficCaptureMode.Admin
                ? TrafficCaptureMode.User
                : previousMode;
            if (_config.trafficCaptureMode == TrafficCaptureMode.Admin)
            {
                _config.trafficCaptureMode = TrafficCaptureMode.User;
            }
            Configuration.Save(_config);
            try
            {
                await _gameModeManager.ApplyConfigurationAsync(_config, GetCaptureExclusionProcessIds()).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogUsefulException(exception);
            }
            TrafficModeChanged?.Invoke(this, EventArgs.Empty);
            ConfigChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        public async Task<bool> SaveTrafficRoutingAsync(
            TrafficCaptureMode captureMode,
            IEnumerable<ApplicationRouteRule> applicationRules,
            IEnumerable<string> gameModeApplications)
        {
            _config.applicationRules = (applicationRules ?? [])
                .Where(rule => rule is not null && !string.IsNullOrWhiteSpace(rule.application))
                .Select(rule => new ApplicationRouteRule
                {
                    enabled = rule.enabled,
                    application = rule.application.Trim(),
                    action = rule.action,
                })
                .ToList();
            _config.gameModeApplications = (gameModeApplications ?? [])
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .Select(pattern => pattern.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _trafficPolicyEngine.UpdateConfiguration(_config);

            TrafficCaptureMode previousMode = _config.trafficCaptureMode;
            _config.trafficCaptureMode = captureMode;
            try
            {
                await _gameModeManager.ApplyConfigurationAsync(_config, GetCaptureExclusionProcessIds()).ConfigureAwait(false);
                Configuration.Save(_config);
                TrafficModeChanged?.Invoke(this, EventArgs.Empty);
                ConfigChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (AdminElevationCanceledException exception)
            {
                logger.Info(exception, "Administrator elevation for Admin Mode was cancelled.");
            }
            catch (Exception exception)
            {
                logger.LogUsefulException(exception);
                ReportError(exception);
            }

            _config.trafficCaptureMode = captureMode == TrafficCaptureMode.Admin
                ? TrafficCaptureMode.User
                : previousMode;
            Configuration.Save(_config);
            try
            {
                await _gameModeManager.ApplyConfigurationAsync(_config, GetCaptureExclusionProcessIds()).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogUsefulException(exception);
            }
            TrafficModeChanged?.Invoke(this, EventArgs.Empty);
            ConfigChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        private async Task ApplyTrafficCaptureAsync(Configuration configurationAtStart)
        {
            try
            {
                await _gameModeManager.ApplyConfigurationAsync(
                    configurationAtStart,
                    GetCaptureExclusionProcessIds()).ConfigureAwait(false);
            }
            catch (AdminElevationCanceledException exception)
            {
                logger.Info(exception, "Administrator elevation for Admin Mode was cancelled.");
                if (ReferenceEquals(_config, configurationAtStart) && _config.trafficCaptureMode == TrafficCaptureMode.Admin)
                {
                    _config.trafficCaptureMode = TrafficCaptureMode.User;
                    Configuration.Save(_config);
                    TrafficModeChanged?.Invoke(this, EventArgs.Empty);
                    ConfigChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception exception)
            {
                logger.LogUsefulException(exception);
                if (ReferenceEquals(_config, configurationAtStart) && _config.trafficCaptureMode == TrafficCaptureMode.Admin)
                {
                    _config.trafficCaptureMode = TrafficCaptureMode.User;
                    Configuration.Save(_config);
                }
                TrafficModeChanged?.Invoke(this, EventArgs.Empty);
                ReportError(exception);
            }
        }

        private int[] GetCaptureExclusionProcessIds()
        {
            return _pluginsByServer.Values
                .Select(plugin => plugin.ProcessId)
                .Where(processId => processId > 0)
                .Append(Environment.ProcessId)
                .Distinct()
                .ToArray();
        }

        #endregion

        #region OS Proxy

        public void ToggleEnable(bool enabled)
        {
            _config.enabled = enabled;
            SaveConfig(_config);

            EnableStatusChanged?.Invoke(this, new EventArgs());
        }

        public void ToggleGlobal(bool global)
        {
            _config.global = global;
            SaveConfig(_config);

            EnableGlobalChanged?.Invoke(this, new EventArgs());
        }

        public void SaveProxy(ForwardProxyConfig proxyConfig)
        {
            _config.proxy = proxyConfig;
            SaveConfig(_config);
        }

        private void UpdateSystemProxy()
        {
            SystemProxy.Update(_config, false, _pacServer);
        }

        #endregion

        #region PAC

        private void PacDaemon_PACFileChanged(object sender, EventArgs e)
        {
            if (!_config.useOnlinePac)
            {
                _pacServer.UpdatePACURL(_config);
                UpdateSystemProxy();
            }
        }

        private void PacServer_PACUpdateCompleted(object sender, GeositeResultEventArgs e)
        {
            if (!_config.useOnlinePac)
            {
                _pacServer.UpdatePACURL(_config);
                UpdateSystemProxy();
            }
            UpdatePACFromGeositeCompleted?.Invoke(this, e);
        }

        private void PacServer_PACUpdateError(object sender, ErrorEventArgs e)
        {
            UpdatePACFromGeositeError?.Invoke(this, e);
        }

        private static readonly IEnumerable<char> IgnoredLineBegins = new[] { '!', '[' };
        private void PacDaemon_UserRuleFileChanged(object sender, EventArgs e)
        {
            if (_config.useOnlinePac || !GeositeUpdater.IsDatabaseAvailable)
                return;

            GeositeUpdater.MergeAndWritePACFile(_config.geositeDirectGroups, _config.geositeProxiedGroups, _config.geositePreferDirect);
            _pacServer.UpdatePACURL(_config);
            UpdateSystemProxy();
        }

        private async Task RefreshActivePacDataAsync(Configuration configAtStart)
        {
            try
            {
                // Let compound UI mode changes (e.g. PAC -> Global) finish their second
                // config write before deciding whether a PAC download is still required.
                await Task.Yield();
                if (!ReferenceEquals(_config, configAtStart))
                    return;

                // PAC data is only fetched when PAC mode is actually active. Global and
                // Disabled modes do not need either GeoSite or an online PAC refresh.
                if (!configAtStart.enabled || configAtStart.global)
                    return;

                if (configAtStart.useOnlinePac)
                {
                    if (string.IsNullOrWhiteSpace(configAtStart.pacUrl))
                        return;

                    await OnlinePacCache.RefreshAsync(configAtStart);

                    // A reload may have changed the mode or URL while the request was in flight.
                    if (_config.enabled && !_config.global && _config.useOnlinePac &&
                        string.Equals(_config.pacUrl, configAtStart.pacUrl, StringComparison.Ordinal))
                    {
                        _pacServer.UpdatePACURL(_config);
                        UpdateSystemProxy();
                    }
                    return;
                }

                if (!GeositeUpdater.IsDatabaseAvailable || GeositeUpdater.NeedsRefresh)
                {
                    await GeositeUpdater.UpdatePACFromGeosite(false);
                    if (_config.enabled && !_config.global && !_config.useOnlinePac)
                    {
                        _pacServer.UpdatePACURL(_config);
                        UpdateSystemProxy();
                    }
                }
            }
            catch (Exception ex)
            {
                // Keep the last known-good cache (or the proxy-all bootstrap PAC) and
                // retry on the next relevant reload/start instead of breaking the client.
                logger.LogUsefulException(ex);
            }
        }

        public void CopyPacUrl()
        {
            Clipboard.SetDataObject(_pacServer.PacUrl);
        }

        public void SavePACUrl(string pacUrl)
        {
            _config.pacUrl = pacUrl;
            SaveConfig(_config);

            ConfigChanged?.Invoke(this, new EventArgs());
        }

        public void SaveGeositeSources(List<string> sources)
        {
            _config.geositeUrls = Configuration.NormalizeGeositeSourceList(sources);

            // The existing pac.txt may have been generated from a different set of
            // databases. Remove it before reload so Local PAC is rebuilt from the new
            // merged source set (or from the safe proxy-all bootstrap while downloading).
            if (!_config.useOnlinePac)
            {
                try
                {
                    File.Delete(PACDaemon.PAC_FILE);
                }
                catch (Exception ex)
                {
                    logger.LogUsefulException(ex);
                }
            }

            SaveConfig(_config);
        }

        public void UseOnlinePAC(bool useOnlinePac)
        {
            _config.useOnlinePac = useOnlinePac;
            SaveConfig(_config);

            ConfigChanged?.Invoke(this, new EventArgs());
        }

        public void TouchPACFile()
        {
            string pacFilename = _pacDaemon.TouchPACFile();

            PACFileReadyToOpen?.Invoke(this, new PathEventArgs() { Path = pacFilename });
        }

        public void TouchUserRuleFile()
        {
            string userRuleFilename = _pacDaemon.TouchUserRuleFile();

            UserRuleFileReadyToOpen?.Invoke(this, new PathEventArgs() { Path = userRuleFilename });
        }

        public void ToggleSecureLocalPac(bool enabled)
        {
            _config.secureLocalPac = enabled;
            SaveConfig(_config);

            ConfigChanged?.Invoke(this, new EventArgs());
        }

        public void ToggleRegeneratePacOnUpdate(bool enabled)
        {
            _config.regeneratePacOnUpdate = enabled;
            SaveConfig(_config);
            ConfigChanged?.Invoke(this, new EventArgs());
        }

        #endregion

        #region  SIP002

        public bool AskAddServerBySSURL(string ssURL)
        {
            var dr = MessageBox.Show(I18N.GetString("Import from URL: {0} ?", ssURL), I18N.GetString("Shadowsocks"), MessageBoxButtons.YesNo);
            if (dr == DialogResult.Yes)
            {
                if (AddServerBySSURL(ssURL))
                {
                    MessageBox.Show(I18N.GetString("Successfully imported from {0}", ssURL));
                    return true;
                }
                else
                {
                    MessageBox.Show(I18N.GetString("Failed to import. Please check if the link is valid."));
                }
            }
            return false;
        }

        public bool AddServerBySSURL(string ssURL)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ssURL))
                    return false;

                var servers = Server.GetServers(ssURL);
                if (servers == null || servers.Count == 0)
                    return false;

                foreach (var server in servers)
                {
                    _config.configs.Add(server);
                    if (server.warnLegacyUrl)
                        MessageBox.Show(I18N.GetString("Warning: importing {0} from a legacy ss:// link. Legacy ss:// links may be removed in a future release. Please update your ss:// links.", server.ToString()));
                }
                _config.index = _config.configs.Count - 1;
                SaveConfig(_config);
                return true;
            }
            catch (Exception e)
            {
                logger.LogUsefulException(e);
                return false;
            }
        }

        public string GetServerURLForCurrentServer()
        {
            return GetCurrentServer().GetURL(_config.generateLegacyUrl);
        }

        #endregion

        #region Misc

        public void ToggleVerboseLogging(bool enabled)
        {
            _config.isVerboseLogging = enabled;
            SaveConfig(_config);
            NLogConfig.LoadConfiguration(); // reload nlog

            VerboseLoggingStatusChanged?.Invoke(this, new EventArgs());
        }

        public void ToggleCheckingUpdate(bool enabled)
        {
            _config.autoCheckUpdate = enabled;
            Configuration.Save(_config);

            ConfigChanged?.Invoke(this, new EventArgs());
        }

        public void ToggleCheckingPreRelease(bool enabled)
        {
            _config.checkPreRelease = enabled;
            Configuration.Save(_config);
            ConfigChanged?.Invoke(this, new EventArgs());
        }

        public void SaveSkippedUpdateVerion(string version)
        {
            _config.skippedUpdateVersion = version;
            Configuration.Save(_config);
        }

        public void SaveLogViewerConfig(LogViewerConfig newConfig)
        {
            _config.logViewer = newConfig;
            Configuration.Save(_config);

            ConfigChanged?.Invoke(this, new EventArgs());
        }

        public void SaveHotkeyConfig(HotkeyConfig newConfig)
        {
            _config.hotkey = newConfig;
            SaveConfig(_config);

            ConfigChanged?.Invoke(this, new EventArgs());
        }

        #endregion

        #region Strategy

        public void SelectStrategy(string strategyID)
        {
            _config.index = -1;
            _config.strategy = strategyID;
            SaveConfig(_config);
        }

        public IList<IStrategy> GetStrategies()
        {
            return _strategyManager.GetStrategies();
        }

        public IStrategy GetCurrentStrategy()
        {
            foreach (var strategy in _strategyManager.GetStrategies())
            {
                if (strategy.ID == _config.strategy)
                {
                    return strategy;
                }
            }
            return null;
        }

        public void UpdateInboundCounter(object sender, SSTransmitEventArgs args)
        {
            GetCurrentStrategy()?.UpdateLastRead(args.server);
            Interlocked.Add(ref _inboundCounter, args.length);
        }

        public void UpdateOutboundCounter(object sender, SSTransmitEventArgs args)
        {
            GetCurrentStrategy()?.UpdateLastWrite(args.server);
            Interlocked.Add(ref _outboundCounter, args.length);
        }

        #endregion

        #region SIP003

        private void StartPlugin()
        {
            var server = _config.GetCurrentServer();
            GetPluginLocalEndPointIfConfigured(server);
        }

        private void StopPlugins()
        {
            foreach (var serverAndPlugin in _pluginsByServer)
            {
                serverAndPlugin.Value?.Dispose();
            }
            _pluginsByServer.Clear();
        }

        public EndPoint GetPluginLocalEndPointIfConfigured(Server server)
        {
            var plugin = _pluginsByServer.GetOrAdd(
                server,
                x => Sip003Plugin.CreateIfConfigured(x, _config.showPluginOutput));

            if (plugin == null)
            {
                return null;
            }

            try
            {
                if (plugin.StartIfNeeded())
                {
                    logger.Info(
                        $"Started SIP003 plugin for {server.Identifier()} on {plugin.LocalEndPoint} - PID: {plugin.ProcessId}");
                }
            }
            catch (Exception ex)
            {
                logger.Error("Failed to start SIP003 plugin: " + ex.Message);
                throw;
            }

            return plugin.LocalEndPoint;
        }

        public void ToggleShowPluginOutput(bool enabled)
        {
            _config.showPluginOutput = enabled;
            SaveConfig(_config);

            ShowPluginOutputChanged?.Invoke(this, new EventArgs());
        }

        #endregion

        #region Traffic Statistics

        private void StartTrafficStatistics(int queueMaxSize)
        {
            trafficPerSecondQueue = new Queue<TrafficPerSecond>();
            for (int i = 0; i < queueMaxSize; i++)
            {
                trafficPerSecondQueue.Enqueue(new TrafficPerSecond());
            }
            _trafficThread = new Thread(new ThreadStart(() => TrafficStatistics(queueMaxSize)))
            {
                IsBackground = true
            };
            _trafficThread.Start();
        }

        private void TrafficStatistics(int queueMaxSize)
        {
            TrafficPerSecond previous, current;
            while (true)
            {
                previous = trafficPerSecondQueue.Last();
                current = new TrafficPerSecond
                {
                    inboundCounter = InboundCounter,
                    outboundCounter = OutboundCounter
                };
                current.inboundIncreasement = current.inboundCounter - previous.inboundCounter;
                current.outboundIncreasement = current.outboundCounter - previous.outboundCounter;

                trafficPerSecondQueue.Enqueue(current);
                if (trafficPerSecondQueue.Count > queueMaxSize)
                    trafficPerSecondQueue.Dequeue();

                TrafficChanged?.Invoke(this, new EventArgs());

                Thread.Sleep(1000);
            }
        }

        #endregion

        #region SIP008


        public async Task<int> UpdateOnlineConfigInternal(string url)
        {
            var onlineServer = await OnlineConfigResolver.GetOnline(url);
            _config.configs = Configuration.SortByOnlineConfig(
                _config.configs
                .Where(c => c.group != url)
                .Concat(onlineServer)
                );
            logger.Info($"updated {onlineServer.Count} server from {url}");
            return onlineServer.Count;
        }

        public async Task<bool> UpdateOnlineConfig(string url)
        {
            var selected = GetCurrentServer();
            try
            {
                int count = await UpdateOnlineConfigInternal(url);
            }
            catch (Exception e)
            {
                logger.LogUsefulException(e);
                return false;
            }
            _config.index = _config.configs.IndexOf(selected);
            SaveConfig(_config);
            return true;
        }

        public async Task<List<string>> UpdateAllOnlineConfig()
        {
            var selected = GetCurrentServer();
            var failedUrls = new List<string>();
            foreach (var url in _config.onlineConfigSource)
            {
                try
                {
                    await UpdateOnlineConfigInternal(url);
                }
                catch (Exception e)
                {
                    logger.LogUsefulException(e);
                    failedUrls.Add(url);
                }
            }

            _config.index = _config.configs.IndexOf(selected);
            SaveConfig(_config);
            return failedUrls;
        }

        public void SaveOnlineConfigSource(List<string> sources)
        {
            _config.onlineConfigSource = sources;
            SaveConfig(_config);
        }

        public void RemoveOnlineConfig(string url)
        {
            _config.onlineConfigSource.RemoveAll(v => v == url);
            _config.configs = Configuration.SortByOnlineConfig(
                _config.configs.Where(c => c.group != url)
                );
            SaveConfig(_config);
        }

        #endregion
    }
}
