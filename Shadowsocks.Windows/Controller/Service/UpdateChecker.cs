using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NLog;
using Shadowsocks.Controller.Service;
using Shadowsocks.Core;
using Shadowsocks.Model;

namespace Shadowsocks.Controller
{
    public sealed class UpdateAvailableEventArgs : EventArgs
    {
        public UpdateAvailableEventArgs(JToken release) => Release = release;
        public JToken Release { get; }
    }

    public sealed class UpdateChecker
    {
        private static readonly SemaphoreSlim UpdateOperationLock = new(1, 1);
        private readonly Logger logger = LogManager.GetCurrentClassLogger();
        private readonly HttpClient httpClient;
        private readonly ShadowsocksController controller;
        private readonly Version currentVersion = new(ApplicationInfo.Version);
        private JToken releaseObject;
        private Version releaseVersion;

        public const string PreferredReleaseZipFilename = "Shadowsocks-win-x64.zip";
        private const string UpdateUrl = ApplicationInfo.ReleasesApiUrl;
        private const string GitHubApiVersion = "2026-03-10";
        private const string GitHubApiAccept = "application/vnd.github+json";
        private const string ReleaseDownloadPathPrefix = "/SlimRG/shadowsocks-reborn/releases/download/";

        public string NewReleaseVersion { get; private set; }
        public string LastCheckError { get; private set; }

        public event EventHandler CheckUpdateCompleted;
        public event EventHandler<UpdateAvailableEventArgs> UpdateAvailable;

        public UpdateChecker(ShadowsocksController controller)
        {
            ArgumentNullException.ThrowIfNull(controller);
            this.controller = controller;
            httpClient = controller.GetHttpClient();
        }

