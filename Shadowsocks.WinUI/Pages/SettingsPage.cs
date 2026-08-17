using System;
using Shadowsocks.Core.Storage;
using System.IO;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shadowsocks.WinUI.UI;

namespace Shadowsocks.WinUI.Pages;

public sealed class SettingsPage : Page, IRefreshablePage
{
    private readonly ComboBox _themeBox;
    private readonly ToggleSwitch _startupToggle;
    private readonly TextBlock _storagePath;
    private readonly WinUIPageContext _context;
    private bool _refreshing;

    internal SettingsPage(WinUIPageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        Content = WinUIStyles.CreatePage(
            "Settings",
            "Application preferences use native Windows controls and follow the Windows 11 settings layout.",
            out StackPanel panel);

        var appearance = new StackPanel { Spacing = 12 };
        appearance.Children.Add(WinUIStyles.CreateSectionTitle("Appearance"));
        appearance.Children.Add(WinUIStyles.CreateText("Theme follows Windows by default. Choose a fixed theme only when you explicitly want to override the system setting."));
        _themeBox = new ComboBox
        {
            Header = "App theme",
            Width = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _themeBox.Items.Add(new ComboBoxItem { Content = "System", Tag = AppThemePreference.System });
        _themeBox.Items.Add(new ComboBoxItem { Content = "Light", Tag = AppThemePreference.Light });
        _themeBox.Items.Add(new ComboBoxItem { Content = "Dark", Tag = AppThemePreference.Dark });
        _themeBox.SelectionChanged += OnThemeChanged;
        _context.SetToolTip(_themeBox, "Choose whether Shadowsocks follows the Windows theme or always uses light or dark mode.");
        appearance.Children.Add(_themeBox);
        panel.Children.Add(WinUIStyles.CreateCard(appearance));

        var behavior = new StackPanel { Spacing = 16 };
        behavior.Children.Add(WinUIStyles.CreateSectionTitle("Behavior"));
        _startupToggle = new ToggleSwitch
        {
            Header = "Start on Boot",
            OnContent = "On",
            OffContent = "Off",
        };
        _startupToggle.Toggled += OnStartupToggled;
        _context.SetToolTip(_startupToggle, "Start Shadowsocks automatically when you sign in to Windows. This option is unavailable in Clean Mode.");
        behavior.Children.Add(_startupToggle);
        if (AppStoragePaths.IsCleanMode)
        {
            behavior.Children.Add(WinUIStyles.CreateText("Start on Boot is unavailable in Clean Mode.", "CaptionTextBlockStyle"));
        }
        panel.Children.Add(WinUIStyles.CreateCard(behavior));

        var storage = new StackPanel { Spacing = 12 };
        storage.Children.Add(WinUIStyles.CreateSectionTitle("Storage"));
        storage.Children.Add(WinUIStyles.CreateText(
            AppStoragePaths.IsCleanMode
                ? "Clean Mode is active. All writable data is stored in a temporary session directory and removed when Shadowsocks exits."
                : @"All writable application data is stored under %LOCALAPPDATA%\Shadowsocks."));
        _storagePath = WinUIStyles.CreateText(AppStoragePaths.StorageRoot, "CaptionTextBlockStyle");
        storage.Children.Add(_storagePath);
        var openDataFolder = new Button
        {
            Content = "Open",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        openDataFolder.Click += OnOpenDataFolder;
        _context.SetToolTip(openDataFolder, "Open the folder containing settings, logs, caches and runtime data for the current mode.");
        storage.Children.Add(openDataFolder);
        panel.Children.Add(WinUIStyles.CreateCard(storage));

        WinUILocalization.Apply(Content, _context.Localization);
    }

    public void Refresh()
    {
        _refreshing = true;
        try
        {
            AppThemePreference currentTheme = _context?.GetTheme() ?? AppThemePreference.System;
            foreach (object item in _themeBox.Items)
            {
                if (item is ComboBoxItem comboItem && comboItem.Tag is AppThemePreference theme && theme == currentTheme)
                {
                    _themeBox.SelectedItem = comboItem;
                    break;
                }
            }
            _startupToggle.IsEnabled = !AppStoragePaths.IsCleanMode;
            _startupToggle.IsOn = !AppStoragePaths.IsCleanMode && _context?.GetStartWithWindows() == true;
            _storagePath.Text = AppStoragePaths.StorageRoot;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void OnThemeChanged(object _, SelectionChangedEventArgs _1)
    {
        if (_refreshing || _context is null || _themeBox.SelectedItem is not ComboBoxItem { Tag: AppThemePreference theme })
        {
            return;
        }

        _context.SetTheme(theme);
    }

    private void OnStartupToggled(object _, Microsoft.UI.Xaml.RoutedEventArgs _1)
    {
        if (_refreshing || _context is null || AppStoragePaths.IsCleanMode)
        {
            return;
        }

        if (!_context.SetStartWithWindows(_startupToggle.IsOn))
        {
            _context.ShowInfo("Startup", "Failed to update Start on Boot.", InfoBarSeverity.Error);
            Refresh();
        }
    }

    private void OnOpenDataFolder(object _, RoutedEventArgs _1)
    {
        try
        {
            Directory.CreateDirectory(AppStoragePaths.StorageRoot);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppStoragePaths.StorageRoot,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            _context.ShowInfo("Storage", "Failed to open data folder.", InfoBarSeverity.Error);
        }
    }

}
