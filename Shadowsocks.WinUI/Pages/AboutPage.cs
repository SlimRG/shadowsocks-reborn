using System;
using System.Diagnostics;
using System.Threading.Tasks;
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
    private readonly ProgressRing _updateProgress;
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
        var licenseButton = new Button { Content = "License" };
        _context.SetToolTip(licenseButton, "Show the Shadowsocks Reborn license bundled with this application.");
        licenseButton.Click += async (_, _) => await ShowLicenseAsync();
        links.Children.Add(licenseButton);

        var noticesButton = new Button { Content = "Third-party notices" };
        _context.SetToolTip(noticesButton, "Show third-party license and source notices bundled with this application.");
        noticesButton.Click += async (_, _) => await ShowThirdPartyNoticesAsync();
        links.Children.Add(noticesButton);
        identity.Children.Add(links);
        panel.Children.Add(WinUIStyles.CreateCard(identity));

        var updates = new StackPanel { Spacing = 12 };
        updates.Children.Add(WinUIStyles.CreateSectionTitle("Updates"));
        _updateStatus = WinUIStyles.CreateText("Not checked yet.");
        updates.Children.Add(_updateStatus);
        _updateAtStartupToggle = new ToggleSwitch
        {
            Header = "Automatically install updates",
            OnContent = "On",
            OffContent = "Off",
        };
        _context.SetToolTip(_updateAtStartupToggle, "Automatically download, verify and install a newer release after the application starts.");
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
        _downloadButton = new Button { Content = "Install update", Visibility = Visibility.Collapsed };
        _context.SetToolTip(_downloadButton, "Download, verify and install the selected update automatically.");
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
        _updateProgress = new ProgressRing
        {
            Width = 20,
            Height = 20,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _context.SetToolTip(_updateProgress, "Waiting for the update operation to finish…");
        actions.Children.Add(_updateProgress);
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
        _context.SetToolTip(_releaseNotes, "Release notes for the selected available update.");
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

    public async Task CheckForUpdatesAsync()
    {
        if (_updateChecker is null)
        {
            _context.ShowInfo("Updates", "Controller is unavailable.", InfoBarSeverity.Error);
            return;
        }

        _updateFound = false;
        SetUpdateBusy(true);
        HideUpdateDetails();
        _updateStatus.Text = _context.L("Checking GitHub releases…");
        try
        {
            await _updateChecker.CheckForVersionUpdate();
            if (!_updateFound)
            {
                _updateStatus.Text = string.IsNullOrWhiteSpace(_updateChecker.LastCheckError)
                    ? _context.LF("You are up to date · {0}", ApplicationInfo.Version)
                    : _context.LF("Update check failed: {0}", _updateChecker.LastCheckError);
            }
        }
        catch (Exception exception)
        {
            _updateStatus.Text = _context.LF("Update check failed: {0}", exception.Message);
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    private async void OnCheckNowClicked(object _, RoutedEventArgs _1)
        => await CheckForUpdatesAsync();

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
            if (!_updateFound && _updateChecker is not null)
            {
                _updateStatus.Text = string.IsNullOrWhiteSpace(_updateChecker.LastCheckError)
                    ? _context.LF("You are up to date · {0}", ApplicationInfo.Version)
                    : _context.LF("Update check failed: {0}", _updateChecker.LastCheckError);
            }
        });
    }

    private async void OnDownloadClicked(object _, RoutedEventArgs _1)
    {
        if (_updateChecker is null)
        {
            return;
        }

        SetUpdateBusy(true);
        try
        {
            _updateStatus.Text = _context.LF("Installing update {0}…", _updateChecker.NewReleaseVersion);
            if (await _updateChecker.DoUpdate())
            {
                _context.RequestApplicationExit();
            }
        }
        catch (Exception exception)
        {
            _context.ShowInfo("Updates", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetUpdateBusy(false);
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

    private void SetUpdateBusy(bool busy)
    {
        _updateProgress.IsActive = busy;
        _updateProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SetUpdateActionsEnabled(!busy);
    }

    private void SetUpdateActionsEnabled(bool enabled)
    {
        _checkButton.IsEnabled = enabled;
        _downloadButton.IsEnabled = enabled;
        _skipButton.IsEnabled = enabled;
        _notNowButton.IsEnabled = enabled;
        _preReleaseToggle.IsEnabled = enabled;
        _updateAtStartupToggle.IsEnabled = enabled;
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

    private Task ShowLicenseAsync()
        => ShowEmbeddedTextAsync(
            () => EmbeddedResources.LocalizedProductLicense(_context.Localization.Culture),
            "License",
            "License resource is unavailable.");

    private Task ShowThirdPartyNoticesAsync()
        => ShowEmbeddedTextAsync(
            () => EmbeddedResources.LocalizedThirdPartyNotices(_context.Localization.Culture),
            "Third-party notices",
            "Third-party notices resource is unavailable.");

    private async Task ShowEmbeddedTextAsync(Func<string> readText, string titleKey, string unavailableKey)
    {
        string text;
        try
        {
            text = readText();
        }
        catch (InvalidOperationException exception)
        {
            _context.ShowInfo(_context.L(titleKey), $"{_context.L(unavailableKey)} {exception.Message}", InfoBarSeverity.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _context.ShowInfo(_context.L(titleKey), _context.L(unavailableKey), InfoBarSeverity.Error);
            return;
        }
        var content = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            IsSpellCheckEnabled = false,
            MinWidth = 620,
            MinHeight = 320,
            MaxHeight = 520,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(content, ScrollBarVisibility.Auto);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _context.L(titleKey),
            Content = content,
            CloseButtonText = _context.L("Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
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
