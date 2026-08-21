using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Shadowsocks.Core.Logging;
using Shadowsocks.Controller.Service;
using Shadowsocks.Controller.Strategy;
using Shadowsocks.Controller.Traffic;
using Shadowsocks.Model;
using Shadowsocks.Core;

namespace Shadowsocks.Controller
{
    public partial class ShadowsocksController
    {
        private readonly Logger logger;
        private readonly HttpClient httpClient;
        private readonly IUserInteractionService userInteraction;

        // Application controller: coordinates configuration, proxy services and routing.
        // User-facing operations are delegated through IUserInteractionService.
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
        private readonly DnsCryptComponentManager _dnsCryptComponentManager;
        private readonly DnsCryptRuntimeManager _dnsCryptRuntimeManager;
        private readonly DnsCryptCoordinator _dnsCryptCoordinator;
        private IReadOnlyList<DnsCryptResolverInfo> _dnsCryptResolverCatalog = Array.Empty<DnsCryptResolverInfo>();
        private IReadOnlyList<string> _dnsCryptAutomaticServerNames = Array.Empty<string>();
        private DateTimeOffset _dnsCryptResolverCatalogLoadedUtc;
        private readonly SemaphoreSlim _dnsCaptureRefreshGate = new(1, 1);
        private int _suppressDnsCaptureRefresh;
        private Version _latestDnsCryptVersion;
        private ManagedHttpProxyService _managedHttpProxy;
        private readonly ConcurrentDictionary<Server, Sip003Plugin> _pluginsByServer;
        private Exception _lastListenerError;

        private long _inboundCounter = 0;
        private long _outboundCounter = 0;
        public long InboundCounter => Interlocked.Read(ref _inboundCounter);
        public long OutboundCounter => Interlocked.Read(ref _outboundCounter);
        public Queue<TrafficPerSecond> trafficPerSecondQueue;

        private bool stopped = false;
        private long _trafficConfigurationGeneration;

        public class PathEventArgs : EventArgs
        {
            public string Path;
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
        public event EventHandler ShowDnsLogsChanged;
        public event EventHandler TrafficChanged;
        public event EventHandler TrafficModeChanged;
        public event EventHandler DnsCryptStatusChanged;

        // when user clicked Edit PAC, and PAC file has already created
        public event EventHandler<PathEventArgs> PACFileReadyToOpen;
        public event EventHandler<PathEventArgs> UserRuleFileReadyToOpen;

        public event EventHandler<GeositeResultEventArgs> UpdatePACFromGeositeCompleted;

        public event ErrorEventHandler UpdatePACFromGeositeError;

        public event ErrorEventHandler Errored;

        #endregion

        public ShadowsocksController(IUserInteractionService userInteraction = null)
        {
            this.userInteraction = userInteraction ?? NullUserInteractionService.Instance;
            logger = LogManager.GetCurrentClassLogger();
            httpClient = new HttpClient();
            _config = Configuration.Load();
            Configuration.Process(ref _config);
            _strategyManager = new StrategyManager(() => _config);
            _trafficPolicyEngine = new TrafficPolicyEngine(_config);
            _adminCaptureManager = new AdminCaptureManager();
            _gameModeManager = new GameModeManager(_adminCaptureManager);
            _gameModeManager.StatusChanged += (_, _) => TrafficModeChanged?.Invoke(this, EventArgs.Empty);
            _dnsCryptComponentManager = new DnsCryptComponentManager();
            _dnsCryptRuntimeManager = new DnsCryptRuntimeManager(
                _dnsCryptComponentManager,
                () => _config?.showDnsLogs == true,
                () => _config?.isVerboseLogging == true);
            _dnsCryptCoordinator = new DnsCryptCoordinator();
            _dnsCryptRuntimeManager.StatusChanged += (_, _) =>
            {
                RememberAutomaticDnsCryptRuntimeResolver();
                DnsCryptStatusChanged?.Invoke(this, EventArgs.Empty);
                if (Volatile.Read(ref _suppressDnsCaptureRefresh) == 0)
                    _ = RefreshDnsCaptureAfterRuntimeChangeAsync();
            };
            _dnsCryptRuntimeManager.ResolverMetricsChanged += (_, _) =>
            {
                RememberAutomaticDnsCryptRuntimeResolver();
                DnsCryptStatusChanged?.Invoke(this, EventArgs.Empty);
            };
            _pluginsByServer = new ConcurrentDictionary<Server, Sip003Plugin>();
            StartTrafficStatistics(61);

        }

