using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NLog;
using Shadowsocks.Controller.Service;
using Shadowsocks.Model;
using Shadowsocks.Core.Storage;

namespace Shadowsocks.Controller
{
    /// <summary>
    /// Processing the locally generated PAC file content.
    /// GeoSite is only touched while Local PAC is active.
    /// </summary>
    public class PACDaemon : IDisposable
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public static string PAC_FILE => AppStoragePaths.LocalPacFile;
        public static string USER_RULE_FILE => AppStoragePaths.UserRuleFile;
        public static string USER_ABP_FILE => AppStoragePaths.UserAbpFile;

        private Configuration config;

        private FileSystemWatcher PACFileWatcher;
        private FileSystemWatcher UserRuleFileWatcher;

        public event EventHandler PACFileChanged;
        public event EventHandler UserRuleFileChanged;

        public PACDaemon(Configuration config)
        {
            Directory.CreateDirectory(AppStoragePaths.PacDataDirectory);
            this.config = config;
            TouchUserRuleFile();
            WatchPacFile();
            WatchUserRuleFile();
        }

        public void UpdateConfiguration(Configuration newConfig)
        {
            config = newConfig;
        }

        public string TouchPACFile()
        {
            bool sourceSetDirty = GeositeUpdater.IsSourceSetDirty;
            if (!File.Exists(PAC_FILE) || sourceSetDirty)
            {
                if (GeositeUpdater.IsDatabaseAvailable && !GeositeUpdater.NeedsRefresh)
                {
                    GeositeUpdater.MergeAndWritePACFile(
                        config.geositeDirectGroups,
                        config.geositeProxiedGroups,
                        config.geositePreferDirect);
                }
                else
                {
                    // The user explicitly asked to open Local PAC before GeoSite finished
                    // downloading, or the source list changed and its caches are not ready.
                    // Use proxy-all rather than serving rules generated from stale sources.
                    File.WriteAllText(PAC_FILE, GetBootstrapLocalPac(), Encoding.UTF8);
                }
            }
            return PAC_FILE;
        }

        internal string TouchUserRuleFile()
        {
            if (!File.Exists(USER_RULE_FILE))
                File.WriteAllText(USER_RULE_FILE, Shadowsocks.Core.EmbeddedResources.UserRule);
            return USER_RULE_FILE;
        }

        internal string GetPACContent()
        {
            bool sourceSetDirty = GeositeUpdater.IsSourceSetDirty;
            if (File.Exists(PAC_FILE) && !sourceSetDirty)
                return File.ReadAllText(PAC_FILE, Encoding.UTF8);

            if (GeositeUpdater.IsDatabaseAvailable && !GeositeUpdater.NeedsRefresh)
            {
                GeositeUpdater.MergeAndWritePACFile(
                    config.geositeDirectGroups,
                    config.geositeProxiedGroups,
                    config.geositePreferDirect);
                return File.ReadAllText(PAC_FILE, Encoding.UTF8);
            }

            // Do not serve a PAC generated from an old source set. Until every newly
            // configured source has a usable cache, proxy everything through Shadowsocks.
            return GetBootstrapLocalPac();
        }

        private static string GetBootstrapLocalPac()
            => "function FindProxyForURL(url, host) { return __PROXY__; }\n";

        private void WatchPacFile()
        {
            PACFileWatcher?.Dispose();
            PACFileWatcher = new FileSystemWatcher(AppStoragePaths.PacDataDirectory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                Filter = Path.GetFileName(PAC_FILE),
                EnableRaisingEvents = true,
            };
            PACFileWatcher.Changed += PACFileWatcher_Changed;
            PACFileWatcher.Created += PACFileWatcher_Changed;
            PACFileWatcher.Deleted += PACFileWatcher_Changed;
            PACFileWatcher.Renamed += PACFileWatcher_Changed;
        }

        private void WatchUserRuleFile()
        {
            UserRuleFileWatcher?.Dispose();
            UserRuleFileWatcher = new FileSystemWatcher(AppStoragePaths.PacDataDirectory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                Filter = Path.GetFileName(USER_RULE_FILE),
                EnableRaisingEvents = true,
            };
            UserRuleFileWatcher.Changed += UserRuleFileWatcher_Changed;
            UserRuleFileWatcher.Created += UserRuleFileWatcher_Changed;
            UserRuleFileWatcher.Deleted += UserRuleFileWatcher_Changed;
            UserRuleFileWatcher.Renamed += UserRuleFileWatcher_Changed;
        }

        private async void PACFileWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            if (PACFileChanged is null)
            {
                return;
            }

            logger.Info($"Detected: PAC file '{e.Name}' was {e.ChangeType.ToString().ToLowerInvariant()}.");
            await DispatchWatcherChangeAsync(
                (FileSystemWatcher)sender,
                () => PACFileChanged?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
        }

        private async void UserRuleFileWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            if (UserRuleFileChanged is null)
            {
                return;
            }

            logger.Info($"Detected: User Rule file '{e.Name}' was {e.ChangeType.ToString().ToLowerInvariant()}.");
            await DispatchWatcherChangeAsync(
                (FileSystemWatcher)sender,
                () => UserRuleFileChanged?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
        }

        private static async Task DispatchWatcherChangeAsync(FileSystemWatcher watcher, Action callback)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                await Task.Delay(10).ConfigureAwait(false);
                callback();
            }
            catch (ObjectDisposedException)
            {
                // Expected while the controller is shutting down.
            }
            catch (Exception exception)
            {
                logger.Warn(exception, "Unable to process PAC file-system watcher notification.");
            }
            finally
            {
                try
                {
                    watcher.EnableRaisingEvents = true;
                }
                catch (ObjectDisposedException)
                {
                    // The watcher was disposed during shutdown.
                }
            }
        }

        public void Dispose()
        {
            PACFileWatcher?.Dispose();
            PACFileWatcher = null;
            UserRuleFileWatcher?.Dispose();
            UserRuleFileWatcher = null;
        }
    }
}
