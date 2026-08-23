using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shadowsocks.Model;
using Shadowsocks.Controller;
using Shadowsocks.Controller.Service;
using Shadowsocks.WinUI.UI;

namespace Shadowsocks.WinUI.Pages;

public sealed class ServersPage : Page, IRefreshablePage
{
    private sealed record PluginChoice(string Id, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private static readonly string[] SupportedMethods =
    [
        "none",
        "plain",
        "aes-256-gcm",
        "aes-192-gcm",
        "aes-128-gcm",
        "chacha20-ietf-poly1305",
    ];

    private readonly WinUIPageContext _context;
    private readonly ListView _serverList;
    private readonly TextBox _host;
    private readonly NumberBox _serverPort;
    private readonly PasswordBox _password;
    private readonly CheckBox _showPassword;
    private readonly ComboBox _method;
    private readonly ComboBox _plugin;
    private readonly TextBox _pluginOptions;
    private readonly TextBlock _pluginOptionsLabel;
    private readonly CheckBox _pluginArgumentsEnabled;
    private readonly TextBox _pluginArguments;
    private readonly TextBlock _pluginArgumentsLabel;
    private readonly TextBox _remarks;
    private readonly NumberBox _timeout;
    private readonly TextBox _group;
    private readonly NumberBox _localPort;
    private readonly Button _deleteButton;
    private readonly Button _duplicateButton;
    private readonly Button _shareButton;
    private readonly Button _moveUpButton;
    private readonly Button _moveDownButton;
    private readonly Button _applyButton;
    private readonly Button _discardButton;
    private readonly Border _editorCard;
    private readonly TextBlock _emptyServersText;
    private readonly List<Server> _servers = new();
    private readonly HashSet<string> _loadedServerIdentities = new(StringComparer.Ordinal);
    private int _selectedIndex = -1;
    private bool _loading;
    private bool _loadedOnce;
    private bool _dirty;
    private bool _selectionValidationPending;
    private readonly Dictionary<string, string> _serverCountryFlags = new(StringComparer.Ordinal);
    private int _countryLookupGeneration;

    internal ServersPage(WinUIPageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        Content = WinUIStyles.CreatePage(
            "Servers",
            "Manage proxy servers and local client settings.",
            out StackPanel panel);

        var workspace = new Grid
        {
            ColumnSpacing = 16,
            MinHeight = 540,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 820,
        };
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(224) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(500) });

        var left = new StackPanel { Spacing = 8 };
        _serverList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MinHeight = 430,
            MaxHeight = 520,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        _serverList.SelectionChanged += OnServerSelectionChanged;
        _context.SetToolTip(_serverList, "Select a server to edit, duplicate, share, move, or delete it.");
        left.Children.Add(_serverList);
        _emptyServersText = WinUIStyles.CreateText(
            "No servers configured. Select Add to create one.",
            "CaptionTextBlockStyle");
        _emptyServersText.Opacity = 0.72;
        _emptyServersText.Visibility = Visibility.Collapsed;
        left.Children.Add(_emptyServersText);