        public async Task CheckForVersionUpdate(int millisecondsDelay = 0)
        {
            if (millisecondsDelay > 0)
            {
                logger.Info("Waiting for {0}ms before checking for version update.", millisecondsDelay);
                await Task.Delay(millisecondsDelay).ConfigureAwait(false);
            }

            Configuration config = controller.GetCurrentConfiguration();
            LastCheckError = null;
            releaseObject = null;
            releaseVersion = null;
            NewReleaseVersion = null;

            logger.Info("Checking GitHub Releases for an application update.");
            try
            {
                string json = await GetReleasesJsonAsync().ConfigureAwait(false);
                JArray releases = JArray.Parse(json);
                Version installedVersion = ResolveEffectiveInstalledVersion();
                JToken selected = SelectLatestEligibleRelease(releases, config, installedVersion, out Version selectedVersion);
                if (selected != null)
                {
                    releaseObject = selected;
                    releaseVersion = selectedVersion;
                    NewReleaseVersion = (string)selected["tag_name"];
                    logger.Info("Found application update {0}.", NewReleaseVersion);
                    UpdateAvailable?.Invoke(this, new UpdateAvailableEventArgs(selected));
                    return;
                }

                logger.Info("No newer eligible application release was found.");
                CheckUpdateCompleted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                LastCheckError = exception.Message;
                logger.LogUsefulException(exception);
                CheckUpdateCompleted?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Downloads, verifies and stages the selected release, then starts the new executable
        /// in updater mode. The caller must shut down the current application only after this
        /// method returns true.
        /// </summary>
        public async Task<bool> DoUpdate()
        {
            if (releaseObject == null || releaseVersion == null)
            {
                throw new InvalidOperationException("No application release has been selected for update.");
            }

            await UpdateOperationLock.WaitAsync().ConfigureAwait(false);
            string transactionDirectory = null;
            try
            {
                Version installedVersion = ResolveEffectiveInstalledVersion();
                if (!IsStrictlyNewerVersion(releaseVersion, installedVersion))
                {
                    throw new InvalidOperationException(
                        $"Release {releaseVersion} is not newer than the installed application version {installedVersion}.");
                }

                JArray assets = releaseObject["assets"] as JArray ?? new JArray();
                JObject zipAsset = SelectReleaseZipAsset(assets)
                    ?? throw new InvalidDataException($"The GitHub release does not contain the required {PreferredReleaseZipFilename} asset.");
                JObject checksumAsset = FindAssetByName(assets, PreferredReleaseZipFilename + ".sha256")
                    ?? throw new InvalidDataException($"The GitHub release does not contain the required {PreferredReleaseZipFilename}.sha256 asset.");

                transactionDirectory = SelfUpdater.CreateTransactionDirectory();
                string zipPath = Path.Combine(transactionDirectory, PreferredReleaseZipFilename);
                string checksumPath = zipPath + ".sha256";
                string updaterPath = SelfUpdater.GetTemporaryUpdaterPath(transactionDirectory);

                await DownloadAssetAsync(zipAsset, zipPath).ConfigureAwait(false);
                await DownloadAssetAsync(checksumAsset, checksumPath).ConfigureAwait(false);
                await VerifySha256Async(zipPath, checksumPath, PreferredReleaseZipFilename).ConfigureAwait(false);
                ExtractCanonicalExecutable(zipPath, updaterPath);
                ValidatePayloadVersion(updaterPath, releaseVersion);
                string payloadSha256 = SelfUpdater.ComputeSha256Hex(updaterPath);

                string targetExecutable = AutoStartup.GetPrimaryExecutablePath();
                _ = SelfUpdater.LaunchStagedUpdater(
                    updaterPath,
                    targetExecutable,
                    transactionDirectory,
                    payloadSha256);
                transactionDirectory = null; // ownership transferred to the staged updater/new process
                logger.Info("Application update handoff started for {0}.", NewReleaseVersion);
                return true;
            }
            catch (Exception exception)
            {
                logger.LogUsefulException(exception);
                throw;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(transactionDirectory))
                {
                    TryDeleteTransaction(transactionDirectory);
                }
                UpdateOperationLock.Release();
            }
        }

        public void SkipUpdate()
        {
            if (releaseObject == null)
            {
                return;
            }

            string version = (string)releaseObject["tag_name"] ?? string.Empty;
            controller.SaveSkippedUpdateVerion(version);
            logger.Info("The application update {0} has been skipped.", version);
        }

        internal static JToken SelectLatestEligibleRelease(
            JArray releases,
            Configuration configuration,
            Version installedVersion,
            out Version selectedVersion)
        {
            selectedVersion = null;
            JToken selected = null;
            bool selectedPrerelease = true;

            foreach (JToken release in releases ?? new JArray())
            {
                if ((bool?)release["draft"] == true)
                {
                    continue;
                }

                string tag = ((string)release["tag_name"] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(tag)
                    || string.Equals(tag, configuration?.skippedUpdateVersion, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryParseReleaseVersion(tag, out Version parsed)
                    || !IsStrictlyNewerVersion(parsed, installedVersion))
                {
                    continue;
                }

                bool prerelease = (bool?)release["prerelease"] == true;
                if (prerelease && configuration?.checkPreRelease != true)
                {
                    continue;
                }

                int versionComparison = selectedVersion == null ? 1 : parsed.CompareTo(selectedVersion);
                if (selected == null
                    || versionComparison > 0
                    || (versionComparison == 0 && selectedPrerelease && !prerelease))
                {
                    selected = release;
                    selectedVersion = parsed;
                    selectedPrerelease = prerelease;
                }
            }

            return selected;
        }

        internal static bool IsStrictlyNewerVersion(Version candidateVersion, Version installedVersion)
        {
            ArgumentNullException.ThrowIfNull(candidateVersion);
            ArgumentNullException.ThrowIfNull(installedVersion);
            return candidateVersion.CompareTo(installedVersion) > 0;
        }

        internal static Version SelectEffectiveInstalledVersion(Version processVersion, Version primaryExecutableVersion)
        {
            ArgumentNullException.ThrowIfNull(processVersion);
            return primaryExecutableVersion != null && primaryExecutableVersion.CompareTo(processVersion) > 0
                ? primaryExecutableVersion
                : processVersion;
        }

        private Version ResolveEffectiveInstalledVersion()
        {
            string primaryExecutable = AutoStartup.GetPrimaryExecutablePath();
            Version primaryVersion = TryReadExecutableVersion(primaryExecutable);
            Version effectiveVersion = SelectEffectiveInstalledVersion(currentVersion, primaryVersion);

            if (primaryVersion != null && !effectiveVersion.Equals(currentVersion))
            {
                logger.Info(
                    "Update check is running from version {0}, but primary executable {1} is version {2}; using {2} as the installed version.",
                    currentVersion,
                    primaryExecutable,
                    primaryVersion);
            }

            return effectiveVersion;
        }

        internal static Version TryReadExecutableVersion(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                return null;
            }

            try
            {
                FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
                return new Version(
                    Math.Max(0, versionInfo.FileMajorPart),
                    Math.Max(0, versionInfo.FileMinorPart),
                    Math.Max(0, versionInfo.FileBuildPart),
                    Math.Max(0, versionInfo.FilePrivatePart));
            }
            catch
            {
                return null;
            }
        }

        internal static bool TryParseReleaseVersion(string tagName, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return false;
            }

            string normalized = tagName.Trim();
            if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[1..];
            }

            int suffixIndex = normalized.AsSpan().IndexOfAny('-', '+');
            if (suffixIndex >= 0)
            {
                normalized = normalized[..suffixIndex];
            }

            string[] parts = normalized.Split('.');
            if (parts.Length is < 2 or > 4)
            {
                return false;
            }

            normalized = parts.Length switch
            {
                2 => normalized + ".0.0",
                3 => normalized + ".0",
                _ => normalized,
            };
            return Version.TryParse(normalized, out version);
        }