        #region Basic

        public Exception LastListenerError => _lastListenerError;
        public bool IsProxyListenerRunning => _listener is not null && _lastListenerError is null;

        public void Start(bool systemWakeUp = false)
        {
            _dnsCryptCoordinator.Resume();
            stopped = false;
            if (_config.firstRunOnNewVersion && !systemWakeUp)
            {
                string previousVersion = string.IsNullOrWhiteSpace(_config.version) ? "unknown" : _config.version;
                logger.Info("Updated from {0} to {1}", previousVersion, ApplicationInfo.Version);
                // Delete pac.txt when regeneratePacOnUpdate is true.
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
                _config.version = ApplicationInfo.Version;
                Configuration.Save(_config);
            }
            Reload();
            StartDnsCryptMaintenance();
        }

        public void Stop()
        {
            if (stopped)
            {
                return;
            }
            stopped = true;
            // Cancel DNSCrypt downloads/update validation before tearing down capture/runtime.
            // This is also used by Windows suspend, so no maintenance task keeps Clean Mode
            // files or runtime processes alive across Stop()/Start().
            StopDnsCryptMaintenance();

            // A manual install/update/settings transaction can still be running while the app
            // exits or Windows suspends. Cancel it, wait for the shared DNS transaction gate,
            // then tear capture down before the local DNS listener.
            try
            {
                _dnsCryptCoordinator.SuspendAndExecuteAsync(
                    "Stop DNSCrypt subsystem",
                    async _ =>
                    {
                        Interlocked.Increment(ref _suppressDnsCaptureRefresh);
                        try
                        {
                            try
                            {
                                await _gameModeManager.StopAsync().ConfigureAwait(false);
                            }
                            catch (Exception exception)
                            {
                                logger.LogUsefulException(exception);
                            }

                            try
                            {
                                await _dnsCryptRuntimeManager.StopAsync().ConfigureAwait(false);
                            }
                            catch (Exception exception)
                            {
                                logger.LogUsefulException(exception);
                            }
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _suppressDnsCaptureRefresh);
                        }
                    }).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                logger.LogUsefulException(exception);
            }
            if (_listener != null)
            {
                _listener.Stop();
            }
            _listener = null;

            GeositeUpdater.UpdateCompleted -= PacServer_PACUpdateCompleted;
            GeositeUpdater.Error -= PacServer_PACUpdateError;
            if (_pacDaemon != null)
            {
                _pacDaemon.PACFileChanged -= PacDaemon_PACFileChanged;
                _pacDaemon.UserRuleFileChanged -= PacDaemon_UserRuleFileChanged;
                _pacDaemon.Dispose();
                _pacDaemon = null;
            }
            _pacServer = null;

            StopPlugins();
            if (_config.enabled)
            {
                SystemProxy.Update(_config, true, null, userInteraction);
            }
            Encryption.RNG.Close();
        }

