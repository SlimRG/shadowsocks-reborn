using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using NLog;
using Shadowsocks.Core;

namespace Shadowsocks.Controller.Service
{
    /// <summary>
    /// Implements the single-file updater handoff without shipping a separate updater binary.
    /// The downloaded new Shadowsocks.exe is staged as Shadowsocks.Update.exe, replaces the
    /// old executable after it exits, starts the installed new copy, and the installed copy
    /// then removes the temporary updater transaction.
    /// </summary>
    public static class SelfUpdater
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static int updateHandoffStarted;

        internal const string UpdateSwitch = "--update";
        internal const string CleanupSwitch = "--update-cleanup";
        internal const string TargetOption = "--update-target";
        internal const string WaitPidOption = "--update-wait-pid";
        internal const string TransactionOption = "--update-transaction";
        internal const string BackupOption = "--update-backup";
        internal const string UpdaterPidOption = "--update-updater-pid";
        internal const string PayloadSha256Option = "--update-sha256";
        internal const string ResumeHiddenSwitch = "--update-resume-hidden";
        internal const string ResumeVisibleSwitch = "--update-resume-visible";
        internal const string TemporaryUpdaterFileName = "Shadowsocks.Update.exe";

        private const int ProcessExitTimeoutMilliseconds = 60000;
        private const int CleanupRetryCount = 20;
        private const int CleanupRetryDelayMilliseconds = 250;

        public static string UpdatesRoot => Path.Combine(
            Path.GetTempPath(),
            "Shadowsocks",
            "Updates");

        /// <summary>
        /// Executes the internal updater process mode before WinUI/AppInstance initialization.
        /// </summary>
        public static bool TryRunUpdaterMode(IReadOnlyList<string> arguments, out int exitCode)
        {
            exitCode = 0;
            if (!ContainsSwitch(arguments, UpdateSwitch))
            {
                return false;
            }

            try
            {
                string targetPath = GetRequiredOption(arguments, TargetOption);
                string transactionDirectory = GetRequiredOption(arguments, TransactionOption);
                int waitPid = ParseRequiredPid(arguments, WaitPidOption);
                string expectedPayloadSha256 = GetRequiredOption(arguments, PayloadSha256Option);
                bool resumeHidden = ContainsSwitch(arguments, ResumeHiddenSwitch);
                bool resumeVisible = !resumeHidden && ContainsSwitch(arguments, ResumeVisibleSwitch);
                exitCode = RunUpdater(targetPath, transactionDirectory, waitPid, expectedPayloadSha256, resumeHidden, resumeVisible);
            }
            catch (Exception exception)
            {
                try
                {
                    Logger.Error(exception, "Application self-update failed in updater mode.");
                }
                catch
                {
                }

                exitCode = 20;
            }

            return true;
        }

        /// <summary>
        /// Runs in the installed new copy. It waits for the temporary updater to exit and then
        /// removes the updater transaction and rollback copy before normal application startup.
        /// Cleanup is best-effort: a successful update must not be made unusable by antivirus
        /// or delayed file-handle release.
        /// </summary>
        public static void CleanupCompletedUpdate(IReadOnlyList<string> arguments)
        {
            if (!ContainsSwitch(arguments, CleanupSwitch))
            {
                return;
            }

            string transactionDirectory = GetOptionalOption(arguments, TransactionOption);
            string backupPath = GetOptionalOption(arguments, BackupOption);
            int updaterPid = ParseOptionalPid(arguments, UpdaterPidOption);

            if (updaterPid > 0 && updaterPid != Environment.ProcessId)
            {
                WaitForProcessExit(updaterPid, ProcessExitTimeoutMilliseconds);
            }

            if (!string.IsNullOrWhiteSpace(backupPath) && IsCanonicalBackupPath(backupPath))
            {
                TryDeleteFileWithRetry(backupPath);
            }

            if (!string.IsNullOrWhiteSpace(transactionDirectory))
            {
                TryDeleteDirectoryWithRetry(transactionDirectory);
            }

            CleanupStaleTransactions(TimeSpan.FromDays(7));
        }

