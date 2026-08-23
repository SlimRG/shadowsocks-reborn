using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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

    public sealed record InstalledPlugin(
        string Id,
        string DisplayName,
        string ExecutablePath,
        string Source,
        bool CanAutoUpdate = false,
        bool AutoUpdate = false,
        string Repository = null,
        string ReleaseTag = null,
        string AssetName = null,
        DateTimeOffset? LastUpdateCheckUtc = null);

    public sealed record PluginUpdateSummary(
        int CheckedCount,
        int UpdatedCount,
        int SkippedInUseCount,
        IReadOnlyList<string> Errors);

    public static class PluginManager
    {
        private const string MetadataFileName = ".plugin.json";
        private const long MaxArchiveBytes = 256L * 1024 * 1024;
        private const long MaxExtractedBytes = 512L * 1024 * 1024;
        private const int MaxArchiveEntries = 4096;
        private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);
        private static readonly TimeSpan TransientDirectoryRetention = TimeSpan.FromDays(1);
        private static readonly JsonSerializerOptions s_metadataWriteOptions = new() { WriteIndented = true };
        private static readonly HttpClient s_httpClient = CreateHttpClient();
        private static readonly object s_storageSync = new();
        private static readonly SemaphoreSlim s_updateSync = new(1, 1);

        internal static string PluginsDirectoryOverride { get; set; }
        internal static HttpClient HttpClientOverride { get; set; }

        private static HttpClient Client => HttpClientOverride ?? s_httpClient;
        private static string PluginsDirectory => string.IsNullOrWhiteSpace(PluginsDirectoryOverride)
            ? AppStoragePaths.PluginsDirectory
            : Path.GetFullPath(PluginsDirectoryOverride);

        public static IReadOnlyList<PluginCatalogEntry> Catalog { get; } =
        [
            new("xray-plugin", "Xray Plugin", "teddysun/xray-plugin", "windows-amd64"),
            new("v2ray-plugin", "V2Ray Plugin", "shadowsocks/v2ray-plugin", "windows-amd64"),
            new("qtun", "QTun", "shadowsocks/qtun", "x86_64-pc-windows-msvc", "client"),
        ];

        public static IReadOnlyList<InstalledPlugin> GetInstalledPlugins()
        {
            lock (s_storageSync)
            {
                Directory.CreateDirectory(PluginsDirectory);
                RecoverInterruptedPluginSwapsLocked();
                var result = new List<InstalledPlugin>();
                foreach (string directory in Directory.EnumerateDirectories(PluginsDirectory))
                {
                    if (Path.GetFileName(directory).StartsWith(".plugin-", StringComparison.OrdinalIgnoreCase))
                        continue;

                    PluginMetadata metadata = TryReadMetadata(directory);
                    if (metadata is null || string.IsNullOrWhiteSpace(metadata.EntryPoint))
                        continue;

                    string executablePath = Path.GetFullPath(Path.Combine(directory, metadata.EntryPoint));
                    if (!IsPathInside(directory, executablePath) || !File.Exists(executablePath))
                        continue;

                    PluginCatalogEntry catalogEntry = GetCatalogEntryForMetadata(metadata);
                    bool canAutoUpdate = catalogEntry is not null;
                    bool autoUpdate = canAutoUpdate && metadata.AutoUpdate != false;
                    result.Add(new InstalledPlugin(
                        metadata.Id,
                        string.IsNullOrWhiteSpace(metadata.DisplayName) ? metadata.Id : metadata.DisplayName,
                        executablePath,
                        metadata.Source ?? "Manual archive",
                        canAutoUpdate,
                        autoUpdate,
                        catalogEntry?.Repository,
                        metadata.ReleaseTag,
                        metadata.AssetName,
                        metadata.LastUpdateCheckUtc));
                }

                return result.OrderBy(plugin => plugin.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }
        }

        public static string ResolveExecutable(string plugin)
        {
            if (string.IsNullOrWhiteSpace(plugin))
                return null;

            string id = plugin.Trim();
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(id);
            InstalledPlugin installed = GetInstalledPlugins().FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(candidate.ExecutablePath), fileNameWithoutExtension, StringComparison.OrdinalIgnoreCase));
            return installed?.ExecutablePath;
        }

        public static async Task<InstalledPlugin> InstallCatalogPluginAsync(PluginCatalogEntry entry, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            await s_updateSync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                GitHubRelease release = await GetLatestReleaseAsync(entry.Repository, cancellationToken).ConfigureAwait(false);
                GitHubAsset asset = SelectReleaseAsset(entry, release);
                bool autoUpdate = GetInstalledPlugins()
                    .FirstOrDefault(plugin => string.Equals(plugin.Id, entry.Id, StringComparison.OrdinalIgnoreCase) && plugin.CanAutoUpdate)
                    ?.AutoUpdate ?? true;

                return await DownloadAndInstallCatalogReleaseAsync(
                    entry,
                    release,
                    asset,
                    autoUpdate,
                    requireExistingCatalogInstallation: false,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"The {entry.DisplayName} package was removed while it was being installed.");
            }
            finally
            {
                s_updateSync.Release();
            }
        }

        public static async Task<PluginUpdateSummary> UpdateCatalogPluginsAsync(
            IReadOnlyCollection<string> pluginsInUse = null,
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            await s_updateSync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CleanupStaleTransientDirectories();
                InstalledPlugin[] installedPlugins = GetInstalledPlugins().ToArray();
                var excluded = new HashSet<string>(
                    (pluginsInUse ?? Array.Empty<string>())
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(GetPluginIdentity),
                    StringComparer.OrdinalIgnoreCase);

                int checkedCount = 0;
                int updatedCount = 0;
                int skippedInUseCount = 0;
                var errors = new List<string>();

                foreach (InstalledPlugin installed in installedPlugins)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!installed.CanAutoUpdate || !installed.AutoUpdate)
                        continue;

                    PluginCatalogEntry entry = FindCatalogEntry(installed.Id);
                    if (entry is null)
                        continue;

                    if (excluded.Contains(GetPluginIdentity(installed.Id)))
                    {
                        skippedInUseCount++;
                        continue;
                    }

                    DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
                    if (!force && !IsAutomaticCheckDue(installed.LastUpdateCheckUtc, nowUtc))
                        continue;

                    PluginMetadata currentMetadata = GetMetadataForAutomaticUpdate(installed.Id, entry);
                    if (currentMetadata is null)
                        continue;

                    checkedCount++;
                    try
                    {
                        GitHubRelease release = await GetLatestReleaseAsync(entry.Repository, cancellationToken).ConfigureAwait(false);
                        GitHubAsset asset = SelectReleaseAsset(entry, release);
                        if (!string.IsNullOrWhiteSpace(currentMetadata.ReleaseTag)
                            && string.Equals(currentMetadata.ReleaseTag, release.TagName, StringComparison.OrdinalIgnoreCase))
                        {
                            UpdateObservedReleaseMetadata(installed.Id, entry, release.TagName, asset.Name, nowUtc);
                            continue;
                        }

                        if (IsReleaseDowngrade(currentMetadata.ReleaseTag, release.TagName))
                        {
                            UpdateLastSuccessfulCheck(installed.Id, entry, nowUtc);
                            continue;
                        }

                        InstalledPlugin updated = await DownloadAndInstallCatalogReleaseAsync(
                            entry,
                            release,
                            asset,
                            autoUpdate: true,
                            requireExistingCatalogInstallation: true,
                            cancellationToken).ConfigureAwait(false);
                        if (updated is not null)
                            updatedCount++;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        errors.Add($"{installed.DisplayName}: {exception.Message}");
                    }
                }

                return new PluginUpdateSummary(checkedCount, updatedCount, skippedInUseCount, errors);
            }
            finally
            {
                s_updateSync.Release();
            }
        }

        public static bool SetAutoUpdate(string pluginId, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(pluginId))
                return false;

            lock (s_storageSync)
            {
                string directory = GetPluginDirectory(pluginId);
                PluginMetadata metadata = TryReadMetadata(directory);
                if (metadata is null || GetCatalogEntryForMetadata(metadata) is null)
                    return false;

                metadata.AutoUpdate = enabled;
                WriteMetadata(directory, metadata);
                return true;
            }
        }

        public static InstalledPlugin InstallManualArchive(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath))
                throw new ArgumentException("Plugin archive path is required.", nameof(archivePath));

            if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return InstallArchive(archivePath, null, null, "Manual ZIP", "client", null, null, null, false, null, false);

            if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                || archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                return InstallArchive(archivePath, null, null, "Manual TAR.GZ", "client", null, null, null, false, null, false);
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

            lock (s_storageSync)
            {
                string directory = GetPluginDirectory(pluginId);
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
        }

        internal static bool IsAutomaticCheckDue(DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc)
        {
            if (!lastCheckUtc.HasValue || lastCheckUtc.Value > nowUtc)
                return true;

            return nowUtc - lastCheckUtc.Value >= AutomaticCheckInterval;
        }

        public static PluginCatalogEntry FindCatalogEntry(string pluginId)
        {
            if (string.IsNullOrWhiteSpace(pluginId))
                return null;

            string identity = GetPluginIdentity(pluginId);
            return Catalog.FirstOrDefault(entry =>
                string.Equals(entry.Id, identity, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(entry.PreferredExecutableMarker)
                    && string.Equals(
                        $"{entry.Id}-{entry.PreferredExecutableMarker}",
                        identity,
                        StringComparison.OrdinalIgnoreCase)));
        }

        private static async Task<InstalledPlugin> DownloadAndInstallCatalogReleaseAsync(
            PluginCatalogEntry entry,
            GitHubRelease release,
            GitHubAsset asset,
            bool autoUpdate,
            bool requireExistingCatalogInstallation,
            CancellationToken cancellationToken)
        {
            string stagingRoot = AppStoragePaths.EnsureTempDirectory(Path.Combine(AppStoragePaths.TempUpdatesRoot, "Plugins"));
            string archivePath = Path.Combine(stagingRoot, $"{entry.Id}-{Guid.NewGuid():N}{GetArchiveSuffix(asset.Name)}");
            try
            {
                using HttpResponseMessage response = await Client.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                long? contentLength = response.Content.Headers.ContentLength;
                if (contentLength is > MaxArchiveBytes)
                    throw new InvalidDataException($"Plugin archive is larger than the {MaxArchiveBytes / (1024 * 1024)} MiB safety limit.");

                await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (FileStream target = new(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await CopyToWithLimitAsync(source, target, MaxArchiveBytes, cancellationToken).ConfigureAwait(false);
                }

                VerifyReleaseAssetDigest(archivePath, asset);

                return InstallArchive(
                    archivePath,
                    entry.Id,
                    entry.DisplayName,
                    $"GitHub: {entry.Repository}",
                    entry.PreferredExecutableMarker,
                    entry,
                    release.TagName,
                    asset.Name,
                    autoUpdate,
                    DateTimeOffset.UtcNow,
                    requireExistingCatalogInstallation,
                    cancellationToken);
            }
            finally
            {
                TryDeleteFile(archivePath);
            }
        }

        private static InstalledPlugin InstallArchive(
            string archivePath,
            string pluginId,
            string displayName,
            string source,
            string preferredExecutableMarker,
            PluginCatalogEntry catalogEntry,
            string releaseTag,
            string assetName,
            bool autoUpdate,
            DateTimeOffset? lastUpdateCheckUtc,
            bool requireExistingCatalogInstallation,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(archivePath))
                throw new FileNotFoundException("Plugin archive was not found.", archivePath);
            if (new FileInfo(archivePath).Length > MaxArchiveBytes)
                throw new InvalidDataException($"Plugin archive is larger than the {MaxArchiveBytes / (1024 * 1024)} MiB safety limit.");

            CleanupStaleTransientDirectories();
            Directory.CreateDirectory(PluginsDirectory);
            string staging = Path.Combine(PluginsDirectory, $".plugin-stage-{Guid.NewGuid():N}");
            string backup = null;

            try
            {
                Directory.CreateDirectory(staging);
                if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractZipSafely(archivePath, staging, cancellationToken);
                }
                else if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                    || archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractTarGz(archivePath, staging, cancellationToken);
                }
                else
                {
                    throw new InvalidDataException("Unsupported plugin archive format.");
                }

                string executable = SelectExecutable(staging, preferredExecutableMarker);
                string safeId = SanitizeId(pluginId ?? InferPluginId(executable));
                string finalDisplayName = string.IsNullOrWhiteSpace(displayName) ? safeId : displayName;
                string destination = GetPluginDirectory(safeId);
                string relativeEntryPoint = Path.GetRelativePath(staging, executable);

                lock (s_storageSync)
                {
                    if (requireExistingCatalogInstallation)
                    {
                        PluginMetadata existingMetadata = TryReadMetadata(destination);
                        PluginCatalogEntry existingCatalogEntry = GetCatalogEntryForMetadata(existingMetadata);
                        if (existingMetadata is null
                            || existingCatalogEntry is null
                            || !string.Equals(existingCatalogEntry.Repository, catalogEntry?.Repository, StringComparison.OrdinalIgnoreCase)
                            || existingMetadata.AutoUpdate == false)
                        {
                            TryDeleteDirectory(staging);
                            return null;
                        }
                    }

                    WriteMetadata(staging, new PluginMetadata
                    {
                        Id = safeId,
                        DisplayName = finalDisplayName,
                        EntryPoint = relativeEntryPoint,
                        Source = source,
                        Repository = catalogEntry?.Repository,
                        ReleaseTag = releaseTag,
                        AssetName = assetName,
                        AutoUpdate = catalogEntry is null ? null : autoUpdate,
                        LastUpdateCheckUtc = lastUpdateCheckUtc,
                    });

                    if (Directory.Exists(destination))
                    {
                        backup = Path.Combine(PluginsDirectory, $".plugin-backup-{safeId}-{Guid.NewGuid():N}");
                        Directory.Move(destination, backup);
                    }

                    try
                    {
                        Directory.Move(staging, destination);
                    }
                    catch
                    {
                        if (backup is not null && Directory.Exists(backup) && !Directory.Exists(destination))
                            Directory.Move(backup, destination);
                        throw;
                    }
                }

                if (backup is not null)
                    TryDeleteDirectory(backup);

                string finalExecutable = Path.Combine(destination, relativeEntryPoint);
                return new InstalledPlugin(
                    safeId,
                    finalDisplayName,
                    finalExecutable,
                    source,
                    catalogEntry is not null,
                    catalogEntry is not null && autoUpdate,
                    catalogEntry?.Repository,
                    releaseTag,
                    assetName,
                    lastUpdateCheckUtc);
            }
            catch
            {
                TryDeleteDirectory(staging);
                throw;
            }
        }

        private static PluginMetadata GetMetadataForAutomaticUpdate(string pluginId, PluginCatalogEntry entry)
        {
            lock (s_storageSync)
            {
                string directory = GetPluginDirectory(pluginId);
                PluginMetadata metadata = TryReadMetadata(directory);
                PluginCatalogEntry installedEntry = GetCatalogEntryForMetadata(metadata);
                if (metadata is null
                    || installedEntry is null
                    || !string.Equals(installedEntry.Repository, entry.Repository, StringComparison.OrdinalIgnoreCase)
                    || metadata.AutoUpdate == false)
                {
                    return null;
                }

                return CloneMetadata(metadata);
            }
        }

        private static bool IsReleaseDowngrade(string installedTag, string candidateTag)
        {
            return UpdateChecker.TryParseReleaseVersion(installedTag, out Version installedVersion)
                && UpdateChecker.TryParseReleaseVersion(candidateTag, out Version candidateVersion)
                && candidateVersion < installedVersion;
        }

        private static void UpdateLastSuccessfulCheck(string pluginId, PluginCatalogEntry entry, DateTimeOffset nowUtc)
        {
            lock (s_storageSync)
            {
                string directory = GetPluginDirectory(pluginId);
                PluginMetadata metadata = TryReadMetadata(directory);
                PluginCatalogEntry installedEntry = GetCatalogEntryForMetadata(metadata);
                if (metadata is null
                    || installedEntry is null
                    || !string.Equals(installedEntry.Repository, entry.Repository, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                metadata.LastUpdateCheckUtc = nowUtc;
                WriteMetadata(directory, metadata);
            }
        }

        private static void UpdateObservedReleaseMetadata(
            string pluginId,
            PluginCatalogEntry entry,
            string releaseTag,
            string assetName,
            DateTimeOffset nowUtc)
        {
            lock (s_storageSync)
            {
                string directory = GetPluginDirectory(pluginId);
                PluginMetadata metadata = TryReadMetadata(directory);
                PluginCatalogEntry installedEntry = GetCatalogEntryForMetadata(metadata);
                if (metadata is null
                    || installedEntry is null
                    || !string.Equals(installedEntry.Repository, entry.Repository, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                metadata.Repository = entry.Repository;
                metadata.ReleaseTag = releaseTag;
                metadata.AssetName = assetName;
                metadata.LastUpdateCheckUtc = nowUtc;
                WriteMetadata(directory, metadata);
            }
        }

        private static PluginMetadata CloneMetadata(PluginMetadata metadata)
            => new()
            {
                Id = metadata.Id,
                DisplayName = metadata.DisplayName,
                EntryPoint = metadata.EntryPoint,
                Source = metadata.Source,
                Repository = metadata.Repository,
                ReleaseTag = metadata.ReleaseTag,
                AssetName = metadata.AssetName,
                AutoUpdate = metadata.AutoUpdate,
                LastUpdateCheckUtc = metadata.LastUpdateCheckUtc,
            };

        private static PluginCatalogEntry GetCatalogEntryForMetadata(PluginMetadata metadata)
        {
            if (metadata is null || string.IsNullOrWhiteSpace(metadata.Id))
                return null;

            PluginCatalogEntry entry = FindCatalogEntry(metadata.Id);
            if (entry is null)
                return null;

            bool trustedRepository = string.Equals(metadata.Repository, entry.Repository, StringComparison.OrdinalIgnoreCase);
            bool legacyCatalogSource = string.Equals(metadata.Source, $"GitHub: {entry.Repository}", StringComparison.OrdinalIgnoreCase);
            return trustedRepository || legacyCatalogSource ? entry : null;
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

        private static void ExtractTarGz(string archivePath, string destination, CancellationToken cancellationToken)
        {
            string destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
            string root = destinationRoot + Path.DirectorySeparatorChar;
            using FileStream source = File.OpenRead(archivePath);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var reader = new TarReader(gzip, leaveOpen: false);

            int entryCount = 0;
            long extractedBytes = 0;
            byte[] buffer = new byte[81920];
            TarEntry entry;
            while ((entry = reader.GetNextEntry()) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entryCount > MaxArchiveEntries)
                    throw new InvalidDataException($"The TAR.GZ contains more than {MaxArchiveEntries} entries.");
                if (string.IsNullOrWhiteSpace(entry.Name))
                    continue;

                string normalizedName = entry.Name.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                ValidateArchiveEntryName(normalizedName, "TAR.GZ");
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
                CopyStreamWithLimit(entry.DataStream, output, buffer, ref extractedBytes, "TAR.GZ", cancellationToken);
            }
        }

        private static void ExtractZipSafely(string zipPath, string destination, CancellationToken cancellationToken)
        {
            string destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
            string root = destinationRoot + Path.DirectorySeparatorChar;
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count > MaxArchiveEntries)
                throw new InvalidDataException($"The ZIP contains more than {MaxArchiveEntries} entries.");

            long declaredExtractedBytes = 0;
            long extractedBytes = 0;
            byte[] buffer = new byte[81920];
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Length > MaxExtractedBytes - declaredExtractedBytes)
                    throw new InvalidDataException($"The ZIP expands beyond the {MaxExtractedBytes / (1024 * 1024)} MiB safety limit.");
                declaredExtractedBytes += entry.Length;

                ValidateArchiveEntryName(entry.FullName, "ZIP");
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
                using Stream input = entry.Open();
                using FileStream output = new(target, FileMode.Create, FileAccess.Write, FileShare.None);
                CopyStreamWithLimit(input, output, buffer, ref extractedBytes, "ZIP", cancellationToken);
            }
        }

        private static void ValidateArchiveEntryName(string entryName, string archiveType)
        {
            if (string.IsNullOrWhiteSpace(entryName) || Path.IsPathRooted(entryName) || entryName.Contains(':'))
                throw new InvalidDataException($"The {archiveType} contains an unsafe path.");
        }

        private static void CopyStreamWithLimit(
            Stream source,
            FileStream destination,
            byte[] buffer,
            ref long extractedBytes,
            string archiveType,
            CancellationToken cancellationToken)
        {
            if (source is null)
                return;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int bytesRead = source.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                    return;

                extractedBytes = AccumulateExtractedBytes(extractedBytes, bytesRead, archiveType);
                destination.Write(buffer, 0, bytesRead);
            }
        }

        internal static long AccumulateExtractedBytes(long extractedBytes, int bytesRead, string archiveType)
        {
            if (bytesRead > MaxExtractedBytes - extractedBytes)
                throw new InvalidDataException($"The {archiveType} expands beyond the {MaxExtractedBytes / (1024 * 1024)} MiB safety limit.");

            return extractedBytes + bytesRead;
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

        private static GitHubAsset SelectReleaseAsset(PluginCatalogEntry entry, GitHubRelease release)
        {
            GitHubAsset asset = release.Assets.FirstOrDefault(candidate =>
                candidate.Name.Contains(entry.WindowsAssetMarker, StringComparison.OrdinalIgnoreCase)
                && IsSupportedArchive(candidate.Name));
            if (asset is null || string.IsNullOrWhiteSpace(asset.DownloadUrl))
                throw new InvalidOperationException($"No Windows x64 archive was found in the latest {entry.Repository} release.");
            return asset;
        }

        private static bool IsSupportedArchive(string fileName)
            => fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

        private static string GetArchiveSuffix(string fileName)
        {
            if (fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                return ".tar.gz";
            if (fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
                return ".tgz";
            if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return ".zip";
            throw new InvalidDataException("Unsupported plugin archive format.");
        }

        private static async Task<GitHubRelease> GetLatestReleaseAsync(string repository, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await Client.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            GitHubRelease release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("GitHub returned an empty release response.");
            if (string.IsNullOrWhiteSpace(release.TagName))
                throw new InvalidDataException("GitHub release response does not contain a release tag.");
            return release;
        }

        private static async Task CopyToWithLimitAsync(
            Stream source,
            FileStream destination,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[81920];
            long totalBytes = 0;
            while (true)
            {
                int bytesRead = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                    return;

                totalBytes += bytesRead;
                if (totalBytes > maximumBytes)
                    throw new InvalidDataException($"Plugin archive is larger than the {maximumBytes / (1024 * 1024)} MiB safety limit.");

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            }
        }

        private static void VerifyReleaseAssetDigest(string archivePath, GitHubAsset asset)
        {
            if (string.IsNullOrWhiteSpace(asset.Digest))
                return;

            const string Prefix = "sha256:";
            if (!asset.Digest.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GitHub returned an unsupported plugin asset digest algorithm.");

            string expected = asset.Digest[Prefix.Length..].Trim();
            if (expected.Length != 64 || expected.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException("GitHub returned an invalid SHA-256 plugin asset digest.");

            using FileStream stream = File.OpenRead(archivePath);
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded plugin archive failed GitHub SHA-256 verification.");
        }

        private static void CleanupStaleTransientDirectories()
        {
            lock (s_storageSync)
            {
                if (!Directory.Exists(PluginsDirectory))
                    return;

                RecoverInterruptedPluginSwapsLocked();
                DateTime cutoffUtc = DateTime.UtcNow - TransientDirectoryRetention;
                foreach (string directory in Directory.EnumerateDirectories(PluginsDirectory, ".plugin-*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (ShouldPreserveRecoverableBackupLocked(directory))
                            continue;

                        if (Directory.GetLastWriteTimeUtc(directory) <= cutoffUtc)
                            Directory.Delete(directory, recursive: true);
                    }
                    catch
                    {
                        // Best effort: an old plugin process can still hold a transient file open.
                    }
                }
            }
        }

        private static bool ShouldPreserveRecoverableBackupLocked(string directory)
        {
            if (!Path.GetFileName(directory).StartsWith(".plugin-backup-", StringComparison.OrdinalIgnoreCase))
                return false;

            PluginMetadata metadata = TryReadMetadata(directory);
            if (metadata is null || string.IsNullOrWhiteSpace(metadata.Id))
                return false;

            try
            {
                return !Directory.Exists(GetPluginDirectory(metadata.Id));
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        private static void RecoverInterruptedPluginSwapsLocked()
        {
            foreach (string backupDirectory in Directory.EnumerateDirectories(PluginsDirectory, ".plugin-backup-*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                PluginMetadata metadata = TryReadMetadata(backupDirectory);
                if (metadata is null || string.IsNullOrWhiteSpace(metadata.Id))
                    continue;

                string destination;
                try
                {
                    destination = GetPluginDirectory(metadata.Id);
                }
                catch (InvalidDataException)
                {
                    continue;
                }

                if (Directory.Exists(destination))
                    continue;

                try
                {
                    Directory.Move(backupDirectory, destination);
                }
                catch (IOException)
                {
                    // Best effort. A subsequent storage read or maintenance pass can retry.
                }
                catch (UnauthorizedAccessException)
                {
                    // Best effort. Preserve the backup rather than deleting recoverable state.
                }
            }
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
            if (string.IsNullOrWhiteSpace(directory))
                return null;

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
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, MetadataFileName);
            string temporaryPath = Path.Combine(directory, $"{MetadataFileName}.tmp-{Guid.NewGuid():N}");
            try
            {
                string json = JsonSerializer.Serialize(metadata, s_metadataWriteOptions);
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }

        private static string GetPluginDirectory(string pluginId)
        {
            string root = Path.GetFullPath(PluginsDirectory);
            string directory = Path.GetFullPath(Path.Combine(root, SanitizeId(pluginId)));
            if (!IsPathInside(root, directory))
                throw new InvalidDataException("Plugin id resolves outside the plugin storage root.");
            return directory;
        }

        private static string GetPluginIdentity(string plugin)
        {
            string value = plugin?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return Path.GetFileNameWithoutExtension(value);
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
            public string Repository { get; set; }
            public string ReleaseTag { get; set; }
            public string AssetName { get; set; }
            public bool? AutoUpdate { get; set; }
            public DateTimeOffset? LastUpdateCheckUtc { get; set; }
        }

        private sealed class GitHubRelease
        {
            [JsonPropertyName("tag_name")]
            public string TagName { get; set; } = string.Empty;

            [JsonPropertyName("assets")]
            public List<GitHubAsset> Assets { get; set; } = [];
        }

        private sealed class GitHubAsset
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("browser_download_url")]
            public string DownloadUrl { get; set; } = string.Empty;

            [JsonPropertyName("digest")]
            public string Digest { get; set; }
        }
    }
}
