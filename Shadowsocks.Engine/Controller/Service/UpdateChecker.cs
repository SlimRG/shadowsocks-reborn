using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NLog;
using Shadowsocks.Model;
using Shadowsocks.Util;

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

        public const string RepositoryUrl = "https://github.com/SlimRG/shadowsocks-reborn";
        public const string IssuesUrl = RepositoryUrl + "/issues";
        private const string UpdateUrl = "https://api.github.com/repos/SlimRG/shadowsocks-reborn/releases";
        public const string Version = "5.0.0.0";

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
                foreach (JObject asset in (JArray)releaseObject["assets"])
                {
                    string filename = (string)asset["name"];
                    string url = (string)asset["browser_download_url"];
                    using HttpResponseMessage response = await httpClient.GetAsync(url).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    await using FileStream output = File.Create(Utils.GetTempPath(filename));
                    await response.Content.CopyToAsync(output).ConfigureAwait(false);
                    if (filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) NewReleaseZipFilename = filename;
                }
                if (!string.IsNullOrWhiteSpace(NewReleaseZipFilename))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select, \"{Utils.GetTempPath(NewReleaseZipFilename)}\"") { UseShellExecute = true });
            }
            catch (Exception e) { logger.LogUsefulException(e); }
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