        protected void Reload()
        {
            Encryption.RNG.Reload();
            // some logic in configuration updated the config when saving, we need to read it again
            _config = Configuration.Load();
            Configuration.Process(ref _config);
            long trafficConfigurationGeneration = Interlocked.Increment(ref _trafficConfigurationGeneration);
            if (!_config.HasConfiguredServer)
            {
                logger.Warn(
                    "No Shadowsocks server is configured. Proxy relay will stay inactive until a server is added. " +
                    "Settings are stored under the active application data root.");
            }
            if (!_config.useOnlinePac)
                GeositeUpdater.ConfigureSources(_config.geositeUrls);

            LoggingConfigurator.Configure(_config.isVerboseLogging);

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
            _lastListenerError = null;

            try
            {
                var strategy = GetCurrentStrategy();
                strategy?.ReloadServers();

                StartPlugin();
                _trafficPolicyEngine.UpdateConfiguration(_config);
                _managedHttpProxy = new ManagedHttpProxyService(_trafficPolicyEngine, _config);

                List<Listener.IService> services = new List<Listener.IService>
                {
                    _pacServer,
                    _managedHttpProxy
                };

                if (_config.HasConfiguredServer)
                {
                    TCPRelay tcpRelay = new TCPRelay(this, _config);
                    tcpRelay.OnInbound += UpdateInboundCounter;
                    tcpRelay.OnOutbound += UpdateOutboundCounter;
                    tcpRelay.OnFailed += (o, e) => GetCurrentStrategy()?.SetFailure(e.server);

                    services.Insert(0, new UDPRelay(this));
                    services.Insert(0, tcpRelay);
                }
                _listener = new Listener(services);
                _listener.Start(_config);
                _ = ApplyTrafficCaptureAsync(_config, trafficConfigurationGeneration);
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
                _lastListenerError = e;
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
        public SystemProxyMode GetSystemProxyMode() => SystemProxy.GetCurrentMode(_config, _pacServer);

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

        public void CompleteFirstRun()
        {
            if (!_config.firstRun)
            {
                return;
            }

            _config.firstRun = false;
            Configuration.Save(_config);
            ConfigChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SaveServers(List<Server> servers, int localPort)
        {
            _config.configs = servers;
            _config.localPort = localPort;
            Configuration.Save(_config);
            QueueDnsCryptAutomaticResolverRefresh();
        }

        public void SelectServerIndex(int index)
        {
            _config.index = index;
            _config.strategy = null;
            SaveConfig(_config);
            QueueDnsCryptAutomaticResolverRefresh();
        }

        public bool RemoveServerAt(int index)
        {
            if (_config.configs is null || index < 0 || index >= _config.configs.Count)
            {
                return false;
            }

            _config.configs.RemoveAt(index);
            if (_config.configs.Count == 0)
            {
                _config.configs.Add(new Server());
                _config.index = 0;
            }
            else if (_config.index > index)
            {
                _config.index--;
            }
            else if (_config.index >= _config.configs.Count)
            {
                _config.index = _config.configs.Count - 1;
            }

            SaveConfig(_config);
            return true;
        }

        public void ToggleShareOverLAN(bool enabled)
        {
            _config.shareOverLan = enabled;
            SaveConfig(_config);

            ShareOverLANStatusChanged?.Invoke(this, new EventArgs());
        }

        public void SetLocalPort(int localPort)
        {
            Configuration.CheckLocalPort(localPort);
            if (_config.localPort == localPort)
            {
                return;
            }

            _config.localPort = localPort;
            SaveConfig(_config);
        }

        public void SetUiTheme(string theme)
        {
            string normalizedTheme = Configuration.NormalizeUiThemePreference(theme);
            if (string.Equals(_config.uiTheme, normalizedTheme, StringComparison.Ordinal))
            {
                return;
            }

            _config.uiTheme = normalizedTheme;
            Configuration.Save(_config);
            ConfigChanged?.Invoke(this, EventArgs.Empty);
        }

        #endregion


        #region Traffic routing

        public TrafficRuntimeMode GetTrafficRuntimeMode() => _gameModeManager.RuntimeMode;

        public TrafficCaptureStatus GetTrafficCaptureStatus()
        {
            return new TrafficCaptureStatus(
                _config.trafficCaptureMode,
                _gameModeManager.RuntimeMode,
                _adminCaptureManager.IsBrokerRunning,
                _adminCaptureManager.IsCaptureActive,
                _gameModeManager.AutomaticGameMode || _adminCaptureManager.IsGameMode,
                _adminCaptureManager.IsCaptureActive && _adminCaptureManager.TcpRedirectPort > 0,
                _adminCaptureManager.IsCaptureActive && _adminCaptureManager.UdpRedirectPort > 0,
                _adminCaptureManager.DnsInterceptionActive,
                _adminCaptureManager.DnsFailClosedActive,
                _adminCaptureManager.TcpRedirectPort,
                _adminCaptureManager.UdpRedirectPort,
                _gameModeManager.RunningGameApplications.ToArray());
        }

        public Task<bool> SetTrafficCaptureModeAsync(TrafficCaptureMode mode)
        {
            return _dnsCryptCoordinator.ExecuteExclusiveAsync(
                "Set traffic capture mode",
                cancellationToken => SetTrafficCaptureModeCoreAsync(mode, cancellationToken));
        }

        private async Task<bool> SetTrafficCaptureModeCoreAsync(
            TrafficCaptureMode mode,
            CancellationToken cancellationToken)
        {
            TrafficCaptureMode previousMode = _config.trafficCaptureMode;
            _config.trafficCaptureMode = mode;
            _trafficPolicyEngine.UpdateConfiguration(_config);

            try
            {
                await ApplyTrafficCaptureConfigurationCoreAsync(_config, cancellationToken).ConfigureAwait(false);
                await StopDnsCryptRuntimeWhenNotRequiredAsync(cancellationToken).ConfigureAwait(false);
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
                await ApplyTrafficCaptureConfigurationCoreAsync(_config, CancellationToken.None).ConfigureAwait(false);
                await StopDnsCryptRuntimeWhenNotRequiredAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogUsefulException(exception);
            }
            TrafficModeChanged?.Invoke(this, EventArgs.Empty);
            ConfigChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        public Task<bool> SaveTrafficRoutingAsync(
            TrafficCaptureMode captureMode,
            IEnumerable<ApplicationRouteRule> applicationRules,
            IEnumerable<string> gameModeApplications)
        {
            ApplicationRouteRule[] rulesSnapshot = (applicationRules ?? []).ToArray();
            string[] gamesSnapshot = (gameModeApplications ?? []).ToArray();
            return _dnsCryptCoordinator.ExecuteExclusiveAsync(
                "Save traffic routing",
                cancellationToken => SaveTrafficRoutingCoreAsync(captureMode, rulesSnapshot, gamesSnapshot, cancellationToken));
        }

        private async Task<bool> SaveTrafficRoutingCoreAsync(
            TrafficCaptureMode captureMode,
            IEnumerable<ApplicationRouteRule> applicationRules,
            IEnumerable<string> gameModeApplications,
            CancellationToken cancellationToken)
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
                await ApplyTrafficCaptureConfigurationCoreAsync(_config, cancellationToken).ConfigureAwait(false);
                await StopDnsCryptRuntimeWhenNotRequiredAsync(cancellationToken).ConfigureAwait(false);
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
                await ApplyTrafficCaptureConfigurationCoreAsync(_config, CancellationToken.None).ConfigureAwait(false);
                await StopDnsCryptRuntimeWhenNotRequiredAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogUsefulException(exception);
            }
            TrafficModeChanged?.Invoke(this, EventArgs.Empty);
            ConfigChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        private async Task StopDnsCryptRuntimeWhenNotRequiredAsync(CancellationToken cancellationToken)
        {
            if (IsDnsCryptRuntimeRequired() || !_dnsCryptRuntimeManager.GetStatus().IsServing)
            {
                return;
            }

            Interlocked.Increment(ref _suppressDnsCaptureRefresh);
            try
            {
                await _dnsCryptRuntimeManager.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _suppressDnsCaptureRefresh);
            }
        }

        private Task ApplyTrafficCaptureAsync(
            Configuration configurationAtStart,
            long configurationGeneration)
        {
            return _dnsCryptCoordinator.ExecuteExclusiveAsync(
                "Apply traffic capture configuration",
                cancellationToken => ApplyTrafficCaptureStartupCoreAsync(
                    configurationAtStart,
                    configurationGeneration,
                    cancellationToken));
        }

        private async Task ApplyTrafficCaptureStartupCoreAsync(
            Configuration configurationAtStart,
            long configurationGeneration,
            CancellationToken cancellationToken)
        {
            if (stopped
                || configurationGeneration != Volatile.Read(ref _trafficConfigurationGeneration)
                || !ReferenceEquals(_config, configurationAtStart))
            {
                return;
            }

            try
            {
                await ApplyTrafficCaptureConfigurationCoreAsync(
                    configurationAtStart,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AdminElevationCanceledException exception)
            {
                logger.Info(exception, "Administrator elevation for Admin Mode was cancelled.");
                if (!stopped
                    && configurationGeneration == Volatile.Read(ref _trafficConfigurationGeneration)
                    && ReferenceEquals(_config, configurationAtStart)
                    && _config.trafficCaptureMode == TrafficCaptureMode.Admin)
                {
                    _config.trafficCaptureMode = TrafficCaptureMode.User;
                    Configuration.Save(_config);
                    TrafficModeChanged?.Invoke(this, EventArgs.Empty);
                    ConfigChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The coordinator caller was cancelled before this configuration became active.
            }
            catch (Exception exception)
            {
                logger.LogUsefulException(exception);
                if (!stopped
                    && configurationGeneration == Volatile.Read(ref _trafficConfigurationGeneration)
                    && ReferenceEquals(_config, configurationAtStart)
                    && _config.trafficCaptureMode == TrafficCaptureMode.Admin)
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
            int dnsCryptProcessId = _dnsCryptRuntimeManager.GetStatus().ProcessId;
            return _pluginsByServer.Values
                .Where(plugin => plugin != null)
                .Select(plugin => plugin.ProcessId)
                .Where(processId => processId > 0)
                .Append(Environment.ProcessId)
                .Append(dnsCryptProcessId)
                .Where(processId => processId > 0)
                .Distinct()
                .ToArray();
        }

        private DnsCaptureRuntimeState GetDnsCaptureRuntimeState()
        {
            DnsCryptRuntimeStatus runtime = _dnsCryptRuntimeManager.GetStatus();
            return runtime.IsServing
                ? new DnsCaptureRuntimeState(runtime.Port, runtime.ProcessId)
                : DnsCaptureRuntimeState.Unavailable;
        }

        private async Task EnsureDnsCryptRuntimeForCaptureAsync(Configuration configuration, CancellationToken cancellationToken)
        {
            if (configuration?.dnsPolicy?.mode != DnsPolicyMode.DnsCrypt)
            {
                return;
            }

            try
            {
                DnsCryptBootstrapPolicy.Validate(configuration, configuration.dnsPolicy?.dnsCrypt);
            }
            catch (InvalidOperationException exception)
            {
                logger.Error(exception, "DNSCrypt runtime was disabled to prevent a DNS bootstrap recursion loop.");
                if (_dnsCryptRuntimeManager.GetStatus().IsServing)
                {
                    Interlocked.Increment(ref _suppressDnsCaptureRefresh);
                    try
                    {
                        await _dnsCryptRuntimeManager.StopAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _suppressDnsCaptureRefresh);
                    }
                }
                return;
            }

            if (_dnsCryptRuntimeManager.GetStatus().IsServing)
                return;

            DnsCryptComponentStatus component = _dnsCryptComponentManager.GetStatus();
            if (!component.IsInstalled)
            {
                logger.Warn("DNSCrypt mode is selected but DNSCrypt Proxy is not installed.");
                return;
            }

            Interlocked.Increment(ref _suppressDnsCaptureRefresh);
            try
            {
                await StartDnsCryptWithResolvedResolversAsync(
                    configuration.dnsPolicy?.dnsCrypt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // DNSCrypt is intentionally fail-closed. Capture configuration must still be
                // applied with an unavailable runtime so NetworkService blocks UDP/TCP 53
                // instead of silently falling back to plaintext system DNS.
                logger.Warn(exception, "DNSCrypt runtime could not be prepared; DNS/53 will remain fail-closed.");
            }
            finally
            {
                Interlocked.Decrement(ref _suppressDnsCaptureRefresh);
            }
        }

        private async Task ApplyTrafficCaptureConfigurationCoreAsync(
            Configuration configuration,
            CancellationToken cancellationToken = default,
            bool ensureDnsRuntime = true)
        {
            if (ensureDnsRuntime)
                await EnsureDnsCryptRuntimeForCaptureAsync(configuration, cancellationToken).ConfigureAwait(false);

            await _gameModeManager.ApplyConfigurationAsync(
                configuration,
                GetCaptureExclusionProcessIds(),
                GetDnsCaptureRuntimeState(),
                cancellationToken).ConfigureAwait(false);
        }

        private Task RefreshDnsCaptureAfterRuntimeChangeAsync()
        {
            return _dnsCryptCoordinator.ExecuteExclusiveAsync(
                "Refresh DNS capture",
                RefreshDnsCaptureAfterRuntimeChangeCoreAsync);
        }

        private async Task RefreshDnsCaptureAfterRuntimeChangeCoreAsync(CancellationToken cancellationToken)
        {
            if (stopped
                || Volatile.Read(ref _suppressDnsCaptureRefresh) != 0
                || _config.trafficCaptureMode != TrafficCaptureMode.Admin)
            {
                return;
            }

            await _dnsCaptureRefreshGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (stopped
                    || Volatile.Read(ref _suppressDnsCaptureRefresh) != 0
                    || _config.trafficCaptureMode != TrafficCaptureMode.Admin)
                {
                    return;
                }

                DnsCaptureRuntimeState dnsRuntime = GetDnsCaptureRuntimeState();
                if (!_adminCaptureManager.IsBrokerRunning)
                {
                    // A game may have prevented the first Admin elevation. Keep the cached
                    // DNS runtime current without creating an unexpected UAC prompt; the
                    // latest endpoint will be used when automatic Game Mode ends.
                    await _gameModeManager.UpdateDnsRuntimeAsync(dnsRuntime).ConfigureAwait(false);
                    return;
                }

                await _gameModeManager.ApplyConfigurationAsync(
                    _config,
                    GetCaptureExclusionProcessIds(),
                    dnsRuntime,
                    cancellationToken).ConfigureAwait(false);
                TrafficModeChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                logger.Warn(exception, "Unable to refresh Admin DNS capture after a DNSCrypt runtime change.");
            }
            finally
            {
                _dnsCaptureRefreshGate.Release();
            }
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
            SystemProxy.Update(_config, false, _pacServer, userInteraction);
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
                    await GeositeUpdater.UpdatePACFromGeosite(_config, false);
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

        public string GetPacUrl() => _pacServer.PacUrl;

        public void CopyPacUrl()
        {
            userInteraction.SetClipboardText(_pacServer.PacUrl);
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

        public Task<bool> UpdatePACFromGeositeAsync()
        {
            return GeositeUpdater.UpdatePACFromGeosite(_config);
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
            if (userInteraction.Confirm(I18N.GetString("Import from URL: {0} ?", ssURL), I18N.GetString("shadowsocks-reborn")))
            {
                if (AddServerBySSURL(ssURL))
                {
                    userInteraction.ShowInfo(I18N.GetString("Successfully imported from {0}", ssURL), I18N.GetString("shadowsocks-reborn"));
                    return true;
                }
                else
                {
                    userInteraction.ShowError(I18N.GetString("Failed to import. Please check if the link is valid."), I18N.GetString("shadowsocks-reborn"));
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
                }
                _config.index = _config.configs.Count - 1;
                SaveConfig(_config);
                ConfigChanged?.Invoke(this, EventArgs.Empty);
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
            return GetCurrentServer().GetURL();
        }

        #endregion

        #region Misc

        public void ToggleVerboseLogging(bool enabled)
        {
            if (_config.isVerboseLogging == enabled)
                return;

            _config.isVerboseLogging = enabled;
            // Log level changes do not require a network/controller reload. Reconfigure NLog
            // in place so toggling a logging option never restarts listeners or SIP003 plugins.
            Configuration.Save(_config);
            LoggingConfigurator.Configure(enabled);

            VerboseLoggingStatusChanged?.Invoke(this, EventArgs.Empty);
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
            // Viewer-only preferences do not affect proxy/runtime state. Broadcasting a global
            // configuration event here caused MainWindow to refresh and rebuild the Logs page.
            Configuration.Save(_config);
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
            QueueDnsCryptAutomaticResolverRefresh();
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
            if (server?.IsConfigured != true || string.IsNullOrWhiteSpace(server.plugin))
            {
                return;
            }

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
            if (server?.IsConfigured != true || string.IsNullOrWhiteSpace(server.plugin))
            {
                return null;
            }

            var plugin = _pluginsByServer.GetOrAdd(
                server,
                x => Sip003Plugin.CreateIfConfigured(x, () => _config.showPluginOutput));

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
            if (_config.showPluginOutput == enabled)
                return;

            _config.showPluginOutput = enabled;
            // Logging visibility is a presentation/runtime flag. A full controller reload restarts
            // the listener and SIP003 process, which is unnecessary and can block plugin shutdown.
            Configuration.Save(_config);
            ShowPluginOutputChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleShowDnsLogs(bool enabled)
        {
            if (_config.showDnsLogs == enabled)
                return;

            _config.showDnsLogs = enabled;
            Configuration.Save(_config);

            ShowDnsLogsChanged?.Invoke(this, EventArgs.Empty);
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
            var onlineServer = await OnlineConfigResolver.GetOnline(httpClient, url);
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
