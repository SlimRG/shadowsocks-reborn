using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Shadowsocks.Core;
using Shadowsocks.Core.Storage;

namespace Shadowsocks.Controller.Service
{
    public sealed record PluginCatalogEntry(
        string Id,
        string DisplayName,
        string Repository,
        string WindowsAssetMarker,
        string PreferredExecutableMarker = null);

    public sealed record InstalledPlugin(string Id, string DisplayName, string ExecutablePath, string Source);

    public static class PluginManager
    {
        private const string MetadataFileName = ".plugin.json";
        private static readonly HttpClient HttpClient = CreateHttpClient();

        internal static string PluginsDirectoryOverride { get; set; }
        private static string PluginsDirectory => string.IsNullOrWhiteSpace(PluginsDirectoryOverride)
            ? AppStoragePaths.PluginsDirectory
            : Path.GetFullPath(PluginsDirectoryOverride);

        public static IReadOnlyList<PluginCatalogEntry> Catalog { get; } =
        [
            new("xray-plugin", "Xray Plugin", "teddysun/xray-plugin", "windows-amd64"),
            new("v2ray-plugin", "V2Ray Plugin", "shadowsocks/v2ray-plugin", "windows-amd64"),
            new("qtun", "QTun", "shadowsocks/qtun", "windows-native", "client"),
        ];

        public static IReadOnlyList<InstalledPlugin> GetInstalledPlugins()
        {
            Directory.CreateDirectory(PluginsDirectory);
            var result = new List<InstalledPlugin>();
            foreach (string directory in Directory.EnumerateDirectories(PluginsDirectory))
            {
                PluginMetadata metadata = TryReadMetadata(directory);
                if (metadata is null || string.IsNullOrWhiteSpace(metadata.EntryPoint))
                    continue;

                string executablePath = Path.GetFullPath(Path.Combine(directory, metadata.EntryPoint));
                if (!IsPathInside(directory, executablePath) || !File.Exists(executablePath))
                    continue;

                result.Add(new InstalledPlugin(
                    metadata.Id,
                    string.IsNullOrWhiteSpace(metadata.DisplayName) ? metadata.Id : metadata.DisplayName,
                    executablePath,
                    metadata.Source ?? "Manual archive"));
            }

            return result.OrderBy(plugin => plugin.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        public static string ResolveExecutable(string plugin)
        {
            if (string.IsNullOrWhiteSpace(plugin))
                return null;

            string id = plugin.Trim();
            InstalledPlugin installed = GetInstalledPlugins().FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(candidate.ExecutablePath), Path.GetFileNameWithoutExtension(id), StringComparison.OrdinalIgnoreCase));
            return installed?.ExecutablePath;
        }

        public static async Task<InstalledPlugin> InstallCatalogPluginAsync(PluginCatalogEntry entry, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            GitHubRelease release = await GetLatestReleaseAsync(entry.Repository, cancellationToken).ConfigureAwait(false);
            GitHubAsset asset = release.Assets.FirstOrDefault(candidate =>
                candidate.Name.Contains(entry.WindowsAssetMarker, StringComparison.OrdinalIgnoreCase)
                && (candidate.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    || candidate.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                    || candidate.Name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)));

            if (asset is null)
                throw new InvalidOperationException($"No Windows x64 archive was found in the latest {entry.Repository} release.");

            string stagingRoot = AppStoragePaths.EnsureTempDirectory(Path.Combine(AppStoragePaths.TempUpdatesRoot, "Plugins"));
            string archiveSuffix = asset.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                ? ".tar.gz"
                : asset.Name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? ".tgz" : ".zip";
            string archivePath = Path.Combine(stagingRoot, $"{entry.Id}-{Guid.NewGuid():N}{archiveSuffix}");
            try
            {
                using HttpResponseMessage response = await HttpClient.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (FileStream target = new(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }

                return InstallArchive(archivePath, entry.Id, entry.DisplayName, $"GitHub: {entry.Repository}", entry.PreferredExecutableMarker);
            }
            finally
            {
                TryDeleteFile(archivePath);
            }
        }

        public static InstalledPlugin InstallManualArchive(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath))
                throw new ArgumentException("Plugin archive path is required.", nameof(archivePath));

            if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return InstallArchive(archivePath, null, null, "Manual ZIP", "client");

