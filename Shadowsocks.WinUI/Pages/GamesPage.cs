using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shadowsocks.Controller.Traffic;
using Shadowsocks.Controller.Traffic.Applications;
using Shadowsocks.Model;
using Shadowsocks.WinUI.UI;

namespace Shadowsocks.WinUI.Pages;

public sealed class GamesPage : Page, IRefreshablePage
{
    private readonly WinUIPageContext _context;
    private readonly TextBlock _modeValue;
    private readonly TextBlock _triggerValue;
    private readonly TextBlock _winDivertValue;
    private readonly StackPanel _applications;
    private readonly TextBox _newApplication;
    private readonly StackPanel _detectedGames;
    private readonly TextBlock _scanStatus;
    private readonly Button _scanButton;
    private readonly ProgressRing _scanProgress;
    private readonly List<string> _applicationItems = new();
    private IReadOnlyList<DiscoveredGame> _discoveredGames = Array.Empty<DiscoveredGame>();
    private bool _dirty;
    private bool _scanStarted;
    private bool _scanRunning;

    internal GamesPage(WinUIPageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        Content = WinUIStyles.CreatePage(
            "Game compatibility",
            "Admin capture is suspended automatically while a configured anti-cheat-sensitive application is running.",
            out StackPanel panel);

        var stateCard = new StackPanel { Spacing = 10 };
        stateCard.Children.Add(WinUIStyles.CreateSectionTitle("Current state"));
        stateCard.Children.Add(WinUIStyles.CreateKeyValueRow("Game Mode", out _modeValue));
        stateCard.Children.Add(WinUIStyles.CreateKeyValueRow("Triggered by", out _triggerValue));
        stateCard.Children.Add(WinUIStyles.CreateKeyValueRow("WinDivert", out _winDivertValue));
        panel.Children.Add(WinUIStyles.CreateCard(stateCard));

        var detectedCard = new StackPanel { Spacing = 12 };
        detectedCard.Children.Add(WinUIStyles.CreateSectionTitle("Detected games"));
        detectedCard.Children.Add(WinUIStyles.CreateText(
            "Shadowsocks can scan local Steam, Epic Games, GOG and Xbox game libraries. Nothing is added until you choose Add."));

        var scanActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        _scanButton = new Button { Content = "Scan installed games" };
        _context.SetToolTip(_scanButton, "Scan Steam, Epic Games, GOG and Xbox libraries for installed games that can be added to automatic Game Mode.");
        _scanButton.Click += async (_, _) => await ScanInstalledGamesAsync();
        scanActions.Children.Add(_scanButton);
        _scanProgress = new ProgressRing
        {
            Width = 20,
            Height = 20,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        scanActions.Children.Add(_scanProgress);
        _scanStatus = WinUIStyles.CreateText("Scan has not run yet.", "CaptionTextBlockStyle");
        _scanStatus.Opacity = 0.72;
        scanActions.Children.Add(_scanStatus);
        detectedCard.Children.Add(scanActions);

        _detectedGames = new StackPanel { Spacing = 8 };
        detectedCard.Children.Add(_detectedGames);
        panel.Children.Add(WinUIStyles.CreateCard(detectedCard));

        var appCard = new StackPanel { Spacing = 12 };
        appCard.Children.Add(WinUIStyles.CreateSectionTitle("Applications"));
        appCard.Children.Add(WinUIStyles.CreateText(
            "Use an executable name, full path, or wildcard pattern. Game Mode is automatic and is never a selectable traffic mode."));
        _applications = new StackPanel { Spacing = 8 };
        appCard.Children.Add(_applications);

        _newApplication = new TextBox
        {
            Header = "Add application manually",
            PlaceholderText = "game.exe or C:\\Games\\Game\\game.exe",
        };
        _context.SetToolTip(_newApplication, "Enter an executable name, full path, or wildcard pattern to trigger automatic Game Mode.");
        appCard.Children.Add(_newApplication);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var add = new Button { Content = "Add" };
        _context.SetToolTip(add, "Add the manually entered application pattern to the Game Mode list.");
        add.Click += OnAddClicked;
        actions.Children.Add(add);
        var save = new Button { Content = "Save" };
        _context.SetToolTip(save, "Save the configured Game Mode application list.");
        save.Click += OnSaveClicked;
        actions.Children.Add(save);
        var discard = new Button { Content = "Discard changes" };
        _context.SetToolTip(discard, "Discard unsaved Game Mode application changes.");
        discard.Click += (_, _) =>
        {
            _dirty = false;
            Refresh();
        };
        actions.Children.Add(discard);
        appCard.Children.Add(actions);
        panel.Children.Add(WinUIStyles.CreateCard(appCard));

        Loaded += async (_, _) =>
        {
            if (!_scanStarted)
            {
                await ScanInstalledGamesAsync();
            }
        };

        WinUILocalization.Apply(Content, _context.Localization);
    }

    public void Refresh()
    {
        Shadowsocks.Controller.ShadowsocksController? controller = _context.Controller;
        if (controller is null)
        {
            _modeValue.Text = _context.L("Unavailable");
            _triggerValue.Text = "—";
            _winDivertValue.Text = "—";
            return;
        }

        TrafficCaptureStatus status = controller.GetTrafficCaptureStatus();
        _modeValue.Text = status.GameModeActive ? _context.L("ACTIVE") : _context.L("Inactive");
        _triggerValue.Text = status.RunningGameApplications.Count > 0
            ? string.Join(", ", status.RunningGameApplications)
            : "—";
        _winDivertValue.Text = status.GameModeActive
            ? _context.L("Suspended")
            : status.WinDivertActive ? _context.L("Active") : _context.L("Inactive");

        if (!_dirty)
        {
            Configuration configuration = controller.GetCurrentConfiguration();
            _applicationItems.Clear();
            _applicationItems.AddRange(configuration.gameModeApplications ?? []);
        }
        RebuildApplications(status.RunningGameApplications);
        RebuildDetectedGames();
    }

    private async Task ScanInstalledGamesAsync()
    {
        if (_scanRunning)
        {
            return;
        }

        _scanStarted = true;
        _scanRunning = true;
        _scanButton.IsEnabled = false;
        _scanProgress.IsActive = true;
        _scanProgress.Visibility = Visibility.Visible;
        _scanStatus.Text = _context.L("Scanning installed games…");
        try
        {
            _discoveredGames = await Task.Run(() => GameDiscoveryService.Discover());
            _scanStatus.Text = _discoveredGames.Count == 0
                ? _context.L("No supported game libraries were detected.")
                : _context.LF("Found {0} games.", _discoveredGames.Count);
        }
        catch (Exception exception)
        {
            _discoveredGames = Array.Empty<DiscoveredGame>();
            _scanStatus.Text = _context.L("Game scan failed.");
            _context.ShowInfo("Game compatibility", exception.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            _scanRunning = false;
            _scanButton.IsEnabled = true;
            _scanProgress.IsActive = false;
            _scanProgress.Visibility = Visibility.Collapsed;
            RebuildDetectedGames();
        }
    }

    private void RebuildDetectedGames()
    {
        _detectedGames.Children.Clear();
        if (_discoveredGames.Count == 0)
        {
            TextBlock empty = WinUIStyles.CreateText(_context.L("No detected games to show."));
            empty.Opacity = 0.68;
            _detectedGames.Children.Add(empty);
            return;
        }

        foreach (DiscoveredGame game in _discoveredGames)
        {
            bool alreadyAdded = _applicationItems.Any(existing =>
                string.Equals(existing, game.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(existing, Path.GetFileName(game.ExecutablePath), StringComparison.OrdinalIgnoreCase));

            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var details = new StackPanel { Spacing = 2 };
            details.Children.Add(WinUIStyles.CreateText(game.Name, "BodyStrongTextBlockStyle"));
            TextBlock source = WinUIStyles.CreateText($"{game.Source} · {game.ExecutablePath}", "CaptionTextBlockStyle");
            source.TextTrimming = TextTrimming.CharacterEllipsis;
            source.Opacity = 0.66;
            details.Children.Add(source);
            row.Children.Add(details);

            var add = new Button
            {
                Content = alreadyAdded ? _context.L("Added") : _context.L("Add"),
                IsEnabled = !alreadyAdded,
                Tag = game.ExecutablePath,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _context.SetToolTip(add, "Add this detected game executable to the automatic Game Mode list.");
            add.Click += OnAddDetectedGameClicked;
            Grid.SetColumn(add, 1);
            row.Children.Add(add);
            _detectedGames.Children.Add(WinUIStyles.CreateCard(row));
        }
    }

    private void RebuildApplications(IReadOnlyList<string> runningApplications)
    {
        _applications.Children.Clear();
        if (_applicationItems.Count == 0)
        {
            TextBlock empty = WinUIStyles.CreateText(_context.L("No game applications are configured yet."));
            empty.Opacity = 0.72;
            _applications.Children.Add(empty);
            return;
        }

        for (int index = 0; index < _applicationItems.Count; index++)
        {
            string application = _applicationItems[index];
            string fileName = Path.GetFileName(application);
            bool running = runningApplications.Any(name =>
                string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, application, StringComparison.OrdinalIgnoreCase));

            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var value = new StackPanel { Spacing = 2 };
            value.Children.Add(WinUIStyles.CreateText(application, "BodyStrongTextBlockStyle"));
            TextBlock state = WinUIStyles.CreateText(running ? _context.L("Running") : _context.L("Not running"), "CaptionTextBlockStyle");
            state.Opacity = running ? 1 : 0.65;
            value.Children.Add(state);
            row.Children.Add(value);

            var remove = new Button { Content = _context.L("Remove"), Tag = index };
            _context.SetToolTip(remove, "Remove this application from the automatic Game Mode list.");
            remove.Click += OnRemoveClicked;
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            _applications.Children.Add(WinUIStyles.CreateCard(row));
        }
    }

    private void OnAddDetectedGameClicked(object sender, RoutedEventArgs _)
    {
        if (sender is not Button { Tag: string executablePath } || string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }
        if (!_applicationItems.Any(existing => string.Equals(existing, executablePath, StringComparison.OrdinalIgnoreCase)))
        {
            _applicationItems.Add(executablePath);
            _dirty = true;
        }
        Refresh();
    }

    private void OnAddClicked(object _, RoutedEventArgs _1)
    {
        string value = _newApplication.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!_applicationItems.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
        {
            _applicationItems.Add(value);
            _dirty = true;
        }
        _newApplication.Text = string.Empty;
        Refresh();
    }

    private void OnRemoveClicked(object sender, RoutedEventArgs _1)
    {
        if (sender is not Button { Tag: int index } || index < 0 || index >= _applicationItems.Count)
        {
            return;
        }

        _applicationItems.RemoveAt(index);
        _dirty = true;
        Refresh();
    }

    private async void OnSaveClicked(object _, RoutedEventArgs _1)
    {
        if (_context.Controller is null)
        {
            return;
        }

        Configuration configuration = _context.Controller.GetCurrentConfiguration();
        bool saved = await _context.Controller.SaveTrafficRoutingAsync(
            configuration.trafficCaptureMode,
            configuration.applicationRules ?? [],
            _applicationItems);
        if (saved)
        {
            _dirty = false;
            _context.ShowInfo("Game compatibility", "Automatic Game Mode application list saved.", InfoBarSeverity.Success);
        }
        else
        {
            _context.ShowInfo("Game compatibility", "The application list could not be applied.", InfoBarSeverity.Warning);
        }
        Refresh();
    }
}
