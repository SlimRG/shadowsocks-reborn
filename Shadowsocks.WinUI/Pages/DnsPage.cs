using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shadowsocks.Controller.Service;
using Shadowsocks.Controller.Traffic;
using Shadowsocks.WinUI.UI;

namespace Shadowsocks.WinUI.Pages;

public sealed class DnsPage : Page, IRefreshablePage
{
    private sealed record ResolverChoice(DnsCryptResolverInfo Resolver)
    {
        public string Name => Resolver.Name;

        public override string ToString()
        {
            string flag = string.IsNullOrWhiteSpace(Resolver.FlagEmoji) ? "•" : Resolver.FlagEmoji;
            string protocol = string.IsNullOrWhiteSpace(Resolver.Protocol) ? "DNS" : Resolver.Protocol;
            string latency = Resolver.LatencyMs is int latencyMs ? $"{latencyMs} ms" : "—";
            return $"{flag}  {Resolver.Name} · {protocol} · {latency}";
        }
    }

    private sealed record SystemDnsSnapshot(IReadOnlyList<string> Adapters, IReadOnlyList<string> Servers);

    private sealed record DohPreset(string Name, string Url)
    {
        public bool IsCustom => string.IsNullOrWhiteSpace(Url);
        public override string ToString() => Name;
    }

    private sealed record DnsServerPreset(string Name, string Primary, string Fallback, bool IsCustom = false)
    {
        public bool IsOriginal => !IsCustom && string.IsNullOrWhiteSpace(Primary);
        public override string ToString() => Name;
    }

