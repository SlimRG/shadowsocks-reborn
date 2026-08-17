using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NLog;
using Shadowsocks.Model;
using Shadowsocks.Core;
using Shadowsocks.Core.Storage;

namespace Shadowsocks.Controller
{
    public sealed class UpdateAvailableEventArgs : EventArgs
    {
        public UpdateAvailableEventArgs(JToken release) => Release = release;
        public JToken Release { get; }
    }

    public class UpdateChecker
    {
        private readonly Logger logger = LogManager.GetCurrentClassLogger();
        private readonly HttpClient httpClient;
        private readonly ShadowsocksController controller;
        private readonly Version currentVersion = new(Version);
        private JToken releaseObject;

        public const string RepositoryUrl = ApplicationInfo.RepositoryUrl;
        public const string IssuesUrl = ApplicationInfo.IssuesUrl;
        private const string UpdateUrl = ApplicationInfo.ReleasesApiUrl;
        public const string Version = ApplicationInfo.Version;

        public string NewReleaseVersion { get; private set; }
        public string NewReleaseZipFilename { get; private set; }

        public event EventHandler CheckUpdateCompleted;
        public event EventHandler<UpdateAvailableEventArgs> UpdateAvailable;

        public UpdateChecker(ShadowsocksController controller)
        {
            this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
            httpClient = controller.GetHttpClient();
        }

        public async Task CheckForVersionUpdate(int millisecondsDelay = 0)
        {
            logger.Info($"Waiting for {millisecondsDelay}ms before checking for version update.");
            await Task.Delay(millisecondsDelay).ConfigureAwait(false);
            Configuration config = controller.GetCurrentConfiguration();
            logger.Info("Checking for version update.");
            try
            {
                string json = await httpClient.GetStringAsync(UpdateUrl).ConfigureAwait(false);
                JArray releases = JArray.Parse(json);
                foreach (JToken release in releases)
                {
                    string tag = (string)release["tag_name"];
                    if (string.IsNullOrWhiteSpace(tag)) continue;
                    if (string.Equals(tag, config.skippedUpdateVersion, StringComparison.OrdinalIgnoreCase)) break;
                    if (!TryParseReleaseVersion(tag, out Version parsed))
                    {
                        logger.Warn($"Ignoring GitHub release with unsupported tag '{tag}'.");
                        continue;
                    }

                    bool prerelease = (bool?)release["prerelease"] == true;
                    if (parsed.CompareTo(currentVersion) > 0 && (!prerelease || config.checkPreRelease))
                    {
                        releaseObject = release;
                        NewReleaseVersion = tag;
                        logger.Info($"Found new version {tag}.");
                        UpdateAvailable?.Invoke(this, new UpdateAvailableEventArgs(release));
                        return;
                    }
                }
                logger.Info("No new versions found.");
                CheckUpdateCompleted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception e)
            {
                logger.LogUsefulException(e);
            }
        }

        private static bool TryParseReleaseVersion(string tagName, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(tagName)) return false;
            string normalized = tagName.Trim();
            if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(1);
            int suffixIndex = normalized.IndexOfAny(new[] { '-', '+' });
            if (suffixIndex >= 0) normalized = normalized.Substring(0, suffixIndex);
            string[] parts = normalized.Split('.');
            if (parts.Length is < 2 or > 4) return false;
            normalized = parts.Length switch { 2 => normalized + ".0.0", 3 => normalized + ".0", _ => normalized };
            return System.Version.TryParse(normalized, out version);
        }

        public async Task DoUpdate()
        {
            if (releaseObject == null) return;
            try
            {
                string updateDirectory = AppStoragePaths.EnsureTempDirectory(AppStoragePaths.TempUpdatesRoot);
                CleanupOldUpdateFiles(updateDirectory);
                foreach (JObject asset in (JArray)releaseObject["assets"])
                {
                    string filename = Path.GetFileName((string)asset["name"] ?? string.Empty);
                    string url = (string)asset["browser_download_url"];
                    if (string.IsNullOrWhiteSpace(filename) || string.IsNullOrWhiteSpace(url))
                        continue;
                    string destination = Path.Combine(updateDirectory, filename);
                    using HttpResponseMessage response = await httpClient.GetAsync(url).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    await using FileStream output = File.Create(destination);
                    await response.Content.CopyToAsync(output).ConfigureAwait(false);
                    if (filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) NewReleaseZipFilename = filename;
                }
                if (!string.IsNullOrWhiteSpace(NewReleaseZipFilename))
                {
                    string downloadedZip = Path.Combine(updateDirectory, NewReleaseZipFilename);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select, \"{downloadedZip}\"") { UseShellExecute = true });
                }
            }
            catch (Exception e) { logger.LogUsefulException(e); }
        }

        private static void CleanupOldUpdateFiles(string directory)
        {
            try
            {
                DateTime cutoffUtc = DateTime.UtcNow.AddDays(-7);
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                            File.Delete(file);
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

        public void SkipUpdate()
        {
            if (releaseObject == null) return;
            string version = (string)releaseObject["tag_name"] ?? string.Empty;
            controller.SaveSkippedUpdateVerion(version);
            logger.Info($"The update {version} has been skipped and will be ignored next time.");
        }
    }
}
