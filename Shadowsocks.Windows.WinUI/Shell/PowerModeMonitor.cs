#nullable enable

using System;
using Microsoft.UI.Xaml;
using WinUIEx;
using WinUIEx.Messaging;

namespace Shadowsocks.Windows.WinUI.Shell;

/// <summary>
/// Receives Windows session-ending and power-broadcast messages from the WinUI
/// window without introducing Microsoft.Win32.SystemEvents, WinForms or WPF
/// dependencies. The window may stay hidden in the notification area; its HWND
/// still receives WM_QUERYENDSESSION, WM_ENDSESSION and WM_POWERBROADCAST.
/// </summary>
public sealed class PowerModeMonitor : IDisposable
{
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
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

    public event EventHandler? SessionEnding;
    public event EventHandler? SessionEndCancelled;
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
        if (e.Message.MessageId == WmQueryEndSession)
        {
            // Capture the shell state before AppWindow.Closing can be raised as part
            // of logoff/shutdown. A system close must not be mistaken for the user
            // explicitly sending the application to the notification area.
            SessionEnding?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (e.Message.MessageId == WmEndSession)
        {
            // wParam == FALSE means Windows cancelled the logoff/shutdown after the
            // earlier WM_QUERYENDSESSION notification. Resume normal close-to-tray behavior.
            if (unchecked((long)e.Message.WParam) == 0)
            {
                SessionEndCancelled?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

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
