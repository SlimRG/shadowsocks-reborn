using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shadowsocks.Controller.Service;
using Windows.ApplicationModel.DataTransfer;
using Shadowsocks.Model;
using Shadowsocks.WinUI.UI;

namespace Shadowsocks.WinUI.Pages;

public sealed class PacGeositePage : Page, IRefreshablePage
{
    private readonly WinUIPageContext _context;
    private readonly ToggleSwitch _onlinePacToggle;
    private readonly TextBox _pacUrl;
    private readonly ToggleSwitch _securePacToggle;
    private readonly ToggleSwitch _regeneratePacToggle;
    private readonly Button _savePacUrlButton;
    private readonly ListView _sources;
    private readonly Border _sourceCard;
    private readonly TextBox _newSources;
    private readonly List<Control> _localPacControls = new();
    private readonly List<string> _sourceItems = new();
    private bool _refreshing;

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

        _pacUrl = new TextBox
        {
            Header = "Online PAC URL",
            PlaceholderText = "https://example.invalid/proxy.pac",
        };
        _context.SetToolTip(_pacUrl, "URL downloaded and served when Online PAC mode is enabled.");
        pacCard.Children.Add(_pacUrl);
        _savePacUrlButton = new Button { Content = "Save PAC URL", HorizontalAlignment = HorizontalAlignment.Left };
        _savePacUrlButton.Click += OnSavePacUrlClicked;
        _context.SetToolTip(_savePacUrlButton, "Save the online PAC URL and refresh the online PAC cache.");
        pacCard.Children.Add(_savePacUrlButton);

        _securePacToggle = new ToggleSwitch
        {
            Header = "Require secret for local PAC URL",
            OnContent = "Secure",
            OffContent = "Open",
        };
        _securePacToggle.Toggled += OnSecurePacToggled;
        _context.SetToolTip(_securePacToggle, "Require a random secret in the local PAC URL before the PAC file is served.");
        pacCard.Children.Add(_securePacToggle);

        _regeneratePacToggle = new ToggleSwitch
        {
            Header = "Regenerate Local PAC after application updates",
            OnContent = "On",
            OffContent = "Off",
        };
        _regeneratePacToggle.Toggled += OnRegeneratePacToggled;
        _context.SetToolTip(_regeneratePacToggle, "Rebuild the local PAC automatically after application or GeoSite data updates.");
        pacCard.Children.Add(_regeneratePacToggle);

        var copyPacUrl = new Button { Content = "Copy Local PAC URL", HorizontalAlignment = HorizontalAlignment.Left };
        copyPacUrl.Click += OnCopyLocalPacUrlClicked;
        _context.SetToolTip(copyPacUrl, "Copy the local PAC endpoint URL to the clipboard.");
        pacCard.Children.Add(copyPacUrl);
        panel.Children.Add(WinUIStyles.CreateCard(pacCard));

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
        sourceCard.Children.Add(_sources);

        var reorderActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var moveUp = new Button { Content = "Move up" };
        _context.SetToolTip(moveUp, "Move the selected GeoSite source one position up.");
        moveUp.Click += (_, _) => MoveSelected(-1);
        reorderActions.Children.Add(moveUp);
        var moveDown = new Button { Content = "Move down" };
        _context.SetToolTip(moveDown, "Move the selected GeoSite source one position down.");
        moveDown.Click += (_, _) => MoveSelected(1);
        reorderActions.Children.Add(moveDown);
        var remove = new Button { Content = "Remove" };
        _context.SetToolTip(remove, "Remove the selected GeoSite source from the list.");
        remove.Click += OnRemoveSelected;
        reorderActions.Children.Add(remove);
        var restore = new Button { Content = "Restore default" };
        _context.SetToolTip(restore, "Restore the built-in default GeoSite source list.");
        restore.Click += OnRestoreDefault;
        reorderActions.Children.Add(restore);
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
        sourceCard.Children.Add(_newSources);

