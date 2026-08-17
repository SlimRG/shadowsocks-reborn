using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shadowsocks.Controller;
using Shadowsocks.Core;
using Shadowsocks.Model;
using Shadowsocks.WinUI.UI;

namespace Shadowsocks.WinUI.Pages;

public sealed class AboutPage : Page, IRefreshablePage
{
    private readonly WinUIPageContext _context;
    private readonly UpdateChecker? _updateChecker;
    private readonly TextBlock _updateStatus;
    private readonly Button _checkButton;
    private readonly Button _downloadButton;
    private readonly Button _skipButton;
    private readonly Button _notNowButton;
    private readonly TextBlock _releaseHeading;
    private readonly TextBox _releaseNotes;
    private readonly ToggleSwitch _preReleaseToggle;
    private readonly ToggleSwitch _updateAtStartupToggle;
    private bool _refreshing;
    private bool _updateFound;

    internal AboutPage(WinUIPageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        if (context.Controller is not null)
        {
            _updateChecker = new UpdateChecker(context.Controller);
            _updateChecker.UpdateAvailable += OnUpdateAvailable;
            _updateChecker.CheckUpdateCompleted += OnUpdateCheckCompleted;
        }

        Content = WinUIStyles.CreatePage(
            "About & updates",
            "Version information, project links and update controls.",
            out StackPanel panel);

        var identity = new StackPanel { Spacing = 10 };
        identity.Children.Add(WinUIStyles.CreateSectionTitle("Shadowsocks Reborn"));
        identity.Children.Add(WinUIStyles.CreateText(_context.LF("Version {0}", ApplicationInfo.Version), "BodyStrongTextBlockStyle"));
        identity.Children.Add(WinUIStyles.CreateText(".NET 10 · WinUI 3 · Windows App SDK · x64"));
        identity.Children.Add(WinUIStyles.CreateText(
            "Core networking is UI-independent. Windows integration, WinDivert coordination and tray support live in the Windows platform layer."));

        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var projectButton = new Button { Content = "Project" };
        _context.SetToolTip(projectButton, "Open the Shadowsocks Reborn project page in your default browser.");
        projectButton.Click += (_, _) => OpenUrl(ApplicationInfo.RepositoryUrl);
        links.Children.Add(projectButton);
        var issuesButton = new Button { Content = "Report issue" };
        _context.SetToolTip(issuesButton, "Open the GitHub issue tracker for bug reports and feature requests.");
        issuesButton.Click += (_, _) => OpenUrl(ApplicationInfo.IssuesUrl);
        links.Children.Add(issuesButton);
        identity.Children.Add(links);
        panel.Children.Add(WinUIStyles.CreateCard(identity));

        var updates = new StackPanel { Spacing = 12 };
        updates.Children.Add(WinUIStyles.CreateSectionTitle("Updates"));
        _updateStatus = WinUIStyles.CreateText("Not checked yet.");
        updates.Children.Add(_updateStatus);
        _updateAtStartupToggle = new ToggleSwitch
        {
            Header = "Check for Updates at Startup",
            OnContent = "On",
            OffContent = "Off",
        };
        _context.SetToolTip(_updateAtStartupToggle, "Check GitHub for a newer Shadowsocks Reborn release after the application starts.");
        _updateAtStartupToggle.Toggled += OnUpdateAtStartupToggled;
        updates.Children.Add(_updateAtStartupToggle);

        _preReleaseToggle = new ToggleSwitch
        {
            Header = "Include prerelease versions",
            OnContent = "On",
            OffContent = "Off",
        };
        _context.SetToolTip(_preReleaseToggle, "Include prerelease builds when checking GitHub for updates.");
        _preReleaseToggle.Toggled += OnPreReleaseToggled;
        updates.Children.Add(_preReleaseToggle);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _checkButton = new Button { Content = "Check for Updates" };
        _context.SetToolTip(_checkButton, "Check GitHub for available updates now.");
        _checkButton.Click += OnCheckNowClicked;
        actions.Children.Add(_checkButton);
        _downloadButton = new Button { Content = "Download update", Visibility = Visibility.Collapsed };
        _context.SetToolTip(_downloadButton, "Download the selected release asset to the application update workspace.");
        _downloadButton.Click += OnDownloadClicked;
        actions.Children.Add(_downloadButton);
        _skipButton = new Button { Content = "Skip this version", Visibility = Visibility.Collapsed };
        _context.SetToolTip(_skipButton, "Ignore this release in future automatic update checks.");
        _skipButton.Click += OnSkipClicked;
        actions.Children.Add(_skipButton);
        _notNowButton = new Button { Content = "Not now", Visibility = Visibility.Collapsed };
        _context.SetToolTip(_notNowButton, "Hide the current update notification without skipping the release.");
        _notNowButton.Click += OnNotNowClicked;
        actions.Children.Add(_notNowButton);
        updates.Children.Add(actions);

        _releaseHeading = WinUIStyles.CreateSectionTitle("Release notes");
        _releaseHeading.Visibility = Visibility.Collapsed;
        updates.Children.Add(_releaseHeading);
        _releaseNotes = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 140,
            MaxHeight = 300,
            Visibility = Visibility.Collapsed,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI"),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_releaseNotes, ScrollBarVisibility.Auto);
        updates.Children.Add(_releaseNotes);
        panel.Children.Add(WinUIStyles.CreateCard(updates));

        WinUILocalization.Apply(Content, _context.Localization);
    }

