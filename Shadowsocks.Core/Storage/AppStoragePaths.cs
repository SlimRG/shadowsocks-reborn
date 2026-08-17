using System;
using System.IO;
using System.Threading;

namespace Shadowsocks.Core.Storage
{
    public static class AppStoragePaths
    {
        public const string ProductDirectoryName = "Shadowsocks";
        private const string CleanDirectoryName = "Clean";
        private const string CleanSessionLockFileName = ".session.lock";

        private static readonly object SyncRoot = new();
        private static bool initialized;
        private static bool cleanMode;
        private static string storageRoot;
        private static string cleanSessionRoot;
        private static FileStream cleanSessionLock;

        public static string LocalAppDataRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductDirectoryName);

        public static string StorageRoot
        {
            get
            {
                EnsureInitialized();
                return storageRoot;
            }
        }

        public static bool IsCleanMode
        {
            get
            {
                EnsureInitialized();
                return cleanMode;
            }
        }

        public static string SettingsFile => Path.Combine(StorageRoot, "settings.json");
        public static string SettingsBackupFile => Path.Combine(StorageRoot, "settings.backup.json");

        public static string CacheRoot => Path.Combine(StorageRoot, "Cache");
        public static string PacCacheDirectory => Path.Combine(CacheRoot, "PAC");
        public static string OnlinePacCacheFile => Path.Combine(PacCacheDirectory, "online-pac-cache.pac");
        public static string OnlinePacMetadataFile => Path.Combine(PacCacheDirectory, "online-pac-cache.meta.json");
        public static string GeositeCacheDirectory => Path.Combine(CacheRoot, "GeoSite");

        public static string DataRoot => Path.Combine(StorageRoot, "Data");
        public static string PacDataDirectory => Path.Combine(DataRoot, "PAC");
        public static string LocalPacFile => Path.Combine(PacDataDirectory, "pac.txt");
        public static string UserRuleFile => Path.Combine(PacDataDirectory, "user-rule.txt");
        public static string UserAbpFile => Path.Combine(PacDataDirectory, "abp.txt");

        public static string LogsDirectory => Path.Combine(StorageRoot, "Logs");
        public static string LogFile => Path.Combine(LogsDirectory, "shadowsocks.log");
        public static string MigrationDirectory => Path.Combine(StorageRoot, "Migration");
        public static string RuntimeRoot => Path.Combine(StorageRoot, "Runtime");
        public static string WinDivertRuntimeRoot => Path.Combine(RuntimeRoot, "WinDivert");
        public static string StartupDirectory => Path.Combine(StorageRoot, "Startup");
        public static string StartupExecutableFile => Path.Combine(StartupDirectory, "Shadowsocks.exe");

        public static string SystemTempProductRoot => Path.Combine(Path.GetTempPath(), ProductDirectoryName);
        public static string CleanSessionsRoot => Path.Combine(SystemTempProductRoot, CleanDirectoryName);
        public static string TempRoot => Path.Combine(StorageRoot, "Temp");
        public static string TempNetworkServiceRoot => Path.Combine(TempRoot, "NetworkService");
        public static string TempUpdatesRoot => Path.Combine(TempRoot, "Updates");
        public static string TempWorkingRoot => Path.Combine(TempRoot, "Working");
        public static string TempStartupLogsRoot => Path.Combine(TempRoot, "StartupLogs");

        public static void Initialize(string executablePath)
        {
            lock (SyncRoot)
            {
                if (initialized)
                    return;

                CleanupStaleCleanSessions(TimeSpan.FromDays(2));

                cleanMode = IsCleanModeExecutableName(executablePath);
                if (cleanMode)
                {
                    string sessionName = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}";
                    cleanSessionRoot = Path.Combine(CleanSessionsRoot, sessionName);
                    Directory.CreateDirectory(cleanSessionRoot);
                    string lockPath = Path.Combine(cleanSessionRoot, CleanSessionLockFileName);
                    cleanSessionLock = new FileStream(
                        lockPath,
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.Read,
                        bufferSize: 1,
                        FileOptions.DeleteOnClose);
                    storageRoot = cleanSessionRoot;
                    AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                }
                else
                {
                    storageRoot = LocalAppDataRoot;
                }

                initialized = true;
            }
        }

        public static bool IsCleanModeExecutableName(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                return false;

            string fileName = Path.GetFileNameWithoutExtension(executablePath.Trim());
            return !string.IsNullOrWhiteSpace(fileName)
                && fileName.EndsWith("p", StringComparison.OrdinalIgnoreCase);
        }

        public static void EnsureStorageDirectories()
        {
            Directory.CreateDirectory(StorageRoot);
            Directory.CreateDirectory(CacheRoot);
            Directory.CreateDirectory(PacCacheDirectory);
            Directory.CreateDirectory(GeositeCacheDirectory);
            Directory.CreateDirectory(DataRoot);
            Directory.CreateDirectory(PacDataDirectory);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(MigrationDirectory);
            Directory.CreateDirectory(RuntimeRoot);
            Directory.CreateDirectory(TempRoot);

            if (!IsCleanMode)
            {
                Directory.CreateDirectory(StartupDirectory);
            }
        }

        public static string EnsureTempDirectory(string path)
        {
            Directory.CreateDirectory(path);
            return path;
        }

        public static void CleanupCleanSession()
        {
            string sessionRoot;
            FileStream sessionLock;

            lock (SyncRoot)
            {
                if (!initialized || !cleanMode || string.IsNullOrWhiteSpace(cleanSessionRoot))
                    return;

                sessionRoot = cleanSessionRoot;
                sessionLock = cleanSessionLock;
                cleanSessionLock = null;
                cleanSessionRoot = null;
            }

            try
            {
                sessionLock?.Dispose();
            }
            catch
            {
            }

            TryDeleteDirectory(sessionRoot);
        }

        public static void CleanupStaleCleanSessions(TimeSpan retention)
        {
            string root = CleanSessionsRoot;
            if (!Directory.Exists(root))
                return;

            DateTime cutoffUtc = DateTime.UtcNow - retention;
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) > cutoffUtc || IsCleanSessionActive(directory))
                        continue;

                    Directory.Delete(directory, recursive: true);
                }
                catch
                {
                }
            }
        }

        private static void EnsureInitialized()
        {
            if (initialized)
                return;

            Initialize(Environment.ProcessPath ?? AppContext.BaseDirectory);
        }

        private static bool IsCleanSessionActive(string directory)
        {
            string lockPath = Path.Combine(directory, CleanSessionLockFileName);
            if (!File.Exists(lockPath))
                return false;

            try
            {
                using FileStream _ = new FileStream(
                    lockPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
                catch
                {
                    return;
                }
            }
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            CleanupCleanSession();
        }
    }
}
