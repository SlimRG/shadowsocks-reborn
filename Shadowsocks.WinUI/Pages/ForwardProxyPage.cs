using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shadowsocks.Model;
using Shadowsocks.WinUI.UI;

namespace Shadowsocks.WinUI.Pages;

public sealed class ForwardProxyPage : Page, IRefreshablePage
{
    private readonly WinUIPageContext _context;
    private readonly RadioButton _noProxy;
    private readonly RadioButton _socks5;
    private readonly RadioButton _http;
    private readonly TextBox _server;
    private readonly NumberBox _port;
    private readonly NumberBox _timeout;
    private readonly TextBox _username;
    private readonly PasswordBox _password;
    private readonly Grid _details;
    private bool _refreshing;

    internal ForwardProxyPage(WinUIPageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        Content = WinUIStyles.CreatePage(
            "Forward Proxy",
            "Connect to the Shadowsocks server through an optional upstream proxy.",
            out StackPanel panel);

        var card = new StackPanel { Spacing = 16 };
        card.Children.Add(WinUIStyles.CreateSectionTitle("Type"));

        var modes = new StackPanel { Spacing = 6 };
        _noProxy = new RadioButton { Content = "No proxy", GroupName = "ForwardProxyType" };
        _socks5 = new RadioButton { Content = "SOCKS5", GroupName = "ForwardProxyType" };
        _http = new RadioButton { Content = "HTTP", GroupName = "ForwardProxyType" };
        _context.SetToolTip(_noProxy, "Connect to the Shadowsocks server directly, without an upstream proxy.");
        _context.SetToolTip(_socks5, "Connect to the Shadowsocks server through an upstream SOCKS5 proxy.");
        _context.SetToolTip(_http, "Connect to the Shadowsocks server through an upstream HTTP proxy.");
        _noProxy.Checked += (_, _) => UpdateDetailsEnabled();
        _socks5.Checked += (_, _) => UpdateDetailsEnabled();
        _http.Checked += (_, _) => UpdateDetailsEnabled();
        modes.Children.Add(_noProxy);
        modes.Children.Add(_socks5);
        modes.Children.Add(_http);
        card.Children.Add(modes);

        card.Children.Add(WinUIStyles.CreateSectionTitle("Details"));
        _details = CreateFormGrid();
        _server = new TextBox { PlaceholderText = "127.0.0.1" };
        _context.SetToolTip(_server, "Hostname or IP address of the upstream proxy.");
        AddRow(_details, "Address", _server);
        _port = CreateNumberBox(1, 65535, 1080);
        _context.SetToolTip(_port, "TCP port of the upstream proxy.");
        AddRow(_details, "Port", _port);
        _timeout = CreateNumberBox(1, ForwardProxyConfig.MaxProxyTimeoutSec, 3);
        _context.SetToolTip(_timeout, "Connection timeout for the upstream proxy, in seconds.");
        AddRow(_details, "Timeout", _timeout);
        card.Children.Add(_details);

        card.Children.Add(WinUIStyles.CreateSectionTitle("Credentials (optional)"));
        var credentials = CreateFormGrid();
        _username = new TextBox();
        _context.SetToolTip(_username, "Optional username for upstream proxy authentication.");
        AddRow(credentials, "Username", _username);
        _password = new PasswordBox { PasswordRevealMode = PasswordRevealMode.Peek };
        _context.SetToolTip(_password, "Optional password for upstream proxy authentication.");
        AddRow(credentials, "Password", _password);
        card.Children.Add(credentials);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var discard = new Button { Content = "Cancel", MinWidth = 92 };
        _context.SetToolTip(discard, "Discard unsaved forward-proxy changes.");
        discard.Click += (_, _) => Refresh();
        actions.Children.Add(discard);
        var save = new Button { Content = "Save", MinWidth = 92 };
        _context.SetToolTip(save, "Validate and save the forward-proxy configuration.");
        save.Click += OnSaveClicked;
        actions.Children.Add(save);
        card.Children.Add(actions);

        panel.Children.Add(WinUIStyles.CreateCard(card));

        WinUILocalization.Apply(Content, _context.Localization);
    }