    private sealed record ResolverCountryFilter(string CountryCode, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ResolverProtocolFilter(string Protocol, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly WinUIPageContext _context;
    private readonly TextBlock _systemDnsAdapters;
    private readonly TextBlock _systemDnsServers;
    private readonly TextBlock _effectiveDnsMode;
    private readonly TextBlock _dnsInterceptionStatus;
    private readonly RadioButton _systemMode;
    private readonly RadioButton _directMode;
    private readonly RadioButton _proxyMode;
    private readonly RadioButton _customDohMode;
    private readonly RadioButton _dnsCryptMode;
    private readonly IReadOnlyList<DohPreset> _dohPresets;
    private readonly IReadOnlyList<DnsServerPreset> _dnsServerPresets;
    private readonly ComboBox _customDohProvider;
    private readonly ComboBox _directDnsProvider;
    private readonly TextBox _directDnsPrimaryServer;
    private readonly TextBox _directDnsFallbackServer;
    private readonly ToggleSwitch _directDnsRouteThroughShadowsocks;
    private readonly TextBox _customDohUrl;
    private readonly ToggleSwitch _customDohRouteThroughShadowsocks;
    private readonly Border _systemSettingsCard;
    private readonly Border _directSettingsCard;
    private readonly Border _proxySettingsCard;
    private readonly Border _customDohSettingsCard;
    private readonly Border _dnsCryptOperationCard;
    private readonly Border _dnsCryptStatusCard;
    private readonly Border _dnsCryptSettingsCard;
    private readonly TextBlock _runtimeStatus;
    private readonly TextBlock _installedVersion;
    private readonly TextBlock _latestVersion;
    private readonly TextBlock _localPort;
    private readonly TextBlock _coverage;
    private readonly TextBlock _privacyDnsInterception;
    private readonly TextBlock _privacyUpstream;
    private readonly TextBlock _privacyTransport;
    private readonly TextBlock _privacyFallback;
    private readonly TextBlock _privacyBootstrap;
    private readonly TextBlock _privacyRoute;
    private readonly TextBlock _privacySelfTest;
    private readonly Button _privacyTestButton;
    private readonly Button _installButton;
    private readonly Button _checkButton;
    private readonly Button _updateButton;
    private readonly Button _restartButton;
    private readonly Button _testButton;
    private readonly Button _reinstallButton;
    private readonly Button _removeButton;
    private readonly ToggleSwitch _autoUpdate;
    private readonly ToggleSwitch _requireDnssec;
    private readonly ToggleSwitch _requireNoLog;
    private readonly ToggleSwitch _requireNoFilter;
    private readonly ToggleSwitch _ipv6Servers;
    private readonly ToggleSwitch _routeThroughShadowsocks;
    private readonly RadioButton _resolverAutomatic;
    private readonly RadioButton _resolverSelected;
    private readonly StackPanel _automaticResolverPanel;
    private readonly ListView _automaticResolverList;
    private readonly TextBlock _automaticResolverStatus;
    private readonly StackPanel _manualResolverPanel;
    private readonly TextBox _resolverSearchBox;
    private readonly ComboBox _resolverProtocolFilter;
    private readonly ComboBox _resolverCountryFilter;
    private readonly ComboBox _resolverAddressFamilyFilter;
    private readonly ToggleSwitch _resolverDnssecFilter;
    private readonly ToggleSwitch _resolverNoLogFilter;
    private readonly ToggleSwitch _resolverNoFilterFilter;
    private readonly ListView _resolverList;
    private readonly Button _loadResolversButton;
    private readonly TextBlock _resolverListStatus;
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _progressText;
    private readonly Button _cancelButton;

    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _settingsApplyCancellation;
    private CancellationTokenSource? _resolverLatencyCancellation;
    private bool _refreshing;
    private bool _busy;
    private bool _resolverCatalogLoaded;
    private bool _resolverCatalogLoading;
    private bool _updatingResolverList;
    private bool _settingsDirty;
    private bool _customDohDirty;
    private bool _directDnsDirty;
    private bool _updatingDohPreset;
    private bool _updatingDirectDnsPreset;
    private IReadOnlyList<DnsCryptResolverInfo> _resolverCatalog = Array.Empty<DnsCryptResolverInfo>();
    private readonly HashSet<string> _manualResolverNames = new(StringComparer.OrdinalIgnoreCase);
    private long _settingsApplyGeneration;
    private string? _privacySelfTestMessage;

    internal DnsPage(WinUIPageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        Content = WinUIStyles.CreatePage(
            "DNS",
            "Manage DNS policy and the optional DNSCrypt Proxy component downloaded from the official upstream release.",
            out StackPanel panel);

        var dnsStateStack = new StackPanel { Spacing = 10 };
        dnsStateStack.Children.Add(WinUIStyles.CreateSectionTitle("DNS status"));
        dnsStateStack.Children.Add(WinUIStyles.CreateKeyValueRow("Active adapters", out _systemDnsAdapters));
        dnsStateStack.Children.Add(WinUIStyles.CreateKeyValueRow("Windows DNS servers", out _systemDnsServers));
        dnsStateStack.Children.Add(WinUIStyles.CreateKeyValueRow("Shadowsocks DNS policy", out _effectiveDnsMode));
        dnsStateStack.Children.Add(WinUIStyles.CreateKeyValueRow("Transparent interception", out _dnsInterceptionStatus));
        TextBlock dnsStateNote = WinUIStyles.CreateText(
            "System DNS information is read from active Windows network adapters; Shadowsocks does not change adapter DNS addresses.");
        dnsStateNote.Opacity = 0.72;
        dnsStateStack.Children.Add(dnsStateNote);
        panel.Children.Add(WinUIStyles.CreateCard(dnsStateStack));

        var modeStack = new StackPanel { Spacing = 10 };
        modeStack.Children.Add(WinUIStyles.CreateSectionTitle("DNS mode"));
        _systemMode = new RadioButton { Content = "System DNS", GroupName = "DnsMode" };
        _directMode = CreateMode("Direct DNS");
        _proxyMode = CreateMode("DNS through Shadowsocks");
        _customDohMode = CreateMode("Custom DoH");
        _dnsCryptMode = new RadioButton { Content = "DNSCrypt", GroupName = "DnsMode" };
        _systemMode.Checked += OnSystemModeChecked;
        _directMode.Checked += OnDirectModeChecked;
        _proxyMode.Checked += OnProxyModeChecked;
        _customDohMode.Checked += OnCustomDohModeChecked;
        _dnsCryptMode.Checked += OnDnsCryptModeChecked;
        modeStack.Children.Add(_systemMode);
        modeStack.Children.Add(_directMode);
        modeStack.Children.Add(_proxyMode);
        modeStack.Children.Add(_customDohMode);
        modeStack.Children.Add(_dnsCryptMode);

        _context.SetToolTip(_systemMode, "Use DNS servers configured by Windows without Shadowsocks DNS redirection.");
        _context.SetToolTip(_directMode, "Send captured DNS traffic directly. You can keep the original destination or specify an IPv4/IPv6 DNS server; transparent enforcement requires Administrator Mode.");
        _context.SetToolTip(_proxyMode, "Route captured DNS traffic through the active Shadowsocks server. Transparent enforcement requires Administrator Mode.");
        _context.SetToolTip(_customDohMode, "Convert captured DNS queries to DNS-over-HTTPS using the configured HTTPS endpoint.");
        _context.SetToolTip(_dnsCryptMode, "Use DNSCrypt Proxy for Shadowsocks-managed DNS and transparent DNS capture when available.");

        TextBlock stageNotice = WinUIStyles.CreateText(
            "Administrator Mode transparently applies DNS through Shadowsocks, Custom DoH and DNSCrypt to captured UDP/TCP DNS traffic. " +
            "In User Mode DNSCrypt still runs for Shadowsocks-managed DNS, but system-wide interception is unavailable.");
        stageNotice.Opacity = 0.72;
        modeStack.Children.Add(stageNotice);
        panel.Children.Add(WinUIStyles.CreateCard(modeStack));

        var systemSettingsStack = new StackPanel { Spacing = 8 };
        systemSettingsStack.Children.Add(WinUIStyles.CreateSectionTitle("System DNS settings"));
        systemSettingsStack.Children.Add(WinUIStyles.CreateText(
            "Windows DNS servers from active network adapters are used without modification."));
        _systemSettingsCard = WinUIStyles.CreateCard(systemSettingsStack);
        panel.Children.Add(_systemSettingsCard);

        var directSettingsStack = new StackPanel { Spacing = 8 };
        directSettingsStack.Children.Add(WinUIStyles.CreateSectionTitle("Direct DNS settings"));
        _dnsServerPresets =
        [
            new DnsServerPreset(_context.L("Original DNS destination"), string.Empty, string.Empty),
            new DnsServerPreset("Cloudflare", "1.1.1.1", "1.0.0.1"),
            new DnsServerPreset("Google Public DNS", "8.8.8.8", "8.8.4.4"),
            new DnsServerPreset("Quad9", "9.9.9.9", "149.112.112.112"),
            new DnsServerPreset("AdGuard DNS", "94.140.14.14", "94.140.15.15"),
            new DnsServerPreset("OpenDNS", "208.67.222.222", "208.67.220.220"),
            new DnsServerPreset(_context.L("Custom"), string.Empty, string.Empty, IsCustom: true),
        ];
        _directDnsProvider = new ComboBox
        {
            Header = "DNS provider",
            ItemsSource = _dnsServerPresets,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _directDnsProvider.SelectionChanged += OnDirectDnsProviderChanged;
        _context.SetToolTip(_directDnsProvider, "Choose a known DNS provider, keep the original DNS destination, or select Custom to enter primary and fallback addresses.");
        directSettingsStack.Children.Add(_directDnsProvider);

        _directDnsPrimaryServer = new TextBox
        {
            Header = "Primary DNS server",
            PlaceholderText = "1.1.1.1",
        };
        _directDnsFallbackServer = new TextBox
        {
            Header = "Fallback DNS server",
            PlaceholderText = "1.0.0.1",
        };
        _directDnsPrimaryServer.TextChanged += OnDirectDnsTextChanged;
        _directDnsFallbackServer.TextChanged += OnDirectDnsTextChanged;
        _directDnsPrimaryServer.LostFocus += OnDirectDnsServerLostFocus;
        _directDnsFallbackServer.LostFocus += OnDirectDnsServerLostFocus;
        _context.SetToolTip(_directDnsPrimaryServer, "Primary IPv4 or IPv6 DNS server. In Custom mode this address is used first.");
        _context.SetToolTip(_directDnsFallbackServer, "Fallback IPv4 or IPv6 DNS server. UDP queries are retried here when the primary resolver does not answer; TCP falls back when the primary connection fails.");
        directSettingsStack.Children.Add(_directDnsPrimaryServer);
        directSettingsStack.Children.Add(_directDnsFallbackServer);
        _directDnsRouteThroughShadowsocks = CreateToggle("Route Direct DNS through Shadowsocks");
        _directDnsRouteThroughShadowsocks.Toggled += OnDirectDnsRouteChanged;
        _context.SetToolTip(
            _directDnsRouteThroughShadowsocks,
            "Send queries to the selected Direct DNS primary/fallback servers through the active local Shadowsocks SOCKS5 proxy instead of connecting to them directly.");
        directSettingsStack.Children.Add(_directDnsRouteThroughShadowsocks);
        directSettingsStack.Children.Add(WinUIStyles.CreateText(
            "Administrator Mode redirects UDP/TCP DNS queries to the selected resolver. The fallback server is used only after the primary path fails or does not answer in time."));
        _directSettingsCard = WinUIStyles.CreateCard(directSettingsStack);
        _directSettingsCard.Visibility = Visibility.Collapsed;
        panel.Children.Add(_directSettingsCard);

        var proxySettingsStack = new StackPanel { Spacing = 8 };
        proxySettingsStack.Children.Add(WinUIStyles.CreateSectionTitle("DNS through Shadowsocks settings"));
        proxySettingsStack.Children.Add(WinUIStyles.CreateText(
            "Captured DNS queries use the active Shadowsocks route; no separate resolver endpoint is required."));
        _proxySettingsCard = WinUIStyles.CreateCard(proxySettingsStack);
        _proxySettingsCard.Visibility = Visibility.Collapsed;
        panel.Children.Add(_proxySettingsCard);

        var customDohSettingsStack = new StackPanel { Spacing = 10 };
        customDohSettingsStack.Children.Add(WinUIStyles.CreateSectionTitle("DNS-over-HTTPS settings"));
        _dohPresets =
        [
            new DohPreset(_context.L("Custom"), string.Empty),
            new DohPreset("Cloudflare", "https://cloudflare-dns.com/dns-query"),
            new DohPreset("Google Public DNS", "https://dns.google/dns-query"),
            new DohPreset(_context.L("Quad9 Secure"), "https://dns.quad9.net/dns-query"),
            new DohPreset(_context.L("Quad9 Unfiltered"), "https://dns10.quad9.net/dns-query"),
            new DohPreset("AdGuard DNS", "https://dns.adguard-dns.com/dns-query"),
            new DohPreset(_context.L("AdGuard DNS Unfiltered"), "https://unfiltered.adguard-dns.com/dns-query"),
            new DohPreset("Mullvad DNS", "https://dns.mullvad.net/dns-query"),
        ];
        _customDohProvider = new ComboBox
        {
            Header = "DoH provider",
            ItemsSource = _dohPresets,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _customDohProvider.SelectionChanged += OnCustomDohProviderChanged;
        _context.SetToolTip(_customDohProvider, "Choose a well-known RFC 8484 DNS-over-HTTPS provider or select Custom to enter your own HTTPS endpoint.");
        customDohSettingsStack.Children.Add(_customDohProvider);

        _customDohUrl = new TextBox
        {
            Header = "DoH URL",
            PlaceholderText = "https://dns.example/dns-query",
        };
        _customDohUrl.TextChanged += (_, _) =>
        {
            if (!_refreshing && !_updatingDohPreset)
            {
                _customDohDirty = true;
                SyncDohPresetFromUrl(_customDohUrl.Text);
            }
        };
        _customDohUrl.LostFocus += OnCustomDohUrlLostFocus;
        _context.SetToolTip(_customDohUrl, "Enter an absolute HTTPS DNS-over-HTTPS endpoint. Changes are applied automatically when you leave the field.");
        customDohSettingsStack.Children.Add(_customDohUrl);
        _customDohRouteThroughShadowsocks = CreateToggle("Route Custom DoH through Shadowsocks");
        _customDohRouteThroughShadowsocks.Toggled += OnCustomDohRouteChanged;
        _context.SetToolTip(_customDohRouteThroughShadowsocks, "Send the HTTPS connection to the Custom DoH provider through the local Shadowsocks SOCKS5 proxy instead of connecting directly.");
        customDohSettingsStack.Children.Add(_customDohRouteThroughShadowsocks);
        customDohSettingsStack.Children.Add(WinUIStyles.CreateText(
            "DNS queries are sent as DNS-over-HTTPS to the configured endpoint."));
        _customDohSettingsCard = WinUIStyles.CreateCard(customDohSettingsStack);
        _customDohSettingsCard.Visibility = Visibility.Collapsed;
        panel.Children.Add(_customDohSettingsCard);

        var progressStack = new StackPanel { Spacing = 8 };
        progressStack.Children.Add(WinUIStyles.CreateSectionTitle("DNSCrypt operation"));
        _progressText = WinUIStyles.CreateText("Ready");
        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            IsIndeterminate = false,
            Value = 0,
        };
        _cancelButton = new Button { Content = "Cancel", IsEnabled = false };
        _cancelButton.Click += (_, _) => _operationCancellation?.Cancel();
        progressStack.Children.Add(_progressText);
        progressStack.Children.Add(_progressBar);
        progressStack.Children.Add(_cancelButton);
        _context.SetToolTip(_cancelButton, "Cancel the current DNSCrypt install, update, validation, or runtime operation.");
        _dnsCryptOperationCard = WinUIStyles.CreateCard(progressStack);
        _dnsCryptOperationCard.Visibility = Visibility.Collapsed;
        panel.Children.Add(_dnsCryptOperationCard);

        var statusStack = new StackPanel { Spacing = 10 };
        statusStack.Children.Add(WinUIStyles.CreateSectionTitle("DNSCrypt Proxy"));
        statusStack.Children.Add(WinUIStyles.CreateKeyValueRow("Status", out _runtimeStatus));
        statusStack.Children.Add(WinUIStyles.CreateKeyValueRow("Installed version", out _installedVersion));
        statusStack.Children.Add(WinUIStyles.CreateKeyValueRow("Latest version", out _latestVersion));
        statusStack.Children.Add(WinUIStyles.CreateKeyValueRow("Local port", out _localPort));
        statusStack.Children.Add(WinUIStyles.CreateKeyValueRow("Coverage", out _coverage));
        statusStack.Children.Add(WinUIStyles.CreateSectionTitle("DNS privacy"));
        statusStack.Children.Add(WinUIStyles.CreateKeyValueRow("DNS interception", out _privacyDnsInterception));
        statusStack.Children.Add(WinUIStyles.CreateKeyValueRow("Upstream", out _privacyUpstream));
        statusStack.Children.Add(WinUIStyles.CreateKeyValueRow("Secure transport", out _privacyTransport));
        statusStack.Children.Add(WinUIStyles.CreateKeyValueRow("Plaintext DNS fallback", out _privacyFallback));
        statusStack.Children.Add(WinUIStyles.CreateKeyValueRow("Plaintext bootstrap", out _privacyBootstrap));
        _context.SetToolTip(_privacyBootstrap, "Active DNS runtime never uses plaintext bootstrap DNS. Explicit resolver-catalog refresh may use one-shot bootstrap DNS only before a signed catalog cache exists.");
        statusStack.Children.Add(WinUIStyles.CreateKeyValueRow("Shadowsocks route", out _privacyRoute));
        statusStack.Children.Add(WinUIStyles.CreateKeyValueRow("Privacy self-test", out _privacySelfTest));
        _privacyTestButton = CreateButton("Run privacy self-test", TestDnsPrivacyAsync);
        _context.SetToolTip(_privacyTestButton, "Verify the local DNSCrypt health check, Administrator DNS interception, fail-closed policy, system-DNS isolation and active runtime bootstrap configuration.");
        statusStack.Children.Add(_privacyTestButton);

        var buttonRows = new StackPanel { Spacing = 8 };
        var primaryButtonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var maintenanceButtonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _installButton = CreateButton("Install DNSCrypt Proxy", InstallAsync);
        _checkButton = CreateButton("Check for DNSCrypt updates", CheckUpdateAsync);
        _updateButton = CreateButton("Update", UpdateAsync);
        _restartButton = CreateButton("Restart", RestartAsync);
        _testButton = CreateButton("Test DNSCrypt", TestDnsCryptAsync);
        _reinstallButton = CreateButton("Reinstall", ReinstallAsync);
        _removeButton = CreateButton("Remove", RemoveAsync);
        _context.SetToolTip(_installButton, "Download, verify, and install the official Windows x64 DNSCrypt Proxy release.");
        _context.SetToolTip(_checkButton, "Check the official DNSCrypt Proxy release channel for a newer version.");
        _context.SetToolTip(_updateButton, "Install the newest verified DNSCrypt Proxy version and keep the previous version for rollback.");
        _context.SetToolTip(_restartButton, "Restart DNSCrypt Proxy with the currently saved settings.");
        _context.SetToolTip(_testButton, "Send a real DNS query through the local DNSCrypt listener and validate the response.");
        _context.SetToolTip(_reinstallButton, "Download and verify the current DNSCrypt Proxy version again, then replace the installed copy.");
        _context.SetToolTip(_removeButton, "Stop DNSCrypt Proxy, switch away from DNSCrypt mode, and remove the downloaded component.");
        primaryButtonRow.Children.Add(_installButton);
        primaryButtonRow.Children.Add(_checkButton);
        primaryButtonRow.Children.Add(_updateButton);
        maintenanceButtonRow.Children.Add(_restartButton);
        maintenanceButtonRow.Children.Add(_testButton);
        maintenanceButtonRow.Children.Add(_reinstallButton);
        maintenanceButtonRow.Children.Add(_removeButton);
        buttonRows.Children.Add(primaryButtonRow);
        buttonRows.Children.Add(maintenanceButtonRow);
        statusStack.Children.Add(buttonRows);
        _dnsCryptStatusCard = WinUIStyles.CreateCard(statusStack);
        _dnsCryptStatusCard.Visibility = Visibility.Collapsed;
        panel.Children.Add(_dnsCryptStatusCard);

        var settingsStack = new StackPanel { Spacing = 10 };
        settingsStack.Children.Add(WinUIStyles.CreateSectionTitle("DNSCrypt settings"));
        _autoUpdate = CreateToggle("Automatically update DNSCrypt Proxy");
        _requireDnssec = CreateToggle("Require DNSSEC");
        _requireNoLog = CreateToggle("Require no-log resolvers");
        _requireNoFilter = CreateToggle("Require unfiltered resolvers");
        _ipv6Servers = CreateToggle("Use IPv6 resolvers");
        _routeThroughShadowsocks = CreateToggle("Route DNSCrypt through Shadowsocks");
        _context.SetToolTip(_autoUpdate, "Automatically check for and install verified DNSCrypt Proxy updates in the background.");
        _context.SetToolTip(_requireDnssec, "Only use resolvers that advertise DNSSEC validation support.");
        _context.SetToolTip(_requireNoLog, "Only use resolvers whose catalog metadata states that they do not keep logs.");
        _context.SetToolTip(_requireNoFilter, "Only use resolvers whose catalog metadata states that they do not filter DNS answers.");
        _context.SetToolTip(_ipv6Servers, "Allow DNSCrypt Proxy to use resolver endpoints reachable over IPv6.");
        _context.SetToolTip(_routeThroughShadowsocks, "Send DNSCrypt Proxy upstream connections through the local Shadowsocks proxy. Server and forward-proxy endpoints must use IP addresses to avoid bootstrap recursion.");
        _autoUpdate.Toggled += OnDnsCryptSettingChanged;
        _requireDnssec.Toggled += OnDnsCryptSettingChanged;
        _requireNoLog.Toggled += OnDnsCryptSettingChanged;
        _requireNoFilter.Toggled += OnDnsCryptSettingChanged;
        _ipv6Servers.Toggled += OnDnsCryptSettingChanged;
        _routeThroughShadowsocks.Toggled += OnDnsCryptSettingChanged;
        settingsStack.Children.Add(_autoUpdate);
        settingsStack.Children.Add(_requireDnssec);
        settingsStack.Children.Add(_requireNoLog);
        settingsStack.Children.Add(_requireNoFilter);
        settingsStack.Children.Add(_ipv6Servers);
        settingsStack.Children.Add(_routeThroughShadowsocks);
        TextBlock bootstrapNote = WinUIStyles.CreateText(
            "When DNSCrypt is routed through Shadowsocks the Shadowsocks server and forward-proxy endpoints " +
            "must use IP addresses to prevent DNS bootstrap recursion.");
        bootstrapNote.Opacity = 0.72;
        settingsStack.Children.Add(bootstrapNote);
        TextBlock leakGuardNote = WinUIStyles.CreateText(
            "In Administrator Mode, DNSCrypt always blocks intercepted plaintext DNS while starting, restarting, or unavailable.");
        leakGuardNote.Opacity = 0.72;
        settingsStack.Children.Add(leakGuardNote);

        settingsStack.Children.Add(WinUIStyles.CreateSectionTitle("Resolver selection"));
        _resolverAutomatic = new RadioButton
        {
            Content = "Automatic",
            GroupName = "DnsCryptResolverSelection",
        };
        _resolverSelected = new RadioButton
        {
            Content = "Manual selection",
            GroupName = "DnsCryptResolverSelection",
        };
        _resolverAutomatic.Checked += OnResolverSelectionChanged;
        _resolverSelected.Checked += OnResolverSelectionChanged;
        _context.SetToolTip(_resolverAutomatic, "Automatically select a resolver from DNSCrypt Proxy's signed catalog using the DNSSEC, no-log, unfiltered and address-family settings.");
        _context.SetToolTip(_resolverSelected, "Choose one or more DNSCrypt or DoH resolvers manually from the signed upstream catalog.");
        settingsStack.Children.Add(_resolverAutomatic);
        settingsStack.Children.Add(_resolverSelected);

        _automaticResolverPanel = new StackPanel { Spacing = 8 };
        _automaticResolverPanel.Children.Add(WinUIStyles.CreateSectionTitle("Automatically selected resolvers"));
        _automaticResolverStatus = WinUIStyles.CreateText("Automatic mode selects a filtered resolver from DNSCrypt Proxy's signed catalog.");
        _automaticResolverStatus.Opacity = 0.72;
        _automaticResolverPanel.Children.Add(_automaticResolverStatus);
        _automaticResolverList = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            MinHeight = 44,
            MaxHeight = 150,
            IsItemClickEnabled = false,
        };
        _automaticResolverPanel.Children.Add(_automaticResolverList);
        settingsStack.Children.Add(_automaticResolverPanel);

        _manualResolverPanel = new StackPanel { Spacing = 8 };
        var resolverCatalogHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        resolverCatalogHeader.Children.Add(WinUIStyles.CreateSectionTitle("Available resolvers"));
        _loadResolversButton = CreateButton("Load resolver list", LoadResolversAsync);
        _context.SetToolTip(_loadResolversButton, "Reload resolver metadata from DNSCrypt Proxy's signed upstream catalog. Latency is measured separately in the background.");
        resolverCatalogHeader.Children.Add(_loadResolversButton);
        _manualResolverPanel.Children.Add(resolverCatalogHeader);
        _resolverSearchBox = new TextBox
        {
            PlaceholderText = "Search resolvers",
        };
        _resolverSearchBox.TextChanged += (_, _) => RebuildResolverList();
        _context.SetToolTip(_resolverSearchBox, "Filter resolvers by name, country code, country name, or description.");
        _manualResolverPanel.Children.Add(_resolverSearchBox);

        var resolverFilters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _resolverProtocolFilter = new ComboBox
        {
            Header = "Protocol",
            ItemsSource = new[]
            {
                new ResolverProtocolFilter(string.Empty, _context.L("All protocols")),
                new ResolverProtocolFilter("DNSCrypt", "DNSCrypt"),
                new ResolverProtocolFilter("DoH", "DoH"),
            },
            SelectedIndex = 0,
            MinWidth = 150,
        };
        _resolverCountryFilter = new ComboBox
        {
            Header = "Country",
            MinWidth = 180,
        };
        _resolverAddressFamilyFilter = new ComboBox
        {
            Header = "Address family",
            ItemsSource = new[] { _context.L("Any address family"), "IPv4", "IPv6" },
            SelectedIndex = 0,
            MinWidth = 150,
        };
        _resolverProtocolFilter.SelectionChanged += (_, _) =>
        {
            RefreshResolverCountryFilter();
            RebuildResolverList();
            StartResolverLatencyRefresh();
        };
        _resolverCountryFilter.SelectionChanged += (_, _) => RebuildResolverList();
        _resolverAddressFamilyFilter.SelectionChanged += (_, _) => RebuildResolverList();
        _context.SetToolTip(_resolverProtocolFilter, "Filter the signed resolver catalog by transport protocol: DNSCrypt or DNS-over-HTTPS.");
        _context.SetToolTip(_resolverCountryFilter, "Show resolvers from a selected country. Country is resolved from the resolver endpoint IP.");
        _context.SetToolTip(_resolverAddressFamilyFilter, "Filter resolver endpoints by IPv4 or IPv6.");
        resolverFilters.Children.Add(_resolverProtocolFilter);
        resolverFilters.Children.Add(_resolverCountryFilter);
        resolverFilters.Children.Add(_resolverAddressFamilyFilter);
        _manualResolverPanel.Children.Add(resolverFilters);

        var resolverPropertyFilters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        _resolverDnssecFilter = CreateToggle("DNSSEC only");
        _resolverNoLogFilter = CreateToggle("No-log only");
        _resolverNoFilterFilter = CreateToggle("Unfiltered only");
        _resolverDnssecFilter.Toggled += (_, _) => RebuildResolverList();
        _resolverNoLogFilter.Toggled += (_, _) => RebuildResolverList();
        _resolverNoFilterFilter.Toggled += (_, _) => RebuildResolverList();
        _context.SetToolTip(_resolverDnssecFilter, "Only show resolvers that advertise DNSSEC validation.");
        _context.SetToolTip(_resolverNoLogFilter, "Only show resolvers whose catalog metadata declares no logging.");
        _context.SetToolTip(_resolverNoFilterFilter, "Only show resolvers that do not filter DNS answers.");
        resolverPropertyFilters.Children.Add(_resolverDnssecFilter);
        resolverPropertyFilters.Children.Add(_resolverNoLogFilter);
        resolverPropertyFilters.Children.Add(_resolverNoFilterFilter);
        _manualResolverPanel.Children.Add(resolverPropertyFilters);

        RefreshResolverCountryFilter();
        _resolverListStatus = WinUIStyles.CreateText("Resolver list is loaded from DNSCrypt Proxy's signed upstream catalog. Latency is measured in the background; the active resolver uses DNSCrypt Proxy's actual RTT when available.");
        _resolverListStatus.Opacity = 0.72;
        _manualResolverPanel.Children.Add(_resolverListStatus);
        _resolverList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            MinHeight = 140,
            MaxHeight = 300,
        };
        _resolverList.SelectionChanged += OnResolverListSelectionChanged;
        _context.SetToolTip(_resolverList, "Select the DNSCrypt or DoH resolvers DNSCrypt Proxy may use in manual-selection mode. Ping is measured asynchronously; the active resolver is replaced with DNSCrypt Proxy's actual RTT when available.");
        _manualResolverPanel.Children.Add(_resolverList);
        settingsStack.Children.Add(_manualResolverPanel);

        TextBlock automaticApplyNote = WinUIStyles.CreateText("DNS settings are saved and applied automatically.");
        automaticApplyNote.Opacity = 0.72;
        settingsStack.Children.Add(automaticApplyNote);
        _dnsCryptSettingsCard = WinUIStyles.CreateCard(settingsStack);
        _dnsCryptSettingsCard.Visibility = Visibility.Collapsed;
        panel.Children.Add(_dnsCryptSettingsCard);

        WinUILocalization.Apply(Content, _context.Localization);
        Refresh();
    }

    public void Refresh()
    {
        if (_context.Controller is null)
            return;

        DnsCryptManagementStatus status;
        try
        {
            status = _context.Controller.GetDnsCryptManagementStatus();
        }
        catch (Exception exception)
        {
            _runtimeStatus.Text = _context.LF("Failed: {0}", exception.Message);
            return;
        }

        _refreshing = true;
        try
        {
            TrafficCaptureStatus traffic = _context.Controller.GetTrafficCaptureStatus();
            DnsPolicyConfig policy = _context.Controller.GetCurrentConfiguration().dnsPolicy ?? new DnsPolicyConfig();
            DnsCryptConfig config = policy.dnsCrypt ?? new DnsCryptConfig();
            if (!_directDnsDirty)
            {
                _directDnsPrimaryServer.Text = policy.directDnsServer ?? string.Empty;
                _directDnsFallbackServer.Text = policy.directDnsFallbackServer ?? string.Empty;
                _directDnsRouteThroughShadowsocks.IsOn = policy.directDnsRouteThroughShadowsocks;
                SyncDirectDnsPresetFromAddresses(_directDnsPrimaryServer.Text, _directDnsFallbackServer.Text);
            }
            if (!_customDohDirty)
            {
                _customDohUrl.Text = policy.customDohUrl ?? string.Empty;
                _customDohRouteThroughShadowsocks.IsOn = policy.customDohRouteThroughShadowsocks;
                SyncDohPresetFromUrl(_customDohUrl.Text);
            }

            ApplyDnsSystemState(status.Mode, traffic);
            ApplyModeState(status.Mode);
            ApplyModeSpecificVisibility(status.Mode);
            ApplyRuntimeState(status, traffic, policy);
            ApplyConfigurationState(config);
            MergeActiveResolverData(status);
            ApplyResolverPresentation(status);
            ApplyControlState(status, traffic);
        }
        finally
        {
            _refreshing = false;
        }
    }


    private void ApplyDnsSystemState(DnsPolicyMode mode, TrafficCaptureStatus traffic)
    {
        SystemDnsSnapshot snapshot = GetSystemDnsSnapshot();
        _systemDnsAdapters.Text = snapshot.Adapters.Count > 0
            ? string.Join(", ", snapshot.Adapters)
            : _context.L("No active network adapters detected.");
        _systemDnsServers.Text = snapshot.Servers.Count > 0
            ? string.Join(", ", snapshot.Servers)
            : _context.L("No active DNS servers detected.");
        _effectiveDnsMode.Text = _context.L(mode switch
        {
            DnsPolicyMode.Direct => "Direct DNS",
            DnsPolicyMode.Proxy => "DNS through Shadowsocks",
            DnsPolicyMode.CustomDoh => "Custom DoH",
            DnsPolicyMode.DnsCrypt => "DNSCrypt",
            _ => "System DNS",
        });
        _dnsInterceptionStatus.Text = traffic.DnsInterceptionActive
            ? _context.L("Active")
            : traffic.RuntimeMode switch
            {
                TrafficRuntimeMode.User => _context.L("Unavailable in User Mode"),
                TrafficRuntimeMode.Game => _context.L("Paused in Game Mode"),
                _ => _context.L("Inactive"),
            };
    }

    private static SystemDnsSnapshot GetSystemDnsSnapshot()
    {
        try
        {
            var adapters = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
            var servers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up
                    || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                try
                {
                    var dnsAddresses = networkInterface.GetIPProperties().DnsAddresses;
                    if (dnsAddresses.Count == 0)
                        continue;

                    adapters.Add(networkInterface.Name);
                    foreach (var address in dnsAddresses)
                        servers.Add(address.ToString());
                }
                catch (NetworkInformationException)
                {
                    // Ignore an adapter that disappears while Windows network state is being enumerated.
                }
            }

            return new SystemDnsSnapshot(adapters.ToArray(), servers.ToArray());
        }
        catch (NetworkInformationException)
        {
            return new SystemDnsSnapshot(Array.Empty<string>(), Array.Empty<string>());
        }
    }

