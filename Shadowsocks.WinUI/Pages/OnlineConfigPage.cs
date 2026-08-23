using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shadowsocks.Model;
using Shadowsocks.WinUI.UI;
using Windows.ApplicationModel.DataTransfer;

namespace Shadowsocks.WinUI.Pages;

public sealed class OnlineConfigPage : Page, IRefreshablePage
{
    private readonly WinUIPageContext _context;
    private readonly ListView _sources;
    private readonly TextBox _url;
    private readonly Button _update;
    private readonly Button _updateAll;
    private readonly Button _copy;
    private readonly Button _remove;
    private readonly Button _add;
    private readonly ProgressRing _progress;
    private readonly List<string> _items = new();
    private bool _busy;

    internal OnlineConfigPage(WinUIPageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        Content = WinUIStyles.CreatePage(
            "Online Config",
            "Manage SIP008 / online configuration sources.",
            out StackPanel panel);

        var card = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _sources = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MinHeight = 300,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _sources.SelectionChanged += (_, _) => UpdateButtons();
        _context.SetToolTip(_sources, "Select an online configuration source to update, copy, or remove it.");
        card.Children.Add(_sources);

        var sourceActions = new StackPanel { Spacing = 8, MinWidth = 120 };
        _update = new Button { Content = "Update", HorizontalAlignment = HorizontalAlignment.Stretch };
        _context.SetToolTip(_update, "Download and apply the selected online configuration source now.");
        _update.Click += OnUpdateClicked;
        sourceActions.Children.Add(_update);
        _updateAll = new Button { Content = "Update all", HorizontalAlignment = HorizontalAlignment.Stretch };
        _context.SetToolTip(_updateAll, "Download and apply every configured online source.");
        _updateAll.Click += OnUpdateAllClicked;
        sourceActions.Children.Add(_updateAll);
        _copy = new Button { Content = "Copy link", HorizontalAlignment = HorizontalAlignment.Stretch };
        _context.SetToolTip(_copy, "Copy the selected online configuration URL to the clipboard.");
        _copy.Click += OnCopyClicked;
        sourceActions.Children.Add(_copy);
        _remove = new Button { Content = "Remove", HorizontalAlignment = HorizontalAlignment.Stretch };
        _context.SetToolTip(_remove, "Remove the selected online configuration source.");
        _remove.Click += OnRemoveClicked;
        sourceActions.Children.Add(_remove);
        _progress = new ProgressRing
        {
            Width = 20,
            Height = 20,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        sourceActions.Children.Add(_progress);
        Grid.SetColumn(sourceActions, 1);
        card.Children.Add(sourceActions);

        var addGrid = new Grid { ColumnSpacing = 8 };
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _url = new TextBox { PlaceholderText = "https://example.com/subscription.json" };
        _context.SetToolTip(_url, "Enter an absolute HTTP or HTTPS SIP008 / online configuration URL.");
        _url.TextChanged += (_, _) => UpdateButtons();
        addGrid.Children.Add(_url);
        _add = new Button { Content = "Add", MinWidth = 96 };
        _context.SetToolTip(_add, "Add the entered URL to the online configuration source list.");
        _add.Click += OnAddClicked;
        Grid.SetColumn(_add, 1);
        addGrid.Children.Add(_add);
        Grid.SetRow(addGrid, 1);
        Grid.SetColumnSpan(addGrid, 2);
        card.Children.Add(addGrid);

        panel.Children.Add(WinUIStyles.CreateCard(card));

        WinUILocalization.Apply(Content, _context.Localization);
    }

    public void Refresh()
    {
        Configuration? config = _context.Controller?.GetCurrentConfiguration();
        if (config is null)
        {
            return;
        }

        string? previous = _sources.SelectedItem?.ToString();
        _items.Clear();
        _items.AddRange(config.onlineConfigSource ?? []);
        Rebuild(previous);
    }

    private void Rebuild(string? select = null)
    {
        _sources.Items.Clear();
        foreach (string item in _items)
        {
            _sources.Items.Add(item);
        }
        if (!string.IsNullOrWhiteSpace(select))
        {
            _sources.SelectedItem = _sources.Items.Cast<object>()
                .FirstOrDefault(item => string.Equals(item?.ToString(), select, StringComparison.Ordinal));
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool idle = !_busy;
        bool hasSelection = _sources.SelectedIndex >= 0;
        _sources.IsEnabled = idle;
        _url.IsEnabled = idle;
        _update.IsEnabled = idle && hasSelection;
        _copy.IsEnabled = idle && hasSelection;
        _remove.IsEnabled = idle && hasSelection;
        _updateAll.IsEnabled = idle && _items.Count > 0;
        string candidate = _url.Text?.Trim() ?? string.Empty;
        _add.IsEnabled = idle
            && IsValidUrl(candidate)
            && !_items.Contains(candidate, StringComparer.OrdinalIgnoreCase);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _progress.IsActive = busy;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private async void OnUpdateClicked(object _, RoutedEventArgs _1)
    {
        if (_context.Controller is null || _sources.SelectedItem is not string url)
        {
            return;
        }

        SetBusy(true);
        try
        {
            bool success = await _context.Controller.UpdateOnlineConfig(url);
            _context.ShowInfo("Online Config", success ? "Online configuration updated." : "Online configuration update failed.",
                success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (Exception exception)
        {
            _context.ShowInfo("Online Config", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnUpdateAllClicked(object _, RoutedEventArgs _1)
    {
        if (_context.Controller is null)
        {
            return;
        }

        SetBusy(true);
        try
        {
            List<string> failed = await _context.Controller.UpdateAllOnlineConfig();
            _context.ShowInfo(
                "Online Config",
                failed.Count == 0 ? _context.L("All online configurations updated.") : _context.LF("Failed sources: {0}", string.Join(", ", failed)),
                failed.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            _context.ShowInfo("Online Config", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnCopyClicked(object _, RoutedEventArgs _1)
    {
        if (_sources.SelectedItem is not string url)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(url);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        _context.ShowInfo("Online Config", "Source URL copied.", InfoBarSeverity.Success);
    }

    private void OnRemoveClicked(object _, RoutedEventArgs _1)
    {
        if (_context.Controller is null || _sources.SelectedItem is not string url)
        {
            return;
        }

        _items.RemoveAll(item => string.Equals(item, url, StringComparison.Ordinal));
        _context.Controller.RemoveOnlineConfig(url);
        Rebuild();
    }

    private void OnAddClicked(object _, RoutedEventArgs _1)
    {
        if (_context.Controller is null)
        {
            return;
        }

        string url = _url.Text?.Trim() ?? string.Empty;
        if (!IsValidUrl(url))
        {
            _context.ShowInfo("Online Config", "Enter an absolute HTTP or HTTPS URL.", InfoBarSeverity.Error);
            return;
        }

        if (!_items.Contains(url, StringComparer.OrdinalIgnoreCase))
        {
            _items.Add(url);
            _context.Controller.SaveOnlineConfigSource(_items.ToList());
        }
        _url.Text = string.Empty;
        Rebuild(url);
    }

    private static bool IsValidUrl(string? value)
        => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
