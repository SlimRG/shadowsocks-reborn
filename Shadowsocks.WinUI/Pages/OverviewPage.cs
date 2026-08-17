using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shadowsocks.Controller;
using Shadowsocks.Model;
using Shadowsocks.WinUI.UI;

namespace Shadowsocks.WinUI.Pages;

public sealed class OverviewPage : Page, IRefreshablePage
{
    private readonly TextBlock _serverValue;
    private readonly TextBlock _statusValue;
    private readonly TextBlock _modeValue;
    private readonly TextBlock _proxyValue;
    private readonly TeachingTip _trayTip;
    private readonly WinUIPageContext _context;

    internal OverviewPage(WinUIPageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        Content = WinUIStyles.CreatePage(
            "Overview",
            "Current proxy, server, and system-shell state at a glance.",
            out StackPanel panel);

        var stateStack = new StackPanel { Spacing = 12 };
        stateStack.Children.Add(WinUIStyles.CreateSectionTitle("Connection state"));
        stateStack.Children.Add(WinUIStyles.CreateKeyValueRow("Server", out _serverValue));
        stateStack.Children.Add(WinUIStyles.CreateKeyValueRow("Controller", out _statusValue));
        stateStack.Children.Add(WinUIStyles.CreateKeyValueRow("Traffic mode", out _modeValue));
        stateStack.Children.Add(WinUIStyles.CreateKeyValueRow("System proxy", out _proxyValue));
        panel.Children.Add(WinUIStyles.CreateCard(stateStack));

        var backgroundStack = new StackPanel { Spacing = 12 };
        backgroundStack.Children.Add(WinUIStyles.CreateSectionTitle("Runs quietly in the background"));
        var explanation = WinUIStyles.CreateText(
            "Closing the window hides it to the notification area. The proxy controller keeps running until you choose Exit from the tray menu.");
        explanation.Opacity = 0.8;
        backgroundStack.Children.Add(explanation);

        var tipButton = new Button { Content = "How tray mode works", HorizontalAlignment = HorizontalAlignment.Left };
        _context.SetToolTip(tipButton, "Explain how closing or hiding the main window differs from exiting the tray application.");
        backgroundStack.Children.Add(tipButton);
        panel.Children.Add(WinUIStyles.CreateCard(backgroundStack));

        _trayTip = new TeachingTip
        {
            Title = "Background mode",
            Subtitle = "Use the tray icon to reopen Shadowsocks Reborn. A plain second launch points you back to the tray instead of starting another proxy.",
            Target = tipButton,
            PreferredPlacement = TeachingTipPlacementMode.Bottom,
            IsLightDismissEnabled = true,
        };
        panel.Children.Add(_trayTip);
        tipButton.Click += (_, _) => _trayTip.IsOpen = true;

        WinUILocalization.Apply(Content, _context.Localization);
    }

    public void Refresh()
    {
        ShadowsocksController? controller = _context.Controller;
        if (controller is null)
        {
            _serverValue.Text = _context.L("Unavailable");
            _statusValue.Text = _context.L("Failed to start");
            _modeValue.Text = "—";
            _proxyValue.Text = "—";
            return;
        }

        Configuration configuration = controller.GetCurrentConfiguration();
        Server? server = controller.GetCurrentServer();
        _serverValue.Text = server?.IsConfigured == true ? server.ToString() : _context.L("Not configured");
        _statusValue.Text = controller.IsProxyListenerRunning ? _context.L("Running") : _context.L("Stopped");
        _modeValue.Text = _context.L(controller.GetTrafficRuntimeMode().ToString());
        _proxyValue.Text = configuration.enabled
            ? configuration.global ? _context.L("Enabled · Global") : _context.L("Enabled · PAC")
            : _context.L("Disabled");
    }
}