    private void ApplyModeSpecificVisibility(DnsPolicyMode mode)
    {
        _systemSettingsCard.Visibility = mode == DnsPolicyMode.System ? Visibility.Visible : Visibility.Collapsed;
        _directSettingsCard.Visibility = mode == DnsPolicyMode.Direct ? Visibility.Visible : Visibility.Collapsed;
        _proxySettingsCard.Visibility = mode == DnsPolicyMode.Proxy ? Visibility.Visible : Visibility.Collapsed;
        _customDohSettingsCard.Visibility = mode == DnsPolicyMode.CustomDoh ? Visibility.Visible : Visibility.Collapsed;

        Visibility dnsCryptVisibility = mode == DnsPolicyMode.DnsCrypt
            ? Visibility.Visible
            : Visibility.Collapsed;
        _dnsCryptOperationCard.Visibility = dnsCryptVisibility;
        _dnsCryptStatusCard.Visibility = dnsCryptVisibility;
        _dnsCryptSettingsCard.Visibility = dnsCryptVisibility;
    }

    private void ApplyModeState(DnsPolicyMode mode)
    {
        _systemMode.IsChecked = mode == DnsPolicyMode.System;
        _directMode.IsChecked = mode == DnsPolicyMode.Direct;
        _proxyMode.IsChecked = mode == DnsPolicyMode.Proxy;
        _customDohMode.IsChecked = mode == DnsPolicyMode.CustomDoh;
        _dnsCryptMode.IsChecked = mode == DnsPolicyMode.DnsCrypt;
    }