    public void Refresh()
    {
        Configuration? configuration = _context.Controller?.GetCurrentConfiguration();
        _refreshing = true;
        try
        {
            _updateAtStartupToggle.IsOn = configuration?.autoCheckUpdate == true;
            _preReleaseToggle.IsOn = configuration?.checkPreRelease == true;
        }
        finally
        {
            _refreshing = false;
        }
    }

    public async void CheckForUpdates()
    {
        if (_updateChecker is null)
        {
            _context.ShowInfo("Updates", "Controller is unavailable.", InfoBarSeverity.Error);
            return;
        }

        _updateFound = false;
        _checkButton.IsEnabled = false;
        HideUpdateDetails();
        _updateStatus.Text = _context.L("Checking GitHub releases…");
        try
        {
            await _updateChecker.CheckForVersionUpdate();
            if (!_updateFound)
            {
                _updateStatus.Text = _context.LF("You are up to date · {0}", ApplicationInfo.Version);
            }
        }
        finally
        {
            _checkButton.IsEnabled = true;
        }
    }

    private void OnCheckNowClicked(object _, RoutedEventArgs _1)
        => CheckForUpdates();

    private void OnUpdateAvailable(object? _, UpdateAvailableEventArgs e)
    {
        string releaseNotes = e.Release["body"]?.ToString() ?? _context.L("Release notes are unavailable.");
        bool isPrerelease = (bool?)e.Release["prerelease"] == true;
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_updateChecker is null)
            {
                return;
            }

            _updateFound = true;
            _updateStatus.Text = _context.LF("{0} available: {1}", isPrerelease ? _context.L("Pre-release") : _context.L("Release"), _updateChecker.NewReleaseVersion);
            _releaseNotes.Text = releaseNotes;
            _downloadButton.Visibility = Visibility.Visible;
            _skipButton.Visibility = Visibility.Visible;
            _notNowButton.Visibility = Visibility.Visible;
            _releaseHeading.Visibility = Visibility.Visible;
            _releaseNotes.Visibility = Visibility.Visible;
        });
    }

    private void OnUpdateCheckCompleted(object? _, EventArgs _1)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!_updateFound)
            {
                _updateStatus.Text = _context.LF("You are up to date · {0}", ApplicationInfo.Version);
            }
        });
    }

    private async void OnDownloadClicked(object _, RoutedEventArgs _1)
    {
        if (_updateChecker is null)
        {
            return;
        }

        _downloadButton.IsEnabled = false;
        try
        {
            await _updateChecker.DoUpdate();
            _context.ShowInfo("Updates", "Update assets were downloaded to the temporary folder.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            _context.ShowInfo("Updates", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _downloadButton.IsEnabled = true;
        }
    }

    private void OnSkipClicked(object _, RoutedEventArgs _1)
    {
        if (_updateChecker is null)
        {
            return;
        }

        _updateChecker.SkipUpdate();
        HideUpdateDetails();
        _updateStatus.Text = _context.L("This version will be skipped.");
    }

    private void OnNotNowClicked(object _, RoutedEventArgs _1)
    {
        HideUpdateDetails();
        _updateStatus.Text = _context.L("Update postponed for this session.");
    }

    private void HideUpdateDetails()
    {
        _downloadButton.Visibility = Visibility.Collapsed;
        _skipButton.Visibility = Visibility.Collapsed;
        _notNowButton.Visibility = Visibility.Collapsed;
        _releaseHeading.Visibility = Visibility.Collapsed;
        _releaseNotes.Visibility = Visibility.Collapsed;
    }

    private void OnUpdateAtStartupToggled(object _, RoutedEventArgs _1)
    {
        if (_refreshing || _context.Controller is null)
        {
            return;
        }

        _context.Controller.ToggleCheckingUpdate(_updateAtStartupToggle.IsOn);
    }

    private void OnPreReleaseToggled(object _, RoutedEventArgs _1)
    {
        if (_refreshing || _context.Controller is null)
        {
            return;
        }

        _context.Controller.ToggleCheckingPreRelease(_preReleaseToggle.IsOn);
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _context.ShowInfo("About", exception.Message, InfoBarSeverity.Error);
        }
    }
}
