using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using NLog;
using Shadowsocks.Core;
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
                return;

            AppStoragePaths.EnsureStorageDirectories();
            var store = new JsonFileSettingsStore(AppStoragePaths.SettingsFile, AppStoragePaths.SettingsBackupFile);
            Configuration.ConfigureSettingsStore(store);

            if (!AppStoragePaths.IsCleanMode)
            {
                MigrateLegacySidecarData();
                MigrateLegacyConfiguration(store);
                RemoveObsoleteLocalizationOverride();
            }

            CleanupStaleTempData();
            initialized = true;
        }

        private static void MigrateLegacyConfiguration(ISettingsStore store)
        {
            if (store.TryGetString(Configuration.SettingsValueName, out string existing)
                && !string.IsNullOrWhiteSpace(existing)
                && Configuration.TryDeserialize(existing, out _))
            {
                return;
            }

            string legacyPath = Configuration.LegacyConfigFilePath;
            if (!File.Exists(legacyPath))
                return;

            try
            {
                string json = File.ReadAllText(legacyPath);
                if (!Configuration.TryDeserialize(json, out Configuration config))
                    throw new InvalidDataException("Legacy gui-config.json could not be parsed.");

                string backupPath = CreateMigrationBackupPath("gui-config", ".json");
                File.Copy(legacyPath, backupPath, overwrite: false);

                Configuration.Save(config);
                string canonical = JsonConvert.SerializeObject(config, Formatting.Indented);
                store.SetString(Configuration.SettingsBackupValueName, canonical);

                TryDeleteLegacyPath(legacyPath, "legacy gui-config.json");
                Logger.Info("Migrated legacy gui-config.json to {0}. Backup: {1}", AppStoragePaths.SettingsFile, backupPath);
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Legacy gui-config.json migration failed. The source file was left untouched.");
            }
        }

        private static void MigrateLegacySidecarData()
        {
            string root = RuntimeEnvironment.WorkingDirectory;
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["pac.txt"] = AppStoragePaths.LocalPacFile,
                ["user-rule.txt"] = AppStoragePaths.UserRuleFile,
                ["abp.txt"] = AppStoragePaths.UserAbpFile,
                ["online-pac-cache.pac"] = AppStoragePaths.OnlinePacCacheFile,
                ["online-pac-cache.meta.json"] = AppStoragePaths.OnlinePacMetadataFile,
            };

            foreach ((string fileName, string destination) in files)
            {
                string source = Path.Combine(root, fileName);
                TryMigrateFile(source, destination);
            }

            string oldGeositeCache = Path.Combine(root, "geosite-cache");
            if (Directory.Exists(oldGeositeCache))
            {
                foreach (string source in Directory.EnumerateFiles(oldGeositeCache, "*", SearchOption.TopDirectoryOnly))
                {
                    TryMigrateFile(source, Path.Combine(AppStoragePaths.GeositeCacheDirectory, Path.GetFileName(source)));
                }
                TryDeleteEmptyDirectory(oldGeositeCache);
            }

            string oldTempLog = Path.Combine(root, "ss_win_temp", "shadowsocks.log");
            TryMigrateFile(oldTempLog, AppStoragePaths.LogFile);
            TryDeleteEmptyDirectory(Path.Combine(root, "ss_win_temp"));
        }

        private static void RemoveObsoleteLocalizationOverride()
        {
            // Localization is embedded in Shadowsocks.exe. Older Phase-10 builds could leave
            // a second i18n.csv in LocalAppData; it is deliberately ignored and removed.
            string obsoletePath = Path.Combine(AppStoragePaths.LocalAppDataRoot, "Data", "i18n.csv");
            try
            {
                if (File.Exists(obsoletePath))
                {
                    File.Delete(obsoletePath);
                    Logger.Info("Removed obsolete external localization catalog: {0}", obsoletePath);
                }
            }
            catch (Exception exception)
            {
                Logger.Debug(exception, "Unable to remove obsolete external localization catalog {0}.", obsoletePath);
            }
        }

        private static void TryMigrateFile(string source, string destination)
        {
            if (!File.Exists(source))
                return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!File.Exists(destination))
                    File.Copy(source, destination, overwrite: false);
                TryDeleteLegacyPath(source, Path.GetFileName(source));
            }
            catch (Exception exception)
            {
                Logger.Warn(exception, "Unable to migrate legacy sidecar {0} to {1}.", source, destination);
            }
        }

        private static string CreateMigrationBackupPath(string stem, string extension)
        {
            Directory.CreateDirectory(AppStoragePaths.MigrationDirectory);
            string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
            return Path.Combine(AppStoragePaths.MigrationDirectory, $"{stem}-{timestamp}{extension}");
        }

        private static void TryDeleteLegacyPath(string path, string description)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception exception)
            {
                Logger.Warn(exception, "Unable to remove {0} after migration: {1}", description, path);
            }
        }

        private static void TryDeleteEmptyDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0)
                    Directory.Delete(path);
            }
            catch
            {
            }
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
                return;

            DateTime cutoffUtc = DateTime.UtcNow - retention;
            foreach (string entry in Directory.EnumerateFileSystemEntries(root))
            {
                try
                {
                    DateTime lastWriteUtc = File.Exists(entry)
                        ? File.GetLastWriteTimeUtc(entry)
                        : Directory.GetLastWriteTimeUtc(entry);
                    if (lastWriteUtc > cutoffUtc)
                        continue;

                    if (File.Exists(entry))
                        File.Delete(entry);
                    else if (Directory.Exists(entry))
                        Directory.Delete(entry, recursive: true);
                }
                catch (Exception exception)
                {
                    Logger.Debug(exception, "Unable to prune stale temp entry {0}.", entry);
                }
            }
        }
    }
}
