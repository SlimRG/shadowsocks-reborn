using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shadowsocks.Controller.Traffic;
using Shadowsocks.Model;
using Shadowsocks.WinUI.UI;

namespace Shadowsocks.WinUI.Pages;

public sealed class TrafficPage : Page, IRefreshablePage
{
    private sealed class RuleEditor
    {
        public required Grid Root { get; init; }
        public required CheckBox Enabled { get; init; }
        public required TextBox Application { get; init; }
        public required ComboBox Action { get; init; }
    }

    private readonly WinUIPageContext _context;
    private readonly ToggleSwitch _systemProxyToggle;
    private readonly ToggleSwitch _shareLanToggle;
    private readonly RadioButton _userMode;
    private readonly RadioButton _adminMode;
    private readonly NumberBox _localPortBox;
    private readonly TextBlock _runtimeMode;
    private readonly TextBlock _winDivertStatus;
    private readonly TextBlock _networkServiceStatus;
    private readonly TextBlock _tcpCaptureStatus;
    private readonly TextBlock _udpCaptureStatus;
    private readonly TextBlock _tcpRedirectPort;
    private readonly TextBlock _udpRedirectPort;
    private readonly StackPanel _ruleList;
    private readonly List<RuleEditor> _rules = new();
    private readonly Button _saveRoutingButton;
    private bool _refreshing;
    private bool _rulesDirty;
    private bool _captureModeChanging;

