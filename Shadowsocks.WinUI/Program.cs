using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Shadowsocks.Core.Storage;
using Shadowsocks.Core;
using AppRuntimeEnvironment = Shadowsocks.Core.RuntimeEnvironment;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Shadowsocks.Controller;
using Shadowsocks.Controller.Service;
using Shadowsocks.Localization;
using Windows.ApplicationModel.Activation;

namespace Shadowsocks.WinUI;

internal static partial class Program
{
    private const uint Infinite = 0xFFFFFFFF;
    private static readonly ConcurrentQueue<AppActivationArguments> PendingActivations = new();
    private static Action<AppActivationArguments>? _activationHandler;
    private static ILocalizationService? _localization;
    private static ProcessSingleInstanceGuard? _processInstanceGuard;
    private static App? _application;

    [STAThread]
    public static int Main()
    {
        string[] rawArguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (SelfUpdater.TryRunUpdaterMode(rawArguments, out int updaterExitCode))
        {
            return updaterExitCode;
        }

        SelfUpdater.CleanupCompletedUpdate(rawArguments);
        string[] applicationArguments = SelfUpdater.RemoveInternalArguments(rawArguments);
        InitializeProcessEnvironment(applicationArguments);
        _localization = CsvLocalizationService.CreateDefault();
        I18N.Configure(_localization);

        AppInstance currentInstance = AppInstance.GetCurrent();
        AppActivationArguments? activationArguments = currentInstance.GetActivatedEventArgs();
        if (activationArguments is null)
        {
            return 1;
        }

        AppInstance keyInstance = AppInstance.FindOrRegisterForKey(CreateInstanceKey());

        if (!keyInstance.IsCurrent)
        {
            // Never create a second-process Win32 dialog. Forward every activation to
            // the running WinUI instance so all user-facing UI stays Fluent/WinUI 3.
            return RedirectActivation(activationArguments, keyInstance);
        }

        // AppInstance registration for an unpackaged application can be scoped by the
        // executable identity. The stable LocalAppData startup copy and the original
        // product EXE therefore need an identity-independent process gate as well.
        _processInstanceGuard = ProcessSingleInstanceGuard.TryAcquire();
        if (_processInstanceGuard is null)
        {
            Debug.WriteLine("Another Shadowsocks Reborn process already owns the process-wide single-instance gate.");
            return 0;
        }

        keyInstance.Activated += OnInstanceActivated;
        App.ConfigureStartupContext(keyInstance, activationArguments, _localization);

        // DISABLE_XAML_GENERATED_MAIN gives us control of startup so AppInstance
        // redirection can happen before WinUI is initialized. Start WinUI through
        // the public application bootstrap rather than calling generated XAML APIs.
        WinRT.ComWrappersSupport.InitializeComWrappers();
        try
        {
            Application.Start(static _ =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _application = new App();
            });
            return 0;
        }
        finally
        {
            _application?.Dispose();
            _application = null;
            _processInstanceGuard?.Dispose();
            _processInstanceGuard = null;
        }
    }

    private static void InitializeProcessEnvironment(string[] commandLineArguments)
    {
        string executablePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, "Shadowsocks.exe");
        string workingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;

        AppRuntimeEnvironment.Initialize(executablePath, workingDirectory, commandLineArguments);
        AppStoragePaths.Initialize(executablePath);
        Directory.SetCurrentDirectory(workingDirectory);
    }

    internal static void RegisterActivationHandler(Action<AppActivationArguments> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _activationHandler = handler;

        while (PendingActivations.TryDequeue(out AppActivationArguments? activation))
        {
            if (activation is not null)
            {
                handler(activation);
            }
        }
    }

    internal static void UnregisterActivationHandler(Action<AppActivationArguments> handler)
    {
        if (_activationHandler == handler)
        {
            _activationHandler = null;
        }
    }

    internal static void DetachActivationSource(AppInstance appInstance)
    {
        ArgumentNullException.ThrowIfNull(appInstance);
        appInstance.Activated -= OnInstanceActivated;
    }

    private static void OnInstanceActivated(object? _, AppActivationArguments activationArguments)
    {
        Action<AppActivationArguments>? handler = _activationHandler;
        if (handler is null)
        {
            PendingActivations.Enqueue(activationArguments);
            return;
        }

        handler(activationArguments);
    }


    internal static bool IsPlainLaunch(AppActivationArguments activationArguments)
    {
        if (activationArguments.Kind != ExtendedActivationKind.Launch)
        {
            return false;
        }

        if (activationArguments.Data is not ILaunchActivatedEventArgs launchArguments)
        {
            return true;
        }

        string arguments = launchArguments.Arguments?.Trim() ?? string.Empty;
        return !arguments.Contains("--open-url", StringComparison.OrdinalIgnoreCase)
            && !arguments.Contains(AutoStartup.StartupHiddenOption, StringComparison.OrdinalIgnoreCase)
            && !arguments.Contains(AutoStartup.StartupVisibleOption, StringComparison.OrdinalIgnoreCase)
            && !arguments.Contains(AutoStartup.StartupOriginOption, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateInstanceKey()
    {
        // One instance per user prevents normal and Clean Mode processes from racing on
        // system proxy state, hotkeys, tray ownership, and Admin Mode resources.
        return "Shadowsocks.Reborn.WinUI";
    }

    private static int RedirectActivation(AppActivationArguments activationArguments, AppInstance keyInstance)
    {
        nint redirectEvent = CreateEvent(nint.Zero, 1, 0, null);
        if (redirectEvent == nint.Zero)
        {
            return 2;
        }

        Exception? redirectException = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await keyInstance.RedirectActivationToAsync(activationArguments).AsTask().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                redirectException = exception;
            }
            finally
            {
                _ = SetEvent(redirectEvent);
            }
        });

        try
        {
            _ = WaitForRedirectCompletion(redirectEvent);

            if (redirectException is not null)
            {
                Debug.WriteLine(redirectException);
                return 3;
            }

            try
            {
                using Process process = Process.GetProcessById(unchecked((int)keyInstance.ProcessId));
                if (process.MainWindowHandle != nint.Zero)
                {
                    _ = SetForegroundWindow(process.MainWindowHandle);
                }
            }
            catch (ArgumentException)
            {
                // The target instance handles the redirected activation itself.
            }

            return 0;
        }
        finally
        {
            _ = CloseHandle(redirectEvent);
        }
    }

    private static unsafe uint WaitForRedirectCompletion(nint redirectEvent)
    {
        nint* handles = stackalloc nint[1];
        handles[0] = redirectEvent;
        return CoWaitForMultipleObjects(0, Infinite, 1, handles, out _);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateEvent(nint eventAttributes, int manualReset, int initialState, string? name);

    [LibraryImport("kernel32.dll", EntryPoint = "SetEvent", SetLastError = true)]
    private static partial int SetEvent(nint eventHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    private static partial int CloseHandle(nint handle);

    [LibraryImport("ole32.dll", EntryPoint = "CoWaitForMultipleObjects")]
    private static unsafe partial uint CoWaitForMultipleObjects(
        uint flags,
        uint timeoutMilliseconds,
        uint handleCount,
        nint* handles,
        out uint index);

    [LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    private static partial int SetForegroundWindow(nint windowHandle);
}
