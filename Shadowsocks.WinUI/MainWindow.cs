using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shadowsocks.Controller;
using Shadowsocks.Model;
using Shadowsocks.Localization;
using Shadowsocks.WinUI.Pages;
using Shadowsocks.WinUI.UI;
using Windows.Graphics;

namespace Shadowsocks.WinUI;

public sealed class MainWindow : Window
{
    private const string OverviewTag = "overview";
    private const string ServersTag = "servers";
    private const string TrafficTag = "traffic";
    private const string GamesTag = "games";
    private const string PacTag = "pac";
    private const string ForwardProxyTag = "forward-proxy";
    private const string OnlineConfigTag = "online-config";
    private const string HotkeysTag = "hotkeys";
    private const string SharingTag = "sharing";
    private const string LogsTag = "logs";
    private const string AboutTag = "about";

    private readonly Grid _rootGrid;
    private readonly TitleBar _titleBar;
    private readonly NavigationView _navigationView;
    private readonly ContentControl _contentHost;
    private readonly InfoBar _statusInfoBar;
    private readonly ShadowsocksController? _controller;
    private readonly Func<bool> _getStartWithWindows;
    private readonly Func<bool, bool> _setStartWithWindows;
    private readonly WinUIPageContext _pageContext;
    private readonly Dictionary<Type, Page> _pageCache = new();
    private readonly ILocalizationService _localization;

    private AppThemePreference _themePreference;
    private Type? _currentPageType;
    private bool _hasBeenActivated;
    private bool _applicationExitRequested;

    public MainWindow(
        ShadowsocksController? controller,
        Func<bool> getStartWithWindows,
        Func<bool, bool> setStartWithWindows,
        Func<HotkeyConfig, IReadOnlyList<string>> registerHotkeys,
        Func<HotkeyConfig, IReadOnlyList<string>> applyHotkeys,
        ILocalizationService localization)
    {
        _controller = controller;
        _getStartWithWindows = getStartWithWindows;
        _setStartWithWindows = setStartWithWindows;
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _themePreference = ParseTheme(controller?.GetCurrentConfiguration().uiTheme);

        Title = _localization["Shadowsocks Reborn"];
        AppWindow.Resize(new SizeInt32(1180, 760));
        AppWindow.Closing += OnAppWindowClosing;
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };

        _rootGrid = new Grid();
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _titleBar = CreateTitleBar();
        Grid.SetRow(_titleBar, 0);
        _rootGrid.Children.Add(_titleBar);

        _navigationView = CreateNavigationView();
        Grid.SetRow(_navigationView, 1);
        _rootGrid.Children.Add(_navigationView);

        _statusInfoBar = new InfoBar
        {
            IsOpen = false,
            IsClosable = true,
            Margin = new Thickness(WinUIStyles.PageGutter, 12, WinUIStyles.PageGutter, 0),
        };

