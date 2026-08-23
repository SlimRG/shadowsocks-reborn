using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using global::Windows.System;
using Shadowsocks.Controller.Hotkeys;
using Shadowsocks.Model;
using Shadowsocks.WinUI.UI;

namespace Shadowsocks.WinUI.Pages;

public sealed partial class HotkeysPage : Page, IRefreshablePage
{
    private static readonly (string Key, string Label)[] Definitions =
    [
        ("SwitchSystemProxy", "Toggle system proxy"),
        ("SwitchSystemProxyMode", "Switch Global / PAC"),
        ("SwitchAllowLan", "Allow clients from LAN"),
        ("ShowLogs", "Open logs window"),
        ("ServerMoveUp", "Switch to previous server"),
        ("ServerMoveDown", "Switch to next server"),
    ];

    private readonly WinUIPageContext _context;
    private readonly CheckBox _registerAtStartup;
    private readonly Dictionary<string, TextBox> _editors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _status = new(StringComparer.Ordinal);
    private readonly Button _saveButton;
    private readonly Button _discardButton;
    private bool _refreshing;

    internal HotkeysPage(WinUIPageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        Content = WinUIStyles.CreatePage(
            "Hotkeys",
            "Configure global keyboard shortcuts.",
            out StackPanel panel);

        var card = new StackPanel { Spacing = 12 };
        card.Children.Add(WinUIStyles.CreateSectionTitle("Global shortcuts"));

        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

        foreach ((string key, string label) in Definitions)
        {
            AddEditorRow(grid, key, label);
        }
        card.Children.Add(grid);

        _registerAtStartup = new CheckBox { Content = "Register hotkeys at startup" };
        _context.SetToolTip(_registerAtStartup, "Register the configured global hotkeys automatically when Shadowsocks starts.");
        _registerAtStartup.Checked += (_, _) => MarkDirty();
        _registerAtStartup.Unchecked += (_, _) => MarkDirty();
        card.Children.Add(_registerAtStartup);

        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var registerAll = new Button { Content = "Register all", MinWidth = 110 };
        _context.SetToolTip(registerAll, "Try to register every configured global hotkey immediately.");
        registerAll.Click += OnRegisterAllClicked;
        actions.Children.Add(registerAll);

        _discardButton = new Button { Content = "Discard changes", MinWidth = 120, IsEnabled = false };
        _context.SetToolTip(_discardButton, "Discard unsaved hotkey edits and reload the saved shortcuts.");
        _discardButton.Click += (_, _) => Refresh();
        Grid.SetColumn(_discardButton, 2);
        actions.Children.Add(_discardButton);

        _saveButton = new Button { Content = "Save", MinWidth = 92, IsEnabled = false };
        _context.SetToolTip(_saveButton, "Save the hotkey configuration and apply it to the running application.");
        _saveButton.Click += OnSaveClicked;
        Grid.SetColumn(_saveButton, 3);
        actions.Children.Add(_saveButton);
        card.Children.Add(actions);

        panel.Children.Add(WinUIStyles.CreateCard(card));
        panel.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = "Shortcut format",
            Message = "Focus a shortcut field and press Ctrl/Alt/Shift/Win plus a key. Press Delete or Backspace with a modifier to use that key; use Esc to clear the field. The status column shows the last registration result.",
        });

        WinUILocalization.Apply(Content, _context.Localization);
    }

    public void Refresh()
    {
        HotkeyConfig? config = _context.Controller?.GetCurrentConfiguration().hotkey;
        if (config is null)
        {
            return;
        }

        _refreshing = true;
        try
        {
            _editors["SwitchSystemProxy"].Text = config.SwitchSystemProxy ?? string.Empty;
            _editors["SwitchSystemProxyMode"].Text = config.SwitchSystemProxyMode ?? string.Empty;
            _editors["SwitchAllowLan"].Text = config.SwitchAllowLan ?? string.Empty;
            _editors["ShowLogs"].Text = config.ShowLogs ?? string.Empty;
            _editors["ServerMoveUp"].Text = config.ServerMoveUp ?? string.Empty;
            _editors["ServerMoveDown"].Text = config.ServerMoveDown ?? string.Empty;
            _registerAtStartup.IsChecked = config.RegHotkeysAtStartup;
            foreach (TextBlock status in _status.Values)
            {
                SetRegistrationStatus(status, "—", "Not tested");
            }
            SetDirty(false);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void AddEditorRow(Grid grid, string key, string label)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        TextBlock labelBlock = WinUIStyles.CreateText(_context.L(label));
        labelBlock.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(labelBlock, row);
        grid.Children.Add(labelBlock);

        var editor = new TextBox
        {
            PlaceholderText = "Ctrl+Alt+S",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsReadOnly = true,
        };
        _context.SetToolTip(editor, "Focus this field and press the desired modifier keys plus a key. Press Esc to clear it.");
        editor.TextChanged += (_, _) => MarkDirty();
        editor.KeyDown += (_, args) => RecordKeyDown(key, args);
        editor.KeyUp += (_, args) => FinishOnKeyUp(key, args);
        _editors[key] = editor;
        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);

        TextBlock status = WinUIStyles.CreateText("—", "BodyStrongTextBlockStyle");
        status.HorizontalAlignment = HorizontalAlignment.Center;
        status.VerticalAlignment = VerticalAlignment.Center;
        SetRegistrationStatus(status, "—", "Not tested");
        _status[key] = status;
        Grid.SetRow(status, row);
        Grid.SetColumn(status, 2);
        grid.Children.Add(status);
    }

    private void RecordKeyDown(string configKey, KeyRoutedEventArgs args)
    {
        args.Handled = true;
        if (args.Key == VirtualKey.Escape)
        {
            _editors[configKey].Text = string.Empty;
            return;
        }

        var parts = new List<string>(5);
        if (IsKeyDown(VirtualKey.Control)) parts.Add("Ctrl");
        if (IsKeyDown(VirtualKey.Menu)) parts.Add("Alt");
        if (IsKeyDown(VirtualKey.Shift)) parts.Add("Shift");
        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows)) parts.Add("Win");

        if (parts.Count == 0)
        {
            _editors[configKey].Text = string.Empty;
            return;
        }

        if (!IsModifier(args.Key))
        {
            string? keyName = ToHotkeyKeyName(args.Key);
            if (!string.IsNullOrWhiteSpace(keyName))
            {
                parts.Add(keyName);
            }
        }

        string value = string.Join("+", parts);
        _editors[configKey].Text = IsModifier(args.Key) ? value + "+" : value;
    }

    private void FinishOnKeyUp(string configKey, KeyRoutedEventArgs args)
    {
        args.Handled = true;
        if (_editors[configKey].Text.EndsWith('+'))
        {
            _editors[configKey].Text = string.Empty;
        }
    }

    // Keep modifier-state lookup on Win32 so the WinUI frontend does not pull the older
    // UWP XAML projection graph into this process. GetKeyState reports the state that
    // belonged to the keyboard message currently being handled, which is exactly what the
    // shortcut recorder needs.
    private static bool IsKeyDown(VirtualKey key)
        => GetKeyState((int)key) < 0;

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int virtualKey);

    private static bool IsModifier(VirtualKey key)
        => key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift
            or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static string? ToHotkeyKeyName(VirtualKey key)
    {
        uint value = (uint)key;
        if (value is >= 0x41 and <= 0x5A)
        {
            return ((char)value).ToString();
        }
        if (value is >= 0x30 and <= 0x39)
        {
            return "D" + (char)value;
        }
        if (value is >= 0x60 and <= 0x69)
        {
            return "NumPad" + (value - 0x60).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (value is >= 0x70 and <= 0x87)
        {
            return "F" + (value - 0x70 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return value switch
        {
            0x08 => "Back",
            0x09 => "Tab",
            0x0D => "Enter",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            0xBA => "Oem1",
            0xBB => "OemPlus",
            0xBC => "OemComma",
            0xBD => "OemMinus",
            0xBE => "OemPeriod",
            0xBF => "Oem2",
            0xC0 => "Oem3",
            0xDB => "Oem4",
            0xDC => "Oem5",
            0xDD => "Oem6",
            0xDE => "Oem7",
            _ => null,
        };
    }

    private void OnRegisterAllClicked(object _, RoutedEventArgs _1)
    {
        HotkeyConfig? config = BuildConfig();
        if (config is null)
        {
            return;
        }

        IReadOnlyList<string> failures = _context.RegisterHotkeys(config);
        UpdateRegistrationStatus(failures);
        _context.ShowInfo(
            "Hotkeys",
            failures.Count == 0 ? _context.L("All configured shortcuts registered.") : _context.LF("Windows refused: {0}", string.Join(", ", failures)),
            failures.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private void OnSaveClicked(object _, RoutedEventArgs _1)
    {
        if (_refreshing)
        {
            return;
        }

        HotkeyConfig? config = BuildConfig();
        if (config is null)
        {
            return;
        }

        IReadOnlyList<string> failures = _context.ApplyHotkeys(config);
        UpdateRegistrationStatus(failures);
        SetDirty(false);
        _context.ShowInfo(
            "Hotkeys",
            failures.Count == 0 ? _context.L("Hotkeys saved and registered.") : _context.LF("Saved, but Windows refused: {0}", string.Join(", ", failures)),
            failures.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private HotkeyConfig? BuildConfig()
    {
        string[] invalid = _editors
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value.Text)
                           && !HotkeyGesture.TryParse(pair.Value.Text.Trim(), out _))
            .Select(pair => _context.L(Definitions.First(definition => definition.Key == pair.Key).Label))
            .ToArray();
        if (invalid.Length > 0)
        {
            _context.ShowInfo("Hotkeys", _context.LF("Invalid shortcut: {0}", string.Join(", ", invalid)), InfoBarSeverity.Error);
            return null;
        }

        return new HotkeyConfig
        {
            SwitchSystemProxy = Normalize("SwitchSystemProxy"),
            SwitchSystemProxyMode = Normalize("SwitchSystemProxyMode"),
            SwitchAllowLan = Normalize("SwitchAllowLan"),
            ShowLogs = Normalize("ShowLogs"),
            ServerMoveUp = Normalize("ServerMoveUp"),
            ServerMoveDown = Normalize("ServerMoveDown"),
            RegHotkeysAtStartup = _registerAtStartup.IsChecked == true,
        };
    }

    private void UpdateRegistrationStatus(IReadOnlyList<string> failures)
    {
        foreach ((string key, TextBlock status) in _status)
        {
            bool failed = failures.Contains(key, StringComparer.Ordinal);
            SetRegistrationStatus(status, failed ? "✖" : "✔", failed ? "Registration failed" : "Registered");
        }
    }

    private void SetRegistrationStatus(TextBlock status, string glyph, string accessibilityKey)
    {
        status.Text = glyph;
        string localizedStatus = _context.L(accessibilityKey);
        ToolTipService.SetToolTip(status, localizedStatus);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(status, localizedStatus);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(status, localizedStatus);
    }

    private void MarkDirty()
    {
        if (!_refreshing)
        {
            SetDirty(true);
        }
    }

    private void SetDirty(bool dirty)
    {
        _saveButton.IsEnabled = dirty;
        _discardButton.IsEnabled = dirty;
    }

    private string Normalize(string key)
    {
        string value = _editors[key].Text?.Trim() ?? string.Empty;
        return HotkeyGesture.TryParse(value, out HotkeyGesture gesture)
            ? gesture.ToString()
            : string.Empty;
    }
}