        internal static JObject SelectReleaseZipAsset(JArray assets)
            => FindAssetByName(assets, PreferredReleaseZipFilename);

        internal static JObject FindAssetByName(JArray assets, string expectedName)
        {
            foreach (JToken token in assets ?? new JArray())
            {
                if (token is JObject asset
                    && string.Equals((string)asset["name"], expectedName, StringComparison.Ordinal))
                {
                    return asset;
                }
            }
            return null;
        }

        internal static async Task VerifySha256Async(string payloadPath, string checksumPath, string expectedFileName)
        {
            string checksumText = (await File.ReadAllTextAsync(checksumPath).ConfigureAwait(false)).Trim();
            Match match = Regex.Match(
                checksumText,
                @"\A(?<hash>[0-9a-fA-F]{64})(?:[ \t]+\*?(?<name>[^\r\n]+))?\z",
                RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                throw new InvalidDataException($"Invalid SHA-256 sidecar: {Path.GetFileName(checksumPath)}.");
            }

            if (match.Groups["name"].Success)
            {
                string sidecarFileName = match.Groups["name"].Value.Trim();
                if (!string.Equals(sidecarFileName, expectedFileName, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"SHA-256 sidecar names '{sidecarFileName}' instead of the required '{expectedFileName}'.");
                }
            }

            await using FileStream payload = File.OpenRead(payloadPath);
            byte[] actualBytes = await SHA256.HashDataAsync(payload).ConfigureAwait(false);
            string actual = Convert.ToHexString(actualBytes);
            if (!string.Equals(actual, match.Groups["hash"].Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SHA-256 verification failed for {Path.GetFileName(payloadPath)}.");
            }
        }

        internal static void ExtractCanonicalExecutable(string zipPath, string destinationExecutable)
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count != 1)
            {
                throw new InvalidDataException("The application update ZIP must contain exactly one file: Shadowsocks.exe.");
            }

            ZipArchiveEntry entry = archive.Entries[0];
            if (!string.Equals(entry.FullName, "Shadowsocks.exe", StringComparison.Ordinal)
                || !string.Equals(entry.Name, "Shadowsocks.exe", StringComparison.Ordinal)
                || entry.Length <= 0)
            {
                throw new InvalidDataException("The application update ZIP must contain exactly one root Shadowsocks.exe file.");
            }

            string destinationDirectory = Path.GetDirectoryName(destinationExecutable)
                ?? throw new InvalidOperationException("The updater destination directory is unavailable.");
            Directory.CreateDirectory(destinationDirectory);

            using Stream input = entry.Open();
            using FileStream output = new(destinationExecutable, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }

        internal static void ValidatePayloadVersion(string executablePath, Version expectedVersion)
        {
            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
            var actualVersion = new Version(
                Math.Max(0, versionInfo.FileMajorPart),
                Math.Max(0, versionInfo.FileMinorPart),
                Math.Max(0, versionInfo.FileBuildPart),
                Math.Max(0, versionInfo.FilePrivatePart));

            if (!actualVersion.Equals(expectedVersion))
            {
                throw new InvalidDataException(
                    $"Update executable version {actualVersion} does not match GitHub release {expectedVersion}.");
            }
        }

        internal static bool IsAllowedReleaseDownloadUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return uri.AbsolutePath.StartsWith(ReleaseDownloadPathPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string> GetReleasesJsonAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UpdateUrl);
            request.Headers.UserAgent.ParseAdd($"Shadowsocks-Reborn/{ApplicationInfo.Version}");
            request.Headers.Accept.ParseAdd(GitHubApiAccept);
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", GitHubApiVersion);

            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        private async Task DownloadAssetAsync(JObject asset, string destination)
        {
            string url = (string)asset["browser_download_url"];
            if (!IsAllowedReleaseDownloadUrl(url))
            {
                throw new InvalidDataException($"GitHub release asset '{asset["name"]}' has an invalid download URL.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd($"Shadowsocks-Reborn/{ApplicationInfo.Version}");
            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await WriteDownloadedAssetAsync(response.Content, destination).ConfigureAwait(false);
        }

        internal static async Task WriteDownloadedAssetAsync(HttpContent content, string destination)
        {
            ArgumentNullException.ThrowIfNull(content);
            ArgumentException.ThrowIfNullOrWhiteSpace(destination);

            string partialPath = destination + ".download";
            try
            {
                File.Delete(partialPath);
                await using (FileStream output = new(partialPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await content.CopyToAsync(output).ConfigureAwait(false);
                    await output.FlushAsync().ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }

                // The temporary stream must be fully disposed before Windows can atomically
                // promote the download to its canonical destination.
                File.Move(partialPath, destination, overwrite: true);
            }
            finally
            {
                TryDeleteFile(partialPath);
            }
        }

        private static void TryDeleteTransaction(string directory)
        {
            try
            {
                if (SelfUpdater.IsCanonicalTransactionDirectory(directory) && Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
