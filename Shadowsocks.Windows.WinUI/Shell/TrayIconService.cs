#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NLog;
using WinUIEx;

namespace Shadowsocks.Windows.Shell;

public enum TraySystemProxyMode
{
    Disabled,
    Pac,
    Global,
}

public enum TrayTrafficMode
{
    User,
    Admin,
}

public enum TrayCommandKind
{
    OpenOverview,
    OpenServers,
    OpenTraffic,
    OpenSharing,
    OpenPac,
    OpenForwardProxy,
    OpenOnlineConfig,
    OpenHotkeys,
    OpenLogs,
    OpenAbout,
    SetSystemProxyDisabled,
    SetSystemProxyPac,
    SetSystemProxyGlobal,
    SetTrafficUser,
    SetTrafficAdmin,
    SelectServer,
    SelectStrategy,
    UseLocalPac,
    UseOnlinePac,
    EditLocalPacFile,
    UpdateLocalPacFromGeosite,
    EditUserRuleFile,
    ToggleSecureLocalPac,
    ToggleRegeneratePacOnUpdate,
    EditOnlinePacUrl,
    ToggleStartWithWindows,
    ToggleProtocolHandler,
    ToggleAllowLan,
    CheckUpdates,
    TogglePreRelease,
    Exit,
}

public sealed record TrayServerMenuItem(int Index, string Text, bool IsChecked);
public sealed record TrayStrategyMenuItem(string Id, string Text, bool IsChecked);

public sealed record TrayMenuState(
    string Tooltip,
    TraySystemProxyMode SystemProxyMode,
    TrayTrafficMode TrafficMode,
    string TrafficStatusText,
    IReadOnlyList<TrayStrategyMenuItem> Strategies,
    IReadOnlyList<TrayServerMenuItem> Servers,
    bool UseOnlinePac,
    bool SecureLocalPac,
    bool RegeneratePacOnUpdate,
    bool StartWithWindows,
    bool StartWithWindowsAvailable,
    bool ProtocolHandlerEnabled,
    bool AllowLan,
    bool CheckPreRelease,
    int TotalServerCount);

public sealed class TrayCommandEventArgs : EventArgs
{
    public TrayCommandEventArgs(TrayCommandKind command, int index = -1, string? value = null)
    {
        Command = command;
        Index = index;
        Value = value;
    }

    public TrayCommandKind Command { get; }
    public int Index { get; }
    public string? Value { get; }
}