    public void Refresh()
    {
        ForwardProxyConfig? proxy = _context.Controller?.GetCurrentConfiguration().proxy;
        if (proxy is null)
        {
            return;
        }

        _refreshing = true;
        try
        {
            _noProxy.IsChecked = !proxy.useProxy;
            _http.IsChecked = proxy.useProxy && proxy.proxyType == ForwardProxyConfig.PROXY_HTTP;
            _socks5.IsChecked = proxy.useProxy && proxy.proxyType != ForwardProxyConfig.PROXY_HTTP;
            _server.Text = proxy.proxyServer ?? string.Empty;
            _port.Value = proxy.proxyPort > 0 ? proxy.proxyPort : 1080;
            _timeout.Value = proxy.proxyTimeout > 0 ? proxy.proxyTimeout : 3;
            _username.Text = proxy.authUser ?? string.Empty;
            _password.Password = proxy.authPwd ?? string.Empty;
            UpdateDetailsEnabled();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void UpdateDetailsEnabled()
    {
        bool enabled = _noProxy.IsChecked != true;
        foreach (UIElement child in _details.Children)
        {
            if (child is Control control)
            {
                control.IsEnabled = enabled;
            }
        }
        _details.Opacity = enabled ? 1.0 : 0.55;
        _username.IsEnabled = enabled;
        _password.IsEnabled = enabled;
    }

    private void OnSaveClicked(object _, RoutedEventArgs _1)
    {
        if (_refreshing || _context.Controller is null)
        {
            return;
        }

        bool useProxy = _noProxy.IsChecked != true;
        if (useProxy && string.IsNullOrWhiteSpace(_server.Text))
        {
            _context.ShowInfo("Forward Proxy", "Proxy address is required.", InfoBarSeverity.Error);
            return;
        }

        int port = double.IsNaN(_port.Value) ? 0 : checked((int)_port.Value);
        int timeout = double.IsNaN(_timeout.Value) ? 0 : checked((int)_timeout.Value);
        if (useProxy && (port is < 1 or > 65535))
        {
            _context.ShowInfo("Forward Proxy", "Proxy port must be between 1 and 65535.", InfoBarSeverity.Error);
            return;
        }
        if (useProxy && (timeout is < 1 or > ForwardProxyConfig.MaxProxyTimeoutSec))
        {
            _context.ShowInfo("Forward Proxy", _context.LF("Timeout must be between 1 and {0} seconds.", ForwardProxyConfig.MaxProxyTimeoutSec), InfoBarSeverity.Error);
            return;
        }

        string username = _username.Text?.Trim() ?? string.Empty;
        string password = _password.Password ?? string.Empty;
        if (string.IsNullOrWhiteSpace(username) != string.IsNullOrWhiteSpace(password))
        {
            _context.ShowInfo("Forward Proxy", "Username and password must be specified together.", InfoBarSeverity.Error);
            return;
        }

        _context.Controller.SaveProxy(new ForwardProxyConfig
        {
            useProxy = useProxy,
            proxyType = _http.IsChecked == true ? ForwardProxyConfig.PROXY_HTTP : ForwardProxyConfig.PROXY_SOCKS5,
            proxyServer = _server.Text?.Trim() ?? string.Empty,
            proxyPort = useProxy ? port : 0,
            proxyTimeout = useProxy ? timeout : 3,
            useAuth = useProxy && !string.IsNullOrWhiteSpace(username),
            authUser = username,
            authPwd = password,
        });
        _context.ShowInfo("Forward Proxy", "Forward proxy settings saved.", InfoBarSeverity.Success);
    }

    private static Grid CreateFormGrid()
    {
        var grid = new Grid { ColumnSpacing = 14, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private static void AddRow(Grid grid, string label, FrameworkElement control)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        TextBlock labelBlock = WinUIStyles.CreateText(label);
        labelBlock.HorizontalAlignment = HorizontalAlignment.Right;
        labelBlock.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(labelBlock, row);
        grid.Children.Add(labelBlock);
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }

    private static NumberBox CreateNumberBox(double minimum, double maximum, double value)
        => new()
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
}
