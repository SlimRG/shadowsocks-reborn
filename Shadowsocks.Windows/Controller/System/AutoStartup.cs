using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using NLog;
using Shadowsocks.Core;
using AppRuntimeEnvironment = Shadowsocks.Core.RuntimeEnvironment;
using Shadowsocks.Core.Storage;
using Shadowsocks.Util;
using Shadowsocks.Windows.Shell;

namespace Shadowsocks.Controller
{
    public static partial class AutoStartup
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Key = "Shadowsocks Reborn";
        internal const string StartupHiddenOption = "--start-hidden";
        internal const string StartupVisibleOption = "--start-visible";
        internal const string StartupOriginOption = "--startup-origin";
        private static bool StartupCopySynchronized;
        private static int TrackedWindowVisibility = -1;

        public static bool Set(bool enabled, bool windowVisible = false)
        {
            if (AppStoragePaths.IsCleanMode)
            {
                Logger.Info("Start with Windows is unavailable in Clean Mode.");
                return false;
            }

            RegistryKey runKey = null;
            try
            {
                runKey = WindowsSystemUtilities.OpenRegistryKey(RunKeyPath, true);
                if (runKey == null)
                {
                    Logger.Error(@"Cannot find HKCU\Software\Microsoft\Windows\CurrentVersion\Run");
                    return false;
                }

                if (enabled)
                {
                    if (!EnsureStartupExecutable())
                    {
                        return false;
                    }

                    runKey.SetValue(Key, BuildStartupCommand(windowVisible));
                    RemoveDuplicateStartupEntries(runKey);
                }
                else
                {
                    runKey.DeleteValue(Key, throwOnMissingValue: false);
                    // Disabling Start with Windows must also remove legacy Shadowsocks
                    // Run values. Otherwise an obsolete key can continue launching a
                    // second process even though the current UI reports autostart off.
                    RemoveDuplicateStartupEntries(runKey);
                    TryDeleteUnusedStartupCopy();
                }

                // When autostartup setting changes, change RegisterForRestart state to avoid starting twice.
                RegisterForRestart(!enabled, windowVisible);
                return true;
            }
            catch (Exception exception)
            {
                Logger.LogUsefulException(exception);
                return false;
            }
            finally
            {
                runKey?.Dispose();
            }
        }

        public static bool Check()
        {
            if (AppStoragePaths.IsCleanMode)
            {
                return false;
            }

            RegistryKey runKey = null;
            try
            {
                runKey = WindowsSystemUtilities.OpenRegistryKey(RunKeyPath, true);
                if (runKey == null)
                {
                    Logger.Error(@"Cannot find HKCU\Software\Microsoft\Windows\CurrentVersion\Run");
                    return false;
                }

                string command = runKey.GetValue(Key)?.ToString();
                if (!IsStartupExecutableCommand(command))
                {
                    string primaryExecutable = GetPrimaryExecutablePath();
                    string currentExecutable = AppRuntimeEnvironment.ExecutablePath;
                    bool canonicalTargetsProduct = CommandTargetsExecutable(command, primaryExecutable)
                        || CommandTargetsExecutable(command, currentExecutable);
                    bool legacyTargetsProduct = HasLegacyStartupEntry(
                        runKey,
                        AppStoragePaths.StartupExecutableFile,
                        primaryExecutable,
                        currentExecutable);
                    if (!canonicalTargetsProduct && !legacyTargetsProduct)
                    {
                        return false;
                    }

                    if (!EnsureStartupExecutable())
                    {
                        return false;
                    }

                    command = BuildStartupCommand(windowVisible: false);
                    runKey.SetValue(Key, command);
                    Logger.Info("Migrated legacy Start with Windows command to the stable LocalAppData startup executable.");
                }

                // A user may update Shadowsocks from a USB drive/download folder while autostart
                // is already enabled. Keep the stable LocalAppData copy synchronized with the
                // currently running product binary without changing the Run key location.
                if (!StartupCopySynchronized
                    && !PathsEqual(AppRuntimeEnvironment.ExecutablePath, AppStoragePaths.StartupExecutableFile))
                {
                    if (!EnsureStartupExecutable())
                    {
                        Logger.Warn("Start with Windows is configured, but the LocalAppData startup copy could not be refreshed.");
                    }
                }

                string[] commandArguments = WindowsCommandLine.ParseArguments(command ?? string.Empty);
                bool windowVisible = !IsHiddenStartup(commandArguments) && IsVisibleStartup(commandArguments);
                string canonical = BuildStartupCommand(windowVisible);
                if (!string.Equals(command?.Trim(), canonical, StringComparison.OrdinalIgnoreCase))
                {
                    runKey.SetValue(Key, canonical);
                }
                RemoveDuplicateStartupEntries(runKey);
                return true;
            }
            catch (Exception exception)
            {
                Logger.LogUsefulException(exception);
                return false;
            }
            finally
            {
                runKey?.Dispose();
            }
        }

