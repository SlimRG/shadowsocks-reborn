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
    /// Serves the Local PAC transport shim and watches user-rule.txt.
    /// All Local PAC routing policy is evaluated by the managed C# FilterEngine;
    /// the PAC file itself only funnels WinINet/WinHTTP traffic into the local proxy.
    /// </summary>
    public class PACDaemon : IDisposable
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public static string PacFile => AppStoragePaths.LocalPacFile;
        public static string UserRuleFile => AppStoragePaths.UserRuleFile;

        private FileSystemWatcher PACFileWatcher;
        private FileSystemWatcher UserRuleFileWatcher;

        public event EventHandler PACFileChanged;
        public event EventHandler UserRuleFileChanged;

        public PACDaemon(Configuration config)
        {
            ArgumentNullException.ThrowIfNull(config);
            Directory.CreateDirectory(AppStoragePaths.PacDataDirectory);
            TouchUserRuleFile();
            WriteManagedFunnelPacIfNeeded();
            WatchPacFile();
            WatchUserRuleFile();
        }

        public static void UpdateConfiguration(Configuration newConfig)
        {
            ArgumentNullException.ThrowIfNull(newConfig);
        }

        internal static string TouchUserRuleFile()
        {
            if (!File.Exists(UserRuleFile))
                File.WriteAllText(UserRuleFile, Shadowsocks.Core.EmbeddedResources.UserRule, Encoding.UTF8);
            return UserRuleFile;
        }

        internal static string GetPACContent()
        {
            // PAC is transport only. EasyList/ABP/GeoSite decisions are authoritative in C#.
            return GetManagedFunnelPac();
        }

        internal static string GetManagedFunnelPac()
            => "function FindProxyForURL(url, host) { return __PROXY__; }\n";

        private static void WriteManagedFunnelPacIfNeeded()
        {
            string expected = GetManagedFunnelPac();
            string current = File.Exists(PacFile)
                ? FileManager.NonExclusiveReadAllText(PacFile, Encoding.UTF8)
                : string.Empty;
            if (!string.Equals(current, expected, StringComparison.Ordinal))
            {
                File.WriteAllText(PacFile, expected, Encoding.UTF8);
            }
        }

        private void WatchPacFile()
        {
            PACFileWatcher?.Dispose();
            PACFileWatcher = new FileSystemWatcher(AppStoragePaths.PacDataDirectory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                Filter = Path.GetFileName(PacFile),
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
                Filter = Path.GetFileName(UserRuleFile),
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
                    // Expected while the controller is shutting down.
                }
            }
        }

        public void Dispose()
        {
            PACFileWatcher?.Dispose();
            PACFileWatcher = null;
            UserRuleFileWatcher?.Dispose();
            UserRuleFileWatcher = null;
            GC.SuppressFinalize(this);
        }
    }
}