    internal TrafficPage(WinUIPageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        Content = WinUIStyles.CreatePage(
            "Traffic Routing",
            "Choose User or Administrator capture, inspect WinDivert state, and route individual applications.",
            out StackPanel panel);

        var captureCard = new StackPanel { Spacing = 12 };
        captureCard.Children.Add(WinUIStyles.CreateSectionTitle("Capture mode"));
        _userMode = new RadioButton
        {
            Content = "User Mode",
            GroupName = "CaptureMode",
        };
        _userMode.Checked += OnCaptureModeChecked;
        _context.SetToolTip(_userMode, "Use the normal user-mode local proxy path without elevation or WinDivert capture.");
        captureCard.Children.Add(_userMode);
        TextBlock userDescription = WinUIStyles.CreateText("No elevation. Uses the normal local proxy path.", "CaptionTextBlockStyle");
        userDescription.Opacity = 0.72;
        captureCard.Children.Add(userDescription);
        var adminModeContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
        };
        adminModeContent.Children.Add(new FontIcon
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
            Glyph = "\uEA18",
            FontSize = 16,
        });
        adminModeContent.Children.Add(new TextBlock
        {
            Text = "Admin Mode",
            VerticalAlignment = VerticalAlignment.Center,
        });
        _adminMode = new RadioButton
        {
            Content = adminModeContent,
            GroupName = "CaptureMode",
        };
        _adminMode.Checked += OnCaptureModeChecked;
        _context.SetToolTip(_adminMode, "Request elevation and enable transparent TCP/UDP capture through NetworkService and WinDivert.");
        captureCard.Children.Add(_adminMode);
        TextBlock adminDescription = WinUIStyles.CreateText("Transparent TCP/UDP capture through the elevated NetworkService helper.", "CaptionTextBlockStyle");
        adminDescription.Opacity = 0.72;
        captureCard.Children.Add(adminDescription);
        panel.Children.Add(WinUIStyles.CreateCard(captureCard));

        var stateCard = new StackPanel { Spacing = 10 };
        stateCard.Children.Add(WinUIStyles.CreateSectionTitle("Administrator capture status"));
        stateCard.Children.Add(WinUIStyles.CreateKeyValueRow("Runtime mode", out _runtimeMode));
        stateCard.Children.Add(WinUIStyles.CreateKeyValueRow("WinDivert", out _winDivertStatus));
        stateCard.Children.Add(WinUIStyles.CreateKeyValueRow("Network service", out _networkServiceStatus));
        stateCard.Children.Add(WinUIStyles.CreateKeyValueRow("TCP capture", out _tcpCaptureStatus));
        stateCard.Children.Add(WinUIStyles.CreateKeyValueRow("UDP capture", out _udpCaptureStatus));
        stateCard.Children.Add(WinUIStyles.CreateKeyValueRow("TCP redirect port", out _tcpRedirectPort));
        stateCard.Children.Add(WinUIStyles.CreateKeyValueRow("UDP redirect port", out _udpRedirectPort));
        panel.Children.Add(WinUIStyles.CreateCard(stateCard));

        var windowsProxyCard = new StackPanel { Spacing = 14 };
        windowsProxyCard.Children.Add(WinUIStyles.CreateSectionTitle("Windows proxy"));
        _systemProxyToggle = new ToggleSwitch
        {
            Header = "Use Shadowsocks as the Windows system proxy",
            OnContent = "On",
            OffContent = "Off",
        };
        _systemProxyToggle.Toggled += OnSystemProxyToggled;
        _context.SetToolTip(_systemProxyToggle, "Configure Windows to use this Shadowsocks instance as the system proxy.");
        windowsProxyCard.Children.Add(_systemProxyToggle);
        _shareLanToggle = new ToggleSwitch
        {
            Header = "Allow other devices to connect",
            OnContent = "Shared",
            OffContent = "Local only",
        };
        _shareLanToggle.Toggled += OnShareLanToggled;
        _context.SetToolTip(_shareLanToggle, "Allow devices on the local network to connect to the local Shadowsocks proxy port.");
        windowsProxyCard.Children.Add(_shareLanToggle);
        _localPortBox = new NumberBox
        {
            Header = "Local SOCKS/HTTP port",
            Minimum = 1,
            Maximum = 65535,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _context.SetToolTip(_localPortBox, "Local SOCKS/HTTP port used by applications and the Windows proxy configuration.");
        windowsProxyCard.Children.Add(_localPortBox);
        var applyPort = new Button { Content = "Apply port", HorizontalAlignment = HorizontalAlignment.Left };
        _context.SetToolTip(applyPort, "Apply the local proxy port immediately.");
        applyPort.Click += OnApplyPortClicked;
        windowsProxyCard.Children.Add(applyPort);
        panel.Children.Add(WinUIStyles.CreateCard(windowsProxyCard));

        var routesCard = new StackPanel { Spacing = 12 };
        routesCard.Children.Add(WinUIStyles.CreateSectionTitle("Application routing"));
        routesCard.Children.Add(WinUIStyles.CreateText(
            "Rules are matched by executable name, full path, or wildcard. Proxy, Direct and Block are explicit actions."));
        routesCard.Children.Add(CreateRoutingHeader());
        _ruleList = new StackPanel { Spacing = 8 };
        routesCard.Children.Add(_ruleList);
        var ruleActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var addRule = new Button { Content = "Add application" };
        _context.SetToolTip(addRule, "Add a per-application routing rule.");
        addRule.Click += (_, _) => AddRule(new ApplicationRouteRule(), markDirty: true);
        ruleActions.Children.Add(addRule);
        _saveRoutingButton = new Button { Content = "Save routing" };
        _context.SetToolTip(_saveRoutingButton, "Validate and save all application routing rules.");
        _saveRoutingButton.Click += OnSaveRoutingClicked;
        ruleActions.Children.Add(_saveRoutingButton);
        var discard = new Button { Content = "Discard changes" };
        _context.SetToolTip(discard, "Discard unsaved routing-rule changes and reload the saved configuration.");
        discard.Click += (_, _) =>
        {
            _rulesDirty = false;
            Refresh();
        };
        ruleActions.Children.Add(discard);
        routesCard.Children.Add(ruleActions);
        panel.Children.Add(WinUIStyles.CreateCard(routesCard));

        panel.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = "Game Mode is automatic",
            Message = "A configured game temporarily changes the runtime state to Game and suspends WinDivert. It is not a third selectable capture mode.",
        });

        WinUILocalization.Apply(Content, _context.Localization);
    }

    public void Refresh()
    {
        Shadowsocks.Controller.ShadowsocksController? controller = _context.Controller;
        Configuration? configuration = controller?.GetCurrentConfiguration();
        if (controller is null || configuration is null)
        {
            return;
        }

        _refreshing = true;
        try
        {
            _systemProxyToggle.IsOn = configuration.enabled;
            _shareLanToggle.IsOn = configuration.shareOverLan;
            _localPortBox.Value = configuration.localPort;

            TrafficCaptureStatus status = controller.GetTrafficCaptureStatus();
            _runtimeMode.Text = _context.L(status.RuntimeMode.ToString());
            _winDivertStatus.Text = status.GameModeActive
                ? _context.L("Suspended by Game Mode")
                : status.WinDivertActive ? _context.L("Active") : _context.L("Inactive");
            _networkServiceStatus.Text = status.NetworkServiceRunning ? _context.L("Running") : _context.L("Stopped");
            _tcpCaptureStatus.Text = status.GameModeActive ? _context.L("Suspended") : status.TcpCaptureActive ? _context.L("Active") : _context.L("Inactive");
            _udpCaptureStatus.Text = status.GameModeActive ? _context.L("Suspended") : status.UdpCaptureActive ? _context.L("Active") : _context.L("Inactive");
            _tcpRedirectPort.Text = status.TcpRedirectPort > 0 ? status.TcpRedirectPort.ToString() : "—";
            _udpRedirectPort.Text = status.UdpRedirectPort > 0 ? status.UdpRedirectPort.ToString() : "—";

            if (!_rulesDirty)
            {
                _userMode.IsChecked = configuration.trafficCaptureMode == TrafficCaptureMode.User;
                _adminMode.IsChecked = configuration.trafficCaptureMode == TrafficCaptureMode.Admin;
                LoadRules(configuration.applicationRules ?? []);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }


    private static Grid CreateRoutingHeader()
    {
        var grid = new Grid
        {
            ColumnSpacing = 8,
            Margin = new Thickness(0, 2, 0, 0),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        TextBlock enabled = WinUIStyles.CreateText("Enabled", "CaptionTextBlockStyle");
        enabled.MinWidth = 64;
        grid.Children.Add(enabled);

        TextBlock application = WinUIStyles.CreateText("Application", "CaptionTextBlockStyle");
        Grid.SetColumn(application, 1);
        grid.Children.Add(application);

        TextBlock action = WinUIStyles.CreateText("Action", "CaptionTextBlockStyle");
        Grid.SetColumn(action, 2);
        grid.Children.Add(action);

        TextBlock operations = WinUIStyles.CreateText("", "CaptionTextBlockStyle");
        operations.MinWidth = 72;
        Grid.SetColumn(operations, 3);
        grid.Children.Add(operations);
        return grid;
    }

    private void LoadRules(IEnumerable<ApplicationRouteRule> rules)
    {
        _rules.Clear();
        _ruleList.Children.Clear();
        foreach (ApplicationRouteRule rule in rules.Where(rule => rule is not null))
        {
            AddRule(rule, markDirty: false);
        }

        if (_rules.Count == 0)
        {
            TextBlock empty = WinUIStyles.CreateText(_context.L("No application-specific rules."));
            empty.Opacity = 0.68;
            _ruleList.Children.Add(empty);
        }
    }

    private void AddRule(ApplicationRouteRule rule, bool markDirty)
    {
        if (_rules.Count == 0)
        {
            _ruleList.Children.Clear();
        }

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var enabled = new CheckBox
        {
            IsChecked = rule.enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        enabled.Checked += (_, _) => MarkRulesDirty();
        enabled.Unchecked += (_, _) => MarkRulesDirty();
        _context.SetToolTip(enabled, "Enable or disable this application routing rule without deleting it.");
        grid.Children.Add(enabled);

        var application = new TextBox
        {
            Text = rule.application ?? string.Empty,
            PlaceholderText = "application.exe",
        };
        application.TextChanged += (_, _) => MarkRulesDirty();
        Grid.SetColumn(application, 1);
        _context.SetToolTip(application, "Executable name, full path, or wildcard pattern used to match a process.");
        grid.Children.Add(application);

        var action = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        action.Items.Add(new ComboBoxItem { Content = _context.L("Proxy"), Tag = TrafficRouteAction.Proxy });
        action.Items.Add(new ComboBoxItem { Content = _context.L("Direct"), Tag = TrafficRouteAction.Direct });
        action.Items.Add(new ComboBoxItem { Content = _context.L("Block"), Tag = TrafficRouteAction.Block });
        TrafficRouteAction normalizedAction = rule.action == TrafficRouteAction.Default ? TrafficRouteAction.Proxy : rule.action;
        foreach (object item in action.Items)
        {
            if (item is ComboBoxItem comboItem && comboItem.Tag is TrafficRouteAction itemAction && itemAction == normalizedAction)
            {
                action.SelectedItem = comboItem;
                break;
            }
        }
        action.SelectionChanged += (_, _) => MarkRulesDirty();
        _context.SetToolTip(action, "Choose whether matching traffic is proxied, sent directly, or blocked.");
        Grid.SetColumn(action, 2);
        grid.Children.Add(action);

        var remove = new Button { Content = _context.L("Remove") };
        _context.SetToolTip(remove, "Remove this application routing rule.");
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);

        var editor = new RuleEditor
        {
            Root = grid,
            Enabled = enabled,
            Application = application,
            Action = action,
        };
        remove.Click += (_, _) =>
        {
            _rules.Remove(editor);
            _ruleList.Children.Remove(grid);
            _rulesDirty = true;
            if (_rules.Count == 0)
            {
                TextBlock empty = WinUIStyles.CreateText(_context.L("No application-specific rules."));
                empty.Opacity = 0.68;
                _ruleList.Children.Add(empty);
            }
        };

        _rules.Add(editor);
        _ruleList.Children.Add(grid);
        if (markDirty)
        {
            _rulesDirty = true;
        }
    }

    private void MarkRulesDirty()
    {
        if (!_refreshing)
        {
            _rulesDirty = true;
        }
    }

    private async void OnCaptureModeChecked(object sender, RoutedEventArgs _)
    {
        if (_refreshing || _captureModeChanging || _context.Controller is null)
        {
            return;
        }

        TrafficCaptureMode requestedMode = ReferenceEquals(sender, _adminMode)
            ? TrafficCaptureMode.Admin
            : TrafficCaptureMode.User;
        if (_context.Controller.GetCurrentConfiguration().trafficCaptureMode == requestedMode)
        {
            return;
        }

        _captureModeChanging = true;
        _userMode.IsEnabled = false;
        _adminMode.IsEnabled = false;
        try
        {
            bool applied = await _context.Controller.SetTrafficCaptureModeAsync(requestedMode);
            if (!applied)
            {
                _context.ShowInfo(
                    "Traffic Routing",
                    requestedMode == TrafficCaptureMode.Admin
                        ? "Administrator mode could not be enabled."
                        : "User mode could not be enabled.",
                    InfoBarSeverity.Warning);
            }
        }
        finally
        {
            _captureModeChanging = false;
            _userMode.IsEnabled = true;
            _adminMode.IsEnabled = true;
            Refresh();
        }
    }

    private void OnSystemProxyToggled(object _, RoutedEventArgs _1)
    {
        if (_refreshing || _context.Controller is null)
        {
            return;
        }

        _context.Controller.ToggleEnable(_systemProxyToggle.IsOn);
    }

    private void OnShareLanToggled(object _, RoutedEventArgs _1)
    {
        if (_refreshing || _context.Controller is null)
        {
            return;
        }

        _context.Controller.ToggleShareOverLAN(_shareLanToggle.IsOn);
    }

    private void OnApplyPortClicked(object _, RoutedEventArgs _1)
    {
        if (_context.Controller is null || double.IsNaN(_localPortBox.Value))
        {
            return;
        }

        try
        {
            int port = checked((int)_localPortBox.Value);
            _context.Controller.SetLocalPort(port);
            if (_context.Controller.LastListenerError is null)
            {
                _context.ShowInfo("Local proxy", _context.LF("Local proxy port changed to {0}.", port), InfoBarSeverity.Success);
            }
        }
        catch (Exception exception)
        {
            _context.ShowInfo("Invalid port", exception.Message, InfoBarSeverity.Error);
            Refresh();
        }
    }

    private async void OnSaveRoutingClicked(object _, RoutedEventArgs _1)
    {
        if (_context.Controller is null)
        {
            return;
        }

        TrafficCaptureMode mode = _context.Controller.GetCurrentConfiguration().trafficCaptureMode;
        var rules = new List<ApplicationRouteRule>();
        foreach (RuleEditor editor in _rules)
        {
            string application = editor.Application.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(application))
            {
                continue;
            }

            TrafficRouteAction action = editor.Action.SelectedItem is ComboBoxItem { Tag: TrafficRouteAction selectedAction }
                ? selectedAction
                : TrafficRouteAction.Proxy;
            if (action is TrafficRouteAction.Default)
            {
                action = TrafficRouteAction.Proxy;
            }
            rules.Add(new ApplicationRouteRule
            {
                enabled = editor.Enabled.IsChecked == true,
                application = application,
                action = action,
            });
        }

        _saveRoutingButton.IsEnabled = false;
        try
        {
            Configuration configuration = _context.Controller.GetCurrentConfiguration();
            bool saved = await _context.Controller.SaveTrafficRoutingAsync(
                mode,
                rules,
                configuration.gameModeApplications ?? []);
            _rulesDirty = !saved;
            _context.ShowInfo(
                "Traffic Routing",
                saved ? "Traffic routing applied." : "The requested routing configuration could not be applied.",
                saved ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        finally
        {
            _saveRoutingButton.IsEnabled = true;
        }
        Refresh();
    }
}