        /// <summary>
        /// Removes updater-only switches before the normal application runtime stores or forwards
        /// its command line.
        /// </summary>
        public static string[] RemoveInternalArguments(IReadOnlyList<string> arguments)
        {
            if (arguments == null || arguments.Count == 0)
            {
                return Array.Empty<string>();
            }

            var result = new List<string>(arguments.Count);
            for (int index = 0; index < arguments.Count; index++)
            {
                string argument = arguments[index] ?? string.Empty;
                if (argument.Equals(UpdateSwitch, StringComparison.OrdinalIgnoreCase)
                    || argument.Equals(CleanupSwitch, StringComparison.OrdinalIgnoreCase)
                    || argument.Equals(ResumeHiddenSwitch, StringComparison.OrdinalIgnoreCase)
                    || argument.Equals(ResumeVisibleSwitch, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsInternalOption(argument))
                {
                    if (!argument.Contains('=') && index + 1 < arguments.Count)
                    {
                        index++;
                    }
                    continue;
                }

                result.Add(argument);
            }

            return result.ToArray();
        }

        public static string CreateTransactionDirectory()
        {
            Directory.CreateDirectory(UpdatesRoot);
            CleanupStaleTransactions(TimeSpan.FromDays(7));

            string directory = Path.Combine(
                UpdatesRoot,
                $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            return directory;
        }

        public static string GetTemporaryUpdaterPath(string transactionDirectory)
        {
            if (string.IsNullOrWhiteSpace(transactionDirectory))
            {
                throw new ArgumentException("Update transaction directory is required.", nameof(transactionDirectory));
            }

            return Path.Combine(Path.GetFullPath(transactionDirectory), TemporaryUpdaterFileName);
        }

        /// <summary>
        /// Starts the staged new executable in updater mode. The caller must shut down the current
        /// application only after this method returns successfully.
        /// </summary>
        public static Process LaunchStagedUpdater(
            string stagedUpdaterPath,
            string targetExecutablePath,
            string transactionDirectory,
            string expectedPayloadSha256)
        {
            if (Interlocked.CompareExchange(ref updateHandoffStarted, 1, 0) != 0)
            {
                throw new InvalidOperationException("An application update handoff is already in progress.");
            }

            try
            {
                string updaterPath = Path.GetFullPath(stagedUpdaterPath ?? string.Empty);
                string targetPath = Path.GetFullPath(targetExecutablePath ?? string.Empty);
                string transactionPath = Path.GetFullPath(transactionDirectory ?? string.Empty);

                ValidateExecutablePath(updaterPath, nameof(stagedUpdaterPath));
                ValidateTargetPath(targetPath);
                ValidateTransactionPath(transactionPath, updaterPath);
                string normalizedPayloadSha256 = NormalizeSha256(expectedPayloadSha256);

                // Keep the verified staged executable read-only/non-deletable across Process.Start,
                // including the UAC prompt. This closes the verify-to-elevate TOCTOU window for
                // a payload staged below the current user's system-temp directory.
                using FileStream stagedReadLock = new(updaterPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                VerifySha256(stagedReadLock, normalizedPayloadSha256);

                string targetDirectory = Path.GetDirectoryName(targetPath)
                    ?? throw new InvalidOperationException("The target executable directory is unavailable.");

                bool requiresElevation = !CanWriteDirectory(targetDirectory);
                var startInfo = new ProcessStartInfo
                {
                    FileName = updaterPath,
                    WorkingDirectory = transactionPath,
                    UseShellExecute = requiresElevation,
                };
                if (requiresElevation)
                {
                    startInfo.Verb = "runas";
                }

                startInfo.ArgumentList.Add(UpdateSwitch);
                startInfo.ArgumentList.Add(TargetOption);
                startInfo.ArgumentList.Add(targetPath);
                startInfo.ArgumentList.Add(WaitPidOption);
                startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add(TransactionOption);
                startInfo.ArgumentList.Add(transactionPath);
                startInfo.ArgumentList.Add(PayloadSha256Option);
                startInfo.ArgumentList.Add(normalizedPayloadSha256);
                bool resumeHidden;
                bool resumeVisible;
                if (AutoStartup.TryGetTrackedWindowVisibility(out bool windowVisible))
                {
                    resumeVisible = windowVisible;
                    resumeHidden = !windowVisible;
                }
                else
                {
                    resumeHidden = RuntimeEnvironment.Arguments.Any(argument =>
                        string.Equals(argument, AutoStartup.StartupHiddenOption, StringComparison.OrdinalIgnoreCase));
                    resumeVisible = !resumeHidden && RuntimeEnvironment.Arguments.Any(argument =>
                        string.Equals(argument, AutoStartup.StartupVisibleOption, StringComparison.OrdinalIgnoreCase));
                }

                if (resumeHidden)
                {
                    startInfo.ArgumentList.Add(ResumeHiddenSwitch);
                }
                else if (resumeVisible)
                {
                    startInfo.ArgumentList.Add(ResumeVisibleSwitch);
                }

                Process process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("The temporary update process could not be started.");

                Logger.Info(
                    "Started application updater process {0} for {1}{2}.",
                    process.Id,
                    targetPath,
                    requiresElevation ? " with elevation" : string.Empty);
                return process;
            }
            catch
            {
                Interlocked.Exchange(ref updateHandoffStarted, 0);
                throw;
            }
        }

        internal static bool IsCanonicalTransactionDirectory(string transactionDirectory)
        {
            if (string.IsNullOrWhiteSpace(transactionDirectory))
            {
                return false;
            }

            string root = EnsureTrailingSeparator(Path.GetFullPath(UpdatesRoot));
            string candidate = EnsureTrailingSeparator(Path.GetFullPath(transactionDirectory));
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase);
        }

        internal static void CleanupStaleTransactions(TimeSpan retention)
        {
            try
            {
                if (!Directory.Exists(UpdatesRoot))
                {
                    return;
                }

                DateTime cutoffUtc = DateTime.UtcNow - retention;
                foreach (string directory in Directory.EnumerateDirectories(UpdatesRoot))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(directory) >= cutoffUtc)
                        {
                            continue;
                        }

                        Directory.Delete(directory, recursive: true);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static int RunUpdater(
            string targetPath,
            string transactionDirectory,
            int waitPid,
            string expectedPayloadSha256,
            bool resumeHidden,
            bool resumeVisible)
        {
            string updaterPath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Unable to resolve the updater executable path.");

            updaterPath = Path.GetFullPath(updaterPath);
            targetPath = Path.GetFullPath(targetPath);
            transactionDirectory = Path.GetFullPath(transactionDirectory);

            ValidateExecutablePath(updaterPath, "updaterPath");
            ValidateTargetPath(targetPath);
            ValidateTransactionPath(transactionDirectory, updaterPath);
            using (FileStream updaterStream = new(updaterPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                VerifySha256(updaterStream, expectedPayloadSha256);
            }

            if (waitPid == Environment.ProcessId)
            {
                throw new InvalidOperationException("Updater cannot wait for its own process ID.");
            }

            if (!WaitForProcessExit(waitPid, ProcessExitTimeoutMilliseconds))
            {
                throw new TimeoutException($"The previous Shadowsocks process {waitPid} did not exit in time.");
            }

            string targetDirectory = Path.GetDirectoryName(targetPath)
                ?? throw new InvalidOperationException("The target executable directory is unavailable.");
            Directory.CreateDirectory(targetDirectory);

            string transactionId = Path.GetFileName(transactionDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string incomingPath = Path.Combine(targetDirectory, $".{Path.GetFileName(targetPath)}.{transactionId}.update-new");
            string backupPath = Path.Combine(targetDirectory, $".{Path.GetFileName(targetPath)}.{transactionId}.update-backup");

            TryDeleteFile(incomingPath);
            TryDeleteFile(backupPath);

            bool hadOriginal = File.Exists(targetPath);
            try
            {
                if (hadOriginal)
                {
                    EnsureStrictlyNewerReplacement(updaterPath, targetPath);
                }

                File.Copy(updaterPath, incomingPath, overwrite: true);
                EnsureFilesMatch(updaterPath, incomingPath);

                if (hadOriginal)
                {
                    // Re-read the target immediately before replacement as a second downgrade
                    // barrier in case another process changed the installed executable after
                    // the first validation.
                    EnsureStrictlyNewerReplacement(updaterPath, targetPath);
                    try
                    {
                        File.Replace(incomingPath, targetPath, backupPath, ignoreMetadataErrors: true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        ReplaceWithMoveFallback(incomingPath, targetPath, backupPath);
                    }
                    catch (IOException)
                    {
                        ReplaceWithMoveFallback(incomingPath, targetPath, backupPath);
                    }
                }
                else
                {
                    File.Move(incomingPath, targetPath);
                }

                EnsureFilesMatch(updaterPath, targetPath);

                var startInfo = new ProcessStartInfo
                {
                    FileName = targetPath,
                    WorkingDirectory = targetDirectory,
                    UseShellExecute = false,
                };
                startInfo.ArgumentList.Add(CleanupSwitch);
                startInfo.ArgumentList.Add(TransactionOption);
                startInfo.ArgumentList.Add(transactionDirectory);
                startInfo.ArgumentList.Add(BackupOption);
                startInfo.ArgumentList.Add(backupPath);
                startInfo.ArgumentList.Add(UpdaterPidOption);
                startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                string resumeStartupOption = ResolveResumeStartupOption(resumeHidden, resumeVisible);
                if (!string.IsNullOrEmpty(resumeStartupOption))
                {
                    startInfo.ArgumentList.Add(resumeStartupOption);
                }

                if (Process.Start(startInfo) is null)
                {
                    throw new InvalidOperationException("The installed updated executable could not be started.");
                }

                return 0;
            }
            catch
            {
                TryDeleteFile(incomingPath);
                if (hadOriginal)
                {
                    try
                    {
                        // File.Replace/fallback may fail before the new payload is fully installed.
                        // If a rollback copy exists, restore it; otherwise the untouched target is
                        // still the previous executable and can simply be started again.
                        if (File.Exists(backupPath))
                        {
                            File.Copy(backupPath, targetPath, overwrite: true);
                        }

                        if (File.Exists(targetPath))
                        {
                            TryStartRollback(targetPath, targetDirectory, resumeHidden, resumeVisible);
                        }
                    }
                    catch (Exception rollbackException)
                    {
                        try
                        {
                            Logger.Error(rollbackException, "Failed to restore the previous executable after an update failure.");
                        }
                        catch
                        {
                        }
                    }
                }

                throw;
            }
        }

        internal static bool IsStrictlyNewerReplacement(Version payloadVersion, Version installedVersion)
        {
            ArgumentNullException.ThrowIfNull(payloadVersion);
            ArgumentNullException.ThrowIfNull(installedVersion);
            return payloadVersion.CompareTo(installedVersion) > 0;
        }

        private static void EnsureStrictlyNewerReplacement(string payloadPath, string installedPath)
        {
            Version payloadVersion = GetExecutableVersion(payloadPath);
            Version installedVersion = GetExecutableVersion(installedPath);
            if (!IsStrictlyNewerReplacement(payloadVersion, installedVersion))
            {
                throw new InvalidDataException(
                    $"Refusing to replace installed version {installedVersion} with non-newer update payload {payloadVersion}.");
            }
        }

        private static Version GetExecutableVersion(string executablePath)
        {
            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
            return new Version(
                Math.Max(0, versionInfo.FileMajorPart),
                Math.Max(0, versionInfo.FileMinorPart),
                Math.Max(0, versionInfo.FileBuildPart),
                Math.Max(0, versionInfo.FilePrivatePart));
        }

        private static void ReplaceWithMoveFallback(string incomingPath, string targetPath, string backupPath)
        {
            if (File.Exists(targetPath))
            {
                File.Copy(targetPath, backupPath, overwrite: true);
            }
            File.Move(incomingPath, targetPath, overwrite: true);
        }

        internal static string ResolveResumeStartupOption(bool resumeHidden, bool resumeVisible)
            => resumeHidden
                ? AutoStartup.StartupHiddenOption
                : resumeVisible ? AutoStartup.StartupVisibleOption : string.Empty;

        private static void TryStartRollback(string targetPath, string workingDirectory, bool resumeHidden, bool resumeVisible)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = targetPath,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                };
                string resumeStartupOption = ResolveResumeStartupOption(resumeHidden, resumeVisible);
                if (!string.IsNullOrEmpty(resumeStartupOption))
                {
                    startInfo.ArgumentList.Add(resumeStartupOption);
                }

                Process.Start(startInfo);
            }
            catch
            {
            }
        }

        private static void ValidateExecutablePath(string executablePath, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                throw new FileNotFoundException("The update executable does not exist.", executablePath);
            }
            if (!string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The update payload must be an .exe file.", parameterName);
            }
        }

        private static void ValidateTargetPath(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw new ArgumentException("The target executable path is required.", nameof(targetPath));
            }
            if (!string.Equals(Path.GetExtension(targetPath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The update target must be an .exe file.");
            }

            string fileName = Path.GetFileNameWithoutExtension(targetPath);
            if (!fileName.StartsWith("Shadowsocks", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The self-updater only replaces a Shadowsocks product executable.");
            }
        }

        private static void ValidateTransactionPath(string transactionDirectory, string updaterPath)
        {
            if (!IsCanonicalTransactionDirectory(transactionDirectory))
            {
                throw new InvalidDataException("The update transaction directory is outside the Shadowsocks system-temp update root.");
            }

            string expectedUpdater = Path.GetFullPath(GetTemporaryUpdaterPath(transactionDirectory));
            if (!string.Equals(expectedUpdater, Path.GetFullPath(updaterPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The updater executable is not the canonical file in its update transaction.");
            }
        }

        private static bool IsCanonicalBackupPath(string backupPath)
        {
            try
            {
                string executablePath = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? string.Empty;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    return false;
                }

                string fullBackup = Path.GetFullPath(backupPath);
                string executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? string.Empty;
                string backupDirectory = Path.GetDirectoryName(fullBackup) ?? string.Empty;
                string expectedPrefix = "." + Path.GetFileName(executablePath) + ".";
                string backupName = Path.GetFileName(fullBackup);
                return string.Equals(executableDirectory, backupDirectory, StringComparison.OrdinalIgnoreCase)
                    && backupName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
                    && backupName.EndsWith(".update-backup", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool CanWriteDirectory(string directory)
        {
            string probe = Path.Combine(directory, $".shadowsocks-update-write-test-{Guid.NewGuid():N}.tmp");
            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                }
                TryDeleteFile(probe);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (SecurityException)
            {
                return false;
            }
        }

        private static bool WaitForProcessExit(int processId, int timeoutMilliseconds)
        {
            if (processId <= 0)
            {
                return true;
            }

            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
                return process.WaitForExit(timeoutMilliseconds);
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        internal static string ComputeSha256Hex(string path)
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }

        internal static void VerifySha256(Stream stream, string expectedSha256)
        {
            ArgumentNullException.ThrowIfNull(stream);

            string normalized = NormalizeSha256(expectedSha256);
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            byte[] actualHash = System.Security.Cryptography.SHA256.HashData(stream);
            byte[] expectedHash = Convert.FromHexString(normalized);
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                throw new InvalidDataException("The staged update executable no longer matches its verified SHA-256 digest.");
            }
        }

        private static string NormalizeSha256(string value)
        {
            string normalized = (value ?? string.Empty).Trim();
            if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("The staged update SHA-256 digest is invalid.");
            }
            return normalized.ToUpperInvariant();
        }

        private static void EnsureFilesMatch(string expectedPath, string actualPath)
        {
            var expected = new FileInfo(expectedPath);
            var actual = new FileInfo(actualPath);
            if (!expected.Exists || !actual.Exists || expected.Length != actual.Length)
            {
                throw new IOException("The copied update executable does not match the staged payload.");
            }

            using FileStream expectedStream = File.OpenRead(expectedPath);
            using FileStream actualStream = File.OpenRead(actualPath);
            byte[] expectedHash = System.Security.Cryptography.SHA256.HashData(expectedStream);
            byte[] actualHash = System.Security.Cryptography.SHA256.HashData(actualStream);
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            {
                throw new IOException("The copied update executable failed SHA-256 verification.");
            }
        }

        private static bool ContainsSwitch(IReadOnlyList<string> arguments, string expected)
        {
            return arguments != null && arguments.Any(argument =>
                string.Equals(argument, expected, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsInternalOption(string argument)
        {
            return IsOption(argument, TargetOption)
                || IsOption(argument, WaitPidOption)
                || IsOption(argument, TransactionOption)
                || IsOption(argument, BackupOption)
                || IsOption(argument, UpdaterPidOption)
                || IsOption(argument, PayloadSha256Option);
        }

        private static bool IsOption(string argument, string option)
        {
            return argument.Equals(option, StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetRequiredOption(IReadOnlyList<string> arguments, string option)
        {
            string value = GetOptionalOption(arguments, option);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException($"Required update argument '{option}' is missing.");
            }
            return value;
        }

        private static string GetOptionalOption(IReadOnlyList<string> arguments, string option)
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
                    return argument.Substring(option.Length + 1).Trim('"');
                }
                if (argument.Equals(option, StringComparison.OrdinalIgnoreCase) && index + 1 < arguments.Count)
                {
                    return (arguments[index + 1] ?? string.Empty).Trim('"');
                }
            }
            return null;
        }

        private static int ParseRequiredPid(IReadOnlyList<string> arguments, string option)
        {
            string value = GetRequiredOption(arguments, option);
            if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid)
                || pid <= 0)
            {
                throw new InvalidDataException($"Update argument '{option}' contains an invalid process ID.");
            }
            return pid;
        }

        private static int ParseOptionalPid(IReadOnlyList<string> arguments, string option)
        {
            string value = GetOptionalOption(arguments, option);
            return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid)
                ? pid
                : 0;
        }

        private static string EnsureTrailingSeparator(string path)
        {
            string fullPath = Path.GetFullPath(path);
            return fullPath.EndsWith(Path.DirectorySeparatorChar)
                ? fullPath
                : fullPath + Path.DirectorySeparatorChar;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteFileWithRetry(string path)
        {
            for (int attempt = 0; attempt < CleanupRetryCount; attempt++)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        return;
                    }
                    File.Delete(path);
                    return;
                }
                catch when (attempt + 1 < CleanupRetryCount)
                {
                    Thread.Sleep(CleanupRetryDelayMilliseconds);
                }
                catch
                {
                    return;
                }
            }
        }

        private static void TryDeleteDirectoryWithRetry(string directory)
        {
            if (!IsCanonicalTransactionDirectory(directory))
            {
                return;
            }

            for (int attempt = 0; attempt < CleanupRetryCount; attempt++)
            {
                try
                {
                    if (!Directory.Exists(directory))
                    {
                        return;
                    }
                    Directory.Delete(directory, recursive: true);
                    return;
                }
                catch when (attempt + 1 < CleanupRetryCount)
                {
                    Thread.Sleep(CleanupRetryDelayMilliseconds);
                }
                catch
                {
                    return;
                }
            }
        }
    }
}