        var sourceActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var add = new Button { Content = "Add" };
        _context.SetToolTip(add, "Append the entered URLs to the GeoSite source list.");
        add.Click += OnAddSources;
        sourceActions.Children.Add(add);
        var saveSources = new Button { Content = "Save sources" };
        _context.SetToolTip(saveSources, "Save the current GeoSite source order and URLs.");
        saveSources.Click += OnSaveSources;
        sourceActions.Children.Add(saveSources);
        var refresh = new Button { Content = "Download / refresh GeoSite" };
        _context.SetToolTip(refresh, "Download the configured GeoSite data now and rebuild dependent local PAC data.");
        refresh.Click += OnRefreshClicked;
        sourceActions.Children.Add(refresh);
        sourceCard.Children.Add(sourceActions);
        _sourceCard = WinUIStyles.CreateCard(sourceCard);
        panel.Children.Add(_sourceCard);

        _localPacControls.Add(_sources);
        _localPacControls.Add(moveUp);
        _localPacControls.Add(moveDown);
        _localPacControls.Add(remove);
        _localPacControls.Add(restore);
        _localPacControls.Add(_newSources);
        _localPacControls.Add(add);
        _localPacControls.Add(saveSources);
        _localPacControls.Add(refresh);

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
            _pacUrl.Text = configuration.pacUrl ?? string.Empty;
            _securePacToggle.IsOn = configuration.secureLocalPac;
            _securePacToggle.IsEnabled = true;
            _regeneratePacToggle.IsOn = configuration.regeneratePacOnUpdate;
            ApplyPacModeState(configuration.useOnlinePac);

            _sourceItems.Clear();
            _sourceItems.AddRange(Configuration.NormalizeGeositeSourceList(configuration.geositeUrls));
            RebuildSourceList();
        }
        finally
        {
            _refreshing = false;
        }
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

        if (string.IsNullOrWhiteSpace(pacUrl))
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
        if (string.IsNullOrWhiteSpace(url))
        {
            _context.ShowInfo("PAC", "Please input PAC URL.", InfoBarSeverity.Error);
            return;
        }

        _context.Controller.SavePACUrl(url);
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

        foreach (string source in parsed)
        {
            if (!_sourceItems.Any(existing => string.Equals(existing, source, StringComparison.OrdinalIgnoreCase)))
            {
                _sourceItems.Add(source);
            }
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
        RebuildSourceList(0);
    }

    private void OnSaveSources(object _, RoutedEventArgs _1)
    {
        if (_context.Controller is null || !IsLocalPacMode())
        {
            return;
        }

        _context.Controller.SaveGeositeSources(_sourceItems.ToList());
        _context.ShowInfo("GeoSite", "GeoSite source list saved.", InfoBarSeverity.Success);
    }

    private async void OnRefreshClicked(object _, RoutedEventArgs _1)
    {
        if (_context.Controller is null || !IsLocalPacMode())
        {
            return;
        }

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
    }

    private void ApplyPacModeState(bool useOnlinePac)
    {
        // Do not leave unavailable PAC actions as disabled visual noise. The
        // active scenario owns the visible editor: Online PAC shows only its
        // URL controls; Local PAC shows the GeoSite editor. Independent local
        // PAC security/regeneration preferences remain visible in both modes.
        Visibility onlineVisibility = useOnlinePac ? Visibility.Visible : Visibility.Collapsed;
        Visibility localVisibility = useOnlinePac ? Visibility.Collapsed : Visibility.Visible;

        _pacUrl.Visibility = onlineVisibility;
        _savePacUrlButton.Visibility = onlineVisibility;
        _sourceCard.Visibility = localVisibility;

        foreach (Control control in _localPacControls)
        {
            control.IsEnabled = true;
        }

        _securePacToggle.IsEnabled = true;
        _regeneratePacToggle.IsEnabled = true;
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
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            _context.ShowInfo("PAC", "Please input PAC URL.", InfoBarSeverity.Error);
        }
    }

    private static List<string> ParseSources(string? value)
        => (value ?? string.Empty)
            .Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(source => source.Trim())
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsValidSource(string source)
        => Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
