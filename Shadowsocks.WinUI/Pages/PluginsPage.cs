using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shadowsocks.Controller.Service;
using Shadowsocks.WinUI.UI;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Shadowsocks.WinUI.Pages;

public sealed class PluginsPage : Page, IRefreshablePage
{
    private readonly WinUIPageContext _context;
    private readonly ComboBox _catalog;
    private readonly Button _installButton;
    private readonly Button _importButton;
    private readonly Button _checkUpdatesButton;
    private readonly ProgressRing _progress;
    private readonly StackPanel _installedList;
    private bool _busy;

    internal PluginsPage(WinUIPageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        Content = WinUIStyles.CreatePage(
            "Plugins",
            "Install SIP003 plugins from the built-in list or import a plugin ZIP or TAR.GZ package. Built-in catalog plugins can update automatically.",
            out StackPanel panel);

        var addStack = new StackPanel { Spacing = 12 };
        addStack.Children.Add(WinUIStyles.CreateSectionTitle("Add plugin"));

        var addRow = new Grid { ColumnSpacing = 8 };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _catalog = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Choose a plugin",
        };
        foreach (PluginCatalogEntry entry in PluginManager.Catalog)
        {
            _catalog.Items.Add(entry);
        }
        _catalog.DisplayMemberPath = nameof(PluginCatalogEntry.DisplayName);
        _catalog.SelectedIndex = PluginManager.Catalog.Count > 0 ? 0 : -1;
        _context.SetToolTip(_catalog, "Choose a supported Windows x64 SIP003 plugin to install.");
        _catalog.SelectionChanged += (_, _) => UpdateControlState();
        addRow.Children.Add(_catalog);

        _installButton = new Button { Content = "Install", MinWidth = 88 };
        _context.SetToolTip(_installButton, "Download the latest Windows x64 release archive and install the selected plugin.");
        _installButton.Click += async (_, _) => await InstallSelectedAsync();
        Grid.SetColumn(_installButton, 1);
        addRow.Children.Add(_installButton);

        _importButton = new Button { Content = "Import ZIP/TAR.GZ", MinWidth = 132 };
        _context.SetToolTip(_importButton, "Import a local ZIP or TAR.GZ package containing a Windows plugin executable.");
        _importButton.Click += async (_, _) => await ImportArchiveAsync();
        Grid.SetColumn(_importButton, 2);
        addRow.Children.Add(_importButton);

        _checkUpdatesButton = new Button { Content = "Check updates", MinWidth = 112 };
        _context.SetToolTip(_checkUpdatesButton, "Check installed catalog plugins for newer GitHub releases now.");
        _checkUpdatesButton.Click += async (_, _) => await CheckUpdatesAsync();
        Grid.SetColumn(_checkUpdatesButton, 3);
        addRow.Children.Add(_checkUpdatesButton);

        _progress = new ProgressRing
        {
            Width = 20,
            Height = 20,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_progress, 4);
        addRow.Children.Add(_progress);

        addStack.Children.Add(addRow);
        panel.Children.Add(WinUIStyles.CreateCard(addStack));

        var installedStack = new StackPanel { Spacing = 12 };
        installedStack.Children.Add(WinUIStyles.CreateSectionTitle("Installed plugins"));
        TextBlock maintenanceHint = WinUIStyles.CreateText(
            "Built-in catalog plugins are checked automatically once per day when they are not in use.",
            "CaptionTextBlockStyle");
        maintenanceHint.Opacity = 0.72;
        installedStack.Children.Add(maintenanceHint);
        _installedList = new StackPanel { Spacing = 8 };
        installedStack.Children.Add(_installedList);
        panel.Children.Add(WinUIStyles.CreateCard(installedStack));

