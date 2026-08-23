using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shadowsocks.Controller.Service;
using Shadowsocks.Controller.Traffic;
using Windows.ApplicationModel.DataTransfer;
using Shadowsocks.Model;
using Shadowsocks.WinUI.UI;

namespace Shadowsocks.WinUI.Pages;

public sealed class PacGeositePage : Page, IRefreshablePage
{
    private static readonly char[] SourceSeparators = ['\r', '\n', ';'];
    private readonly WinUIPageContext _context;
    private readonly ToggleSwitch _onlinePacToggle;
    private readonly TextBox _pacUrl;
    private readonly ToggleSwitch _securePacToggle;
    private readonly ToggleSwitch _regeneratePacToggle;
    private readonly Button _savePacUrlButton;
    private readonly StackPanel _onlinePacOptions;
    private readonly StackPanel _localPacOptions;
    private readonly Button _copyLocalPacUrlButton;
    private readonly TextBlock _effectiveRoutingMode;
    private readonly TextBlock _snapshotStatus;
    private readonly TextBlock _ruleStatus;
    private readonly TextBlock _decisionStatus;
    private readonly TextBlock _adminRoutingStatus;
    private readonly ListView _sources;
    private readonly Border _routingCard;
    private readonly Border _userRulesCard;
    private readonly Border _sourceCard;
    private readonly TextBox _newSources;
    private readonly Button _moveSourceUpButton;
    private readonly Button _moveSourceDownButton;
    private readonly Button _removeSourceButton;
    private readonly Button _addSourceButton;
    private readonly Button _saveSourcesButton;
    private readonly Button _refreshGeoSiteButton;
    private readonly Button _restoreDefaultButton;
    private readonly ProgressRing _geoSiteProgress;
    private readonly TextBlock _sourcePendingNote;
    private readonly List<string> _sourceItems = new();
    private bool _refreshing;
    private bool _sourcesDirty;
    private bool _geoSiteRefreshRunning;
    private string _savedPacUrl = string.Empty;

