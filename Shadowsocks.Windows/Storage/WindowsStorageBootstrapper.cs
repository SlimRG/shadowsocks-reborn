using System;
using System.IO;
using NLog;
using Shadowsocks.Core.Storage;
using Shadowsocks.Model;

namespace Shadowsocks.Windows.Storage
{
    public static class WindowsStorageBootstrapper
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static bool initialized;

        public static void Initialize()
        {
            if (initialized)
            {
                return;
            }

            AppStoragePaths.EnsureStorageDirectories();
            Configuration.ConfigureSettingsStore(
                new JsonFileSettingsStore(AppStoragePaths.SettingsFile, AppStoragePaths.SettingsBackupFile));

            CleanupStaleTempData();
            initialized = true;
        }

        private static void CleanupStaleTempData()
        {
            AppStoragePaths.CleanupStaleCleanSessions(TimeSpan.FromDays(2));
            PruneTempChildren(AppStoragePaths.TempStartupLogsRoot, TimeSpan.FromDays(2));
            PruneTempChildren(AppStoragePaths.TempUpdatesRoot, TimeSpan.FromDays(7));
            PruneTempChildren(AppStoragePaths.TempWorkingRoot, TimeSpan.FromDays(7));
            Shadowsocks.Controller.Traffic.NetworkServiceRuntime.CleanupStaleRuntime();
        }

        private static void PruneTempChildren(string root, TimeSpan retention)
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            DateTime cutoffUtc = DateTime.UtcNow - retention;
            foreach (string entry in Directory.EnumerateFileSystemEntries(root))
            {
                try
                {
                    DateTime lastWriteUtc = File.Exists(entry)
                        ? File.GetLastWriteTimeUtc(entry)
                        : Directory.GetLastWriteTimeUtc(entry);
                    if (lastWriteUtc > cutoffUtc)
                    {
                        continue;
                    }

                    if (File.Exists(entry))
                    {
                        File.Delete(entry);
                    }
                    else if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                }
                catch (Exception exception)
                {
                    Logger.Debug(exception, "Unable to prune stale temp entry {0}.", entry);
                }
            }
        }
    }
}