    private void ApplyRuntimeState(DnsCryptManagementStatus status, TrafficCaptureStatus traffic, DnsPolicyConfig policy)
    {
        _runtimeStatus.Text = status.Runtime.State switch
        {
            DnsCryptRuntimeState.NotInstalled => _context.L("Not installed"),
            DnsCryptRuntimeState.Stopped => _context.L("Stopped"),
            DnsCryptRuntimeState.Starting => _context.L("Starting"),
            DnsCryptRuntimeState.Running => _context.L("Running"),
            DnsCryptRuntimeState.Updating => _context.L("Updating"),
            _ => _context.L("Failed"),
        };
        _installedVersion.Text = status.Component.IsInstalled
            ? status.Component.ActiveVersion?.ToString() ?? "—"
            : _context.L("Not installed");
        _latestVersion.Text = status.LatestVersion?.ToString() ?? "—";
        _localPort.Text = status.Runtime.IsServing && status.Runtime.Port > 0
            ? $"127.0.0.1:{status.Runtime.Port}"
            : "—";
        _coverage.Text = status.Mode switch
        {
            DnsPolicyMode.System => _context.L("System DNS is unchanged."),
            DnsPolicyMode.Direct => traffic.RuntimeMode == TrafficRuntimeMode.Admin
                ? policy.directDnsRouteThroughShadowsocks
                    ? _context.L("Direct DNS queries are routed to the selected resolver through Shadowsocks.")
                    : _context.L("DNS is forced direct and bypasses Shadowsocks routing.")
                : _context.L("Direct DNS is selected; User Mode does not intercept system DNS."),
            DnsPolicyMode.Proxy => traffic.RuntimeMode == TrafficRuntimeMode.Admin && traffic.DnsInterceptionActive
                ? _context.L("DNS traffic is routed through Shadowsocks for captured applications.")
                : _context.L("DNS through Shadowsocks requires Administrator Mode for transparent system-wide interception."),
            DnsPolicyMode.CustomDoh => traffic.RuntimeMode == TrafficRuntimeMode.Admin && traffic.DnsInterceptionActive
                ? _context.L("Custom DoH is active for captured applications.")
                : traffic.RuntimeMode == TrafficRuntimeMode.Admin && traffic.DnsFailClosedActive
                    ? _context.L("DNS is blocked because the Custom DoH endpoint is unavailable or invalid.")
                    : _context.L("Custom DoH requires Administrator Mode for transparent system-wide interception."),
            DnsPolicyMode.DnsCrypt => traffic.RuntimeMode == TrafficRuntimeMode.Admin && traffic.DnsInterceptionActive
                ? _context.L("Transparent DNS interception is active for captured applications.")
                : traffic.RuntimeMode == TrafficRuntimeMode.Admin && traffic.DnsFailClosedActive
                    ? _context.L("DNS is blocked (fail-closed) because DNSCrypt is unavailable.")
                    : traffic.RuntimeMode == TrafficRuntimeMode.Game
                        ? status.Runtime.IsServing
                            ? _context.L("DNSCrypt is running, but system-wide interception is paused in Game Mode.")
                            : _context.L("System-wide DNS interception is paused in Game Mode.")
                        : status.Runtime.IsServing
                            ? _context.L("DNSCrypt is running in User Mode; system-wide interception requires Administrator Mode.")
                            : _context.L("DNSCrypt is selected but its runtime is unavailable."),
            _ => _context.L("System DNS is unchanged."),
        };

        DnsCryptConfig dnsCryptConfig = policy.dnsCrypt ?? new DnsCryptConfig();
        IReadOnlyList<DnsCryptResolverInfo> activeResolvers = status.ActiveResolvers ?? Array.Empty<DnsCryptResolverInfo>();
        _privacyDnsInterception.Text = traffic.RuntimeMode == TrafficRuntimeMode.Admin
            ? traffic.DnsInterceptionActive
                ? _context.L("Active")
                : traffic.DnsFailClosedActive
                    ? _context.L("Blocked")
                    : _context.L("Inactive")
            : _context.L("Administrator Mode required");
        _privacyUpstream.Text = activeResolvers.Count > 0
            ? string.Join(", ", activeResolvers.Select(resolver => resolver.Name))
            : "—";
        string[] transports = activeResolvers
            .Select(resolver => resolver.Protocol?.Trim())
            .OfType<string>()
            .Where(protocol => !string.IsNullOrWhiteSpace(protocol))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _privacyTransport.Text = transports.Length > 0 ? string.Join(", ", transports) : "—";
        _privacyFallback.Text = dnsCryptConfig.failClosed ? _context.L("Blocked") : _context.L("Allowed");
        _privacyBootstrap.Text = status.Mode == DnsPolicyMode.DnsCrypt
            ? _context.L("Disabled in DNS runtime")
            : "—";
        _privacyRoute.Text = dnsCryptConfig.routeThroughShadowsocks
            ? _context.L("Through Shadowsocks")
            : _context.L("Direct from DNSCrypt Proxy");
        _privacySelfTest.Text = _privacySelfTestMessage ?? _context.L("Not run");

        if (!_busy && !string.IsNullOrWhiteSpace(status.LastMaintenanceError))
        {
            _progressText.Text = _context.LF("Automatic DNSCrypt update failed: {0}", status.LastMaintenanceError);
        }
    }