        _contentHost = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(WinUIStyles.PageGutter, 0, WinUIStyles.PageGutter, WinUIStyles.PageGutter),
        };

        var contentHost = new Grid();
        contentHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        contentHost.Children.Add(_statusInfoBar);
        Grid.SetRow(_contentHost, 1);
        contentHost.Children.Add(_contentHost);
        _navigationView.Content = contentHost;

        _pageContext = new WinUIPageContext(
            _controller,
            () => _rootGrid.XamlRoot,
            () => WinRT.Interop.WindowNative.GetWindowHandle(this),
            NavigateToSharing,
            ShowInfo,
            ShowDialogAsync,
            () => _themePreference,
            ApplyAndSaveTheme,
            _getStartWithWindows,
            _setStartWithWindows,
            SetAlwaysOnTop,
            registerHotkeys,
            applyHotkeys,
            _localization);

        Content = _rootGrid;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_titleBar);
        ApplyTheme(_themePreference);

        if (_controller is not null)
        {
            SubscribeControllerEvents(_controller);
        }

        NavigateTo(OverviewTag);
        _navigationView.SelectedItem = _navigationView.MenuItems[0];
        RefreshShellSummary();

    }

    public void NavigateToLogs()
    {
        NavigateTo(LogsTag);
        foreach (object item in _navigationView.MenuItems)
        {
            if (item is NavigationViewItem navigationItem
                && string.Equals(navigationItem.Tag?.ToString(), LogsTag, StringComparison.Ordinal))
            {
                _navigationView.SelectedItem = navigationItem;
                break;
            }
        }
        ShowFromTray();
    }

    public void NavigateToOverview() => NavigateAndSelect(OverviewTag);
    public void NavigateToServers() => NavigateAndSelect(ServersTag);
    public void NavigateToTraffic() => NavigateAndSelect(TrafficTag);
    public void NavigateToPac() => NavigateAndSelect(PacTag);
    public void NavigateToForwardProxy() => NavigateAndSelect(ForwardProxyTag);
    public void NavigateToHotkeys() => NavigateAndSelect(HotkeysTag);
    public void NavigateToSharing() => NavigateAndSelect(SharingTag);

    public void NavigateToSharing(int serverIndex)
    {
        NavigateAndSelect(SharingTag);
        if (_contentHost.Content is SharingPage sharingPage)
        {
            sharingPage.SelectServer(serverIndex);
        }
    }

    public void NavigateToAbout(bool checkNow = false)
    {
        NavigateAndSelect(AboutTag);
        if (checkNow && _contentHost.Content is AboutPage aboutPage)
        {
            aboutPage.CheckForUpdates();
        }
    }

    public void NavigateToOnlineConfig() => NavigateAndSelect(OnlineConfigTag);

    public async Task NavigateToSharingAndImportAsync(string url)
    {
        NavigateAndSelect(SharingTag);
        ShowFromTray();
        if (_contentHost.Content is SharingPage sharingPage)
        {
            await sharingPage.ImportUrlAsync(url);
        }
    }

    public void HideToTray()
    {
        if (!_applicationExitRequested)
        {
            AppWindow.Hide();
        }
    }

    public void ShowFromTray()
    {
        if (_applicationExitRequested)
        {
            return;
        }

        if (!_hasBeenActivated)
        {
            _hasBeenActivated = true;
            Activate();
        }
        else
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter
                && presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore(true);
            }

            AppWindow.Show(true);
            Activate();
        }

        // AppWindow.Show(true) requests activation, but an already-visible desktop
        // window may still remain behind another top-level window. A tray click is
        // explicit user interaction, so also request foreground activation for the HWND.
        nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (windowHandle != nint.Zero)
        {
            _ = SetForegroundWindow(windowHandle);
        }
    }

    public void SetShellStatus(string status, InfoBarSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        ShowInfo(_localization["System shell"], status, severity);
    }

    public void SetShellStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        if (status.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            ShowInfo(_localization["System shell"], status, InfoBarSeverity.Error);
        }
        else if (status.Contains("warning", StringComparison.OrdinalIgnoreCase)
                 || status.Contains("hotkey", StringComparison.OrdinalIgnoreCase))
        {
            ShowInfo(_localization["System shell"], status, InfoBarSeverity.Warning);
        }
        else if (status.Contains("activation", StringComparison.OrdinalIgnoreCase))
        {
            ShowInfo(_localization["Activation"], status, InfoBarSeverity.Informational);
        }
    }


    public Task<ContentDialogResult> ShowMessageAsync(string title, string content, string closeButtonText = "OK")
        => ShowDialogAsync(_localization[title], _localization[content], _localization[closeButtonText]);

    public async Task ShowAlreadyRunningDialogAsync()
    {
        XamlRoot? xamlRoot = await GetDialogXamlRootAsync();
        if (xamlRoot is null)
        {
            SetShellStatus(_localization["Shadowsocks Reborn is already running"], InfoBarSeverity.Informational);
            return;
        }

        var icon = new FontIcon
        {
            Glyph = "\uE946",
            FontSize = 30,
            Margin = new Thickness(2, 2, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };

        var text = new StackPanel
        {
            Spacing = 8,
            MinWidth = 300,
            MaxWidth = 480,
        };
        text.Children.Add(new TextBlock
        {
            Text = _localization["The existing instance has been brought to the foreground."],
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = _localization["Closing the window hides it to the notification area. The proxy controller keeps running until you choose Exit from the tray menu."],
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
        });

        var content = new Grid
        {
            Margin = new Thickness(0, 4, 0, 0),
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(icon);
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = _localization["Shadowsocks Reborn is already running"],
            Content = content,
            CloseButtonText = _localization["OK"],
            DefaultButton = ContentDialogButton.Close,
        };

        await dialog.ShowAsync();
    }

    private async Task<XamlRoot?> GetDialogXamlRootAsync()
    {
        XamlRoot? xamlRoot = _rootGrid.XamlRoot;
        if (xamlRoot is not null)
        {
            return xamlRoot;
        }

        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler handler = null!;
        handler = (_, _) =>
        {
            _rootGrid.Loaded -= handler;
            loaded.TrySetResult(true);
        };
        _rootGrid.Loaded += handler;

        try
        {
            _ = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            return _rootGrid.XamlRoot;
        }
        finally
        {
            _rootGrid.Loaded -= handler;
        }
    }

    public async Task ShowControllerErrorAsync(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        XamlRoot? xamlRoot = _rootGrid.XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        var details = new TextBox
        {
            Text = exception.ToString(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 520,
            MaxWidth = 720,
            MaxHeight = 360,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = _localization.Format("shadowsocks-reborn Error: {0}", exception.Message),
            Content = details,
            CloseButtonText = _localization["OK"],
        };
        await dialog.ShowAsync();
    }

    public void CloseForApplicationExit()
    {
        if (_applicationExitRequested)
        {
            return;
        }

        _applicationExitRequested = true;
        if (_controller is not null)
        {
            UnsubscribeControllerEvents(_controller);
        }

        Close();
    }

    private TitleBar CreateTitleBar()
    {
        var titleBar = new TitleBar
        {
            Title = _localization["Shadowsocks Reborn"],
            Subtitle = _localization["Starting…"],
            IconSource = new SymbolIconSource { Symbol = Symbol.Globe },
            IsPaneToggleButtonVisible = true,
        };
        titleBar.PaneToggleRequested += (_, _) => _navigationView.IsPaneOpen = !_navigationView.IsPaneOpen;
        return titleBar;
    }

    private NavigationView CreateNavigationView()
    {
        var navigationView = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = false,
            IsTitleBarAutoPaddingEnabled = false,
            AlwaysShowHeader = false,
            IsSettingsVisible = true,
            OpenPaneLength = 272,
            CompactPaneLength = 48,
        };

        navigationView.MenuItems.Add(CreateNavigationItem(_localization["Overview"], OverviewTag, Symbol.Home, "Open the connection overview and current runtime status."));
        navigationView.MenuItems.Add(CreateNavigationItem(_localization["Servers"], ServersTag, Symbol.World, "Manage Shadowsocks servers and local client connection settings."));
        navigationView.MenuItems.Add(CreateNavigationItem(_localization["Traffic"], TrafficTag, Symbol.Sync, "Configure capture mode, Windows proxy settings and per-application routing."));
        navigationView.MenuItems.Add(CreateNavigationItem(_localization["Game Mode"], GamesTag, Symbol.Play, "Manage applications that automatically suspend WinDivert while they are running."));
        navigationView.MenuItems.Add(CreateNavigationItem(_localization["PAC / GeoSite"], PacTag, Symbol.Globe, "Configure PAC behavior, local PAC security and GeoSite sources."));
        navigationView.MenuItems.Add(CreateNavigationItem(_localization["Forward Proxy"], ForwardProxyTag, Symbol.Forward, "Configure an optional upstream proxy used to reach Shadowsocks servers."));
        navigationView.MenuItems.Add(CreateNavigationItem(_localization["Online Config"], OnlineConfigTag, Symbol.Sync, "Manage SIP008 and other online server configuration sources."));
        navigationView.MenuItems.Add(CreateNavigationItem(_localization["Hotkeys"], HotkeysTag, Symbol.Keyboard, "Configure global keyboard shortcuts for common Shadowsocks actions."));
        navigationView.MenuItems.Add(CreateNavigationItem(_localization["Share / QR"], SharingTag, Symbol.Share, "Share servers and import ss:// links or QR codes."));
        navigationView.MenuItems.Add(new NavigationViewItemSeparator());
        navigationView.MenuItems.Add(CreateNavigationItem(_localization["Logs"], LogsTag, Symbol.Document, "View logs, traffic history and logging options."));
        navigationView.MenuItems.Add(CreateNavigationItem(_localization["About"], AboutTag, Symbol.Help, "View version information and configure update checks."));

        navigationView.SelectionChanged += OnNavigationSelectionChanged;
        navigationView.DisplayModeChanged += OnNavigationDisplayModeChanged;
        return navigationView;
    }

    private NavigationViewItem CreateNavigationItem(string content, string tag, Symbol symbol, string tooltipKey)
    {
        var item = new NavigationViewItem
        {
            Content = content,
            Tag = tag,
            Icon = new SymbolIcon(symbol),
        };
        string tooltip = _localization[tooltipKey];
        ToolTipService.SetToolTip(item, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(item, tooltip);
        return item;
    }

    private void OnNavigationSelectionChanged(NavigationView _, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavigatePage(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItemContainer?.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void NavigateTo(string tag)
    {
        Type pageType = tag switch
        {
            ServersTag => typeof(ServersPage),
            TrafficTag => typeof(TrafficPage),
            GamesTag => typeof(GamesPage),
            PacTag => typeof(PacGeositePage),
            ForwardProxyTag => typeof(ForwardProxyPage),
            OnlineConfigTag => typeof(OnlineConfigPage),
            HotkeysTag => typeof(HotkeysPage),
            SharingTag => typeof(SharingPage),
            LogsTag => typeof(LogsPage),
            AboutTag => typeof(AboutPage),
            _ => typeof(OverviewPage),
        };
        NavigatePage(pageType);
    }

    private void NavigatePage(Type pageType)
    {
        if (_currentPageType == pageType)
        {
            (_contentHost.Content as IRefreshablePage)?.Refresh();
            return;
        }

        Page page = CreatePage(pageType);
        _currentPageType = pageType;
        _contentHost.Content = page;
        (page as IRefreshablePage)?.Refresh();
    }

    private Page CreatePage(Type pageType)
    {
        if (_pageCache.TryGetValue(pageType, out Page? cachedPage))
        {
            return cachedPage;
        }

        Page page = pageType == typeof(OverviewPage) ? new OverviewPage(_pageContext)
            : pageType == typeof(ServersPage) ? new ServersPage(_pageContext)
            : pageType == typeof(TrafficPage) ? new TrafficPage(_pageContext)
            : pageType == typeof(GamesPage) ? new GamesPage(_pageContext)
            : pageType == typeof(PacGeositePage) ? new PacGeositePage(_pageContext)
            : pageType == typeof(ForwardProxyPage) ? new ForwardProxyPage(_pageContext)
            : pageType == typeof(HotkeysPage) ? new HotkeysPage(_pageContext)
            : pageType == typeof(SharingPage) ? new SharingPage(_pageContext)
            : pageType == typeof(OnlineConfigPage) ? new OnlineConfigPage(_pageContext)
            : pageType == typeof(LogsPage) ? new LogsPage(_pageContext)
            : pageType == typeof(AboutPage) ? new AboutPage(_pageContext)
            : pageType == typeof(SettingsPage) ? new SettingsPage(_pageContext)
            : throw new ArgumentOutOfRangeException(nameof(pageType), pageType, "Unsupported WinUI page type.");

        _pageCache.Add(pageType, page);
        return page;
    }

    private void NavigateAndSelect(string tag)
    {
        NavigateTo(tag);
        foreach (object item in _navigationView.MenuItems)
        {
            if (item is NavigationViewItem navigationItem
                && string.Equals(navigationItem.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                _navigationView.SelectedItem = navigationItem;
                break;
            }
        }
    }

    private void OnNavigationDisplayModeChanged(NavigationView _, NavigationViewDisplayModeChangedEventArgs _1)
    {
        // Keep the page edge fixed when NavigationView changes between expanded, compact
        // and minimal modes. Page-specific MaxWidth values used to make this edge jump.
        double gutter = WinUIStyles.PageGutter;
        _contentHost.Padding = new Thickness(gutter, 0, gutter, gutter);
        _statusInfoBar.Margin = new Thickness(gutter, 12, gutter, 0);
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
    }

    private void OnControllerStateChanged(object? _, EventArgs _1)
    {
        _ = _rootGrid.DispatcherQueue.TryEnqueue(() =>
        {
            RefreshShellSummary();
            (_contentHost.Content as IRefreshablePage)?.Refresh();
        });
    }

    private void SetAlwaysOnTop(bool enabled)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = enabled;
        }
    }

    private void RefreshShellSummary()
    {
        if (_controller is null)
        {
            _titleBar.Subtitle = _localization["Controller unavailable"];
            return;
        }

        string running = _controller.IsProxyListenerRunning ? _localization["Running"] : _localization["Stopped"];
        _titleBar.Subtitle = _localization.Format("{0} · {1} mode", running, _localization[_controller.GetTrafficRuntimeMode().ToString()]);
    }

    private void ApplyAndSaveTheme(AppThemePreference theme)
    {
        _themePreference = theme;
        ApplyTheme(theme);

        _controller?.SetUiTheme(theme.ToString());

        ShowInfo(_localization["Appearance"], theme == AppThemePreference.System
            ? _localization["Theme now follows Windows."]
            : _localization.Format("{0} theme enabled.", _localization[theme.ToString()]), InfoBarSeverity.Success);
    }

    private void ApplyTheme(AppThemePreference theme)
    {
        _rootGrid.RequestedTheme = theme switch
        {
            AppThemePreference.Light => ElementTheme.Light,
            AppThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    private static AppThemePreference ParseTheme(string? value)
    {
        return Enum.TryParse(value, ignoreCase: true, out AppThemePreference theme)
            ? theme
            : AppThemePreference.System;
    }

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        _statusInfoBar.Title = title;
        _statusInfoBar.Message = message;
        _statusInfoBar.Severity = severity;
        _statusInfoBar.IsOpen = true;
    }

    private async Task<ContentDialogResult> ShowDialogAsync(string title, string content, string closeButtonText)
    {
        XamlRoot? xamlRoot = _rootGrid.XamlRoot;
        if (xamlRoot is null)
        {
            return ContentDialogResult.None;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = closeButtonText,
        };
        return await dialog.ShowAsync();
    }

    private void OnAppWindowClosing(AppWindow _, AppWindowClosingEventArgs args)
    {
        if (_applicationExitRequested)
        {
            return;
        }

        args.Cancel = true;
        HideToTray();
    }
}
