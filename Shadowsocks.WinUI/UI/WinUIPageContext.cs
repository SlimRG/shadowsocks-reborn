using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shadowsocks.Controller;
using Shadowsocks.Model;
using Shadowsocks.Localization;

namespace Shadowsocks.WinUI.UI;

internal sealed class WinUIPageContext
{
    public WinUIPageContext(
        ShadowsocksController? controller,
        Func<XamlRoot?> getXamlRoot,
        Func<nint> getWindowHandle,
        Action<int> navigateToSharingServer,
        Action<string, string, InfoBarSeverity> showInfo,
        Func<string, string, string, Task<ContentDialogResult>> showDialogAsync,
        Func<AppThemePreference> getTheme,
        Action<AppThemePreference> setTheme,
        Func<bool> getStartWithWindows,
        Func<bool, bool> setStartWithWindows,
        Action<bool> setAlwaysOnTop,
        Func<HotkeyConfig, IReadOnlyList<string>> registerHotkeys,
        Func<HotkeyConfig, IReadOnlyList<string>> applyHotkeys,
        Action requestApplicationExit,
        ILocalizationService localization)
    {
        Controller = controller;
        GetXamlRoot = getXamlRoot;
        GetWindowHandle = getWindowHandle;
        NavigateToSharingServer = navigateToSharingServer;
        _showInfo = showInfo;
        _showDialogAsync = showDialogAsync;
        GetTheme = getTheme;
        SetTheme = setTheme;
        GetStartWithWindows = getStartWithWindows;
        SetStartWithWindows = setStartWithWindows;
        SetAlwaysOnTop = setAlwaysOnTop;
        RegisterHotkeys = registerHotkeys;
        ApplyHotkeys = applyHotkeys;
        RequestApplicationExit = requestApplicationExit ?? throw new ArgumentNullException(nameof(requestApplicationExit));
        Localization = localization ?? throw new ArgumentNullException(nameof(localization));
    }

    public ShadowsocksController? Controller { get; }
    public Func<XamlRoot?> GetXamlRoot { get; }
    public Func<nint> GetWindowHandle { get; }
    public Action<int> NavigateToSharingServer { get; }
    private readonly Action<string, string, InfoBarSeverity> _showInfo;
    private readonly Func<string, string, string, Task<ContentDialogResult>> _showDialogAsync;

    public void ShowInfo(string title, string message, InfoBarSeverity severity)
        => _showInfo(Localization[title], Localization[message], severity);

    public Task<ContentDialogResult> ShowDialogAsync(string title, string message, string closeButtonText)
        => _showDialogAsync(Localization[title], Localization[message], Localization[closeButtonText]);
    public Func<AppThemePreference> GetTheme { get; }
    public Action<AppThemePreference> SetTheme { get; }
    public Func<bool> GetStartWithWindows { get; }
    public Func<bool, bool> SetStartWithWindows { get; }
    public Action<bool> SetAlwaysOnTop { get; }
    public Func<HotkeyConfig, IReadOnlyList<string>> RegisterHotkeys { get; }
    public Func<HotkeyConfig, IReadOnlyList<string>> ApplyHotkeys { get; }
    public Action RequestApplicationExit { get; }
    public ILocalizationService Localization { get; }

    public string L(string key) => Localization[key];
    public string LF(string key, params object[] args) => Localization.Format(key, args);

    public void SetToolTip(FrameworkElement element, string key)
    {
        ArgumentNullException.ThrowIfNull(element);
        string text = L(key);
        ToolTipService.SetToolTip(element, text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(element, text);
    }
}