        // Match the original Config form: two compact button columns beneath the server list.
        var listButtons = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        listButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        listButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int row = 0; row < 4; row++)
        {
            listButtons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var add = CreateListButton("Add", OnAddClicked);
        _context.SetToolTip(add, "Create a new server entry.");
        listButtons.Children.Add(add);
        _deleteButton = CreateListButton("Delete", OnRemoveClicked);
        _context.SetToolTip(_deleteButton, "Delete the selected server.");
        Grid.SetColumn(_deleteButton, 1);
        listButtons.Children.Add(_deleteButton);

        _duplicateButton = CreateListButton("Duplicate", OnDuplicateClicked);
        _context.SetToolTip(_duplicateButton, "Create a copy of the selected server.");
        Grid.SetRow(_duplicateButton, 1);
        listButtons.Children.Add(_duplicateButton);
        _shareButton = CreateListButton("Share Server Config", OnShareClicked);
        _context.SetToolTip(_shareButton, "Open Share / QR for the selected server.");
        Grid.SetRow(_shareButton, 3);
        Grid.SetColumnSpan(_shareButton, 2);
        listButtons.Children.Add(_shareButton);

        _moveUpButton = CreateListButton("Move up", (_, _) => MoveSelected(-1));
        _context.SetToolTip(_moveUpButton, "Move the selected server one position up.");
        Grid.SetRow(_moveUpButton, 2);
        listButtons.Children.Add(_moveUpButton);
        _moveDownButton = CreateListButton("Move down", (_, _) => MoveSelected(1));
        _context.SetToolTip(_moveDownButton, "Move the selected server one position down.");
        Grid.SetRow(_moveDownButton, 2);
        Grid.SetColumn(_moveDownButton, 1);
        listButtons.Children.Add(_moveDownButton);
        left.Children.Add(listButtons);

        Border leftCard = WinUIStyles.CreateCard(left);
        leftCard.Padding = new Thickness(12);
        Grid.SetColumn(leftCard, 0);
        workspace.Children.Add(leftCard);

        var right = new StackPanel { Spacing = 12 };

        var editorStack = new StackPanel { Spacing = 10 };
        editorStack.Children.Add(WinUIStyles.CreateSectionTitle("Server"));
        var form = CreateFormGrid();

        _remarks = new TextBox { MaxLength = 128, PlaceholderText = "Optional display name" };
        _context.SetToolTip(_remarks, "Friendly name shown in server lists. If left empty, Shadowsocks assigns a unique Server N name.");
        AddFormRow(form, "Server Name", _remarks);
        _host = new TextBox { MaxLength = 512, PlaceholderText = "example.com or 203.0.113.10" };
        _context.SetToolTip(_host, "Hostname or IP address of the Shadowsocks server.");
        AddFormRow(form, "Server IP", _host);
        _serverPort = CreateNumberBox(1, 65535, Server.DefaultPort);
        _context.SetToolTip(_serverPort, "TCP/UDP port exposed by the Shadowsocks server.");
        AddFormRow(form, "Server Port", _serverPort);
        _password = new PasswordBox { MaxLength = 256, PasswordRevealMode = PasswordRevealMode.Hidden };
        _context.SetToolTip(_password, "Password used by this Shadowsocks server configuration.");
        AddFormRow(form, "Password", _password);
        _showPassword = new CheckBox { Content = "Show Password" };
        _context.SetToolTip(_showPassword, "Temporarily reveal the password for manually managed servers. Link-managed servers keep this option hidden.");
        _showPassword.Checked += (_, _) => _password.PasswordRevealMode = PasswordRevealMode.Visible;
        _showPassword.Unchecked += (_, _) => _password.PasswordRevealMode = PasswordRevealMode.Hidden;
        AddFormRow(form, string.Empty, _showPassword);
        _method = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (string method in SupportedMethods)
        {
            _method.Items.Add(method);
        }
        _context.SetToolTip(_method, "Encryption method negotiated with the selected Shadowsocks server.");
        AddFormRow(form, "Encryption", _method);
        _plugin = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _context.SetToolTip(_plugin, "Optional SIP003 plugin installed on the Plugins page.");
        RefreshPluginChoices(null);
        AddFormRow(form, "Plugin", _plugin);
        _pluginOptions = new TextBox { MaxLength = 256 };
        _context.SetToolTip(_pluginOptions, "Options passed to the SIP003 plugin through SS_PLUGIN_OPTIONS.");
        _pluginOptionsLabel = AddFormRow(form, "Plugin Options", _pluginOptions)!;
        _pluginArgumentsEnabled = new CheckBox { Content = "Need Plugin Argument" };
        _context.SetToolTip(_pluginArgumentsEnabled, "Enable an additional command-line argument string for the plugin.");
        _pluginArgumentsEnabled.Checked += (_, _) =>
        {
            UpdatePluginFieldsVisibility();
            MarkDirty();
        };
        _pluginArgumentsEnabled.Unchecked += (_, _) =>
        {
            UpdatePluginFieldsVisibility();
            MarkDirty();
        };
        AddFormRow(form, string.Empty, _pluginArgumentsEnabled);
        _pluginArguments = new TextBox { MaxLength = 512 };
        _context.SetToolTip(_pluginArguments, "Additional command-line arguments passed directly to the plugin process.");
        _pluginArgumentsLabel = AddFormRow(form, "Plugin Arguments", _pluginArguments)!;
        _timeout = CreateNumberBox(1, Server.MaxServerTimeoutSec, 5);
        _context.SetToolTip(_timeout, "Connection timeout for this server, in seconds.");
        AddFormRow(form, "Timeout (Sec)", _timeout);
        _group = new TextBox { IsReadOnly = true, MaxLength = 64 };
        _context.SetToolTip(_group, "Read-only group supplied by an imported or online configuration, when available.");
        AddFormRow(form, "Group", _group);
        editorStack.Children.Add(form);
        _editorCard = WinUIStyles.CreateCard(editorStack);
        _editorCard.Padding = new Thickness(16, 14, 16, 14);
        right.Children.Add(_editorCard);

        var localStack = new StackPanel { Spacing = 10 };
        localStack.Children.Add(WinUIStyles.CreateSectionTitle("Local client"));
        var localGrid = CreateFormGrid();
        _localPort = CreateNumberBox(1, 65535, 1080);
        _context.SetToolTip(_localPort, "Local SOCKS/HTTP listening port used by Shadowsocks.");
        AddFormRow(localGrid, "Proxy Port", _localPort);
        localStack.Children.Add(localGrid);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        _discardButton = new Button { Content = "Discard changes", MinWidth = 112, IsEnabled = false };
        _context.SetToolTip(_discardButton, "Restore the selected server fields to their last saved values.");
        _discardButton.Click += (_, _) => DiscardChanges();
        actions.Children.Add(_discardButton);
        _applyButton = new Button { Content = "Apply", MinWidth = 88, IsEnabled = false };
        _context.SetToolTip(_applyButton, "Validate and save the current server and local client settings.");
        _applyButton.Click += OnSaveClicked;
        actions.Children.Add(_applyButton);
        localStack.Children.Add(actions);
        Border localCard = WinUIStyles.CreateCard(localStack);
        localCard.Padding = new Thickness(16, 14, 16, 14);
        right.Children.Add(localCard);

        Grid.SetColumn(right, 1);
        workspace.Children.Add(right);
        panel.Children.Add(workspace);

        _host.TextChanged += (_, _) => MarkDirty();
        _serverPort.ValueChanged += (_, _) => MarkDirty();
        _password.PasswordChanged += (_, _) => MarkDirty();
        _method.SelectionChanged += (_, _) => MarkDirty();
        _plugin.SelectionChanged += (_, _) =>
        {
            UpdatePluginFieldsVisibility();
            MarkDirty();
        };
        _pluginOptions.TextChanged += (_, _) => MarkDirty();
        _pluginArguments.TextChanged += (_, _) => MarkDirty();
        _remarks.TextChanged += (_, _) => MarkDirty();
        _timeout.ValueChanged += (_, _) => MarkDirty();
        _localPort.ValueChanged += (_, _) => MarkDirty();
        UpdatePluginFieldsVisibility();

        WinUILocalization.Apply(Content, _context.Localization);
    }

    public void Refresh()
    {
        RefreshPluginChoices((_plugin.SelectedItem as PluginChoice)?.Id);
        Configuration? configuration = _context.Controller?.GetCurrentConfiguration();
        if (_loadedOnce && _dirty)
        {
            if (configuration is not null)
            {
                MergeExternallyAddedServers(configuration);
            }
            return;
        }

        if (configuration is null)
        {
            return;
        }

        _servers.Clear();
        _loadedServerIdentities.Clear();
        foreach (Server server in configuration.configs ?? [])
        {
            _servers.Add(CloneServer(server));
            _loadedServerIdentities.Add(BuildServerIdentity(server));
        }
        _loading = true;
        try
        {
            _localPort.Value = configuration.localPort;
        }
        finally
        {
            _loading = false;
        }

        _selectedIndex = _servers.Count == 0
            ? -1
            : Math.Clamp(configuration.index, 0, _servers.Count - 1);
        RebuildServerList(_selectedIndex);
        LoadSelectedIntoEditor();
        _loadedOnce = true;
        SetDirty(false);
    }

    private static Button CreateListButton(string text, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        button.Click += handler;
        return button;
    }

    private static Grid CreateFormGrid()
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(142) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private static TextBlock? AddFormRow(Grid grid, string label, FrameworkElement control)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        TextBlock? text = null;
        if (!string.IsNullOrEmpty(label))
        {
            text = WinUIStyles.CreateText(label);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.HorizontalAlignment = HorizontalAlignment.Right;
            text.TextAlignment = TextAlignment.Right;
            Grid.SetRow(text, row);
            grid.Children.Add(text);
        }
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return text;
    }

    private static NumberBox CreateNumberBox(double minimum, double maximum, double value)
        => new()
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };

    private void RebuildServerList(int selectedIndex)
    {
        _loading = true;
        try
        {
            _serverList.Items.Clear();
            for (int index = 0; index < _servers.Count; index++)
            {
                Server server = _servers[index];
                _serverList.Items.Add(new ListViewItem
                {
                    Content = CreateServerListContent(server, index),
                    Padding = new Thickness(8, 5, 8, 5),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                });
            }
            _serverList.SelectedIndex = _servers.Count == 0
                ? -1
                : Math.Clamp(selectedIndex, 0, _servers.Count - 1);
            _emptyServersText.Visibility = _servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _loading = false;
        }
        UpdateListButtons();
        _ = RefreshServerCountryFlagsAsync(++_countryLookupGeneration);
    }


    private void MergeExternallyAddedServers(Configuration configuration)
    {
        bool added = false;
        foreach (Server server in configuration.configs ?? [])
        {
            string identity = BuildServerIdentity(server);
            if (_loadedServerIdentities.Contains(identity))
            {
                continue;
            }

            if (!_servers.Any(existing => string.Equals(BuildServerIdentity(existing), identity, StringComparison.Ordinal)))
            {
                _servers.Add(CloneServer(server));
                added = true;
            }

            _loadedServerIdentities.Add(identity);
        }

        if (added)
        {
            RebuildServerList(Math.Clamp(_selectedIndex, 0, _servers.Count - 1));
        }
    }

    private static string BuildServerIdentity(Server server)
        => string.Join("\u001F",
            server.server ?? string.Empty,
            server.ServerPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            server.method ?? string.Empty,
            server.password ?? string.Empty,
            server.plugin ?? string.Empty,
            server.PluginOptions ?? string.Empty,
            server.remarks ?? string.Empty);

    private void UpdateServerListItem(int index)
    {
        if (index < 0 || index >= _servers.Count || index >= _serverList.Items.Count)
        {
            return;
        }

        if (_serverList.Items[index] is ListViewItem item)
        {
            Server server = _servers[index];
            item.Content = CreateServerListContent(server, index);
        }
    }

    private Grid CreateServerListContent(Server server, int index)
    {
        string name = server.IsConfigured
            ? !string.IsNullOrWhiteSpace(server.remarks)
                ? server.remarks.Trim()
                : _context.LF("Server {0}", index + 1)
            : _context.LF("New server {0}", index + 1);
        string identity = BuildServerIdentity(server);
        _serverCountryFlags.TryGetValue(identity, out string? flag);

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameBlock = new TextBlock
        {
            Text = name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(nameBlock, 0);
        row.Children.Add(nameBlock);

        var flagBlock = new TextBlock
        {
            Text = flag ?? string.Empty,
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 24,
            TextAlignment = TextAlignment.Right,
        };
        Grid.SetColumn(flagBlock, 1);
        row.Children.Add(flagBlock);
        return row;
    }

    private async Task RefreshServerCountryFlagsAsync(int generation)
    {
        if (_context.Controller is null)
            return;
        for (int index = 0; index < _servers.Count; index++)
        {
            if (generation != _countryLookupGeneration)
                return;
            Server server = _servers[index];
            if (!server.IsConfigured)
                continue;
            string identity = BuildServerIdentity(server);
            if (_serverCountryFlags.ContainsKey(identity))
                continue;
            try
            {
                IpCountryInfo? country = await ShadowsocksController.GetServerCountryAsync(server);
                if (generation != _countryLookupGeneration)
                    return;
                if (country is not null && !string.IsNullOrWhiteSpace(country.FlagEmoji))
                {
                    _serverCountryFlags[identity] = country.FlagEmoji;
                    UpdateServerListItem(index);
                }
            }
            catch
            {
                // Country flags are best-effort presentation metadata and must never block server editing.
            }
        }
    }

    private async void OnServerSelectionChanged(object _, SelectionChangedEventArgs _1)
    {
        if (_loading || _selectionValidationPending || _serverList.SelectedIndex < 0)
        {
            return;
        }

        int requestedIndex = _serverList.SelectedIndex;
        int previousIndex = _selectedIndex;
        if (previousIndex < 0 || previousIndex >= _servers.Count || previousIndex == requestedIndex)
        {
            _selectedIndex = requestedIndex;
            LoadSelectedIntoEditor();
            UpdateListButtons();
            return;
        }

        _selectionValidationPending = true;
        _serverList.IsEnabled = false;
        try
        {
            CaptureEditorIntoSelected();
            Server current = _servers[previousIndex];

            if (IsCompletelyBlank(current) && _servers.Count > 1)
            {
                bool discard = await ConfirmDiscardUnconfiguredServerAsync();
                if (!discard)
                {
                    RestoreServerSelection(previousIndex);
                    return;
                }

                _servers.RemoveAt(previousIndex);
                SetDirty(true);
                if (requestedIndex > previousIndex)
                {
                    requestedIndex--;
                }
                requestedIndex = Math.Clamp(requestedIndex, 0, _servers.Count - 1);
                _selectedIndex = requestedIndex;
                RebuildServerList(_selectedIndex);
                LoadSelectedIntoEditor();
                return;
            }

            try
            {
                Configuration.CheckServer(current);
            }
            catch (Exception exception)
            {
                RestoreServerSelection(previousIndex);
                _context.ShowInfo(
                    "Auto save failed",
                    _context.LF("{0} Fix the current server before selecting another one.", exception.Message),
                    InfoBarSeverity.Error);
                return;
            }

            UpdateServerListItem(previousIndex);
            _selectedIndex = requestedIndex;
            LoadSelectedIntoEditor();
            UpdateListButtons();
        }
        finally
        {
            _serverList.IsEnabled = true;
            _selectionValidationPending = false;
        }
    }

    private async System.Threading.Tasks.Task<bool> ConfirmDiscardUnconfiguredServerAsync()
    {
        XamlRoot? xamlRoot = _context.GetXamlRoot();
        if (xamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = _context.L("Unconfigured server"),
            Content = _context.L("This server has not been configured. Discard it and switch to the selected server?"),
            PrimaryButtonText = _context.L("Discard"),
            CloseButtonText = _context.L("Stay"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void RestoreServerSelection(int index)
    {
        _loading = true;
        try
        {
            _serverList.SelectedIndex = index;
        }
        finally
        {
            _loading = false;
        }
        UpdateListButtons();
    }

    private bool ValidateCurrentServerForAction()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _servers.Count)
        {
            return true;
        }

        try
        {
            CaptureEditorIntoSelected();
            Configuration.CheckServer(_servers[_selectedIndex]);
            UpdateServerListItem(_selectedIndex);
            return true;
        }
        catch (Exception exception)
        {
            _context.ShowInfo("Server configuration", exception.Message, InfoBarSeverity.Error);
            return false;
        }
    }


    private void RefreshPluginChoices(string? currentPlugin)
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            string selected = currentPlugin ?? string.Empty;
            _plugin.Items.Clear();
            _plugin.Items.Add(new PluginChoice(string.Empty, _context.L("None")));
            foreach (InstalledPlugin plugin in PluginManager.GetInstalledPlugins())
            {
                _plugin.Items.Add(new PluginChoice(plugin.Id, plugin.DisplayName));
            }

            if (!string.IsNullOrWhiteSpace(selected)
                && !_plugin.Items.OfType<PluginChoice>().Any(item => string.Equals(item.Id, selected, StringComparison.OrdinalIgnoreCase)))
            {
                _plugin.Items.Add(new PluginChoice(selected, selected));
            }

            SelectPlugin(selected);
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void SelectPlugin(string? plugin)
    {
        string value = plugin?.Trim() ?? string.Empty;
        PluginChoice? match = _plugin.Items.OfType<PluginChoice>().FirstOrDefault(item =>
            string.Equals(item.Id, value, StringComparison.OrdinalIgnoreCase));
        if (match is null && !string.IsNullOrWhiteSpace(value))
        {
            match = new PluginChoice(value, value);
            _plugin.Items.Add(match);
        }
        _plugin.SelectedItem = match ?? _plugin.Items.OfType<PluginChoice>().FirstOrDefault();
    }

    private void LoadSelectedIntoEditor()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _servers.Count)
        {
            ClearEditor();
            return;
        }

        _loading = true;
        try
        {
            Server server = _servers[_selectedIndex];
            _host.Text = server.server ?? string.Empty;
            _serverPort.Value = server.ServerPort > 0 ? server.ServerPort : Server.DefaultPort;
            _password.Password = server.password ?? string.Empty;
            _showPassword.IsChecked = false;
            _password.PasswordRevealMode = PasswordRevealMode.Hidden;
            _showPassword.Visibility = IsPasswordRevealAllowed(server) ? Visibility.Visible : Visibility.Collapsed;
            _method.SelectedItem = SupportedMethods.Contains(server.method, StringComparer.OrdinalIgnoreCase)
                ? server.method
                : Server.DefaultMethod;
            SelectPlugin(server.plugin);
            _pluginOptions.Text = server.PluginOptions ?? string.Empty;
            _pluginArguments.Text = server.PluginArguments ?? string.Empty;
            _pluginArgumentsEnabled.IsChecked = !string.IsNullOrWhiteSpace(server.PluginArguments);
            _remarks.Text = server.remarks ?? string.Empty;
            _group.Text = server.group ?? string.Empty;
            _timeout.Value = server.timeout > 0 ? server.timeout : 5;
            UpdatePluginFieldsVisibility();
        }
        finally
        {
            _loading = false;
        }
    }


    private void ClearEditor()
    {
        _loading = true;
        try
        {
            _host.Text = string.Empty;
            _serverPort.Value = Server.DefaultPort;
            _password.Password = string.Empty;
            _showPassword.IsChecked = false;
            _password.PasswordRevealMode = PasswordRevealMode.Hidden;
            _showPassword.Visibility = Visibility.Visible;
            _method.SelectedItem = Server.DefaultMethod;
            SelectPlugin(string.Empty);
            _pluginOptions.Text = string.Empty;
            _pluginArguments.Text = string.Empty;
            _pluginArgumentsEnabled.IsChecked = false;
            _remarks.Text = string.Empty;
            _group.Text = string.Empty;
            _timeout.Value = 5;
            UpdatePluginFieldsVisibility();
        }
        finally
        {
            _loading = false;
        }
    }

    private bool HasSelectedPlugin()
        => _plugin.SelectedItem is PluginChoice choice && !string.IsNullOrWhiteSpace(choice.Id);

    private void UpdatePluginFieldsVisibility()
    {
        bool hasPlugin = HasSelectedPlugin();
        Visibility pluginVisibility = hasPlugin ? Visibility.Visible : Visibility.Collapsed;
        _pluginOptions.Visibility = pluginVisibility;
        _pluginOptionsLabel.Visibility = pluginVisibility;
        _pluginArgumentsEnabled.Visibility = pluginVisibility;

        Visibility argumentsVisibility = hasPlugin && _pluginArgumentsEnabled.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        _pluginArguments.Visibility = argumentsVisibility;
        _pluginArgumentsLabel.Visibility = argumentsVisibility;
    }

    private void CaptureEditorIntoSelected()
    {
        if (_loading || _selectedIndex < 0 || _selectedIndex >= _servers.Count)
        {
            return;
        }

        Server server = _servers[_selectedIndex];
        server.server = _host.Text?.Trim() ?? string.Empty;
        server.ServerPort = double.IsNaN(_serverPort.Value) ? Server.DefaultPort : checked((int)_serverPort.Value);
        server.password = _password.Password ?? string.Empty;
        server.method = _method.SelectedItem?.ToString() ?? Server.DefaultMethod;
        server.plugin = (_plugin.SelectedItem as PluginChoice)?.Id ?? string.Empty;
        bool hasPlugin = !string.IsNullOrWhiteSpace(server.plugin);
        server.PluginOptions = hasPlugin ? (_pluginOptions.Text?.Trim() ?? string.Empty) : string.Empty;
        server.PluginArguments = hasPlugin && _pluginArgumentsEnabled.IsChecked == true
            ? (_pluginArguments.Text?.Trim() ?? string.Empty)
            : string.Empty;
        server.remarks = _remarks.Text?.Trim() ?? string.Empty;
        server.timeout = double.IsNaN(_timeout.Value) ? 5 : checked((int)_timeout.Value);
    }

    private void MarkDirty()
    {
        if (!_loading)
        {
            SetDirty(true);
        }
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        _applyButton.IsEnabled = dirty;
        _discardButton.IsEnabled = dirty;
    }

    private void OnAddClicked(object _, RoutedEventArgs _1)
    {
        if (!ValidateCurrentServerForAction())
        {
            return;
        }
        _servers.Add(new Server());
        SetDirty(true);
        _selectedIndex = _servers.Count - 1;
        RebuildServerList(_selectedIndex);
        LoadSelectedIntoEditor();
    }

    private void OnDuplicateClicked(object _, RoutedEventArgs _1)
    {
        if (!ValidateCurrentServerForAction())
        {
            return;
        }
        if (_selectedIndex < 0 || _selectedIndex >= _servers.Count)
        {
            return;
        }

        _servers.Insert(_selectedIndex + 1, CloneServer(_servers[_selectedIndex]));
        SetDirty(true);
        _selectedIndex++;
        RebuildServerList(_selectedIndex);
        LoadSelectedIntoEditor();
    }


    private void OnShareClicked(object _, RoutedEventArgs _1)
    {
        if (_selectedIndex < 0 || _selectedIndex >= _servers.Count)
        {
            return;
        }

        if (_dirty && !SaveChanges(showSuccess: false))
        {
            return;
        }

        _context.NavigateToSharingServer(_selectedIndex);
    }

    private void OnRemoveClicked(object _, RoutedEventArgs _1)
    {
        if (_selectedIndex < 0 || _selectedIndex >= _servers.Count)
        {
            return;
        }

        _servers.RemoveAt(_selectedIndex);
        SetDirty(true);
        _selectedIndex = _servers.Count == 0 ? -1 : Math.Min(_selectedIndex, _servers.Count - 1);
        RebuildServerList(_selectedIndex);
        LoadSelectedIntoEditor();
    }

    private void MoveSelected(int delta)
    {
        CaptureEditorIntoSelected();
        int target = _selectedIndex + delta;
        if (_selectedIndex < 0 || target < 0 || target >= _servers.Count)
        {
            return;
        }

        Server server = _servers[_selectedIndex];
        _servers.RemoveAt(_selectedIndex);
        _servers.Insert(target, server);
        SetDirty(true);
        _selectedIndex = target;
        RebuildServerList(_selectedIndex);
        LoadSelectedIntoEditor();
    }

    private void UpdateListButtons()
    {
        bool hasSelection = _selectedIndex >= 0 && _selectedIndex < _servers.Count;
        SetEditorEnabled(hasSelection);
        _editorCard.Opacity = hasSelection ? 1.0 : 0.62;
        _deleteButton.IsEnabled = hasSelection;
        _duplicateButton.IsEnabled = hasSelection;
        _shareButton.IsEnabled = hasSelection;
        _moveUpButton.IsEnabled = _selectedIndex > 0;
        _moveDownButton.IsEnabled = _selectedIndex >= 0 && _selectedIndex < _servers.Count - 1;
    }

    private void SetEditorEnabled(bool enabled)
    {
        _remarks.IsEnabled = enabled;
        _host.IsEnabled = enabled;
        _serverPort.IsEnabled = enabled;
        _password.IsEnabled = enabled;
        _showPassword.IsEnabled = enabled;
        _method.IsEnabled = enabled;
        _plugin.IsEnabled = enabled;
        _pluginOptions.IsEnabled = enabled;
        _pluginArgumentsEnabled.IsEnabled = enabled;
        _pluginArguments.IsEnabled = enabled;
        _timeout.IsEnabled = enabled;
        _group.IsEnabled = enabled;
    }

    private void DiscardChanges()
    {
        SetDirty(false);
        _loadedOnce = false;
        Refresh();
    }

    private void OnSaveClicked(object _, RoutedEventArgs _1)
        => SaveChanges(showSuccess: true);

    private bool SaveChanges(bool showSuccess)
    {
        if (_context.Controller is null)
        {
            return false;
        }

        try
        {
            CaptureEditorIntoSelected();
            int localPort = double.IsNaN(_localPort.Value) ? 0 : checked((int)_localPort.Value);
            Configuration.CheckLocalPort(localPort);

            var toSave = new List<Server>();
            int savedSelection = -1;
            for (int index = 0; index < _servers.Count; index++)
            {
                Server server = _servers[index];
                if (IsCompletelyBlank(server))
                    continue;

                if (index == _selectedIndex)
                    savedSelection = toSave.Count;
                Server copy = CloneServer(server);
                Configuration.CheckServer(copy);
                toSave.Add(copy);
            }

            _selectedIndex = toSave.Count == 0
                ? -1
                : savedSelection >= 0 ? savedSelection : 0;
            _context.Controller.SaveServers(toSave, localPort);
            _context.Controller.SelectServerIndex(_selectedIndex);
            _context.Controller.CompleteFirstRun();
            if (showSuccess)
            {
                _context.ShowInfo("Servers", "Server configuration saved.", InfoBarSeverity.Success);
            }
            _loadedOnce = false;
            SetDirty(false);
            Refresh();
            return true;
        }
        catch (Exception exception)
        {
            _context.ShowInfo("Server configuration", exception.Message, InfoBarSeverity.Error);
            return false;
        }
    }

    private static bool IsPasswordRevealAllowed(Server server)
    {
        if (server is null)
        {
            return true;
        }
        if (server.importedFromUrl)
        {
            return false;
        }
        return !Uri.TryCreate(server.group, UriKind.Absolute, out Uri? groupUri)
            || (groupUri.Scheme != Uri.UriSchemeHttp && groupUri.Scheme != Uri.UriSchemeHttps);
    }

    private static bool IsCompletelyBlank(Server server)
        => string.IsNullOrWhiteSpace(server.server)
           && string.IsNullOrWhiteSpace(server.password)
           && string.IsNullOrWhiteSpace(server.remarks)
           && string.IsNullOrWhiteSpace(server.plugin)
           && string.IsNullOrWhiteSpace(server.group);

    private static Server CloneServer(Server source)
        => new()
        {
            server = source.server ?? string.Empty,
            ServerPort = source.ServerPort,
            password = source.password ?? string.Empty,
            method = source.method ?? Server.DefaultMethod,
            plugin = source.plugin ?? string.Empty,
            PluginOptions = source.PluginOptions ?? string.Empty,
            PluginArguments = source.PluginArguments ?? string.Empty,
            remarks = source.remarks ?? string.Empty,
            group = source.group ?? string.Empty,
            timeout = source.timeout,
            importedFromUrl = source.importedFromUrl,
        };
}
