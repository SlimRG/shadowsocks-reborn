using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using NLog;
using Shadowsocks.Controller;
using Shadowsocks.Controller.Hotkeys;
using Shadowsocks.Model;

namespace Shadowsocks.WinUI.Shell;

internal sealed class WinUIHotkeyManager : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ShadowsocksController _controller;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Action _showWindow;
    private readonly Action _showLogsPage;
    private readonly GlobalHotkeyService _hotkeys = new();
    private bool _disposed;

    public WinUIHotkeyManager(
        ShadowsocksController controller,
        DispatcherQueue dispatcherQueue,
        Action showWindow,
        Action showLogsPage)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        ArgumentNullException.ThrowIfNull(showWindow);
        ArgumentNullException.ThrowIfNull(showLogsPage);
        _controller = controller;
        _dispatcherQueue = dispatcherQueue;
        _showWindow = showWindow;
        _showLogsPage = showLogsPage;
    }

    public IReadOnlyList<string> RegisterConfiguredHotkeys()
    {
        HotkeyConfig? config = _controller.GetCurrentConfiguration().hotkey;
        return ApplyConfiguration(config, respectStartupFlag: true);
    }

    public IReadOnlyList<string> ApplyConfiguration(HotkeyConfig? config, bool respectStartupFlag = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UnregisterAll();

        if (config is null || (respectStartupFlag && !config.RegHotkeysAtStartup))
        {
            return Array.Empty<string>();
        }

        var failures = new List<string>();
        Register("SwitchSystemProxy", config.SwitchSystemProxy, ToggleSystemProxy, failures);
        Register("SwitchSystemProxyMode", config.SwitchSystemProxyMode, ToggleSystemProxyMode, failures);
        Register("SwitchAllowLan", config.SwitchAllowLan, ToggleAllowLan, failures);
        Register("ShowLogs", config.ShowLogs, ShowLogs, failures);
        Register("ServerMoveUp", config.ServerMoveUp, () => MoveServer(-1), failures);
        Register("ServerMoveDown", config.ServerMoveDown, () => MoveServer(1), failures);
        return failures;
    }

    private void UnregisterAll()
    {
        _hotkeys.Unregister("SwitchSystemProxy");
        _hotkeys.Unregister("SwitchSystemProxyMode");
        _hotkeys.Unregister("SwitchAllowLan");
        _hotkeys.Unregister("ShowLogs");
        _hotkeys.Unregister("ServerMoveUp");
        _hotkeys.Unregister("ServerMoveDown");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hotkeys.Dispose();
    }

    private void Register(string name, string gesture, Action callback, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            _hotkeys.Unregister(name);
            return;
        }

        if (!HotkeyGesture.TryParse(gesture, out _))
        {
            Logger.Warn("Cannot parse configured hotkey {0}: {1}", name, gesture);
            failures.Add(name);
            return;
        }

        bool registered = _hotkeys.Register(name, gesture, () => _dispatcherQueue.TryEnqueue(() => callback()));
        if (!registered)
        {
            Logger.Warn("Cannot register configured hotkey {0}: {1}", name, gesture);
            failures.Add(name);
        }
    }

    private void ToggleSystemProxy()
    {
        Configuration config = _controller.GetCurrentConfiguration();
        _controller.ToggleEnable(!config.Enabled);
    }

    private void ToggleSystemProxyMode()
    {
        Configuration config = _controller.GetCurrentConfiguration();
        if (config.Enabled)
        {
            _controller.ToggleGlobal(!config.global);
        }
    }

    private void ToggleAllowLan()
    {
        Configuration config = _controller.GetCurrentConfiguration();
        _controller.ToggleShareOverLAN(!config.shareOverLan);
    }

    private void ShowLogs()
    {
        _showLogsPage();
    }

    private void MoveServer(int delta)
    {
        Configuration config = _controller.GetCurrentConfiguration();
        int[] configuredIndices = (config.configs ?? [])
            .Select((server, index) => (server, index))
            .Where(item => item.server?.IsConfigured == true)
            .Select(item => item.index)
            .ToArray();
        if (configuredIndices.Length == 0)
        {
            return;
        }

        int position = Array.IndexOf(configuredIndices, config.index);
        if (position < 0)
        {
            position = delta >= 0 ? -1 : 0;
        }

        position = (position + delta + configuredIndices.Length) % configuredIndices.Length;
        _controller.SelectServerIndex(configuredIndices[position]);
    }
}