        private static bool EnsureStartupExecutable()
        {
            if (AppStoragePaths.IsCleanMode)
            {
                return false;
            }

            string source = GetPrimaryExecutablePath();
            string destination = AppStoragePaths.StartupExecutableFile;
            try
            {
                if (PathsEqual(source, destination))
                {
                    StartupCopySynchronized = File.Exists(destination);
                    return StartupCopySynchronized;
                }
                if (!File.Exists(source))
                {
                    Logger.Error("Cannot create the autostart copy because the current executable does not exist: {0}", source);
                    return false;
                }

                Directory.CreateDirectory(AppStoragePaths.StartupDirectory);
                if (File.Exists(destination) && FilesMatch(source, destination))
                {
                    StartupCopySynchronized = true;
                    return true;
                }

                string staging = Path.Combine(
                    AppStoragePaths.StartupDirectory,
                    $"Shadowsocks.exe.new.{Environment.ProcessId}.{Guid.NewGuid():N}");
                try
                {
                    File.Copy(source, staging, overwrite: true);
                    if (!FilesMatch(source, staging))
                    {
                        throw new IOException("The copied autostart executable failed SHA-256 validation.");
                    }

                    File.Move(staging, destination, overwrite: true);
                    if (!FilesMatch(source, destination))
                    {
                        throw new IOException("The installed autostart executable failed SHA-256 validation.");
                    }
                }
                finally
                {
                    TryDeleteFile(staging);
                }

                StartupCopySynchronized = true;
                Logger.Info("Installed Start with Windows executable at {0}", destination);
                return true;
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Unable to install the Start with Windows executable in LocalAppData.");
                return false;
            }
        }

        private static bool FilesMatch(string first, string second)
        {
            var firstInfo = new FileInfo(first);
            var secondInfo = new FileInfo(second);
            if (!firstInfo.Exists || !secondInfo.Exists || firstInfo.Length != secondInfo.Length)
            {
                return false;
            }

            using FileStream firstStream = File.OpenRead(first);
            using FileStream secondStream = File.OpenRead(second);
            byte[] firstHash = SHA256.HashData(firstStream);
            byte[] secondHash = SHA256.HashData(secondStream);
            return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
        }

        private static string BuildStartupCommand(bool windowVisible)
        {
            string executable = $"\"{AppStoragePaths.StartupExecutableFile}\"";
            string uiStateArgument = windowVisible ? StartupVisibleOption : StartupHiddenOption;
            string primaryExecutable = GetPrimaryExecutablePath();
            return $"{executable} {uiStateArgument} {StartupOriginOption} \"{primaryExecutable}\"";
        }

        /// <summary>
        /// Returns the user-facing product executable that should be updated. When Windows
        /// starts the stable LocalAppData startup copy, the original product path is carried
        /// in the Run command so an automatic update cannot update only the startup copy.
        /// </summary>
        internal static string GetPrimaryExecutablePath()
            => ResolvePrimaryExecutablePath(
                AppRuntimeEnvironment.ExecutablePath,
                AppStoragePaths.StartupExecutableFile,
                AppRuntimeEnvironment.Arguments);