    internal PacGeositePage(WinUIPageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        Content = WinUIStyles.CreatePage(
            "PAC / GeoSite",
            "Choose Local GeoSite PAC or an Online PAC and manage the ordered GeoSite source list.",
            out StackPanel panel);

        var pacCard = new StackPanel { Spacing = 12 };
        pacCard.Children.Add(WinUIStyles.CreateSectionTitle("PAC mode"));
        _onlinePacToggle = new ToggleSwitch
        {
            Header = "Online PAC",
            OnContent = "Online",
            OffContent = "Local GeoSite",
        };
        _onlinePacToggle.Toggled += OnOnlinePacToggled;
        _context.SetToolTip(_onlinePacToggle, "Switch between an online PAC URL and the locally generated GeoSite PAC.");
        pacCard.Children.Add(_onlinePacToggle);

        _onlinePacOptions = new StackPanel { Spacing = 10 };
        _pacUrl = new TextBox
        {
            Header = "Online PAC URL",
            PlaceholderText = "https://example.invalid/proxy.pac",
        };
        _context.SetToolTip(_pacUrl, "URL downloaded and served when Online PAC mode is enabled.");
        _pacUrl.TextChanged += (_, _) => UpdatePacActionState();
        _onlinePacOptions.Children.Add(_pacUrl);
        _savePacUrlButton = new Button { Content = "Save PAC URL", HorizontalAlignment = HorizontalAlignment.Left };
        _savePacUrlButton.Click += OnSavePacUrlClicked;
        _context.SetToolTip(_savePacUrlButton, "Save the online PAC URL used when Online PAC mode is enabled.");
        _onlinePacOptions.Children.Add(_savePacUrlButton);
        pacCard.Children.Add(_onlinePacOptions);

        _localPacOptions = new StackPanel { Spacing = 10 };
        _securePacToggle = new ToggleSwitch
        {
            Header = "Require secret for local PAC URL",
            OnContent = "Secure",
            OffContent = "Open",
        };
        _securePacToggle.Toggled += OnSecurePacToggled;
        _context.SetToolTip(_securePacToggle, "Require a random secret in the local PAC URL before the PAC file is served.");
        _localPacOptions.Children.Add(_securePacToggle);

        _regeneratePacToggle = new ToggleSwitch
        {
            Header = "Regenerate Local PAC after application updates",
            OnContent = "On",
            OffContent = "Off",
        };
        _regeneratePacToggle.Toggled += OnRegeneratePacToggled;
        _context.SetToolTip(_regeneratePacToggle, "Rebuild the local PAC automatically after application or GeoSite data updates.");
        _localPacOptions.Children.Add(_regeneratePacToggle);

        _copyLocalPacUrlButton = new Button { Content = "Copy Local PAC URL", HorizontalAlignment = HorizontalAlignment.Left };
        _copyLocalPacUrlButton.Click += OnCopyLocalPacUrlClicked;
        _context.SetToolTip(_copyLocalPacUrlButton, "Copy the local PAC endpoint URL to the clipboard.");
        _localPacOptions.Children.Add(_copyLocalPacUrlButton);
        pacCard.Children.Add(_localPacOptions);
        panel.Children.Add(WinUIStyles.CreateCard(pacCard));

        var routingCard = new StackPanel { Spacing = 10 };
        routingCard.Children.Add(WinUIStyles.CreateSectionTitle("Local managed routing"));
        routingCard.Children.Add(WinUIStyles.CreateText(
            "Local GeoSite and ABP/EasyList network rules are always evaluated by the managed C# FilterEngine. The local PAC contains only a minimal proxy funnel and never evaluates filtering rules."));

        _effectiveRoutingMode = WinUIStyles.CreateText(string.Empty);
        _snapshotStatus = WinUIStyles.CreateText(string.Empty, "CaptionTextBlockStyle");
        _ruleStatus = WinUIStyles.CreateText(string.Empty, "CaptionTextBlockStyle");
        _decisionStatus = WinUIStyles.CreateText(string.Empty, "CaptionTextBlockStyle");
        _adminRoutingStatus = WinUIStyles.CreateText(string.Empty, "CaptionTextBlockStyle");
        routingCard.Children.Add(_effectiveRoutingMode);
        routingCard.Children.Add(_snapshotStatus);
        routingCard.Children.Add(_ruleStatus);
        routingCard.Children.Add(_decisionStatus);
        routingCard.Children.Add(_adminRoutingStatus);

        var refreshRoutingStatus = new Button
        {
            Content = "Refresh routing diagnostics",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        refreshRoutingStatus.Click += (_, _) => Refresh();
        _context.SetToolTip(refreshRoutingStatus, "Refresh the local managed-routing generation, rule counts and decision counters shown above.");
        routingCard.Children.Add(refreshRoutingStatus);
        _routingCard = WinUIStyles.CreateCard(routingCard);
        panel.Children.Add(_routingCard);

        var userRulesStack = new StackPanel { Spacing = 10 };
        userRulesStack.Children.Add(WinUIStyles.CreateSectionTitle("Local user rules"));
        userRulesStack.Children.Add(WinUIStyles.CreateText(
            "Edit user-rule.txt to add your own ABP/EasyList-style network routing rules. User rules are evaluated before GeoSite defaults; normal rules use the proxy and @@ exception rules go direct. Changes are watched and applied automatically."));
        var openUserRules = new Button
        {
            Content = "Open user-rule.txt",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        openUserRules.Click += OnOpenUserRulesClicked;
        _context.SetToolTip(openUserRules, "Create or open the active user-rule.txt file in your default text editor.");
        userRulesStack.Children.Add(openUserRules);
        _userRulesCard = WinUIStyles.CreateCard(userRulesStack);
        panel.Children.Add(_userRulesCard);

        var sourceCard = new StackPanel { Spacing = 12 };
        sourceCard.Children.Add(WinUIStyles.CreateSectionTitle("GeoSite sources"));
        sourceCard.Children.Add(WinUIStyles.CreateText(
            "Sources are tried independently and merged. A missing .sha256sum sidecar is non-fatal."));

        _sources = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MinHeight = 150,
            MaxHeight = 260,
        };
        _sources.SelectionChanged += (_, _) => UpdateSourceActionState();
        _context.SetToolTip(_sources, "Select a GeoSite source before moving or removing it.");
        sourceCard.Children.Add(_sources);

        var reorderActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _moveSourceUpButton = new Button { Content = "Move up" };
        _context.SetToolTip(_moveSourceUpButton, "Move the selected GeoSite source one position up.");
        _moveSourceUpButton.Click += (_, _) => MoveSelected(-1);
        reorderActions.Children.Add(_moveSourceUpButton);
        _moveSourceDownButton = new Button { Content = "Move down" };
        _context.SetToolTip(_moveSourceDownButton, "Move the selected GeoSite source one position down.");
        _moveSourceDownButton.Click += (_, _) => MoveSelected(1);
        reorderActions.Children.Add(_moveSourceDownButton);
        _removeSourceButton = new Button { Content = "Remove" };
        _context.SetToolTip(_removeSourceButton, "Remove the selected GeoSite source from the list.");
        _removeSourceButton.Click += OnRemoveSelected;
        reorderActions.Children.Add(_removeSourceButton);
        _restoreDefaultButton = new Button { Content = "Restore default" };
        _context.SetToolTip(_restoreDefaultButton, "Restore the built-in default GeoSite source list.");
        _restoreDefaultButton.Click += OnRestoreDefault;
        reorderActions.Children.Add(_restoreDefaultButton);
        sourceCard.Children.Add(reorderActions);

        _newSources = new TextBox
        {
            Header = "Add source URL(s)",
            PlaceholderText = "One URL per line",
            AcceptsReturn = true,
            MinHeight = 72,
            TextWrapping = TextWrapping.Wrap,
        };
        _context.SetToolTip(_newSources, "Enter one or more GeoSite source URLs, one per line.");
        _newSources.TextChanged += (_, _) => UpdateSourceActionState();
        sourceCard.Children.Add(_newSources);

        var sourceActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _addSourceButton = new Button { Content = "Add" };
        _context.SetToolTip(_addSourceButton, "Append the entered URLs to the GeoSite source list.");
        _addSourceButton.Click += OnAddSources;
        sourceActions.Children.Add(_addSourceButton);
        _saveSourcesButton = new Button { Content = "Save sources", IsEnabled = false };
        _context.SetToolTip(_saveSourcesButton, "Save the current GeoSite source order and URLs.");
        _saveSourcesButton.Click += OnSaveSources;
        sourceActions.Children.Add(_saveSourcesButton);
        _refreshGeoSiteButton = new Button { Content = "Download / refresh GeoSite" };
        _context.SetToolTip(_refreshGeoSiteButton, "Download the configured GeoSite data now and rebuild dependent local PAC data.");
        _refreshGeoSiteButton.Click += OnRefreshClicked;
        sourceActions.Children.Add(_refreshGeoSiteButton);
        _geoSiteProgress = new ProgressRing
        {
            Width = 20,
            Height = 20,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        sourceActions.Children.Add(_geoSiteProgress);
        sourceCard.Children.Add(sourceActions);
        _sourcePendingNote = WinUIStyles.CreateText(
            "Save GeoSite source changes before downloading or refreshing data.",
            "CaptionTextBlockStyle");
        _sourcePendingNote.Opacity = 0.72;
        _sourcePendingNote.Visibility = Visibility.Collapsed;
        sourceCard.Children.Add(_sourcePendingNote);
        _sourceCard = WinUIStyles.CreateCard(sourceCard);
        panel.Children.Add(_sourceCard);

        WinUILocalization.Apply(Content, _context.Localization);
    }

    public void Refresh()
    {
        Configuration? configuration = _context.Controller?.GetCurrentConfiguration();
        if (configuration is null)
        {
            return;
        }

        _refreshing = true;
        try
        {
            _onlinePacToggle.IsOn = configuration.useOnlinePac;
            _savedPacUrl = configuration.pacUrl?.Trim() ?? string.Empty;
            _pacUrl.Text = _savedPacUrl;
            _securePacToggle.IsOn = configuration.secureLocalPac;
            _securePacToggle.IsEnabled = true;
            _regeneratePacToggle.IsOn = configuration.regeneratePacOnUpdate;
            ApplyPacModeState(configuration.useOnlinePac);
            if (!configuration.useOnlinePac)
            {
                RefreshRoutingDiagnostics();
            }

            _sourceItems.Clear();
            _sourceItems.AddRange(Configuration.NormalizeGeositeSourceList(configuration.geositeUrls));
            _sourcesDirty = false;
            RebuildSourceList();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshRoutingDiagnostics()
    {
        if (_context.Controller is null)
        {
            return;
        }

        ManagedRoutingStatus status = _context.Controller.GetManagedRoutingStatus();
        _effectiveRoutingMode.Text = _context.LF("Effective routing mode: {0}", _context.L(status.EffectiveMode));
        _snapshotStatus.Text = _context.LF(
            "Snapshot: {0}, generation {1}, source: {2}",
            _context.L(status.SnapshotMode),
            status.Generation,
            _context.L(status.Source));
        _ruleStatus.Text = _context.LF(
            "Rules: {0} default + {1} user; invalid: {2}",
            status.DefaultRuleCount,
            status.UserRuleCount,
            status.InvalidRuleCount);
        _decisionStatus.Text = _context.LF(
            "Managed decisions in this snapshot: {0} direct / {1} proxy",
            status.DirectDecisionCount,
            status.ProxyDecisionCount);
        _adminRoutingStatus.Text = status.AdminManagedRoutingActive
            ? _context.LF("Administrator relay: managed routing active ({0} rules)", status.AdminManagedRoutingRuleCount)
            : _context.L("Administrator relay: managed routing inactive");
    }

    private void OnOpenUserRulesClicked(object _, RoutedEventArgs _1)
    {
        if (_context.Controller is null || !IsLocalPacMode())
        {
            return;
        }

        _context.Controller.TouchUserRuleFile();
    }

    private void OnCopyLocalPacUrlClicked(object _, RoutedEventArgs _1)
    {
        if (_context.Controller is null)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(_context.Controller.GetPacUrl());
        Clipboard.SetContent(package);
        Clipboard.Flush();
        _context.ShowInfo("PAC", "Local PAC URL copied", InfoBarSeverity.Success);
    }

    private void RebuildSourceList(int selectedIndex = -1)
    {
        _sources.Items.Clear();
        foreach (string source in _sourceItems)
        {
            _sources.Items.Add(source);
        }

        if (_sourceItems.Count > 0)
        {
            _sources.SelectedIndex = selectedIndex >= 0
                ? Math.Min(selectedIndex, _sourceItems.Count - 1)
                : Math.Min(_sources.SelectedIndex, _sourceItems.Count - 1);
        }
        else
        {
            _sources.SelectedIndex = -1;
        }
        UpdateSourceActionState();
    }

    private async void OnOnlinePacToggled(object _, RoutedEventArgs _1)
    {
        if (_refreshing || _context.Controller is null)
        {
            return;
        }

        if (!_onlinePacToggle.IsOn)
        {
            _context.Controller.UseOnlinePAC(false);
            ApplyPacModeState(useOnlinePac: false);
            return;
        }

        Configuration configuration = _context.Controller.GetCurrentConfiguration();
        string pacUrl = configuration.pacUrl?.Trim() ?? string.Empty;

        if (!IsValidPacUrl(pacUrl))
        {
            _onlinePacToggle.IsEnabled = false;
            try
            {
                string? enteredUrl = await PromptForOnlinePacUrlAsync();
                if (string.IsNullOrWhiteSpace(enteredUrl))
                {
                    SetOnlinePacToggleWithoutEvent(false);
                    ApplyPacModeState(useOnlinePac: false);
                    return;
                }

                pacUrl = enteredUrl;
                _context.Controller.SavePACUrl(pacUrl);
                _savedPacUrl = pacUrl;
                _pacUrl.Text = pacUrl;
            }
            finally
            {
                _onlinePacToggle.IsEnabled = true;
            }
        }

        _context.Controller.UseOnlinePAC(true);
        ApplyPacModeState(useOnlinePac: true);
    }

    private void OnSavePacUrlClicked(object _, RoutedEventArgs _1)
    {
        if (_context.Controller is null)
        {
            return;
        }

        string url = _pacUrl.Text?.Trim() ?? string.Empty;
        if (!IsValidPacUrl(url))
        {
            _context.ShowInfo("PAC", "Enter an absolute HTTP or HTTPS PAC URL.", InfoBarSeverity.Error);
            return;
        }

        _context.Controller.SavePACUrl(url);
        _savedPacUrl = url;
        UpdatePacActionState();
        _context.ShowInfo("PAC", "Online PAC URL saved.", InfoBarSeverity.Success);
    }

    private void OnSecurePacToggled(object _, RoutedEventArgs _1)
    {
        if (!_refreshing && _context.Controller is not null)
        {
            _context.Controller.ToggleSecureLocalPac(_securePacToggle.IsOn);
        }
    }

    private void OnRegeneratePacToggled(object _, RoutedEventArgs _1)
    {
        if (!_refreshing && _context.Controller is not null)
        {
            _context.Controller.ToggleRegeneratePacOnUpdate(_regeneratePacToggle.IsOn);
        }
    }

    private void OnAddSources(object _, RoutedEventArgs _1)
    {
        if (!IsLocalPacMode())
        {
            return;
        }

        List<string> parsed = ParseSources(_newSources.Text);
        if (parsed.Count == 0 || parsed.Any(source => !IsValidSource(source)))
        {
            _context.ShowInfo("GeoSite", "Every source must be an absolute HTTP or HTTPS URL.", InfoBarSeverity.Error);
            return;
        }

        bool changed = false;
        foreach (string source in parsed)
        {
            if (!_sourceItems.Any(existing => string.Equals(existing, source, StringComparison.OrdinalIgnoreCase)))
            {
                _sourceItems.Add(source);
                changed = true;
            }
        }

        if (changed)
        {
            SetSourcesDirty(true);
        }
        _newSources.Text = string.Empty;
        RebuildSourceList(_sourceItems.Count - 1);
    }

    private void OnRemoveSelected(object _, RoutedEventArgs _1)
    {
        if (!IsLocalPacMode())
        {
            return;
        }

        int index = _sources.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        if (_sourceItems.Count <= 1)
        {
            _context.ShowInfo("GeoSite", "At least one GeoSite source is required.", InfoBarSeverity.Warning);
            return;
        }

        _sourceItems.RemoveAt(index);
        SetSourcesDirty(true);
        RebuildSourceList(Math.Min(index, _sourceItems.Count - 1));
    }

    private void MoveSelected(int delta)
    {
        if (!IsLocalPacMode())
        {
            return;
        }

        int index = _sources.SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _sourceItems.Count)
        {
            return;
        }

        string value = _sourceItems[index];
        _sourceItems.RemoveAt(index);
        _sourceItems.Insert(target, value);
        SetSourcesDirty(true);
        RebuildSourceList(target);
    }

    private void OnRestoreDefault(object _, RoutedEventArgs _1)
    {
        if (!IsLocalPacMode())
        {
            return;
        }

        _sourceItems.Clear();
        _sourceItems.Add(GeositeUpdater.DefaultSourceUrl);
        SetSourcesDirty(true);
        RebuildSourceList(0);
    }

    private void OnSaveSources(object _, RoutedEventArgs _1)
    {
        if (_context.Controller is null || !IsLocalPacMode())
        {
            return;
        }

        _context.Controller.SaveGeositeSources(_sourceItems.ToList());
        SetSourcesDirty(false);
        _context.ShowInfo("GeoSite", "GeoSite source list saved.", InfoBarSeverity.Success);
    }

    private async void OnRefreshClicked(object _, RoutedEventArgs _1)
    {
        if (_context.Controller is null || !IsLocalPacMode() || _geoSiteRefreshRunning)
        {
            return;
        }

        SetGeoSiteRefreshBusy(true);
        try
        {
            bool updated = await _context.Controller.UpdatePACFromGeositeAsync();
            _context.ShowInfo(
                "GeoSite",
                updated ? "GeoSite data refreshed and Local PAC was regenerated." : "GeoSite refresh did not complete successfully.",
                updated ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            _context.ShowInfo("GeoSite", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetGeoSiteRefreshBusy(false);
        }
    }

    private void ApplyPacModeState(bool useOnlinePac)
    {
        // Do not leave scenario-specific PAC actions as disabled visual noise.
        // Online PAC hides Local managed-routing diagnostics, user-rule.txt editing
        // and GeoSite sources; Local PAC restores those local routing controls.
        Visibility onlineVisibility = useOnlinePac ? Visibility.Visible : Visibility.Collapsed;
        Visibility localVisibility = useOnlinePac ? Visibility.Collapsed : Visibility.Visible;

        _onlinePacOptions.Visibility = onlineVisibility;
        _localPacOptions.Visibility = localVisibility;
        _routingCard.Visibility = localVisibility;
        _userRulesCard.Visibility = localVisibility;
        _sourceCard.Visibility = localVisibility;

        UpdatePacActionState();
        UpdateSourceActionState();
    }

    private void UpdatePacActionState()
    {
        string candidate = _pacUrl.Text?.Trim() ?? string.Empty;
        _savePacUrlButton.IsEnabled = IsValidPacUrl(candidate)
            && !string.Equals(candidate, _savedPacUrl, StringComparison.Ordinal);
    }

    private void UpdateSourceActionState()
    {
        bool idle = !_geoSiteRefreshRunning;
        int index = _sources.SelectedIndex;
        _sources.IsEnabled = idle;
        _newSources.IsEnabled = idle;
        _moveSourceUpButton.IsEnabled = idle && index > 0;
        _moveSourceDownButton.IsEnabled = idle && index >= 0 && index < _sourceItems.Count - 1;
        _removeSourceButton.IsEnabled = idle && index >= 0 && _sourceItems.Count > 1;
        _restoreDefaultButton.IsEnabled = idle && (_sourceItems.Count != 1
            || !string.Equals(_sourceItems[0], GeositeUpdater.DefaultSourceUrl, StringComparison.OrdinalIgnoreCase));

        List<string> pending = ParseSources(_newSources.Text);
        _addSourceButton.IsEnabled = idle && pending.Count > 0 && pending.All(IsValidSource);
        _saveSourcesButton.IsEnabled = idle && _sourcesDirty;
        _refreshGeoSiteButton.IsEnabled = idle && !_sourcesDirty;
        _sourcePendingNote.Visibility = _sourcesDirty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetGeoSiteRefreshBusy(bool busy)
    {
        _geoSiteRefreshRunning = busy;
        _geoSiteProgress.IsActive = busy;
        _geoSiteProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateSourceActionState();
    }

    private void SetSourcesDirty(bool dirty)
    {
        _sourcesDirty = dirty;
        UpdateSourceActionState();
    }

    private void SetOnlinePacToggleWithoutEvent(bool isOn)
    {
        _refreshing = true;
        try
        {
            _onlinePacToggle.IsOn = isOn;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private bool IsLocalPacMode()
        => _context.Controller is not null
           && !_context.Controller.GetCurrentConfiguration().useOnlinePac;

    private async Task<string?> PromptForOnlinePacUrlAsync()
    {
        XamlRoot? xamlRoot = _context.GetXamlRoot();
        if (xamlRoot is null)
        {
            _context.ShowInfo("PAC", "Please input PAC URL before enabling Online PAC.", InfoBarSeverity.Warning);
            return null;
        }

        while (true)
        {
            var input = new TextBox
            {
                Text = _pacUrl.Text?.Trim() ?? string.Empty,
                MinWidth = 480,
                PlaceholderText = "https://example.com/proxy.pac",
            };
            input.SelectionStart = input.Text.Length;
            _context.SetToolTip(input, "Enter an absolute HTTP or HTTPS PAC URL.");

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = _context.L("Edit Online PAC URL"),
                Content = input,
                PrimaryButtonText = _context.L("OK"),
                CloseButtonText = _context.L("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return null;
            }

            string value = input.Text.Trim();
            if (IsValidPacUrl(value))
            {
                return value;
            }

            _context.ShowInfo("PAC", "Enter an absolute HTTP or HTTPS PAC URL.", InfoBarSeverity.Error);
        }
    }

    private static List<string> ParseSources(string? value)
        => (value ?? string.Empty)
            .Split(SourceSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(source => source.Trim())
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsValidPacUrl(string? value)
        => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static bool IsValidSource(string source)
        => Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