            if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                || archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                return InstallArchive(archivePath, null, null, "Manual TAR.GZ", "client");
            }

            throw new InvalidDataException("Only ZIP and TAR.GZ plugin packages are supported.");
        }

        public static InstalledPlugin InstallManualZip(string zipPath)
        {
            if (string.IsNullOrWhiteSpace(zipPath))
                throw new ArgumentException("ZIP path is required.", nameof(zipPath));
            if (!zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Only ZIP plugin packages are supported.");

            return InstallManualArchive(zipPath);
        }

        public static void Remove(string pluginId)
        {
            if (string.IsNullOrWhiteSpace(pluginId))
                return;

            string root = Path.GetFullPath(PluginsDirectory);
            string directory = Path.GetFullPath(Path.Combine(root, SanitizeId(pluginId)));
            if (!IsPathInside(root, directory) || !Directory.Exists(directory))
                return;

            Directory.Delete(directory, recursive: true);
        }

        private static InstalledPlugin InstallArchive(string archivePath, string pluginId, string displayName, string source, string preferredExecutableMarker)
        {
            if (!File.Exists(archivePath))
                throw new FileNotFoundException("Plugin archive was not found.", archivePath);

            Directory.CreateDirectory(PluginsDirectory);
            string staging = Path.Combine(PluginsDirectory, $".plugin-{Guid.NewGuid():N}.tmp");

            try
            {
                Directory.CreateDirectory(staging);
                if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractZipSafely(archivePath, staging);
                }
                else if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                    || archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractTarGz(archivePath, staging);
                }
                else
                {
                    throw new InvalidDataException("Unsupported plugin archive format.");
                }

                string executable = SelectExecutable(staging, preferredExecutableMarker);
                string safeId = SanitizeId(pluginId ?? InferPluginId(executable));
                string finalDisplayName = string.IsNullOrWhiteSpace(displayName) ? safeId : displayName;
                string destination = Path.Combine(PluginsDirectory, safeId);
                string relativeEntryPoint = Path.GetRelativePath(staging, executable);
                WriteMetadata(staging, new PluginMetadata
                {
                    Id = safeId,
                    DisplayName = finalDisplayName,
                    EntryPoint = relativeEntryPoint,
                    Source = source,
                });

                if (Directory.Exists(destination))
                    Directory.Delete(destination, recursive: true);
                Directory.Move(staging, destination);

                string finalExecutable = Path.Combine(destination, relativeEntryPoint);
                return new InstalledPlugin(safeId, finalDisplayName, finalExecutable, source);
            }
            catch
            {
                TryDeleteDirectory(staging);
                throw;
            }
        }

        private static string InferPluginId(string executablePath)
        {
            string fileName = Path.GetFileNameWithoutExtension(executablePath);
            if (fileName.Contains("xray-plugin", StringComparison.OrdinalIgnoreCase))
                return "xray-plugin";
            if (fileName.Contains("v2ray-plugin", StringComparison.OrdinalIgnoreCase))
                return "v2ray-plugin";
            if (fileName.Contains("qtun", StringComparison.OrdinalIgnoreCase))
                return "qtun";
            return fileName;
        }

        private static void ExtractTarGz(string archivePath, string destination)
        {
            string destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
            string root = destinationRoot + Path.DirectorySeparatorChar;
            using FileStream source = File.OpenRead(archivePath);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var reader = new TarReader(gzip, leaveOpen: false);

            TarEntry entry;
            while ((entry = reader.GetNextEntry()) != null)
            {
                if (string.IsNullOrWhiteSpace(entry.Name))
                    continue;

                string normalizedName = entry.Name.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                string target = Path.GetFullPath(Path.Combine(destination, normalizedName));
                if (!target.Equals(destinationRoot, StringComparison.OrdinalIgnoreCase)
                    && !target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The TAR.GZ contains an unsafe path.");
                }

                if (entry.EntryType == TarEntryType.Directory || entry.EntryType == TarEntryType.DirectoryList)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                if (entry.EntryType != TarEntryType.RegularFile
                    && entry.EntryType != TarEntryType.V7RegularFile
                    && entry.EntryType != TarEntryType.ContiguousFile)
                {
                    throw new InvalidDataException("The TAR.GZ contains an unsupported link or special entry.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using FileStream output = new(target, FileMode.Create, FileAccess.Write, FileShare.None);
                entry.DataStream?.CopyTo(output);
            }
        }

        private static void ExtractZipSafely(string zipPath, string destination)
        {
            string destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
            string root = destinationRoot + Path.DirectorySeparatorChar;
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!target.Equals(destinationRoot, StringComparison.OrdinalIgnoreCase)
                    && !target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The ZIP contains an unsafe path.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }

        private static string SelectExecutable(string root, string preferredMarker)
        {
            string[] executables = Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories).ToArray();
            if (executables.Length == 0)
                throw new InvalidDataException("The plugin archive does not contain a Windows executable.");
            if (executables.Length == 1)
                return executables[0];

            if (!string.IsNullOrWhiteSpace(preferredMarker))
            {
                string[] preferred = executables.Where(path =>
                    Path.GetFileName(path).Contains(preferredMarker, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (preferred.Length == 1)
                    return preferred[0];
            }

            string[] clientExecutables = executables.Where(path =>
                Path.GetFileName(path).Contains("client", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(path).Contains("server", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (clientExecutables.Length == 1)
                return clientExecutables[0];

            throw new InvalidDataException("The plugin archive contains multiple executables and the client executable could not be selected automatically.");
        }

        private static async Task<GitHubRelease> GetLatestReleaseAsync(string repository, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await HttpClient.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("GitHub returned an empty release response.");
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Shadowsocks-Reborn", ApplicationInfo.Version));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        private static PluginMetadata TryReadMetadata(string directory)
        {
            try
            {
                string path = Path.Combine(directory, MetadataFileName);
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<PluginMetadata>(File.ReadAllText(path))
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteMetadata(string directory, PluginMetadata metadata)
        {
            string json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(directory, MetadataFileName), json);
        }

        private static string SanitizeId(string value)
        {
            string id = new(value.Trim().Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-').ToArray());
            id = id.Trim('-', '.', '_');
            if (string.IsNullOrWhiteSpace(id))
                id = "plugin";
            return id;
        }

        private static bool IsPathInside(string root, string path)
        {
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string normalizedPath = Path.GetFullPath(path);
            return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
        }

        private sealed class PluginMetadata
        {
            public string Id { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string EntryPoint { get; set; } = string.Empty;
            public string Source { get; set; }
        }

        private sealed class GitHubRelease
        {
            [JsonPropertyName("assets")]
            public List<GitHubAsset> Assets { get; set; } = [];
        }

        private sealed class GitHubAsset
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("browser_download_url")]
            public string DownloadUrl { get; set; } = string.Empty;
        }
    }
}
