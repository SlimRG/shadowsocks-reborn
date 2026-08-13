using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NLog;
using Shadowsocks.Controller.Service;
using Shadowsocks.Model;
using Shadowsocks.Properties;

namespace Shadowsocks.Controller
{
    /// <summary>
    /// Processing the locally generated PAC file content.
    /// GeoSite is only touched while Local PAC is active.
    /// </summary>
    public class PACDaemon
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public const string PAC_FILE = "pac.txt";
        public const string USER_RULE_FILE = "user-rule.txt";
        public const string USER_ABP_FILE = "abp.txt";

        private Configuration config;

        private FileSystemWatcher PACFileWatcher;
        private FileSystemWatcher UserRuleFileWatcher;

        public event EventHandler PACFileChanged;
        public event EventHandler UserRuleFileChanged;

        public PACDaemon(Configuration config)
        {
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
                File.WriteAllText(USER_RULE_FILE, Resources.user_rule);
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
            PACFileWatcher = new FileSystemWatcher(Program.WorkingDirectory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                Filter = PAC_FILE,
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
            UserRuleFileWatcher = new FileSystemWatcher(Program.WorkingDirectory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                Filter = USER_RULE_FILE,
                EnableRaisingEvents = true,
            };
            UserRuleFileWatcher.Changed += UserRuleFileWatcher_Changed;
            UserRuleFileWatcher.Created += UserRuleFileWatcher_Changed;
            UserRuleFileWatcher.Deleted += UserRuleFileWatcher_Changed;
            UserRuleFileWatcher.Renamed += UserRuleFileWatcher_Changed;
        }

        private void PACFileWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            if (PACFileChanged == null)
                return;

            logger.Info($"Detected: PAC file '{e.Name}' was {e.ChangeType.ToString().ToLower()}.");
            Task.Factory.StartNew(() =>
            {
                ((FileSystemWatcher)sender).EnableRaisingEvents = false;
                System.Threading.Thread.Sleep(10);
                PACFileChanged(this, EventArgs.Empty);
                ((FileSystemWatcher)sender).EnableRaisingEvents = true;
            });
        }

        private void UserRuleFileWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            if (UserRuleFileChanged == null)
                return;

            logger.Info($"Detected: User Rule file '{e.Name}' was {e.ChangeType.ToString().ToLower()}.");
            Task.Factory.StartNew(() =>
            {
                ((FileSystemWatcher)sender).EnableRaisingEvents = false;
                System.Threading.Thread.Sleep(10);
                UserRuleFileChanged(this, EventArgs.Empty);
                ((FileSystemWatcher)sender).EnableRaisingEvents = true;
            });
        }
    }
}
