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

namespace Shadowsocks.Controller
{
    public static partial class AutoStartup
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Key = "Shadowsocks Reborn";
        private const string LegacyKeyPrefix = "shadowsocks-reborn_";
        private const string StartupArguments = "--start-hidden";
        private static bool StartupCopySynchronized;

        public static bool Set(bool enabled, string arguments = null)
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

                CleanupLegacyRunValues(runKey);
                if (enabled)
                {
                    if (!EnsureStartupExecutable())
                    {
                        return false;
                    }

                    runKey.SetValue(Key, BuildStartupCommand(arguments));
                }
                else
                {
                    runKey.DeleteValue(Key, throwOnMissingValue: false);
                    TryDeleteUnusedStartupCopy();
                }

                // When autostartup setting changes, change RegisterForRestart state to avoid starting twice.
                RegisterForRestart(!enabled);
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

                bool legacyStartupWasEnabled = CleanupLegacyRunValues(runKey);
                string command = runKey.GetValue(Key)?.ToString();
                if (!IsStartupExecutableCommand(command) && legacyStartupWasEnabled)
                {
                    if (!EnsureStartupExecutable())
                    {
                        return false;
                    }
                    command = BuildStartupCommand(StartupArguments);
                    runKey.SetValue(Key, command);
                    Logger.Info("Migrated legacy Start with Windows registration to the LocalAppData executable copy.");
                }
                if (!IsStartupExecutableCommand(command))
                {
                    return false;
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

                string canonical = BuildStartupCommand(StartupArguments);
                if (!string.Equals(command?.Trim(), canonical, StringComparison.OrdinalIgnoreCase))
                {
                    runKey.SetValue(Key, canonical);
                }
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

            string source = AppRuntimeEnvironment.ExecutablePath;
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

        private static string BuildStartupCommand(string arguments)
        {
            string executable = $"\"{AppStoragePaths.StartupExecutableFile}\"";
            string normalizedArguments = string.IsNullOrWhiteSpace(arguments) ? StartupArguments : arguments.Trim();
            return $"{executable} {normalizedArguments}";
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

        private static bool CleanupLegacyRunValues(RegistryKey runKey)
        {
            bool hadLegacyValue = false;
            foreach (string valueName in runKey.GetValueNames())
            {
                if (valueName.StartsWith(LegacyKeyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    hadLegacyValue |= !string.IsNullOrWhiteSpace(runKey.GetValue(valueName)?.ToString());
                    runKey.DeleteValue(valueName, throwOnMissingValue: false);
                }
            }
            return hadLegacyValue;
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

        public static void RegisterForRestart(bool register)
        {
            if (AppStoragePaths.IsCleanMode)
            {
                if (!register)
                {
                    UnregisterApplicationRestart();
                }
                return;
            }

            if (register && !Check())
            {
                string[] args = new List<string>(AppRuntimeEnvironment.Arguments)
                    .Select(p => p.Replace("\"", "\\\""))
                    .Select(p => p.IndexOf(' ') >= 0 ? "\"" + p + "\"" : p)
                    .ToArray();
                string cmdline = string.Join(" ", args);
                RegisterApplicationRestart(
                    cmdline,
                    (int)(ApplicationRestartFlags.RestartNoCrash | ApplicationRestartFlags.RestartNoHang));
                Logger.Debug("Register restart after system reboot, command line:" + cmdline);
            }
            else if (!register)
            {
                UnregisterApplicationRestart();
                Logger.Debug("Unregister restart after system reboot");
            }
        }
    }
}
