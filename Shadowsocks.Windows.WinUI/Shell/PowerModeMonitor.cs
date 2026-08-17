#nullable enable

using System;
using Microsoft.UI.Xaml;
using WinUIEx;
using WinUIEx.Messaging;

namespace Shadowsocks.Windows.WinUI.Shell;

/// <summary>
/// Receives Windows power broadcast messages from the WinUI window without
/// introducing Microsoft.Win32.SystemEvents, WinForms or WPF dependencies.
/// The window may stay hidden in the notification area; its HWND still receives
/// WM_POWERBROADCAST notifications.
/// </summary>
public sealed class PowerModeMonitor : IDisposable
{
    private const uint WmPowerBroadcast = 0x0218;
    private const long PbtApmSuspend = 0x0004;
    private const long PbtApmResumeSuspend = 0x0007;
    private const long PbtApmResumeAutomatic = 0x0012;

    private readonly WindowMessageMonitor _monitor;
    private bool _disposed;

    public PowerModeMonitor(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _monitor = new WindowMessageMonitor(window.GetWindowHandle());
        _monitor.WindowMessageReceived += OnWindowMessageReceived;
    }

    public event EventHandler? Suspending;
    public event EventHandler? Resumed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _monitor.WindowMessageReceived -= OnWindowMessageReceived;
        _monitor.Dispose();
    }

    private void OnWindowMessageReceived(object? sender, WindowMessageEventArgs e)
    {
        if (e.Message.MessageId != WmPowerBroadcast)
        {
            return;
        }

        long powerEvent = unchecked((long)e.Message.WParam);
        if (powerEvent == PbtApmSuspend)
        {
            Suspending?.Invoke(this, EventArgs.Empty);
        }
        else if (powerEvent == PbtApmResumeSuspend || powerEvent == PbtApmResumeAutomatic)
        {
            Resumed?.Invoke(this, EventArgs.Empty);
        }
    }
}