    private void ApplyConfigurationState(DnsCryptConfig config)
    {
        if (_settingsDirty)
            return;

        _autoUpdate.IsOn = config.autoUpdate;
        _requireDnssec.IsOn = config.requireDnssec;
        _requireNoLog.IsOn = config.requireNoLog;
        _requireNoFilter.IsOn = config.requireNoFilter;
        _ipv6Servers.IsOn = config.ipv6Servers;
        _routeThroughShadowsocks.IsOn = config.routeThroughShadowsocks;

        bool automaticResolvers = config.automaticResolvers;
        _resolverAutomatic.IsChecked = automaticResolvers;
        _resolverSelected.IsChecked = !automaticResolvers;
        _manualResolverNames.Clear();
        foreach (string name in config.serverNames ?? [])
        {
            if (!string.IsNullOrWhiteSpace(name))
                _manualResolverNames.Add(name.Trim());
        }
        EnsureConfiguredResolversVisible(_manualResolverNames);
        RebuildResolverList();
    }

    private void MergeActiveResolverData(DnsCryptManagementStatus status)
    {
        IReadOnlyList<DnsCryptResolverInfo> active = status.ActiveResolvers ?? Array.Empty<DnsCryptResolverInfo>();
        if (active.Count == 0)
            return;

        var activeByName = active
            .Where(resolver => !string.IsNullOrWhiteSpace(resolver.Name))
            .GroupBy(resolver => resolver.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        if (_resolverCatalog.Count == 0)
        {
            _resolverCatalog = active.ToArray();
            return;
        }

        _resolverCatalog = _resolverCatalog
            .Select(resolver => activeByName.TryGetValue(resolver.Name, out DnsCryptResolverInfo? activeResolver)
                ? resolver with
                {
                    Protocol = string.IsNullOrWhiteSpace(activeResolver.Protocol) ? resolver.Protocol : activeResolver.Protocol,
                    CountryCode = string.IsNullOrWhiteSpace(activeResolver.CountryCode) ? resolver.CountryCode : activeResolver.CountryCode,
                    CountryName = string.IsNullOrWhiteSpace(activeResolver.CountryName) ? resolver.CountryName : activeResolver.CountryName,
                    FlagEmoji = string.IsNullOrWhiteSpace(activeResolver.FlagEmoji) ? resolver.FlagEmoji : activeResolver.FlagEmoji,
                    LatencyMs = activeResolver.LatencyMs ?? resolver.LatencyMs,
                }
                : resolver)
            .ToArray();
    }

    private void ApplyResolverPresentation(DnsCryptManagementStatus status)
    {
        _automaticResolverList.Items.Clear();
        bool pendingAutomaticApply = _settingsDirty
            && _resolverAutomatic.IsChecked == true
            && !status.AutomaticResolvers;
        IReadOnlyList<DnsCryptResolverInfo> active = pendingAutomaticApply
            ? Array.Empty<DnsCryptResolverInfo>()
            : status.ActiveResolvers ?? Array.Empty<DnsCryptResolverInfo>();
        foreach (DnsCryptResolverInfo resolver in active)
            _automaticResolverList.Items.Add(new ResolverChoice(resolver));

        bool metadataMissing = active.Any(resolver =>
            string.IsNullOrWhiteSpace(resolver.Protocol)
            || (string.IsNullOrWhiteSpace(resolver.CountryCode) && string.IsNullOrWhiteSpace(resolver.CountryName)));
        if (status.AutomaticResolvers
            && status.Component.IsInstalled
            && active.Count > 0
            && metadataMissing
            && !_resolverCatalogLoaded
            && !_resolverCatalogLoading)
        {
            _ = LoadResolversInBackgroundAsync();
        }

        if (pendingAutomaticApply)
        {
            _automaticResolverStatus.Text = _context.L("Applying DNS settings");
        }
        else if (active.Count > 0 && status.Runtime.IsServing)
        {
            _automaticResolverStatus.Text = _context.LF(
                "Automatic filtered selection is active ({0} resolver(s)).",
                active.Count);
        }
        else if (status.AutomaticResolvers && active.Count > 0)
        {
            _automaticResolverStatus.Text = _context.L(
                "Automatic filtered resolver is starting.");
        }
        else
        {
            _automaticResolverStatus.Text = _context.L(
                "Automatic mode selects a resolver from the signed catalog using the DNSSEC, no-log, unfiltered and address-family settings above.");
        }

        UpdateResolverSelectionVisibility();
    }

    private void ApplyControlState(DnsCryptManagementStatus status, TrafficCaptureStatus traffic)
    {
        bool installed = status.Component.IsInstalled;
        bool dnsCryptSelected = status.Mode == DnsPolicyMode.DnsCrypt;

        _installButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        _installButton.IsEnabled = !installed && !_busy;
        _checkButton.IsEnabled = installed && !_busy;
        _updateButton.IsEnabled = installed && status.UpdateAvailable && !_busy;
        _restartButton.IsEnabled = installed
            && dnsCryptSelected
            && status.Runtime.State is not DnsCryptRuntimeState.Starting and not DnsCryptRuntimeState.Updating
            && !_busy;
        _testButton.IsEnabled = installed && status.Runtime.IsServing && !_busy;
        _privacyTestButton.IsEnabled = installed && dnsCryptSelected && !_busy;
        _reinstallButton.IsEnabled = installed && !_busy;
        _removeButton.IsEnabled = installed && !_busy;
        _dnsCryptMode.IsEnabled = !_busy;
        _systemMode.IsEnabled = !_busy;
        _directMode.IsEnabled = !_busy;
        _proxyMode.IsEnabled = !_busy;
        _customDohMode.IsEnabled = !_busy;
        _directDnsProvider.IsEnabled = !_busy;
        _directDnsPrimaryServer.IsEnabled = !_busy;
        _directDnsFallbackServer.IsEnabled = !_busy;
        _directDnsRouteThroughShadowsocks.IsEnabled = !_busy;
        _customDohUrl.IsEnabled = !_busy;
        _customDohRouteThroughShadowsocks.IsEnabled = !_busy;
        _autoUpdate.IsEnabled = !_busy;
        _requireDnssec.IsEnabled = !_busy;
        _requireNoLog.IsEnabled = !_busy;
        _requireNoFilter.IsEnabled = !_busy;
        _ipv6Servers.IsEnabled = !_busy;
        _routeThroughShadowsocks.IsEnabled = !_busy;
        _resolverAutomatic.IsEnabled = !_busy;
        _resolverSelected.IsEnabled = !_busy;
        _loadResolversButton.IsEnabled = installed && !_busy;
        _resolverSearchBox.IsEnabled = !_busy;
        _resolverProtocolFilter.IsEnabled = !_busy;
        _resolverCountryFilter.IsEnabled = !_busy;
        _resolverAddressFamilyFilter.IsEnabled = !_busy;
        _resolverDnssecFilter.IsEnabled = !_busy;
        _resolverNoLogFilter.IsEnabled = !_busy;
        _resolverNoFilterFilter.IsEnabled = !_busy;
        _resolverList.IsEnabled = !_busy && _resolverSelected.IsChecked == true;
        UpdateResolverSelectionVisibility();
    }

    private static RadioButton CreateMode(string text) => new()
    {
        Content = text,
        GroupName = "DnsMode",
    };

    private Button CreateButton(string text, Func<Task> action)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) => await action();
        return button;
    }

    private static ToggleSwitch CreateToggle(string header) => new()
    {
        Header = header,
    };

    private void OnResolverSelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateResolverSelectionVisibility();
        if (_refreshing || _busy)
            return;

        _settingsDirty = true;
        _resolverList.IsEnabled = _resolverSelected.IsChecked == true;
        if (_resolverSelected.IsChecked == true)
        {
            if (!_resolverCatalogLoaded)
                _ = LoadResolversInBackgroundAsync();
            else
                StartResolverLatencyRefresh();

            if (_manualResolverNames.Count == 0)
            {
                _resolverListStatus.Text = _context.L("Select at least one resolver.");
                return;
            }
        }
        ScheduleDnsCryptSettingsApply();
    }

    private void OnDnsCryptSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _busy)
            return;

        _settingsDirty = true;
        ScheduleDnsCryptSettingsApply();
    }

    private void OnResolverListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || _updatingResolverList || _busy)
            return;

        foreach (object item in e.AddedItems)
        {
            if (item is ResolverChoice choice)
                _manualResolverNames.Add(choice.Name);
        }
        foreach (object item in e.RemovedItems)
        {
            if (item is ResolverChoice choice)
                _manualResolverNames.Remove(choice.Name);
        }

        _settingsDirty = true;
        if (_manualResolverNames.Count == 0)
        {
            _resolverListStatus.Text = _context.L("Select at least one resolver.");
            return;
        }
        ScheduleDnsCryptSettingsApply();
    }

    private void UpdateResolverSelectionVisibility()
    {
        bool manual = _resolverSelected.IsChecked == true;
        _manualResolverPanel.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        _automaticResolverPanel.Visibility = manual ? Visibility.Collapsed : Visibility.Visible;
        if (!manual)
        {
            _resolverLatencyCancellation?.Cancel();
            _resolverLatencyCancellation?.Dispose();
            _resolverLatencyCancellation = null;
        }
    }


    private void OnDirectDnsTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_refreshing || _updatingDirectDnsPreset)
            return;

        _directDnsDirty = true;
        SyncDirectDnsPresetFromAddresses(_directDnsPrimaryServer.Text, _directDnsFallbackServer.Text);
    }

    private void SyncDirectDnsPresetFromAddresses(string primary, string fallback)
    {
        string normalizedPrimary = NormalizeDnsAddressForComparison(primary);
        string normalizedFallback = NormalizeDnsAddressForComparison(fallback);
        DnsServerPreset? match = _dnsServerPresets.FirstOrDefault(preset =>
            !preset.IsCustom
            && string.Equals(NormalizeDnsAddressForComparison(preset.Primary), normalizedPrimary, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeDnsAddressForComparison(preset.Fallback), normalizedFallback, StringComparison.OrdinalIgnoreCase));
        DnsServerPreset selected = match ?? _dnsServerPresets.First(static preset => preset.IsCustom);

        _updatingDirectDnsPreset = true;
        try
        {
            _directDnsProvider.SelectedItem = selected;
            _directDnsPrimaryServer.IsReadOnly = !selected.IsCustom;
            _directDnsFallbackServer.IsReadOnly = !selected.IsCustom;
        }
        finally
        {
            _updatingDirectDnsPreset = false;
        }
    }

    private async void OnDirectDnsProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || _updatingDirectDnsPreset || _busy || _context.Controller is null
            || _directDnsProvider.SelectedItem is not DnsServerPreset preset)
        {
            return;
        }

        _updatingDirectDnsPreset = true;
        try
        {
            _directDnsPrimaryServer.IsReadOnly = !preset.IsCustom;
            _directDnsFallbackServer.IsReadOnly = !preset.IsCustom;
            if (preset.IsCustom)
            {
                _directDnsDirty = true;
                _directDnsPrimaryServer.Focus(FocusState.Programmatic);
                return;
            }

            _directDnsPrimaryServer.Text = preset.Primary;
            _directDnsFallbackServer.Text = preset.Fallback;
            _directDnsDirty = _directMode.IsChecked == true;
        }
        finally
        {
            _updatingDirectDnsPreset = false;
        }

        if (_directMode.IsChecked == true)
            await ApplyDirectDnsServersAsync(preset.Primary, preset.Fallback);
    }

    private static string NormalizeDnsAddressForComparison(string value)
        => value?.Trim().TrimStart('[').TrimEnd(']') ?? string.Empty;

    private void SyncDohPresetFromUrl(string url)
    {
        string normalized = url?.Trim() ?? string.Empty;
        DohPreset? match = _dohPresets.FirstOrDefault(preset =>
            !preset.IsCustom && string.Equals(preset.Url, normalized, StringComparison.OrdinalIgnoreCase));
        DohPreset selected = match ?? _dohPresets.First(static preset => preset.IsCustom);

        _updatingDohPreset = true;
        try
        {
            _customDohProvider.SelectedItem = selected;
            _customDohUrl.IsReadOnly = !selected.IsCustom;
        }
        finally
        {
            _updatingDohPreset = false;
        }
    }

    private async void OnCustomDohProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || _updatingDohPreset || _busy || _context.Controller is null
            || _customDohProvider.SelectedItem is not DohPreset preset)
        {
            return;
        }

        _updatingDohPreset = true;
        try
        {
            _customDohUrl.IsReadOnly = !preset.IsCustom;
            if (preset.IsCustom)
            {
                _customDohDirty = true;
                _customDohUrl.Focus(FocusState.Programmatic);
                return;
            }

            _customDohUrl.Text = preset.Url;
            _customDohDirty = _customDohMode.IsChecked == true;
        }
        finally
        {
            _updatingDohPreset = false;
        }

        if (_customDohMode.IsChecked == true)
        {
            await ApplyCustomDohUrlAsync(preset.Url);
        }
    }

    private async Task ApplyCustomDohUrlAsync(string url)
    {
        if (_context.Controller is null)
            return;

        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            _customDohDirty = true;
            _context.ShowInfo("DNS", "Enter a valid HTTPS DoH URL to enable Custom DoH.", InfoBarSeverity.Warning);
            return;
        }

        bool routeThroughShadowsocks = _customDohRouteThroughShadowsocks.IsOn;
        await RunOperationAsync(async cancellationToken =>
        {
            await _context.Controller.SetCustomDohPolicyAsync(
                uri.AbsoluteUri,
                routeThroughShadowsocks,
                cancellationToken);
            _customDohDirty = false;
            SetProgressText("Custom DoH settings applied.");
        });
    }

    private async void OnCustomDohRouteChanged(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _busy || _customDohMode.IsChecked != true || _context.Controller is null)
            return;
        _customDohDirty = true;
        await ApplyCustomDohUrlAsync(_customDohUrl.Text);
    }

    private async void OnDirectDnsRouteChanged(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _busy || _directMode.IsChecked != true || _context.Controller is null)
            return;

        _directDnsDirty = true;
        await ApplyDirectDnsServersAsync(_directDnsPrimaryServer.Text, _directDnsFallbackServer.Text);
    }

    private async void OnDirectDnsServerLostFocus(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _busy || !_directDnsDirty || _directMode.IsChecked != true || _context.Controller is null)
            return;
        await ApplyDirectDnsServersAsync(_directDnsPrimaryServer.Text, _directDnsFallbackServer.Text);
    }

    private async Task ApplyDirectDnsServersAsync(string primaryValue, string fallbackValue)
    {
        if (_context.Controller is null)
            return;

        string primary = NormalizeDnsAddressForComparison(primaryValue);
        string fallback = NormalizeDnsAddressForComparison(fallbackValue);
        if (primary.Length > 0 && !IPAddress.TryParse(primary, out _))
        {
            _directDnsDirty = true;
            _context.ShowInfo("DNS", "Enter a valid primary IPv4 or IPv6 DNS server address.", InfoBarSeverity.Warning);
            return;
        }
        if (fallback.Length > 0 && !IPAddress.TryParse(fallback, out _))
        {
            _directDnsDirty = true;
            _context.ShowInfo("DNS", "Enter a valid fallback IPv4 or IPv6 DNS server address.", InfoBarSeverity.Warning);
            return;
        }
        if (primary.Length == 0 && fallback.Length > 0)
        {
            _directDnsDirty = true;
            _context.ShowInfo("DNS", "Enter a primary DNS server before configuring a fallback server.", InfoBarSeverity.Warning);
            return;
        }

        bool routeThroughShadowsocks = _directDnsRouteThroughShadowsocks.IsOn;
        await RunOperationAsync(async cancellationToken =>
        {
            await _context.Controller.SetDirectDnsPolicyAsync(primary, fallback, routeThroughShadowsocks, cancellationToken);
            _directDnsDirty = false;
            SetProgressText(primary.Length == 0 ? "Direct DNS enabled." : "DNS server settings applied.");
        });
    }

    private async void OnCustomDohUrlLostFocus(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _busy || !_customDohDirty || _customDohMode.IsChecked != true || _context.Controller is null)
            return;

        await ApplyCustomDohUrlAsync(_customDohUrl.Text);
    }

    private async void OnSystemModeChecked(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _busy || _context.Controller is null)
            return;
        await RunOperationAsync(async cancellationToken =>
        {
            await _context.Controller.SetDnsPolicyAsync(DnsPolicyMode.System, cancellationToken);
            SetProgressText("System DNS enabled.");
        });
    }

    private async void OnDirectModeChecked(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _busy || _context.Controller is null)
            return;
        ApplyModeSpecificVisibility(DnsPolicyMode.Direct);
        await ApplyDirectDnsServersAsync(_directDnsPrimaryServer.Text, _directDnsFallbackServer.Text);
    }

    private async void OnProxyModeChecked(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _busy || _context.Controller is null)
            return;
        await RunOperationAsync(async cancellationToken =>
        {
            await _context.Controller.SetDnsPolicyAsync(DnsPolicyMode.Proxy, cancellationToken);
            SetProgressText("DNS through Shadowsocks enabled.");
        });
    }

    private async void OnCustomDohModeChecked(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _busy || _context.Controller is null)
            return;

        ApplyModeSpecificVisibility(DnsPolicyMode.CustomDoh);
        if (!Uri.TryCreate(_customDohUrl.Text?.Trim(), UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            _customDohDirty = true;
            _context.ShowInfo("DNS", "Enter a valid HTTPS DoH URL to enable Custom DoH.", InfoBarSeverity.Warning);
            _customDohUrl.Focus(FocusState.Programmatic);
            return;
        }

        await ApplyCustomDohUrlAsync(uri.AbsoluteUri);
    }

    private async void OnDnsCryptModeChecked(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _busy || _context.Controller is null)
            return;
        await RequestEnableDnsCryptAsync();
    }

    internal async Task RequestEnableDnsCryptAsync()
    {
        if (_busy || _context.Controller is null)
            return;

        DnsCryptManagementStatus status = _context.Controller.GetDnsCryptManagementStatus();
        if (!status.Component.IsInstalled)
        {
            ContentDialogResult result = await ShowInstallDialogAsync();
            if (result != ContentDialogResult.Primary)
            {
                Refresh();
                return;
            }
        }

        await RunOperationAsync(async cancellationToken =>
        {
            if (!_context.Controller.GetDnsCryptManagementStatus().Component.IsInstalled)
                await _context.Controller.InstallDnsCryptAsync(CreateProgress(), cancellationToken);
            await _context.Controller.SetDnsPolicyAsync(DnsPolicyMode.DnsCrypt, cancellationToken);
            SetProgressText("DNSCrypt mode selected; runtime is running.");
        });
    }

    private Task InstallAsync() => RunOperationAsync(async cancellationToken =>
    {
        await _context.Controller!.InstallDnsCryptAsync(CreateProgress(), cancellationToken);
        SetProgressText("DNSCrypt Proxy installed.");
    });

    private Task CheckUpdateAsync() => RunOperationAsync(async cancellationToken =>
    {
        SetProgressText("Checking latest version");
        DnsCryptReleaseInfo release = await _context.Controller!.CheckDnsCryptUpdateAsync(cancellationToken);
        DnsCryptManagementStatus status = _context.Controller.GetDnsCryptManagementStatus();
        SetProgressText(status.UpdateAvailable
            ? _context.LF("DNSCrypt Proxy {0} is available.", release.Version)
            : _context.L("DNSCrypt Proxy is up to date."));
    });

    private Task UpdateAsync() => RunOperationAsync(async cancellationToken =>
    {
        await _context.Controller!.UpdateDnsCryptAsync(CreateProgress(), cancellationToken);
        SetProgressText("DNSCrypt Proxy updated.");
    });

    private Task RestartAsync() => RunOperationAsync(async cancellationToken =>
    {
        SetProgressText("Starting DNSCrypt");
        await _context.Controller!.RestartDnsCryptAsync(cancellationToken);
        SetProgressText("DNSCrypt Proxy restarted.");
    });

    private Task TestDnsCryptAsync() => RunOperationAsync(async cancellationToken =>
    {
        SetProgressText("Testing DNSCrypt");
        bool healthy = await _context.Controller!.TestDnsCryptAsync(cancellationToken);
        if (!healthy)
            throw new InvalidOperationException(_context.L("DNSCrypt test failed."));
        SetProgressText("DNSCrypt test passed.");
    });

    private Task TestDnsPrivacyAsync() => RunOperationAsync(async cancellationToken =>
    {
        SetProgressText("Testing DNS privacy");
        DnsPrivacySelfTestResult result = await _context.Controller!.TestDnsPrivacyAsync(cancellationToken);
        if (result.Passed)
        {
            _privacySelfTestMessage = _context.L("Passed");
            SetProgressText("DNS privacy self-test passed.");
            _context.ShowInfo(
                "DNS privacy",
                _context.LF("Protected DNS path verified: {0} over {1}.", result.Upstream, result.Transport),
                InfoBarSeverity.Success);
            return;
        }

        string failure = result.Failures.Count > 0
            ? string.Join(" ", result.Failures.Select(_context.L))
            : _context.L("Unknown DNS privacy failure.");
        _privacySelfTestMessage = _context.LF("Failed: {0}", failure);
        SetProgressText(_context.LF("DNS privacy self-test failed: {0}", failure));
        _context.ShowInfo(
            "DNS privacy",
            _context.LF("DNS privacy self-test failed: {0}", failure),
            InfoBarSeverity.Error);
    });

    private Task ReinstallAsync() => RunOperationAsync(async cancellationToken =>
    {
        await _context.Controller!.ReinstallDnsCryptAsync(CreateProgress(), cancellationToken);
        SetProgressText("DNSCrypt Proxy reinstalled.");
    });

    private async Task RemoveAsync()
    {
        if (_context.Controller is null)
            return;
        ContentDialogResult result = await ShowRemoveDialogAsync();
        if (result != ContentDialogResult.Primary)
            return;
        await RunOperationAsync(async cancellationToken =>
        {
            await _context.Controller.RemoveDnsCryptAsync(CreateProgress(), cancellationToken);
            SetProgressText("DNSCrypt Proxy removed.");
        });
    }

    private void ScheduleDnsCryptSettingsApply()
    {
        _settingsApplyCancellation?.Cancel();
        _settingsApplyCancellation?.Dispose();
        _settingsApplyCancellation = new CancellationTokenSource();
        long generation = ++_settingsApplyGeneration;
        _ = ApplyDnsCryptSettingsDebouncedAsync(generation, _settingsApplyCancellation.Token);
    }

    private async Task ApplyDnsCryptSettingsDebouncedAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(450, cancellationToken);
            if (_context.Controller is null || generation != _settingsApplyGeneration)
                return;

            DnsCryptConfig config = BuildDnsCryptConfigFromControls();
            if (_resolverSelected.IsChecked == true && config.serverNames.Count == 0)
            {
                _resolverListStatus.Text = _context.L("Select at least one resolver.");
                return;
            }

            SetProgressText("Applying DNS settings");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(28));
            await _context.Controller.SaveDnsCryptSettingsAsync(config, timeout.Token);
            if (generation != _settingsApplyGeneration)
                return;

            _settingsDirty = false;
            SetProgressText("DNS settings applied.");
            Refresh();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            SetProgressText("DNS settings apply timed out; previous runtime was restored.");
            _resolverListStatus.Text = _context.L("The selected resolver did not become reachable in time. Previous DNSCrypt settings remain active.");
        }
        catch (Exception exception)
        {
            SetProgressText(_context.LF("Failed: {0}", exception.Message));
            _resolverListStatus.Text = _context.LF("Resolver settings were not applied: {0}", exception.Message);
            _context.ShowInfo(
                "DNSCrypt Proxy",
                _context.LF("DNSCrypt operation failed: {0}", exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private bool IsManualResolverSupported(string name)
    {
        if (!_resolverCatalogLoaded)
            return true;

        DnsCryptResolverInfo? resolver = _resolverCatalog.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        return resolver is not null && IsSupportedResolverProtocol(resolver);
    }

    private DnsCryptConfig BuildDnsCryptConfigFromControls()
    {
        return new DnsCryptConfig
        {
            autoUpdate = _autoUpdate.IsOn,
            requireDnssec = _requireDnssec.IsOn,
            requireNoLog = _requireNoLog.IsOn,
            requireNoFilter = _requireNoFilter.IsOn,
            ipv4Servers = true,
            ipv6Servers = _ipv6Servers.IsOn,
            routeThroughShadowsocks = _routeThroughShadowsocks.IsOn,
            automaticResolvers = _resolverAutomatic.IsChecked == true,
            failClosed = true,
            serverNames = _resolverSelected.IsChecked == true
                ? _manualResolverNames
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Where(IsManualResolverSupported)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [],
        };
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_busy || _context.Controller is null)
            return;

        _busy = true;
        _operationCancellation = new CancellationTokenSource();
        _cancelButton.IsEnabled = true;
        _progressBar.IsIndeterminate = true;
        Refresh();
        try
        {
            await operation(_operationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            SetProgressText("Operation cancelled.");
        }
        catch (Exception exception)
        {
            SetProgressText(_context.LF("Failed: {0}", exception.Message));
            bool dnsCryptContext = _dnsCryptMode.IsChecked == true;
            _context.ShowInfo(
                dnsCryptContext ? "DNSCrypt Proxy" : "DNS",
                dnsCryptContext
                    ? _context.LF("DNSCrypt operation failed: {0}", exception.Message)
                    : _context.LF("DNS operation failed: {0}", exception.Message),
                InfoBarSeverity.Error);
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            _busy = false;
            _cancelButton.IsEnabled = false;
            _progressBar.IsIndeterminate = false;
            Refresh();
        }
    }

    private IProgress<DnsCryptComponentProgress> CreateProgress() => new Progress<DnsCryptComponentProgress>(progress =>
    {
        string stage = progress.Stage switch
        {
            DnsCryptComponentStage.CheckingRelease => _context.L("Checking latest version"),
            DnsCryptComponentStage.DownloadingArchive => _context.L("Downloading DNSCrypt Proxy"),
            DnsCryptComponentStage.DownloadingSignature => _context.L("Downloading signature"),
            DnsCryptComponentStage.Verifying => _context.L("Verifying signature"),
            DnsCryptComponentStage.Installing => _context.L("Installing"),
            DnsCryptComponentStage.Prepared => _context.L("Prepared"),
            DnsCryptComponentStage.ValidatingRuntime => _context.L("Validating DNSCrypt runtime"),
            DnsCryptComponentStage.Activating => _context.L("Activating"),
            DnsCryptComponentStage.Starting => _context.L("Starting DNSCrypt"),
            DnsCryptComponentStage.Removing => _context.L("Removing"),
            _ => _context.L("Ready"),
        };

        if (progress.TotalBytes is > 0)
        {
            double percent = Math.Clamp(progress.BytesTransferred * 100d / progress.TotalBytes.Value, 0d, 100d);
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = percent;
            _progressText.Text = $"{stage} · {FormatBytes(progress.BytesTransferred)} / {FormatBytes(progress.TotalBytes.Value)}";
        }
        else
        {
            _progressBar.IsIndeterminate = progress.Stage is not (DnsCryptComponentStage.Ready or DnsCryptComponentStage.Prepared);
            _progressText.Text = stage;
        }
    });

    private async Task<ContentDialogResult> ShowInstallDialogAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _context.GetXamlRoot(),
            Title = _context.L("DNSCrypt Proxy is not installed."),
            Content = new TextBlock
            {
                Text = _context.L("Shadowsocks can download and verify the official Windows x64 release from DNSCrypt/dnscrypt-proxy."),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = _context.L("Download and enable"),
            CloseButtonText = _context.L("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync();
    }

    private async Task<ContentDialogResult> ShowRemoveDialogAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _context.GetXamlRoot(),
            Title = _context.L("Remove DNSCrypt Proxy"),
            Content = new TextBlock
            {
                Text = _context.L("DNS mode will switch to System DNS and the downloaded DNSCrypt component will be removed."),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = _context.L("Remove"),
            CloseButtonText = _context.L("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync();
    }

    private void SetProgressText(string keyOrLocalizedText)
    {
        string localized = _context.L(keyOrLocalizedText);
        _progressText.Text = localized;
    }

    private Task LoadResolversAsync() => RunOperationAsync(async cancellationToken =>
    {
        SetProgressText("Loading resolver list");
        IReadOnlyList<DnsCryptResolverInfo> resolvers = await _context.Controller!.GetDnsCryptResolversAsync(cancellationToken);
        _resolverCatalog = resolvers.Where(IsSupportedResolverProtocol).ToArray();
        _resolverCatalogLoaded = true;
        EnsureConfiguredResolversVisible(_manualResolverNames);
        RefreshResolverCountryFilter();
        RebuildResolverList();
        _resolverListStatus.Text = _context.LF("Loaded {0} upstream resolvers. Measuring latency in the background.", _resolverCatalog.Count);
        SetProgressText("Resolver list loaded.");
        StartResolverLatencyRefresh();
    });

    private async Task LoadResolversInBackgroundAsync()
    {
        if (_context.Controller is null || _resolverCatalogLoaded || _resolverCatalogLoading)
            return;

        _resolverCatalogLoading = true;
        try
        {
            _resolverListStatus.Text = _context.L("Loading resolver list");
            IReadOnlyList<DnsCryptResolverInfo> resolvers = await _context.Controller.GetDnsCryptResolversAsync();
            _resolverCatalog = resolvers.Where(IsSupportedResolverProtocol).ToArray();
            _resolverCatalogLoaded = true;
            EnsureConfiguredResolversVisible(_manualResolverNames);
            RefreshResolverCountryFilter();
            RebuildResolverList();
            _resolverListStatus.Text = _context.LF("Loaded {0} upstream resolvers. Measuring latency in the background.", _resolverCatalog.Count);
            if (_resolverSelected.IsChecked == true)
                StartResolverLatencyRefresh();
            else
                Refresh();
        }
        catch (Exception exception)
        {
            _resolverListStatus.Text = _context.LF("Failed: {0}", exception.Message);
        }
        finally
        {
            _resolverCatalogLoading = false;
        }
    }

    private void StartResolverLatencyRefresh()
    {
        if (!_resolverCatalogLoaded || _context.Controller is null || _resolverCatalog.Count == 0)
            return;

        _resolverLatencyCancellation?.Cancel();
        _resolverLatencyCancellation?.Dispose();
        _resolverLatencyCancellation = new CancellationTokenSource();
        _ = RefreshResolverLatenciesInBackgroundAsync(_resolverLatencyCancellation.Token);
    }

    private async Task RefreshResolverLatenciesInBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            string[] unresolved = _resolverCatalog
                .Where(IsResolverProtocolVisible)
                .Where(resolver => !resolver.LatencyMs.HasValue)
                .OrderBy(resolver => _manualResolverNames.Contains(resolver.Name) ? 0 : 1)
                .ThenBy(resolver => resolver.CountryCode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(resolver => resolver.Name, StringComparer.OrdinalIgnoreCase)
                .Select(resolver => resolver.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            const int batchSize = 80;
            for (int offset = 0; offset < unresolved.Length; offset += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] batch = unresolved.Skip(offset).Take(batchSize).ToArray();
                IReadOnlyDictionary<string, int> measured = await _context.Controller!
                    .ProbeDnsCryptResolverLatenciesAsync(batch, cancellationToken);
                if (measured.Count > 0)
                {
                    _resolverCatalog = _resolverCatalog.Select(resolver => measured.TryGetValue(resolver.Name, out int latencyMs)
                        ? resolver with { LatencyMs = latencyMs }
                        : resolver).ToArray();
                    RebuildResolverList();
                }

                int measuredCount = _resolverCatalog.Count(resolver => resolver.LatencyMs.HasValue);
                _resolverListStatus.Text = _context.LF(
                    "Latency available for {0} of {1} resolver(s).",
                    measuredCount,
                    _resolverCatalog.Count);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _resolverListStatus.Text = _context.LF("Resolver latency measurement failed: {0}", exception.Message);
        }
    }

    private void RefreshResolverCountryFilter()
    {
        if (_resolverCountryFilter is null)
            return;

        string selectedCode = (_resolverCountryFilter.SelectedItem as ResolverCountryFilter)?.CountryCode ?? string.Empty;
        var choices = new List<ResolverCountryFilter>
        {
            new(string.Empty, _context.L("All countries")),
        };
        choices.AddRange(_resolverCatalog
            .Where(IsResolverProtocolVisible)
            .Where(resolver => !string.IsNullOrWhiteSpace(resolver.CountryCode))
            .GroupBy(resolver => resolver.CountryCode, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                DnsCryptResolverInfo resolver = group.First();
                string flag = string.IsNullOrWhiteSpace(resolver.FlagEmoji) ? "•" : resolver.FlagEmoji;
                string name = string.IsNullOrWhiteSpace(resolver.CountryName) ? resolver.CountryCode : resolver.CountryName;
                return new ResolverCountryFilter(group.Key, $"{flag} {name}");
            })
            .OrderBy(choice => choice.Label, StringComparer.CurrentCultureIgnoreCase));

        _resolverCountryFilter.ItemsSource = choices;
        _resolverCountryFilter.SelectedItem = choices.FirstOrDefault(choice =>
            string.Equals(choice.CountryCode, selectedCode, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
    }

    private void EnsureConfiguredResolversVisible(IEnumerable<string> configuredNames)
    {
        string[] configured = configuredNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (configured.Length == 0)
            return;

        var known = _resolverCatalog
            .Select(resolver => resolver.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        DnsCryptResolverInfo[] missing = configured
            .Where(name => !known.Contains(name))
            .Select(name => new DnsCryptResolverInfo(
                name, string.Empty, false, null, false, false, string.Empty, Array.Empty<string>()))
            .ToArray();
        if (missing.Length > 0)
            _resolverCatalog = _resolverCatalog.Concat(missing).ToArray();
    }

    private void RebuildResolverList()
    {
        if (_resolverList is null || _resolverSearchBox is null)
            return;

        string query = _resolverSearchBox.Text?.Trim() ?? string.Empty;
        IEnumerable<DnsCryptResolverInfo> filtered = _resolverCatalog.Where(IsResolverProtocolVisible);
        if (query.Length > 0)
        {
            filtered = filtered.Where(resolver =>
                Contains(resolver.Name, query)
                || Contains(resolver.CountryCode, query)
                || Contains(resolver.CountryName, query)
                || Contains(resolver.Description, query));
        }


        if (_resolverCountryFilter?.SelectedItem is ResolverCountryFilter country
            && !string.IsNullOrWhiteSpace(country.CountryCode))
        {
            filtered = filtered.Where(resolver =>
                string.Equals(resolver.CountryCode, country.CountryCode, StringComparison.OrdinalIgnoreCase));
        }

        if (_resolverAddressFamilyFilter?.SelectedIndex == 1)
            filtered = filtered.Where(resolver => !resolver.IPv6);
        else if (_resolverAddressFamilyFilter?.SelectedIndex == 2)
            filtered = filtered.Where(resolver => resolver.IPv6);
        if (_resolverDnssecFilter?.IsOn == true)
            filtered = filtered.Where(resolver => resolver.Dnssec == true);
        if (_resolverNoLogFilter?.IsOn == true)
            filtered = filtered.Where(resolver => resolver.NoLog);
        if (_resolverNoFilterFilter?.IsOn == true)
            filtered = filtered.Where(resolver => resolver.NoFilter);

        DnsCryptResolverInfo[] visible = filtered
            .OrderBy(resolver => resolver.CountryCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(resolver => resolver.LatencyMs ?? int.MaxValue)
            .ThenBy(resolver => resolver.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _updatingResolverList = true;
        try
        {
            _resolverList.Items.Clear();
            foreach (DnsCryptResolverInfo resolver in visible)
            {
                var choice = new ResolverChoice(resolver);
                _resolverList.Items.Add(choice);
                if (_manualResolverNames.Contains(resolver.Name))
                    _resolverList.SelectedItems.Add(choice);
            }
        }
        finally
        {
            _updatingResolverList = false;
        }

        if (_resolverCatalogLoaded && query.Length > 0)
            _resolverListStatus.Text = _context.LF("Showing {0} matching resolver(s).", visible.Length);
    }

    private bool IsResolverProtocolVisible(DnsCryptResolverInfo resolver)
    {
        if (!IsSupportedResolverProtocol(resolver))
            return false;

        string selectedProtocol = (_resolverProtocolFilter.SelectedItem as ResolverProtocolFilter)?.Protocol ?? string.Empty;
        if (string.IsNullOrWhiteSpace(selectedProtocol))
            return true;
        return string.Equals(resolver.Protocol?.Trim(), selectedProtocol, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(selectedProtocol, "DNSCrypt", StringComparison.OrdinalIgnoreCase)
                && Contains(resolver.Protocol, "DNSCrypt"));
    }

    private static bool IsSupportedResolverProtocol(DnsCryptResolverInfo resolver)
        => resolver is not null
            && (Contains(resolver.Protocol, "DNSCrypt")
                || string.Equals(resolver.Protocol?.Trim(), "DoH", StringComparison.OrdinalIgnoreCase));

    private static bool Contains(string? value, string query)
        => !string.IsNullOrWhiteSpace(value)
            && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        return $"{bytes / (1024d * 1024d):0.0} MB";
    }
}
