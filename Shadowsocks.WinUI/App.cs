using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using NLog;
using Shadowsocks.Controller;
using Shadowsocks.Controller.Traffic;
using Shadowsocks.Core;
using Shadowsocks.Core.Logging;
using Shadowsocks.Core.Storage;
using Shadowsocks.Model;
using Shadowsocks.Localization;
using Shadowsocks.WinUI.Shell;
using Shadowsocks.Windows.Shell;
using Shadowsocks.Windows.WinUI.Shell;
using Shadowsocks.Windows.Storage;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;

namespace Shadowsocks.WinUI;

public sealed partial class App : Application
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static AppInstance? s_startupAppInstance;
    private static AppActivationArguments? s_startupActivation;
    private static ILocalizationService? s_startupLocalization;

    private readonly AppInstance _appInstance;
    private readonly AppActivationArguments _initialActivation;
    private readonly ILocalizationService _localization;

    private DispatcherQueue? _dispatcherQueue;
    private ShadowsocksController? _controller;
    private MainWindow? _window;
    private TrayIconService? _trayIcon;
    private WinUIHotkeyManager? _hotkeyManager;
    private NativeUserInteractionService? _userInteraction;
    private PowerModeMonitor? _powerModeMonitor;
    private UpdateChecker? _startupUpdateChecker;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _resumeRestartCancellation;
    private readonly ConcurrentQueue<Exception> _pendingControllerErrors = new();
    private bool _controllerErrorDialogActive;
    private bool _alreadyRunningDialogActive;
    private bool _shuttingDown;

    public App()
    {
        _appInstance = s_startupAppInstance
            ?? throw new InvalidOperationException("WinUI startup context was not configured before App creation.");
        _initialActivation = s_startupActivation
            ?? throw new InvalidOperationException("WinUI activation context was not configured before App creation.");
        _localization = s_startupLocalization
            ?? throw new InvalidOperationException("WinUI localization service was not configured before App creation.");

        s_startupAppInstance = null;
        s_startupActivation = null;
        s_startupLocalization = null;

        AppStoragePaths.EnsureStorageDirectories();
        LoggingConfigurator.Configure();
        WindowsStorageBootstrapper.Initialize();
        Directory.SetCurrentDirectory(AppStoragePaths.RuntimeRoot);

        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Failed to initialize the WinUI application resource dictionary.");
            throw;
        }

        UnhandledException += OnUnhandledException;
    }

    internal static void ConfigureStartupContext(
        AppInstance appInstance,
        AppActivationArguments initialActivation,
        ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(appInstance);
        ArgumentNullException.ThrowIfNull(initialActivation);
        ArgumentNullException.ThrowIfNull(localization);

        if (s_startupAppInstance is not null || s_startupActivation is not null || s_startupLocalization is not null)
        {
            throw new InvalidOperationException("WinUI startup context has already been configured.");
        }

        s_startupAppInstance = appInstance;
        s_startupActivation = initialActivation;
        s_startupLocalization = localization;
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs _)
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        Program.RegisterActivationHandler(OnRedirectedActivation);
        if (!AppStoragePaths.IsCleanMode)
        {
            AutoStartup.RegisterForRestart(true);
        }

        try
        {
            _userInteraction = new NativeUserInteractionService();
            _controller = new ShadowsocksController(_userInteraction);
            SubscribeControllerEvents(_controller);
            _controller.Start();
        }
        catch (Exception exception)
        {
            _pendingControllerErrors.Enqueue(exception);
            Logger.Error(exception, "Failed to start the Shadowsocks controller from the WinUI shell.");
        }

        _window = new MainWindow(
            _controller,
            AutoStartup.Check,
            SetStartWithWindows,
            RegisterHotkeys,
            ApplyHotkeys,
            _localization);
        if (_userInteraction is not null)
        {
            _userInteraction.OwnerWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        }
        if (_window.Content is FrameworkElement contentRoot)
        {
            contentRoot.Loaded += OnMainWindowContentLoaded;
        }
        try
        {
            InitializeSystemShell();
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Failed to initialize one or more WinUI system-shell services.");
            _window.SetShellStatus(_localization.Format("Shell initialization warning: {0}", exception.Message), InfoBarSeverity.Warning);
        }

        try
        {
            InitializePowerModeMonitor();
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Failed to initialize Windows suspend/resume monitoring.");
            _window.SetShellStatus(_localization.Format("Shell initialization warning: {0}", exception.Message), InfoBarSeverity.Warning);
        }

        ScheduleStartupOnlineConfigRefresh();

        Configuration? startupConfiguration = _controller?.GetCurrentConfiguration();
        bool firstRun = startupConfiguration?.firstRun == true;
        if (firstRun)
        {
            // Match the original Shadowsocks startup behavior: only the first run
            // opens configuration. Normal launches remain tray-only until the user
            // explicitly requests a window.
            _window.NavigateToServers();
            _window.ShowFromTray();
        }
        else if (startupConfiguration?.autoCheckUpdate == true)
        {
            StartAutomaticUpdateCheck();
        }

        ProcessActivation(_initialActivation, showWindow: false);

        // Controller startup errors (for example, an occupied local port) still
        // need a visible XamlRoot so the user sees the same explicit error flow
        // as the legacy client. This is an exceptional path, not normal startup.
        if (!firstRun && !_pendingControllerErrors.IsEmpty)
        {
            ShowMainWindow();
        }
    }

    private void InitializePowerModeMonitor()
    {
        if (_window is null)
        {
            throw new InvalidOperationException("The main WinUI window is not initialized.");
        }

        _powerModeMonitor = new PowerModeMonitor(_window);
        _powerModeMonitor.Suspending += OnSystemSuspending;
        _powerModeMonitor.Resumed += OnSystemResumed;
    }

    private void OnSystemSuspending(object? sender, EventArgs e)
    {
        _resumeRestartCancellation?.Cancel();
        _resumeRestartCancellation?.Dispose();
        _resumeRestartCancellation = null;

        ShadowsocksController? controller = _controller;
        if (controller is null || _shuttingDown)
        {
            return;
        }

        try
        {
            controller.Stop();
            Logger.Info("Controller stopped because Windows is suspending.");
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Controller stop failed while Windows was suspending.");
        }
    }

    private void OnSystemResumed(object? sender, EventArgs e)
    {
        if (_controller is null || _shuttingDown)
        {
            return;
        }

        _resumeRestartCancellation?.Cancel();
        _resumeRestartCancellation?.Dispose();
        _resumeRestartCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        CancellationToken cancellationToken = _resumeRestartCancellation.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested || _shuttingDown)
                {
                    return;
                }

                ShadowsocksController? controller = _controller;
                if (controller is null)
                {
                    return;
                }

                controller.Start(systemWakeUp: true);
                Logger.Info("Controller restarted after Windows resumed.");
            }
            catch (OperationCanceledException)
            {
                // Normal during a second suspend/resume cycle or application shutdown.
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Controller restart failed after Windows resumed.");
                _pendingControllerErrors.Enqueue(exception);
                _ = _dispatcherQueue?.TryEnqueue(() =>
                {
                    ShowMainWindow();
                    DrainControllerErrorQueue();
                });
            }
        }, cancellationToken);
    }

    private void ScheduleStartupOnlineConfigRefresh()
    {
        if (_controller is null)
        {
            return;
        }

        CancellationToken cancellationToken = _lifetimeCancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested || _shuttingDown)
                {
                    return;
                }

                ShadowsocksController? controller = _controller;
                if (controller is null)
                {
                    return;
                }

                IReadOnlyList<string> failures = await controller.UpdateAllOnlineConfig().ConfigureAwait(false);
                if (failures.Count > 0)
                {
                    Logger.Warn("Startup online-config refresh completed with {0} failure(s): {1}", failures.Count, string.Join(", ", failures));
                }
                else
                {
                    Logger.Info("Startup online-config refresh completed.");
                }
            }
            catch (OperationCanceledException)
            {
                // Normal during application shutdown.
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Startup online-config refresh failed.");
            }
        }, cancellationToken);
    }

    private IReadOnlyList<string> RegisterHotkeys(HotkeyConfig config)
    {
        if (_controller is null)
        {
            return new[] { _localization["Controller unavailable"] };
        }

        return _hotkeyManager?.ApplyConfiguration(config) ?? Array.Empty<string>();
    }

    private IReadOnlyList<string> ApplyHotkeys(HotkeyConfig config)
    {
        if (_controller is null)
        {
            return new[] { _localization["Controller unavailable"] };
        }

        _controller.SaveHotkeyConfig(config);
        return _hotkeyManager?.ApplyConfiguration(config) ?? Array.Empty<string>();
    }

    private void InitializeSystemShell()
    {
        if (_window is null || _dispatcherQueue is null)
        {
            throw new InvalidOperationException("The main WinUI window is not initialized.");
        }

        _trayIcon = new TrayIconService(
            RuntimeEnvironment.ExecutablePath,
            BuildTrayMenuState(),
            text => _localization[text]);
        _trayIcon.CommandRequested += OnTrayCommandRequested;

        if (_controller is not null)
        {
            _hotkeyManager = new WinUIHotkeyManager(
                _controller,
                _dispatcherQueue,
                ShowMainWindow,
                _window.NavigateToLogs);
            var hotkeyFailures = _hotkeyManager.RegisterConfiguredHotkeys();
            if (hotkeyFailures.Count > 0)
            {
                _window.SetShellStatus(_localization.Format("Tray active · {0} hotkey(s) failed to register", hotkeyFailures.Count), InfoBarSeverity.Warning);
            }
            else
            {
                _window.SetShellStatus(_localization["Single instance · tray active · system shell ready"]);
            }
        }
        else
        {
            _window.SetShellStatus(_localization["Single instance · tray active · controller unavailable"]);
        }
    }

    private void SubscribeControllerEvents(ShadowsocksController controller)
    {
        controller.ConfigChanged += OnControllerStateChanged;
        controller.EnableStatusChanged += OnControllerStateChanged;
        controller.EnableGlobalChanged += OnControllerStateChanged;
        controller.ShareOverLANStatusChanged += OnControllerStateChanged;
        controller.VerboseLoggingStatusChanged += OnControllerStateChanged;
        controller.ShowPluginOutputChanged += OnControllerStateChanged;
        controller.TrafficModeChanged += OnControllerStateChanged;
        controller.TrafficChanged += OnControllerTrafficChanged;
        controller.PACFileReadyToOpen += OnControllerPathReadyToOpen;
        controller.UserRuleFileReadyToOpen += OnControllerPathReadyToOpen;
        controller.Errored += OnControllerErrored;
    }

    private void UnsubscribeControllerEvents(ShadowsocksController controller)
    {
        controller.ConfigChanged -= OnControllerStateChanged;
        controller.EnableStatusChanged -= OnControllerStateChanged;
        controller.EnableGlobalChanged -= OnControllerStateChanged;
        controller.ShareOverLANStatusChanged -= OnControllerStateChanged;
        controller.VerboseLoggingStatusChanged -= OnControllerStateChanged;
        controller.ShowPluginOutputChanged -= OnControllerStateChanged;
        controller.TrafficModeChanged -= OnControllerStateChanged;
        controller.TrafficChanged -= OnControllerTrafficChanged;
        controller.PACFileReadyToOpen -= OnControllerPathReadyToOpen;
        controller.UserRuleFileReadyToOpen -= OnControllerPathReadyToOpen;
        controller.Errored -= OnControllerErrored;
    }

    private void OnMainWindowContentLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement contentRoot)
        {
            contentRoot.Loaded -= OnMainWindowContentLoaded;
        }

        DrainControllerErrorQueue();
    }

    private void OnControllerErrored(object? _, System.IO.ErrorEventArgs e)
    {
        Exception exception = e.GetException();
        _pendingControllerErrors.Enqueue(exception);
        DispatcherQueue? dispatcherQueue = _dispatcherQueue;
        if (dispatcherQueue is not null)
        {
            _ = dispatcherQueue.TryEnqueue(() =>
            {
                // Legacy Shadowsocks surfaced controller errors immediately even when
                // it was otherwise tray-only. Lazily activate the WinUI host only for
                // that exceptional dialog path.
                if (_window?.Content?.XamlRoot is null)
                {
                    ShowMainWindow();
                }

                DrainControllerErrorQueue();
            });
        }
    }

    private void OnControllerPathReadyToOpen(object? _, ShadowsocksController.PathEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Path))
        {
            return;
        }

        _ = _dispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Path) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                _window?.SetShellStatus(_localization.Format("Failed to open {0}: {1}", e.Path, exception.Message), InfoBarSeverity.Error);
            }
        });
    }

    private async void DrainControllerErrorQueue()
    {
        if (_controllerErrorDialogActive || _window is null || _window.Content?.XamlRoot is null)
        {
            return;
        }

        _controllerErrorDialogActive = true;
        try
        {
            while (_pendingControllerErrors.TryDequeue(out Exception? exception))
            {
                await _window.ShowControllerErrorAsync(exception);
            }
        }
        finally
        {
            _controllerErrorDialogActive = false;
        }
    }

    private void OnControllerStateChanged(object? _, EventArgs _1)
    {
        _ = _dispatcherQueue?.TryEnqueue(UpdateTrayState);
    }

    private void OnControllerTrafficChanged(object? _, EventArgs _1)
    {
        if (_dispatcherQueue is null || _controller is null)
        {
            return;
        }

        ShadowsocksController.TrafficPerSecond? current = _controller.trafficPerSecondQueue.LastOrDefault();
        bool inbound = current?.inboundIncreasement > 0;
        bool outbound = current?.outboundIncreasement > 0;
        _ = _dispatcherQueue.TryEnqueue(() => _trayIcon?.UpdateActivity(inbound, outbound));
    }

    private void UpdateTrayState()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.UpdateState(BuildTrayMenuState());
        }
    }

    private TrayMenuState BuildTrayMenuState()
    {
        if (_controller is null)
        {
            return new TrayMenuState(
                _localization["shadowsocks-reborn"] + "\n" + _localization["Controller unavailable"],
                TraySystemProxyMode.Disabled,
                TrayTrafficMode.User,
                _localization["WinDivert: inactive"],
                Array.Empty<TrayStrategyMenuItem>(),
                Array.Empty<TrayServerMenuItem>(),
                false, false, false,
                AutoStartup.Check(),
                !AppStoragePaths.IsCleanMode,
                ProtocolHandler.Check(),
                false,
                false,
                0);
        }

        Configuration config = _controller.GetCurrentConfiguration();
        SystemProxyMode proxyMode = _controller.GetSystemProxyMode();
        TrafficCaptureStatus traffic = _controller.GetTrafficCaptureStatus();

        TraySystemProxyMode trayProxyMode = proxyMode switch
        {
            SystemProxyMode.Global => TraySystemProxyMode.Global,
            SystemProxyMode.Pac => TraySystemProxyMode.Pac,
            _ => TraySystemProxyMode.Disabled,
        };
        TrayTrafficMode trayTrafficMode = config.trafficCaptureMode == TrafficCaptureMode.Admin
            ? TrayTrafficMode.Admin
            : TrayTrafficMode.User;
        string trafficStatus = traffic.RuntimeMode switch
        {
            TrafficRuntimeMode.Admin => _localization["WinDivert: active"],
            TrafficRuntimeMode.Game => _localization["WinDivert: paused (game running)"],
            _ => _localization["WinDivert: inactive"],
        };

        var strategies = _controller.GetStrategies()
            .Select(strategy => new TrayStrategyMenuItem(
                strategy.ID,
                strategy.Name,
                string.Equals(config.strategy, strategy.ID, StringComparison.Ordinal)))
            .ToArray();

        var serverIndices = Enumerable.Range(0, Math.Min(config.configs.Count, 20)).ToList();
        if (config.index >= 20 && config.index < config.configs.Count && !serverIndices.Contains(config.index))
        {
            serverIndices.Add(config.index);
        }
        var servers = serverIndices
            .Select(index => new TrayServerMenuItem(index, config.configs[index].ToString(), string.IsNullOrEmpty(config.strategy) && config.index == index))
            .ToArray();

        string serverInfo = _controller.GetCurrentStrategy()?.Name
            ?? (_controller.GetCurrentServer()?.ToString() ?? _localization["No server configured"]);
        string proxyText = proxyMode == SystemProxyMode.Disabled
            ? _localization.Format("Running: Port {0}", config.localPort)
            : _localization["System Proxy On:"] + " " + (proxyMode == SystemProxyMode.Global ? _localization["Global"] : _localization["PAC"]);
        string tooltip = $"{_localization["shadowsocks-reborn"]} {UpdateChecker.Version}\n{proxyText}\n{serverInfo}";

        return new TrayMenuState(
            tooltip,
            trayProxyMode,
            trayTrafficMode,
            trafficStatus,
            strategies,
            servers,
            config.useOnlinePac,
            config.secureLocalPac,
            config.regeneratePacOnUpdate,
            AutoStartup.Check(),
            !AppStoragePaths.IsCleanMode,
            ProtocolHandler.Check(),
            config.shareOverLan,
            config.checkPreRelease,
            config.configs.Count);
    }

    private async void OnTrayCommandRequested(object? _, TrayCommandEventArgs e)
    {
        if (_window is null)
        {
            return;
        }

        try
        {
            switch (e.Command)
            {
                case TrayCommandKind.OpenOverview:
                    _window.NavigateToOverview();
                    ShowMainWindow();
                    break;
                case TrayCommandKind.OpenServers:
                    _window.NavigateToServers();
                    ShowMainWindow();
                    break;
                case TrayCommandKind.OpenTraffic:
                    _window.NavigateToTraffic();
                    ShowMainWindow();
                    break;
                case TrayCommandKind.OpenSharing:
                    _window.NavigateToSharing();
                    ShowMainWindow();
                    break;
                case TrayCommandKind.OpenPac:
                    _window.NavigateToPac();
                    ShowMainWindow();
                    break;
                case TrayCommandKind.EditOnlinePacUrl:
                    await EditOnlinePacUrlAsync(enableOnlineAfterSave: false);
                    break;
                case TrayCommandKind.OpenForwardProxy:
                    _window.NavigateToForwardProxy();
                    ShowMainWindow();
                    break;
                case TrayCommandKind.OpenOnlineConfig:
                    _window.NavigateToOnlineConfig();
                    ShowMainWindow();
                    break;
                case TrayCommandKind.OpenHotkeys:
                    _window.NavigateToHotkeys();
                    ShowMainWindow();
                    break;
                case TrayCommandKind.OpenLogs:
                    _window.NavigateToLogs();
                    ShowMainWindow();
                    break;
                case TrayCommandKind.OpenAbout:
                    _window.NavigateToAbout();
                    ShowMainWindow();
                    break;
                case TrayCommandKind.CheckUpdates:
                    _window.NavigateToAbout(checkNow: true);
                    ShowMainWindow();
                    break;
                case TrayCommandKind.SetSystemProxyDisabled:
                    _controller?.ToggleEnable(false);
                    break;
                case TrayCommandKind.SetSystemProxyPac:
                    if (_controller is not null)
                    {
                        _controller.ToggleEnable(true);
                        _controller.ToggleGlobal(false);
                    }
                    break;
                case TrayCommandKind.SetSystemProxyGlobal:
                    if (_controller is not null)
                    {
                        _controller.ToggleEnable(true);
                        _controller.ToggleGlobal(true);
                    }
                    break;
                case TrayCommandKind.SetTrafficUser:
                    if (_controller is not null)
                    {
                        await _controller.SetTrafficCaptureModeAsync(TrafficCaptureMode.User);
                    }
                    break;
                case TrayCommandKind.SetTrafficAdmin:
                    if (_controller is not null)
                    {
                        await _controller.SetTrafficCaptureModeAsync(TrafficCaptureMode.Admin);
                    }
                    break;
                case TrayCommandKind.SelectServer:
                    if (_controller is not null && e.Index >= 0)
                    {
                        _controller.SelectServerIndex(e.Index);
                    }
                    break;
                case TrayCommandKind.SelectStrategy:
                    if (_controller is not null && !string.IsNullOrWhiteSpace(e.Value))
                    {
                        _controller.SelectStrategy(e.Value);
                    }
                    break;
                case TrayCommandKind.UseLocalPac:
                    _controller?.UseOnlinePAC(false);
                    break;
                case TrayCommandKind.UseOnlinePac:
                    if (_controller is not null)
                    {
                        if (string.IsNullOrWhiteSpace(_controller.GetCurrentConfiguration().pacUrl))
                        {
                            await EditOnlinePacUrlAsync(enableOnlineAfterSave: true);
                        }
                        else
                        {
                            _controller.UseOnlinePAC(true);
                        }
                    }
                    break;
                case TrayCommandKind.EditLocalPacFile:
                    _controller?.TouchPACFile();
                    break;
                case TrayCommandKind.EditUserRuleFile:
                    _controller?.TouchUserRuleFile();
                    break;
                case TrayCommandKind.UpdateLocalPacFromGeosite:
                    if (_controller is not null)
                    {
                        bool updated = await _controller.UpdatePACFromGeositeAsync();
                        _window.SetShellStatus(updated ? _localization["Local PAC updated from GeoSite"] : _localization["GeoSite update did not complete"]);
                    }
                    break;
                case TrayCommandKind.ToggleSecureLocalPac:
                    if (_controller is not null)
                    {
                        _controller.ToggleSecureLocalPac(!_controller.GetCurrentConfiguration().secureLocalPac);
                    }
                    break;
                case TrayCommandKind.ToggleRegeneratePacOnUpdate:
                    if (_controller is not null)
                    {
                        _controller.ToggleRegeneratePacOnUpdate(!_controller.GetCurrentConfiguration().regeneratePacOnUpdate);
                    }
                    break;
                case TrayCommandKind.ToggleStartWithWindows:
                    SetStartWithWindows(!AutoStartup.Check());
                    break;
                case TrayCommandKind.ToggleProtocolHandler:
                    if (!ProtocolHandler.Set(!ProtocolHandler.Check()))
                    {
                        _window.SetShellStatus(_localization["Failed to update ss:// protocol association"], InfoBarSeverity.Error);
                    }
                    break;
                case TrayCommandKind.ToggleAllowLan:
                    if (_controller is not null)
                    {
                        _controller.ToggleShareOverLAN(!_controller.GetCurrentConfiguration().shareOverLan);
                    }
                    break;
                case TrayCommandKind.TogglePreRelease:
                    if (_controller is not null)
                    {
                        _controller.ToggleCheckingPreRelease(!_controller.GetCurrentConfiguration().checkPreRelease);
                    }
                    break;
                case TrayCommandKind.Exit:
                    ShutdownAndExit();
                    return;
            }
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Tray command failed: {0}", e.Command);
            _window.SetShellStatus(_localization.Format("Tray command failed: {0}", exception.Message), InfoBarSeverity.Error);
        }
        finally
        {
            UpdateTrayState();
        }
    }

    private async Task EditOnlinePacUrlAsync(bool enableOnlineAfterSave)
    {
        if (_controller is null || _window is null)
        {
            return;
        }

        // Preserve the legacy Online PAC scenario: if no URL exists, editing is
        // performed first and Online PAC is enabled only after a non-empty URL is saved.
        ShowMainWindow();
        await Task.Yield();

        XamlRoot? xamlRoot = _window.Content?.XamlRoot;
        if (xamlRoot is null)
        {
            _window.NavigateToPac();
            _window.SetShellStatus(_localization["Please input PAC Url"]);
            return;
        }

        string originalUrl = _controller.GetCurrentConfiguration().pacUrl ?? string.Empty;
        var input = new TextBox
        {
            Text = originalUrl,
            MinWidth = 480,
            PlaceholderText = "https://example.com/proxy.pac",
            SelectionStart = originalUrl.Length,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = _localization["Edit Online PAC URL"],
            Content = input,
            PrimaryButtonText = _localization["OK"],
            CloseButtonText = _localization["Cancel"],
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        string pacUrl = input.Text.Trim();
        if (string.IsNullOrEmpty(pacUrl))
        {
            _window.SetShellStatus(_localization["Please input PAC Url"]);
            return;
        }

        if (!string.Equals(pacUrl, originalUrl, StringComparison.Ordinal))
        {
            _controller.SavePACUrl(pacUrl);
        }

        if (enableOnlineAfterSave)
        {
            _controller.UseOnlinePAC(true);
        }
    }

    private void StartAutomaticUpdateCheck()
    {
        if (_controller is null || _startupUpdateChecker is not null)
        {
            return;
        }

        _startupUpdateChecker = new UpdateChecker(_controller);
        _startupUpdateChecker.UpdateAvailable += OnAutomaticUpdateAvailable;
        _ = _startupUpdateChecker.CheckForVersionUpdate(3000);
    }

    private void OnAutomaticUpdateAvailable(object? _, UpdateAvailableEventArgs e)
    {
        _ = _dispatcherQueue?.TryEnqueue(() =>
        {
            if (_window is null || _startupUpdateChecker is null)
            {
                return;
            }

            _window.SetShellStatus(_localization.Format("Update available: {0}", _startupUpdateChecker.NewReleaseVersion));
            ShowMainWindow();
            _window.NavigateToAbout(checkNow: true);
        });
    }

    private void OnRedirectedActivation(AppActivationArguments activationArguments)
    {
        DispatcherQueue? dispatcherQueue = _dispatcherQueue;
        if (dispatcherQueue is null)
        {
            return;
        }

        if (Program.IsPlainLaunch(activationArguments))
        {
            _ = dispatcherQueue.TryEnqueue(async () => await ShowAlreadyRunningDialogAsync());
            return;
        }

        _ = dispatcherQueue.TryEnqueue(() => ProcessActivation(activationArguments, showWindow: false));
    }

    private async Task ShowAlreadyRunningDialogAsync()
    {
        MainWindow? window = _window;
        if (window is null || _alreadyRunningDialogActive)
        {
            return;
        }

        _alreadyRunningDialogActive = true;
        try
        {
            window.ShowFromTray();
            await window.ShowAlreadyRunningDialogAsync();
        }
        catch (InvalidOperationException exception)
        {
            // ContentDialog allows one modal dialog per XamlRoot. If another one is
            // already open, keep the activation useful and fall back to the Fluent
            // shell InfoBar instead of ever returning to a Win32 MessageBox.
            Logger.Debug(exception, "The already-running ContentDialog could not be shown because another dialog is active.");
            window.SetShellStatus(_localization["Shadowsocks Reborn is already running"], InfoBarSeverity.Informational);
        }
        finally
        {
            _alreadyRunningDialogActive = false;
        }
    }

    private void ProcessActivation(AppActivationArguments activationArguments, bool showWindow)
    {
        if (showWindow)
        {
            ShowMainWindow();
        }

        if (_controller is null)
        {
            return;
        }

        string? openUrl = TryGetOpenUrl(activationArguments);
        if (!string.IsNullOrWhiteSpace(openUrl) && _window is not null)
        {
            ShowMainWindow();
            _ = _window.NavigateToSharingAndImportAsync(openUrl);
        }
    }

    private static string? TryGetOpenUrl(AppActivationArguments activationArguments)
    {
        if (activationArguments.Kind == ExtendedActivationKind.Protocol
            && activationArguments.Data is IProtocolActivatedEventArgs protocolArguments)
        {
            return protocolArguments.Uri?.AbsoluteUri;
        }

        if (activationArguments.Kind != ExtendedActivationKind.Launch
            || activationArguments.Data is not ILaunchActivatedEventArgs launchArguments)
        {
            return null;
        }

        string[] arguments = WindowsCommandLine.ParseArguments(launchArguments.Arguments);
        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            if (argument.StartsWith("--open-url=", StringComparison.OrdinalIgnoreCase))
            {
                return argument["--open-url=".Length..].Trim('"');
            }

            if (argument.Equals("--open-url", StringComparison.OrdinalIgnoreCase)
                && index + 1 < arguments.Length)
            {
                return arguments[index + 1].Trim('"');
            }
        }

        return null;
    }

    private bool SetStartWithWindows(bool enabled)
    {
        if (AppStoragePaths.IsCleanMode)
        {
            _window?.SetShellStatus(_localization["Start on Boot is unavailable in Clean Mode."]);
            return false;
        }

        if (!AutoStartup.Set(enabled, enabled ? "--start-hidden" : null))
        {
            return false;
        }

        UpdateTrayState();
        _window?.SetShellStatus(enabled ? _localization["Start on Boot enabled"] : _localization["Start on Boot disabled"]);
        return true;
    }

    private void ShowMainWindow()
    {
        _window?.ShowFromTray();
    }

    private void ShutdownAndExit()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        Program.UnregisterActivationHandler(OnRedirectedActivation);
        Program.DetachActivationSource(_appInstance);

        _lifetimeCancellation.Cancel();
        _resumeRestartCancellation?.Cancel();
        _resumeRestartCancellation?.Dispose();
        _resumeRestartCancellation = null;

        if (_powerModeMonitor is not null)
        {
            _powerModeMonitor.Suspending -= OnSystemSuspending;
            _powerModeMonitor.Resumed -= OnSystemResumed;
            _powerModeMonitor.Dispose();
            _powerModeMonitor = null;
        }

        if (_trayIcon is not null)
        {
            _trayIcon.CommandRequested -= OnTrayCommandRequested;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _hotkeyManager?.Dispose();
        _hotkeyManager = null;

        if (_startupUpdateChecker is not null)
        {
            _startupUpdateChecker.UpdateAvailable -= OnAutomaticUpdateAvailable;
            _startupUpdateChecker = null;
        }

        if (!AppStoragePaths.IsCleanMode)
        {
            AutoStartup.RegisterForRestart(false);
        }

        if (_controller is not null)
        {
            UnsubscribeControllerEvents(_controller);
            try
            {
                _controller.Stop();
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Controller shutdown failed.");
            }
            finally
            {
                _controller = null;
            }
        }

        _userInteraction = null;
        _lifetimeCancellation.Dispose();

        _appInstance.UnregisterKey();

        MainWindow? window = _window;
        _window = null;
        window?.CloseForApplicationExit();

        if (AppStoragePaths.IsCleanMode)
        {
            LogManager.Shutdown();
            AppStoragePaths.CleanupCleanSession();
        }

        Exit();
    }

    private static void OnUnhandledException(object _, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Logger.Error(e.Exception, "Unhandled WinUI system-shell exception.");
    }
}