/// <summary>
/// WinUI 3 notification-area adapter backed by WinUIEx.TrayIcon.
/// It mirrors the original Shadowsocks tray hierarchy while keeping all controller
/// decisions in the application layer through a plain command/state boundary.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint TrayIconId = 1;
    private const int MaxTooltipLength = 127;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly TrayIcon _trayIcon;
    private readonly Func<string, string> _localize;
    private TrayMenuState _state;
    private nint _baseIconHandle;
    private nint _inIconHandle;
    private nint _outIconHandle;
    private nint _bothIconHandle;
    private bool _hasInboundActivity;
    private bool _hasOutboundActivity;
    private bool _disposed;

    public TrayIconService(
        string executablePath,
        TrayMenuState initialState,
        Func<string, string>? localize = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(initialState);

        _state = initialState;
        _localize = localize ?? (text => text);

        (_baseIconHandle, _inIconHandle, _outIconHandle, _bothIconHandle) = CreateStateIcons(_state);
        _trayIcon = new TrayIcon(
            TrayIconId,
            new IconId((ulong)_baseIconHandle),
            NormalizeTooltip(initialState.Tooltip));
        _trayIcon.LeftDoubleClick += OnLeftDoubleClick;
        _trayIcon.ContextMenu += OnContextMenu;
        _trayIcon.IsVisible = true;
        ApplyActivityIcon();

        Logger.Info("Shadowsocks notification-area icon registered through WinUIEx using embedded tray assets.");
    }

    public event EventHandler<TrayCommandEventArgs>? CommandRequested;

    public bool IsRegistered => !_disposed && _trayIcon.IsVisible;

    public void UpdateState(TrayMenuState state)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(state);
        bool pacSourceChanged = state.SystemProxyMode == TraySystemProxyMode.Pac
            && _state.UseOnlinePac != state.UseOnlinePac;
        bool visualStateChanged = _state.SystemProxyMode != state.SystemProxyMode || pacSourceChanged;
        _state = state;
        _trayIcon.Tooltip = NormalizeTooltip(state.Tooltip);
        if (visualStateChanged)
        {
            RebuildStateIcons();
        }
    }

    public void UpdateActivity(bool hasInbound, bool hasOutbound)
    {
        ThrowIfDisposed();
        if (_hasInboundActivity == hasInbound && _hasOutboundActivity == hasOutbound)
        {
            return;
        }

        _hasInboundActivity = hasInbound;
        _hasOutboundActivity = hasOutbound;
        ApplyActivityIcon();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trayIcon.LeftDoubleClick -= OnLeftDoubleClick;
        _trayIcon.ContextMenu -= OnContextMenu;
        _trayIcon.Dispose();
        ReleaseStateIcons();
        Logger.Info("Shadowsocks WinUIEx notification-area icon disposed.");
    }

    private void OnLeftDoubleClick(TrayIcon _, TrayIconEventArgs _1)
        => Raise(TrayCommandKind.OpenServers);

    private void OnContextMenu(TrayIcon _, TrayIconEventArgs args)
    {
        TrayMenuState state = _state;
        var flyout = new MenuFlyout();

        flyout.Items.Add(BuildSystemProxyMenu(state));
        flyout.Items.Add(BuildTrafficMenu(state));
        flyout.Items.Add(BuildServersMenu(state));
        flyout.Items.Add(BuildPacMenu(state));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateItem("Forward Proxy", TrayCommandKind.OpenForwardProxy));
        flyout.Items.Add(CreateItem("Online Config", TrayCommandKind.OpenOnlineConfig));
        flyout.Items.Add(new MenuFlyoutSeparator());
        ToggleMenuFlyoutItem startupItem = CreateToggleItem("Start on Boot", state.StartWithWindows, TrayCommandKind.ToggleStartWithWindows);
        startupItem.IsEnabled = state.StartWithWindowsAvailable;
        flyout.Items.Add(startupItem);
        flyout.Items.Add(CreateToggleItem("Associate ss:// Links", state.ProtocolHandlerEnabled, TrayCommandKind.ToggleProtocolHandler));
        flyout.Items.Add(CreateToggleItem("Allow other devices to connect", state.AllowLan, TrayCommandKind.ToggleAllowLan));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateItem("Hotkeys", TrayCommandKind.OpenHotkeys));
        flyout.Items.Add(BuildHelpMenu(state));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateItem("Quit", TrayCommandKind.Exit));

        args.Flyout = flyout;
    }

    private MenuFlyoutSubItem BuildSystemProxyMenu(TrayMenuState state)
    {
        var menu = new MenuFlyoutSubItem { Text = L("System Proxy") };
        menu.Items.Add(CreateToggleItem("Disable", state.SystemProxyMode == TraySystemProxyMode.Disabled, TrayCommandKind.SetSystemProxyDisabled));
        menu.Items.Add(CreateToggleItem("PAC", state.SystemProxyMode == TraySystemProxyMode.Pac, TrayCommandKind.SetSystemProxyPac));
        menu.Items.Add(CreateToggleItem("Global", state.SystemProxyMode == TraySystemProxyMode.Global, TrayCommandKind.SetSystemProxyGlobal));
        return menu;
    }

    private MenuFlyoutSubItem BuildTrafficMenu(TrayMenuState state)
    {
        var menu = new MenuFlyoutSubItem { Text = L("Traffic Mode") };
        menu.Items.Add(CreateToggleItem("User Mode", state.TrafficMode == TrayTrafficMode.User, TrayCommandKind.SetTrafficUser));

        ToggleMenuFlyoutItem adminModeItem = CreateToggleItem(
            "Admin Mode",
            state.TrafficMode == TrayTrafficMode.Admin,
            TrayCommandKind.SetTrafficAdmin);
        adminModeItem.Icon = new FontIcon
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
            Glyph = "\uEA18", // Shield: marks the UAC/elevation path.
        };
        menu.Items.Add(adminModeItem);

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = state.TrafficStatusText, IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateItem("Traffic Routing", TrayCommandKind.OpenTraffic));
        return menu;
    }

    private MenuFlyoutSubItem BuildServersMenu(TrayMenuState state)
    {
        var menu = new MenuFlyoutSubItem { Text = L("Servers") };

        foreach (TrayStrategyMenuItem strategy in state.Strategies)
        {
            menu.Items.Add(CreateToggleItem(strategy.Text, strategy.IsChecked, TrayCommandKind.SelectStrategy, value: strategy.Id, localize: false));
        }

        if (state.Strategies.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        foreach (TrayServerMenuItem server in state.Servers)
        {
            menu.Items.Add(CreateToggleItem(server.Text, server.IsChecked, TrayCommandKind.SelectServer, server.Index, localize: false));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateItem("Servers", TrayCommandKind.OpenServers));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (state.TotalServerCount > 20)
        {
            menu.Items.Add(CreateItem(LF("More than 20 servers (total: {0})", state.TotalServerCount), TrayCommandKind.OpenServers, localize: false));
        }
        menu.Items.Add(CreateItem("Share Server Config", TrayCommandKind.OpenSharing));
        return menu;
    }

    private MenuFlyoutSubItem BuildPacMenu(TrayMenuState state)
    {
        // Match the legacy MenuViewController PAC state matrix exactly.
        bool localPac = !state.UseOnlinePac;

        var menu = new MenuFlyoutSubItem { Text = L("PAC") };
        menu.Items.Add(CreateToggleItem("Local PAC", localPac, TrayCommandKind.UseLocalPac));
        menu.Items.Add(CreateToggleItem("Online PAC", state.UseOnlinePac, TrayCommandKind.UseOnlinePac));
        menu.Items.Add(new MenuFlyoutSeparator());

        if (localPac)
        {
            menu.Items.Add(CreateItem("Edit Local PAC File", TrayCommandKind.EditLocalPacFile));
            menu.Items.Add(CreateItem("Update Local PAC from Geosite", TrayCommandKind.UpdateLocalPacFromGeosite));
            menu.Items.Add(CreateItem("GeoSite Sources", TrayCommandKind.OpenPac));
            menu.Items.Add(CreateItem("Edit User Rule for Geosite", TrayCommandKind.EditUserRuleFile));
        }

        menu.Items.Add(CreateToggleItem("Require secret for local PAC URL", state.SecureLocalPac, TrayCommandKind.ToggleSecureLocalPac));
        menu.Items.Add(CreateToggleItem("Regenerate Local PAC after application updates", state.RegeneratePacOnUpdate, TrayCommandKind.ToggleRegeneratePacOnUpdate));

        if (state.UseOnlinePac)
        {
            menu.Items.Add(CreateItem("Edit Online PAC URL", TrayCommandKind.EditOnlinePacUrl));
        }

        return menu;
    }

    private MenuFlyoutSubItem BuildHelpMenu(TrayMenuState state)
    {
        var help = new MenuFlyoutSubItem { Text = L("Help") };
        help.Items.Add(CreateItem("Logs", TrayCommandKind.OpenLogs));

        var updates = new MenuFlyoutSubItem { Text = L("Updates") };
        updates.Items.Add(CreateItem("Check for Updates", TrayCommandKind.CheckUpdates));
        updates.Items.Add(new MenuFlyoutSeparator());
        updates.Items.Add(CreateToggleItem("Include prerelease versions", state.CheckPreRelease, TrayCommandKind.TogglePreRelease));
        help.Items.Add(updates);
        help.Items.Add(CreateItem("About", TrayCommandKind.OpenAbout));
        return help;
    }

    private MenuFlyoutItem CreateItem(
        string text,
        TrayCommandKind command,
        int index = -1,
        string? value = null,
        bool localize = true)
    {
        var item = new MenuFlyoutItem
        {
            Text = localize ? L(text) : text,
        };
        item.Click += (_, _) => Raise(command, index, value);
        return item;
    }

    private ToggleMenuFlyoutItem CreateToggleItem(
        string text,
        bool isChecked,
        TrayCommandKind command,
        int index = -1,
        string? value = null,
        bool localize = true)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = localize ? L(text) : text,
            IsChecked = isChecked,
        };
        item.Click += (_, _) => Raise(command, index, value);
        return item;
    }

    private void Raise(TrayCommandKind command, int index = -1, string? value = null)
        => CommandRequested?.Invoke(this, new TrayCommandEventArgs(command, index, value));

    private string L(string text) => _localize(text);

    private string LF(string text, params object[] args)
        => string.Format(System.Globalization.CultureInfo.CurrentCulture, L(text), args);

    private void RebuildStateIcons()
    {
        nint oldBase = _baseIconHandle;
        nint oldIn = _inIconHandle;
        nint oldOut = _outIconHandle;
        nint oldBoth = _bothIconHandle;

        (_baseIconHandle, _inIconHandle, _outIconHandle, _bothIconHandle) = CreateStateIcons(_state);
        ApplyActivityIcon();

        DestroyIconIfNeeded(oldBase);
        DestroyIconIfNeeded(oldIn);
        DestroyIconIfNeeded(oldOut);
        DestroyIconIfNeeded(oldBoth);
    }

    private static (nint Base, nint In, nint Out, nint Both) CreateStateIcons(TrayMenuState state)
    {
        // Restrained, high-separation tray palette. The old bright red/orange/green/blue
        // scheme was visually noisy and adjacent states looked too similar at 16-24 px.
        // Disabled = graphite, Local PAC = bronze, Online PAC = forest teal, Global = navy.
        Color mask = state.SystemProxyMode switch
        {
            TraySystemProxyMode.Global => Color.FromArgb(255, 45, 84, 128),       // #2D5480
            TraySystemProxyMode.Pac when state.UseOnlinePac => Color.FromArgb(255, 42, 112, 91), // #2A705B
            TraySystemProxyMode.Pac => Color.FromArgb(255, 166, 105, 38),         // #A66926
            _ => Color.FromArgb(255, 91, 101, 115),                               // #5B6573
        };
        int size = SelectIconSize();

        using Bitmap fill = LoadTrayAsset("ss32Fill.png");
        using Bitmap outline = LoadTrayAsset("ss32Outline.png");
        using Bitmap inbound = LoadTrayAsset("ss32In.png");
        using Bitmap outbound = LoadTrayAsset("ss32Out.png");
        using Bitmap colored = ChangeBitmapColor(fill, mask);
        using Bitmap baseBitmap = AddBitmapOverlay(colored, outline);
        using Bitmap inBitmap = AddBitmapOverlay(baseBitmap, inbound);
        using Bitmap outBitmap = AddBitmapOverlay(baseBitmap, outbound);
        using Bitmap bothBitmap = AddBitmapOverlay(baseBitmap, inbound, outbound);
        using Bitmap resizedBase = ResizeBitmap(baseBitmap, size, size);
        using Bitmap resizedIn = ResizeBitmap(inBitmap, size, size);
        using Bitmap resizedOut = ResizeBitmap(outBitmap, size, size);
        using Bitmap resizedBoth = ResizeBitmap(bothBitmap, size, size);

        return (
            resizedBase.GetHicon(),
            resizedIn.GetHicon(),
            resizedOut.GetHicon(),
            resizedBoth.GetHicon());
    }

    private void ApplyActivityIcon()
    {
        nint handle = _hasInboundActivity && _hasOutboundActivity ? _bothIconHandle
            : _hasInboundActivity ? _inIconHandle
            : _hasOutboundActivity ? _outIconHandle
            : _baseIconHandle;
        if (handle != nint.Zero)
        {
            _trayIcon.SetIcon(new IconId((ulong)handle));
        }
    }

    private void ReleaseStateIcons()
    {
        DestroyIconIfNeeded(_baseIconHandle);
        DestroyIconIfNeeded(_inIconHandle);
        DestroyIconIfNeeded(_outIconHandle);
        DestroyIconIfNeeded(_bothIconHandle);
        _baseIconHandle = _inIconHandle = _outIconHandle = _bothIconHandle = nint.Zero;
    }

    private static void DestroyIconIfNeeded(nint handle)
    {
        if (handle != nint.Zero)
        {
            _ = DestroyIcon(handle);
        }
    }

    private static Bitmap LoadTrayAsset(string suffix)
    {
        Assembly assembly = typeof(TrayIconService).Assembly;
        string? name = assembly.GetManifestResourceNames()
            .FirstOrDefault(resource => resource.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            throw new InvalidOperationException($"Embedded tray asset not found: {suffix}");
        }
        using Stream stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded tray asset stream not found: {name}");
        using var source = new Bitmap(stream);
        return new Bitmap(source);
    }

    private static Bitmap AddBitmapOverlay(Bitmap original, params Bitmap[] overlays)
    {
        var bitmap = new Bitmap(original.Width, original.Height, PixelFormat.Format32bppArgb);
        using Graphics canvas = Graphics.FromImage(bitmap);
        canvas.DrawImage(original, Point.Empty);
        foreach (Bitmap overlay in overlays)
        {
            using var resizedOverlay = new Bitmap(overlay, original.Size);
            canvas.DrawImage(resizedOverlay, Point.Empty);
        }
        return bitmap;
    }

    private static Bitmap ChangeBitmapColor(Bitmap original, Color colorMask)
    {
        var bitmap = new Bitmap(original);
        for (int x = 0; x < bitmap.Width; x++)
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                Color color = original.GetPixel(x, y);
                if (color.A == 0)
                {
                    continue;
                }
                bitmap.SetPixel(x, y, Color.FromArgb(
                    color.A * colorMask.A / 255,
                    color.R * colorMask.R / 255,
                    color.G * colorMask.G / 255,
                    color.B * colorMask.B / 255));
            }
        }
        return bitmap;
    }

    private static Bitmap ResizeBitmap(Bitmap original, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.DrawImage(original, new Rectangle(0, 0, width, height));
        return bitmap;
    }

    private static int SelectIconSize()
    {
        using Graphics graphics = Graphics.FromHwnd(nint.Zero);
        int dpi = (int)graphics.DpiX;
        return dpi < 97 ? 16 : dpi < 121 ? 20 : dpi < 145 ? 24 : 28;
    }

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint iconHandle);

    private static string NormalizeTooltip(string? tooltip)
    {
        string value = string.IsNullOrWhiteSpace(tooltip)
            ? "shadowsocks-reborn"
            : tooltip.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();

        return value.Length <= MaxTooltipLength
            ? value
            : value[..MaxTooltipLength];
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