        WinUILocalization.Apply(Content, _context.Localization);
        Refresh();
    }

    public void Refresh()
    {
        _installedList.Children.Clear();
        InstalledPlugin[] installed = PluginManager.GetInstalledPlugins().ToArray();
        if (installed.Length == 0)
        {
            TextBlock empty = WinUIStyles.CreateText(_context.L("No plugins installed."));
            empty.Opacity = 0.72;
            _installedList.Children.Add(empty);
            UpdateControlState();
            return;
        }

        foreach (InstalledPlugin plugin in installed)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(WinUIStyles.CreateText(plugin.DisplayName, "BodyStrongTextBlockStyle"));
            TextBlock source = WinUIStyles.CreateText(plugin.Source, "CaptionTextBlockStyle");
            source.Opacity = 0.72;
            text.Children.Add(source);
            if (!string.IsNullOrWhiteSpace(plugin.ReleaseTag))
            {
                TextBlock release = WinUIStyles.CreateText(_context.LF("Release: {0}", plugin.ReleaseTag), "CaptionTextBlockStyle");
                release.Opacity = 0.72;
                text.Children.Add(release);
            }
            row.Children.Add(text);

            if (plugin.CanAutoUpdate)
            {
                var automaticUpdates = new ToggleSwitch
                {
                    Header = _context.L("Automatic updates"),
                    IsOn = plugin.AutoUpdate,
                    Tag = plugin.Id,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                _context.SetToolTip(automaticUpdates, "Automatically check this catalog plugin for a newer release once per day.");
                automaticUpdates.Toggled += OnAutoUpdateToggled;
                Grid.SetColumn(automaticUpdates, 1);
                row.Children.Add(automaticUpdates);
            }

            var remove = new Button { Content = _context.L("Remove"), Tag = plugin.Id, VerticalAlignment = VerticalAlignment.Center };
            _context.SetToolTip(remove, "Remove this installed plugin from Shadowsocks storage.");
            remove.Click += OnRemoveClicked;
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            _installedList.Children.Add(row);
        }
        UpdateControlState();
    }

    private async Task InstallSelectedAsync()
    {
        if (_catalog.SelectedItem is not PluginCatalogEntry entry)
            return;

        SetBusy(true);
        try
        {
            InstalledPlugin installed = await PluginManager.InstallCatalogPluginAsync(entry);
            _context.ShowInfo(_context.L("Plugins"), _context.LF("{0} installed.", installed.DisplayName), InfoBarSeverity.Success);
            Refresh();
        }
        catch (Exception exception)
        {
            _context.ShowInfo(_context.L("Plugins"), exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ImportArchiveAsync()
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.Downloads,
        };
        picker.FileTypeFilter.Add(".zip");
        picker.FileTypeFilter.Add(".gz");
        picker.FileTypeFilter.Add(".tgz");

        nint windowHandle = _context.GetWindowHandle();
        if (windowHandle != nint.Zero)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        }

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        SetBusy(true);
        try
        {
            InstalledPlugin installed = await Task.Run(() => PluginManager.InstallManualArchive(file.Path));
            _context.ShowInfo(_context.L("Plugins"), _context.LF("{0} installed.", installed.DisplayName), InfoBarSeverity.Success);
            Refresh();
        }
        catch (Exception exception)
        {
            _context.ShowInfo(_context.L("Plugins"), exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task CheckUpdatesAsync()
    {
        if (_context.Controller is null)
            return;

        SetBusy(true);
        try
        {
            PluginUpdateSummary summary = await _context.Controller.CheckPluginUpdatesAsync(force: true);
            Refresh();

            if (summary.Errors.Count > 0)
            {
                _context.ShowInfo(_context.L("Plugins"), string.Join(Environment.NewLine, summary.Errors), InfoBarSeverity.Error);
            }
            else if (summary.UpdatedCount > 0)
            {
                _context.ShowInfo(_context.L("Plugins"), _context.LF("{0} plugin(s) updated.", summary.UpdatedCount), InfoBarSeverity.Success);
            }
            else if (summary.SkippedInUseCount > 0)
            {
                _context.ShowInfo(_context.L("Plugins"), _context.L("Plugin updates were deferred because one or more plugins are in use."), InfoBarSeverity.Informational);
            }
            else
            {
                _context.ShowInfo(_context.L("Plugins"), _context.L("Installed catalog plugins are up to date."), InfoBarSeverity.Success);
            }
        }
        catch (Exception exception)
        {
            _context.ShowInfo(_context.L("Plugins"), exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs _)
    {
        if (sender is not ToggleSwitch { Tag: string pluginId } automaticUpdates)
            return;

        try
        {
            if (!PluginManager.SetAutoUpdate(pluginId, automaticUpdates.IsOn))
                Refresh();
        }
        catch (IOException exception)
        {
            _context.ShowInfo(_context.L("Plugins"), exception.Message, InfoBarSeverity.Error);
            Refresh();
        }
        catch (UnauthorizedAccessException exception)
        {
            _context.ShowInfo(_context.L("Plugins"), exception.Message, InfoBarSeverity.Error);
            Refresh();
        }
    }

    private void OnRemoveClicked(object sender, RoutedEventArgs _)
    {
        if (sender is not Button { Tag: string pluginId })
            return;

        try
        {
            PluginManager.Remove(pluginId);
            _context.ShowInfo(_context.L("Plugins"), _context.L("Plugin removed."), InfoBarSeverity.Success);
            Refresh();
        }
        catch (IOException exception)
        {
            _context.ShowInfo(_context.L("Plugins"), exception.Message, InfoBarSeverity.Error);
        }
        catch (UnauthorizedAccessException exception)
        {
            _context.ShowInfo(_context.L("Plugins"), exception.Message, InfoBarSeverity.Error);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _progress.IsActive = busy;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateControlState();
    }

    private void UpdateControlState()
    {
        bool idle = !_busy;
        InstalledPlugin[] installed = PluginManager.GetInstalledPlugins().ToArray();
        PluginCatalogEntry? selected = _catalog.SelectedItem as PluginCatalogEntry;
        bool selectedInstalled = selected is not null
            && installed.Any(plugin => string.Equals(plugin.Id, selected.Id, StringComparison.OrdinalIgnoreCase));

        _catalog.IsEnabled = idle;
        _installButton.Content = _context.L(selectedInstalled ? "Reinstall" : "Install");
        _context.SetToolTip(
            _installButton,
            selectedInstalled
                ? "Download the latest Windows x64 release archive and replace the installed selected plugin."
                : "Download the latest Windows x64 release archive and install the selected plugin.");
        _installButton.IsEnabled = idle && selected is not null;
        _importButton.IsEnabled = idle;
        _checkUpdatesButton.IsEnabled = idle && installed.Any(plugin => plugin.CanAutoUpdate);
        _installedList.IsHitTestVisible = idle;
        _installedList.Opacity = idle ? 1.0 : 0.65;
    }
}
