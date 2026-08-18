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
    private readonly ProgressRing _progress;
    private readonly StackPanel _installedList;

    internal PluginsPage(WinUIPageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        Content = WinUIStyles.CreatePage(
            "Plugins",
            "Install SIP003 plugins from the built-in list or import a plugin ZIP or TAR.GZ package.",
            out StackPanel panel);

        var addStack = new StackPanel { Spacing = 12 };
        addStack.Children.Add(WinUIStyles.CreateSectionTitle("Add plugin"));

        var addRow = new Grid { ColumnSpacing = 8 };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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

        _progress = new ProgressRing
        {
            Width = 20,
            Height = 20,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_progress, 3);
        addRow.Children.Add(_progress);

        addStack.Children.Add(addRow);
        panel.Children.Add(WinUIStyles.CreateCard(addStack));

        var installedStack = new StackPanel { Spacing = 12 };
        installedStack.Children.Add(WinUIStyles.CreateSectionTitle("Installed plugins"));
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
            return;
        }

        foreach (InstalledPlugin plugin in installed)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(WinUIStyles.CreateText(plugin.DisplayName, "BodyStrongTextBlockStyle"));
            TextBlock source = WinUIStyles.CreateText(plugin.Source, "CaptionTextBlockStyle");
            source.Opacity = 0.72;
            text.Children.Add(source);
            row.Children.Add(text);

            var remove = new Button { Content = _context.L("Remove"), Tag = plugin.Id, VerticalAlignment = VerticalAlignment.Center };
            _context.SetToolTip(remove, "Remove this installed plugin from Shadowsocks storage.");
            remove.Click += OnRemoveClicked;
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            _installedList.Children.Add(row);
        }
    }

    private async Task InstallSelectedAsync()
    {
        if (_catalog.SelectedItem is not PluginCatalogEntry entry)
            return;

        SetBusy(true);
        try
        {
            InstalledPlugin installed = await PluginManager.InstallCatalogPluginAsync(entry);
            _context.ShowInfo("Plugins", _context.LF("{0} installed.", installed.DisplayName), InfoBarSeverity.Success);
            Refresh();
        }
        catch (Exception exception)
        {
            _context.ShowInfo("Plugins", exception.Message, InfoBarSeverity.Error);
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
            _context.ShowInfo("Plugins", _context.LF("{0} installed.", installed.DisplayName), InfoBarSeverity.Success);
            Refresh();
        }
        catch (Exception exception)
        {
            _context.ShowInfo("Plugins", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnRemoveClicked(object sender, RoutedEventArgs _)
    {
        if (sender is not Button { Tag: string pluginId })
            return;

        try
        {
            PluginManager.Remove(pluginId);
            _context.ShowInfo("Plugins", "Plugin removed.", InfoBarSeverity.Success);
            Refresh();
        }
        catch (IOException exception)
        {
            _context.ShowInfo("Plugins", exception.Message, InfoBarSeverity.Error);
        }
        catch (UnauthorizedAccessException exception)
        {
            _context.ShowInfo("Plugins", exception.Message, InfoBarSeverity.Error);
        }
    }

    private void SetBusy(bool busy)
    {
        _catalog.IsEnabled = !busy;
        _installButton.IsEnabled = !busy;
        _importButton.IsEnabled = !busy;
        _progress.IsActive = busy;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
}