        internal static string ResolvePrimaryExecutablePath(
            string currentExecutablePath,
            string startupExecutablePath,
            IReadOnlyList<string> arguments)
        {
            string current = Path.GetFullPath(currentExecutablePath);
            string startup = Path.GetFullPath(startupExecutablePath);
            if (!PathsEqual(current, startup))
            {
                return current;
            }

            string origin = GetOption(arguments, StartupOriginOption);
            if (string.IsNullOrWhiteSpace(origin))
            {
                return current;
            }

            try
            {
                string candidate = Path.GetFullPath(origin);
                string fileName = Path.GetFileNameWithoutExtension(candidate);
                if (File.Exists(candidate)
                    && fileName.StartsWith("Shadowsocks", StringComparison.OrdinalIgnoreCase)
                    && !PathsEqual(candidate, startup))
                {
                    return candidate;
                }
            }
            catch
            {
            }

            return current;
        }

        private static string GetOption(IReadOnlyList<string> arguments, string option)
        {
            if (arguments == null)
            {
                return null;
            }

            for (int index = 0; index < arguments.Count; index++)
            {
                string argument = arguments[index] ?? string.Empty;
                if (argument.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
                {
                    return argument.Substring(option.Length + 1).Trim('\"');
                }
                if (argument.Equals(option, StringComparison.OrdinalIgnoreCase) && index + 1 < arguments.Count)
                {
                    return (arguments[index + 1] ?? string.Empty).Trim('\"');
                }
            }
            return null;
        }

        private static bool HasLegacyStartupEntry(RegistryKey runKey, params string[] executablePaths)
        {
            foreach (string valueName in runKey.GetValueNames())
            {
                if (valueName.Equals(Key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string command = runKey.GetValue(valueName)?.ToString();
                if (executablePaths.Any(path => CommandTargetsExecutable(command, path)))
                {
                    return true;
                }
            }

            return false;
        }

        private static void RemoveDuplicateStartupEntries(RegistryKey runKey)
        {
            string startupExecutable = AppStoragePaths.StartupExecutableFile;
            string primaryExecutable = GetPrimaryExecutablePath();
            string currentExecutable = AppRuntimeEnvironment.ExecutablePath;

            foreach (string valueName in runKey.GetValueNames())
            {
                if (valueName.Equals(Key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string command = runKey.GetValue(valueName)?.ToString();
                if (CommandTargetsExecutable(command, startupExecutable)
                    || CommandTargetsExecutable(command, primaryExecutable)
                    || CommandTargetsExecutable(command, currentExecutable))
                {
                    runKey.DeleteValue(valueName, throwOnMissingValue: false);
                    Logger.Info("Removed duplicate Start with Windows registry entry '{0}'.", valueName);
                }
            }
        }

        internal static bool CommandTargetsExecutable(string command, string executablePath)
        {
            if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            string target;
            try
            {
                target = Path.GetFullPath(executablePath);
            }
            catch
            {
                return false;
            }

            string[] parsed = WindowsCommandLine.ParseArguments(command);
            if (parsed.Length > 0 && PathsEqual(parsed[0], target))
            {
                return true;
            }

            // Older Shadowsocks versions could persist an unquoted executable path.
            // Preserve compatibility long enough to remove that stale Run entry.
            string trimmed = command.Trim();
            string quotedTarget = $"\"{target}\"";
            return trimmed.Equals(target, StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals(quotedTarget, StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(quotedTarget + " ", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(target + " ", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStartupExecutableCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            string path = AppStoragePaths.StartupExecutableFile;
            string trimmed = command.Trim();
            string quoted = $"\"{path}\"";
            return trimmed.Equals(path, StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals(quoted, StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(quoted + " ", StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDeleteUnusedStartupCopy()
        {
            if (PathsEqual(AppRuntimeEnvironment.ExecutablePath, AppStoragePaths.StartupExecutableFile))
            {
                return;
            }
            TryDeleteFile(AppStoragePaths.StartupExecutableFile);
            StartupCopySynchronized = false;
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception)
            {
                Logger.Debug(exception, "Unable to remove autostart staging/copy file {0}", path);
            }
        }

        private static bool PathsEqual(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            {
                return false;
            }
            return string.Equals(
                Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        [LibraryImport("kernel32.dll", EntryPoint = "RegisterApplicationRestart", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial int RegisterApplicationRestart(string commandLineArgs, int flags);

        [LibraryImport("kernel32.dll", EntryPoint = "UnregisterApplicationRestart", SetLastError = true)]
        private static partial int UnregisterApplicationRestart();

        [Flags]
        private enum ApplicationRestartFlags
        {
            RestartAlways = 0,
            RestartNoCrash = 1,
            RestartNoHang = 2,
            RestartNoPatch = 4,
            RestartNoReboot = 8,
        }

        internal static bool IsHiddenStartup(IReadOnlyList<string> arguments)
            => HasStartupOption(arguments, StartupHiddenOption);

        internal static bool IsVisibleStartup(IReadOnlyList<string> arguments)
            => HasStartupOption(arguments, StartupVisibleOption);

        private static bool HasStartupOption(IReadOnlyList<string> arguments, string option)
            => arguments?.Any(argument =>
                string.Equals(argument, option, StringComparison.OrdinalIgnoreCase)) == true;

        internal static string BuildRestartCommandLine(IReadOnlyList<string> arguments, bool windowVisible)
        {
            var restartArguments = new List<string>();
            foreach (string argument in arguments ?? Array.Empty<string>())
            {
                if (string.Equals(argument, StartupHiddenOption, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(argument, StartupVisibleOption, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                restartArguments.Add(argument);
            }

            restartArguments.Add(windowVisible ? StartupVisibleOption : StartupHiddenOption);

            return string.Join(
                " ",
                restartArguments
                    .Select(argument => argument.Replace("\"", "\\\""))
                    .Select(argument => argument.Contains(' ') ? "\"" + argument + "\"" : argument));
        }

        /// <summary>
        /// Keeps Windows reboot/session restoration synchronized with the shell's
        /// actual visibility. Start with Windows owns restoration when enabled;
        /// otherwise Restart Manager owns it. The mechanisms remain mutually exclusive.
        /// </summary>
        public static void SynchronizeUiState(bool windowVisible)
        {
            System.Threading.Volatile.Write(ref TrackedWindowVisibility, windowVisible ? 1 : 0);
            if (AppStoragePaths.IsCleanMode)
            {
                return;
            }

            RegistryKey runKey = null;
            try
            {
                bool startupEnabled = Check();
                if (!startupEnabled)
                {
                    RegisterForRestart(true, windowVisible);
                    return;
                }

                runKey = WindowsSystemUtilities.OpenRegistryKey(RunKeyPath, true);
                if (runKey == null)
                {
                    Logger.Error(@"Cannot find HKCU\Software\Microsoft\Windows\CurrentVersion\Run");
                    RegisterForRestart(true, windowVisible);
                    return;
                }

                runKey.SetValue(Key, BuildStartupCommand(windowVisible));
                RemoveDuplicateStartupEntries(runKey);
                RegisterForRestart(false, windowVisible);
            }
            catch (Exception exception)
            {
                Logger.LogUsefulException(exception);
            }
            finally
            {
                runKey?.Dispose();
            }
        }

        internal static bool TryGetTrackedWindowVisibility(out bool windowVisible)
        {
            int tracked = System.Threading.Volatile.Read(ref TrackedWindowVisibility);
            windowVisible = tracked == 1;
            return tracked >= 0;
        }

        public static void RegisterForRestart(bool register, bool windowVisible = false)
        {
            if (AppStoragePaths.IsCleanMode)
            {
                if (!register)
                {
                    UnregisterApplicationRestart();
                }
                return;
            }

            if (!register)
            {
                UnregisterApplicationRestart();
                Logger.Debug("Unregister restart after system reboot");
                return;
            }

            string cmdline = BuildRestartCommandLine(AppRuntimeEnvironment.Arguments, windowVisible);
            int result = RegisterApplicationRestart(
                cmdline,
                (int)(ApplicationRestartFlags.RestartNoCrash | ApplicationRestartFlags.RestartNoHang));
            if (result != 0)
            {
                Logger.Warn("RegisterApplicationRestart failed with HRESULT 0x{0:X8}.", result);
                return;
            }

            Logger.Debug("Register restart after system reboot, command line: " + cmdline);
        }
    }
}
