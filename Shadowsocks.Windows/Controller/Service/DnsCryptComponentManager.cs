using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Shadowsocks.Core.Storage;

namespace Shadowsocks.Controller.Service
{
    internal sealed class DnsCryptActivationLease
    {
        internal DnsCryptActivationLease(
            Version version,
            string targetDirectory,
            string backupDirectory,
            bool hadMetadata,
            string originalActiveVersion,
            string originalPreviousVersion,
            bool originalAutoUpdate,
            DateTimeOffset? originalLastUpdateCheckUtc)
        {
            Version = version;
            TargetDirectory = targetDirectory;
            BackupDirectory = backupDirectory;
            HadMetadata = hadMetadata;
            OriginalActiveVersion = originalActiveVersion;
            OriginalPreviousVersion = originalPreviousVersion;
            OriginalAutoUpdate = originalAutoUpdate;
            OriginalLastUpdateCheckUtc = originalLastUpdateCheckUtc;
        }

        internal Version Version { get; }
        internal string TargetDirectory { get; }
        internal string BackupDirectory { get; }
        internal bool HadMetadata { get; }
        internal string OriginalActiveVersion { get; }
        internal string OriginalPreviousVersion { get; }
        internal bool OriginalAutoUpdate { get; }
        internal DateTimeOffset? OriginalLastUpdateCheckUtc { get; }
        internal bool Completed { get; set; }
    }

    public sealed partial class DnsCryptComponentManager : IDisposable
    {
        public const string ComponentId = "dnscrypt-proxy";
        public const string Repository = "DNSCrypt/dnscrypt-proxy";
        public const string ExpectedExecutableName = "dnscrypt-proxy.exe";
        public const string ReleaseSigningPublicKey = "RWTk1xXqcTODeYttYMCMLo0YJHaFEHn7a3akqHlb/7QvIQXHVPxKbjB5";

        private const string LatestReleaseApiUrl = "https://api.github.com/repos/DNSCrypt/dnscrypt-proxy/releases/latest";
        private const string MetadataFileName = "component.json";
        private const int MaxArchiveEntries = 512;
        private const long MaxReleaseMetadataBytes = 2L * 1024 * 1024;
        private const long MaxArchiveDownloadBytes = 128L * 1024 * 1024;
        private const long MaxSignatureDownloadBytes = 64L * 1024;
        private const long MaxExtractedBytes = 256L * 1024 * 1024;
        private const long MaxSingleExtractedFileBytes = 128L * 1024 * 1024;
        private static readonly Regex ReleaseAssetRegex = new(
            @"^dnscrypt-proxy-win64-(?<version>[0-9]+\.[0-9]+\.[0-9]+)\.zip$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly JsonSerializerOptions MetadataJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly HttpClient httpClient;
        private readonly bool ownsHttpClient;
        private readonly string componentDirectory;
        private readonly string updateDirectory;
        private readonly string[] trustedReleasePublicKeys;
        private readonly SemaphoreSlim operationLock = new(1, 1);
        private bool disposed;

        public DnsCryptComponentManager(HttpClient httpClient = null)
            : this(
                httpClient,
                AppStoragePaths.DnsCryptComponentDirectory,
                AppStoragePaths.DnsCryptUpdateDirectory,
                [ReleaseSigningPublicKey],
                ownsHttpClient: httpClient is null)
        {
        }

        internal DnsCryptComponentManager(
            Func<string> proxyHostProvider,
            Func<int> proxyPortProvider)
            : this(
                ShadowsocksDohHttpClient.CreateTunneledClient(
                    proxyHostProvider,
                    proxyPortProvider,
                    TimeSpan.FromMinutes(5)),
                AppStoragePaths.DnsCryptComponentDirectory,
                AppStoragePaths.DnsCryptUpdateDirectory,
                [ReleaseSigningPublicKey],
                ownsHttpClient: true)
        {
        }

        internal DnsCryptComponentManager(
            HttpClient httpClient,
            string componentDirectory,
            string updateDirectory,
            IReadOnlyList<string> trustedReleasePublicKeys)
            : this(httpClient, componentDirectory, updateDirectory, trustedReleasePublicKeys, ownsHttpClient: false)
        {
        }

        private DnsCryptComponentManager(
            HttpClient httpClient,
            string componentDirectory,
            string updateDirectory,
            IReadOnlyList<string> trustedReleasePublicKeys,
            bool ownsHttpClient)
        {
            if (string.IsNullOrWhiteSpace(componentDirectory))
                throw new ArgumentException("A DNSCrypt component directory is required.", nameof(componentDirectory));
            if (string.IsNullOrWhiteSpace(updateDirectory))
                throw new ArgumentException("A DNSCrypt update directory is required.", nameof(updateDirectory));
            if (trustedReleasePublicKeys is null || trustedReleasePublicKeys.Count == 0)
                throw new ArgumentException("At least one trusted DNSCrypt release signing key is required.", nameof(trustedReleasePublicKeys));

            this.httpClient = httpClient ?? CreateHttpClient();
            this.ownsHttpClient = ownsHttpClient || httpClient is null;
            this.componentDirectory = Path.GetFullPath(componentDirectory);
            this.updateDirectory = Path.GetFullPath(updateDirectory);
            this.trustedReleasePublicKeys = trustedReleasePublicKeys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (this.trustedReleasePublicKeys.Length == 0)
                throw new ArgumentException("At least one non-empty DNSCrypt release signing key is required.", nameof(trustedReleasePublicKeys));

            CleanupTransientState();
        }

        public async Task<DnsCryptReleaseInfo> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            try
            {
                using HttpRequestMessage request = CreateGitHubApiRequest(HttpMethod.Get, new Uri(LatestReleaseApiUrl));
                using HttpResponseMessage response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                byte[] content = await ReadContentBytesWithLimitAsync(
                    response.Content,
                    MaxReleaseMetadataBytes,
                    cancellationToken).ConfigureAwait(false);
                GitHubRelease release = JsonSerializer.Deserialize<GitHubRelease>(content)
                    ?? throw new InvalidDataException("GitHub returned an empty DNSCrypt release document.");

                return SelectRelease(release);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or DnsCryptBootstrapException)
            {
                Logger.Error(exception, "DNSCrypt GitHub release lookup failed through DoH-over-Shadowsocks: {0}", LatestReleaseApiUrl);
                throw new DnsCryptComponentNetworkException(
                    "DNSCrypt component release metadata could not be fetched through DoH-over-Shadowsocks.",
                    exception);
            }
        }

        public async Task<DnsCryptPreparedComponent> PrepareLatestAsync(
            IProgress<DnsCryptComponentProgress> progress = null,
            bool forceDownload = false,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.CheckingRelease));
                DnsCryptReleaseInfo release = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
                return await PrepareReleaseCoreAsync(release, progress, forceDownload, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                operationLock.Release();
            }
        }

        internal async Task<DnsCryptPreparedComponent> PrepareReleaseAsync(
            DnsCryptReleaseInfo release,
            IProgress<DnsCryptComponentProgress> progress = null,
            bool forceDownload = false,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(release);
            await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await PrepareReleaseCoreAsync(release, progress, forceDownload, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                operationLock.Release();
            }
        }

        // Deliberately no public "download-and-activate" shortcut here. A downloaded
        // DNSCrypt release is untrusted for execution until DnsCryptRuntimeManager has
        // validated its reported version, generated configuration and DNS health-check.
        // The controller therefore uses PrepareLatestAsync -> ValidatePreparedAsync ->
        // ActivatePrepared -> runtime start/health-check -> CommitActivation. When an
        // existing version directory is replaced, its backup remains rollback-capable
        // until the post-activation runtime has proved healthy.

        public void ActivateVersion(Version version)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(version);
            ValidateComponentVersion(version);
            operationLock.Wait();
            try
            {
                ActivateVersionCore(version);
            }
            finally
            {
                operationLock.Release();
            }
        }

        internal DnsCryptActivationLease ActivatePrepared(DnsCryptPreparedComponent prepared)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(prepared);
            ArgumentNullException.ThrowIfNull(prepared.Version);
            ValidateComponentVersion(prepared.Version);
            ArgumentException.ThrowIfNullOrWhiteSpace(prepared.DirectoryPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(prepared.ExecutablePath);

            operationLock.Wait();
            try
            {
                string targetDirectory = GetVersionDirectory(prepared.Version);
                string preparedDirectory = Path.GetFullPath(prepared.DirectoryPath);
                string expectedExecutable = Path.GetFullPath(Path.Combine(preparedDirectory, ExpectedExecutableName));
                if (!string.Equals(Path.GetFullPath(prepared.ExecutablePath), expectedExecutable, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(expectedExecutable))
                {
                    throw new InvalidOperationException("The prepared DNSCrypt component is incomplete or points outside its prepared directory.");
                }

                if (string.Equals(
                        preparedDirectory.TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {
                    ActivateVersionCore(prepared.Version);
                    return null;
                }

                string componentRoot = Path.GetFullPath(componentDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!preparedDirectory.StartsWith(componentRoot, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(preparedDirectory).StartsWith(".prepared-", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The prepared DNSCrypt component directory is not trusted.");
                }

                ComponentMetadata originalMetadata = ReadMetadata();
                string backupDirectory = Path.Combine(
                    componentDirectory,
                    $".activate-backup-{FormatVersion(prepared.Version)}-{Guid.NewGuid():N}");
                bool movedTarget = false;
                bool movedPrepared = false;
                try
                {
                    if (Directory.Exists(targetDirectory))
                    {
                        Directory.Move(targetDirectory, backupDirectory);
                        movedTarget = true;
                    }
                    Directory.Move(preparedDirectory, targetDirectory);
                    movedPrepared = true;
                    ActivateVersionCore(prepared.Version);

                    return movedTarget
                        ? new DnsCryptActivationLease(
                            prepared.Version,
                            targetDirectory,
                            backupDirectory,
                            originalMetadata is not null,
                            originalMetadata?.ActiveVersion,
                            originalMetadata?.PreviousVersion,
                            originalMetadata?.AutoUpdate ?? true,
                            originalMetadata?.LastUpdateCheckUtc)
                        : null;
                }
                catch
                {
                    if (movedPrepared && Directory.Exists(targetDirectory))
                        TryDeleteDirectory(targetDirectory);
                    if (movedTarget && Directory.Exists(backupDirectory) && !Directory.Exists(targetDirectory))
                        Directory.Move(backupDirectory, targetDirectory);
                    throw;
                }
            }
            finally
            {
                operationLock.Release();
            }
        }

        internal void CommitActivation(DnsCryptActivationLease activation)
        {
            if (activation is null)
                return;
            ThrowIfDisposed();

            operationLock.Wait();
            try
            {
                ValidateActivationLease(activation);
                if (activation.Completed)
                    return;

                TryDeleteDirectory(activation.BackupDirectory);
                activation.Completed = true;
            }
            finally
            {
                operationLock.Release();
            }
        }

        internal void RollbackActivation(DnsCryptActivationLease activation)
        {
            ArgumentNullException.ThrowIfNull(activation);
            ThrowIfDisposed();

            operationLock.Wait();
            try
            {
                ValidateActivationLease(activation);
                if (activation.Completed)
                    throw new InvalidOperationException("The DNSCrypt activation transaction has already been completed.");
                if (!Directory.Exists(activation.BackupDirectory))
                    throw new DirectoryNotFoundException("The DNSCrypt activation backup is no longer available.");

                if (Directory.Exists(activation.TargetDirectory))
                    TryDeleteDirectory(activation.TargetDirectory);
                Directory.Move(activation.BackupDirectory, activation.TargetDirectory);

                if (activation.HadMetadata)
                {
                    ComponentMetadata restored = CreateDefaultMetadata();
                    restored.ActiveVersion = activation.OriginalActiveVersion;
                    restored.PreviousVersion = activation.OriginalPreviousVersion;
                    restored.AutoUpdate = activation.OriginalAutoUpdate;
                    restored.LastUpdateCheckUtc = activation.OriginalLastUpdateCheckUtc;
                    WriteMetadata(restored);
                }
                else
                {
                    TryDeleteFile(GetMetadataPath());
                }

                activation.Completed = true;
            }
            finally
            {
                operationLock.Release();
            }
        }

        private void ValidateActivationLease(DnsCryptActivationLease activation)
        {
            ArgumentNullException.ThrowIfNull(activation.Version);
            ValidateComponentVersion(activation.Version);

            string expectedTarget = Path.GetFullPath(GetVersionDirectory(activation.Version));
            string target = Path.GetFullPath(activation.TargetDirectory);
            if (!string.Equals(expectedTarget, target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The DNSCrypt activation target is not trusted.");

            string componentRoot = Path.GetFullPath(componentDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string backup = Path.GetFullPath(activation.BackupDirectory);
            if (!backup.StartsWith(componentRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(backup).StartsWith(".activate-backup-", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The DNSCrypt activation backup is not trusted.");
            }
        }

        public void DiscardPrepared(DnsCryptPreparedComponent prepared)
        {
            ThrowIfDisposed();
            if (prepared is null)
                return;
            ArgumentNullException.ThrowIfNull(prepared.Version);
            ValidateComponentVersion(prepared.Version);
            ArgumentException.ThrowIfNullOrWhiteSpace(prepared.DirectoryPath);

            operationLock.Wait();
            try
            {
                ComponentMetadata metadata = ReadMetadata();
                string formatted = FormatVersion(prepared.Version);
                string preparedDirectory = Path.GetFullPath(prepared.DirectoryPath);
                string targetDirectory = Path.GetFullPath(GetVersionDirectory(prepared.Version));

                if (!string.Equals(
                    preparedDirectory.TrimEnd(Path.DirectorySeparatorChar),
                    targetDirectory.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                {
                    string componentRoot = Path.GetFullPath(componentDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (preparedDirectory.StartsWith(componentRoot, StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileName(preparedDirectory).StartsWith(".prepared-", StringComparison.OrdinalIgnoreCase)
                        && Directory.Exists(preparedDirectory))
                    {
                        Directory.Delete(preparedDirectory, recursive: true);
                    }
                    return;
                }

                if (string.Equals(metadata?.ActiveVersion, formatted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(metadata?.PreviousVersion, formatted, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (Directory.Exists(targetDirectory))
                    Directory.Delete(targetDirectory, recursive: true);
            }
            finally
            {
                operationLock.Release();
            }
        }

        public void DiscardPreparedVersion(Version version)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(version);
            ValidateComponentVersion(version);
            operationLock.Wait();
            try
            {
                ComponentMetadata metadata = ReadMetadata();
                string formatted = FormatVersion(version);
                if (string.Equals(metadata?.ActiveVersion, formatted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(metadata?.PreviousVersion, formatted, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string directory = GetVersionDirectory(version);
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            finally
            {
                operationLock.Release();
            }
        }

        public DnsCryptPreparedComponent Rollback()
        {
            ThrowIfDisposed();
            operationLock.Wait();
            try
            {
                ComponentMetadata metadata = ReadMetadata();
                Version previous = ParseStoredVersion(metadata?.PreviousVersion);
                Version active = ParseStoredVersion(metadata?.ActiveVersion);
                if (previous is null)
                    throw new InvalidOperationException("No previous DNSCrypt Proxy version is available for rollback.");

                string previousExecutable = GetExecutablePath(previous);
                if (!File.Exists(previousExecutable))
                    throw new InvalidOperationException($"The previous DNSCrypt Proxy version {FormatVersion(previous)} is incomplete.");

                ComponentMetadata updated = metadata ?? CreateDefaultMetadata();
                updated.ActiveVersion = FormatVersion(previous);
                updated.PreviousVersion = active is null ? null : FormatVersion(active);
                WriteMetadata(updated);
                CleanupUnusedVersions(updated.ActiveVersion, updated.PreviousVersion);

                return new DnsCryptPreparedComponent(previous, GetVersionDirectory(previous), previousExecutable);
            }
            finally
            {
                operationLock.Release();
            }
        }

        public DnsCryptComponentStatus GetStatus()
        {
            ThrowIfDisposed();
            ComponentMetadata metadata = ReadMetadata();
            Version active = ParseStoredVersion(metadata?.ActiveVersion);
            Version previous = ParseStoredVersion(metadata?.PreviousVersion);
            if (previous is not null && !File.Exists(GetExecutablePath(previous)))
                previous = null;
            string executablePath = active is null ? null : GetExecutablePath(active);
            bool installed = active is not null && File.Exists(executablePath);

            return new DnsCryptComponentStatus(
                installed,
                installed ? active : null,
                previous,
                installed ? executablePath : null,
                metadata?.AutoUpdate ?? true,
                metadata?.LastUpdateCheckUtc);
        }

        public void SetAutoUpdate(bool enabled)
        {
            ThrowIfDisposed();
            operationLock.Wait();
            try
            {
                ComponentMetadata metadata = ReadMetadata() ?? CreateDefaultMetadata();
                metadata.AutoUpdate = enabled;
                WriteMetadata(metadata);
            }
            finally
            {
                operationLock.Release();
            }
        }

        public void RecordUpdateCheck(DateTimeOffset checkedAtUtc)
        {
            ThrowIfDisposed();
            operationLock.Wait();
            try
            {
                ComponentMetadata metadata = ReadMetadata() ?? CreateDefaultMetadata();
                metadata.LastUpdateCheckUtc = checkedAtUtc.ToUniversalTime();
                WriteMetadata(metadata);
            }
            finally
            {
                operationLock.Release();
            }
        }

        public void Remove()
        {
            ThrowIfDisposed();
            operationLock.Wait();
            try
            {
                if (Directory.Exists(componentDirectory))
                    Directory.Delete(componentDirectory, recursive: true);
                if (Directory.Exists(updateDirectory))
                    Directory.Delete(updateDirectory, recursive: true);
            }
            finally
            {
                operationLock.Release();
            }
        }

        private ComponentMetadata ReadMetadata()
        {
            string path = GetMetadataPath();
            if (!File.Exists(path))
                return null;

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                ComponentMetadata metadata = JsonSerializer.Deserialize<ComponentMetadata>(json, MetadataJsonOptions);
                if (metadata is null
                    || !string.Equals(metadata.ComponentId, ComponentId, StringComparison.Ordinal)
                    || !string.Equals(metadata.Repository, Repository, StringComparison.Ordinal))
                {
                    return null;
                }
                return metadata;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private void WriteMetadata(ComponentMetadata metadata)
        {
            Directory.CreateDirectory(componentDirectory);
            metadata.ComponentId = ComponentId;
            metadata.Repository = Repository;
            string path = GetMetadataPath();
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                string json = JsonSerializer.Serialize(metadata, MetadataJsonOptions);
                File.WriteAllText(temporary, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                TryDeleteFile(temporary);
            }
        }

        private static ComponentMetadata CreateDefaultMetadata() => new()
        {
            ComponentId = ComponentId,
            Repository = Repository,
            AutoUpdate = true,
        };

        private string GetMetadataPath() => Path.Combine(componentDirectory, MetadataFileName);
        private string GetVersionDirectory(Version version) => Path.Combine(componentDirectory, FormatVersion(version));
        private string GetExecutablePath(Version version) => Path.Combine(GetVersionDirectory(version), ExpectedExecutableName);

        private void CleanupTransientState()
        {
            if (Directory.Exists(componentDirectory))
            {
                foreach (string directory in Directory.EnumerateDirectories(componentDirectory))
                {
                    string name = Path.GetFileName(directory);
                    if (name.StartsWith(".prepared-", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(".install-", StringComparison.OrdinalIgnoreCase))
                    {
                        TryDeleteDirectory(directory);
                        continue;
                    }

                    if (TryParseActivationBackupVersion(name, out Version backupVersion))
                    {
                        string targetDirectory = GetVersionDirectory(backupVersion);
                        if (Directory.Exists(targetDirectory))
                        {
                            TryDeleteDirectory(directory);
                        }
                        else
                        {
                            try
                            {
                                Directory.Move(directory, targetDirectory);
                            }
                            catch
                            {
                                // Keep the backup intact if crash recovery cannot restore it.
                            }
                        }
                    }
                }

                foreach (string temporaryMetadata in Directory.EnumerateFiles(
                    componentDirectory, MetadataFileName + ".tmp-*", SearchOption.TopDirectoryOnly))
                {
                    TryDeleteFile(temporaryMetadata);
                }
            }

            if (Directory.Exists(updateDirectory))
            {
                foreach (string directory in Directory.EnumerateDirectories(updateDirectory))
                    TryDeleteDirectory(directory);
                foreach (string file in Directory.EnumerateFiles(updateDirectory))
                    TryDeleteFile(file);
            }
        }

        private void CleanupUnusedVersions(string activeVersion, string previousVersion)
        {
            if (!Directory.Exists(componentDirectory))
                return;

            foreach (string directory in Directory.EnumerateDirectories(componentDirectory))
            {
                string name = Path.GetFileName(directory);
                if (name.StartsWith(".install-", StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteDirectory(directory);
                    continue;
                }

                if (string.Equals(name, activeVersion, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, previousVersion, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Version.TryParse(name, out _))
                    TryDeleteDirectory(directory);
            }
        }

        private static void ValidateZipEntryType(ZipArchiveEntry entry)
        {
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType != 0 && unixType != 0x4000 && unixType != 0x8000)
                throw new InvalidDataException($"DNSCrypt archive contains unsupported special entry '{entry.FullName}'.");

            if ((((FileAttributes)entry.ExternalAttributes) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"DNSCrypt archive contains a reparse-point entry '{entry.FullName}'.");
        }

        private static void CopyWithLimit(Stream input, FileStream output, long declaredLength, long maxBytes)
        {
            byte[] buffer = new byte[64 * 1024];
            long copied = 0;
            while (true)
            {
                int read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;

                copied = checked(copied + read);
                if (copied > maxBytes || copied > declaredLength)
                    throw new InvalidDataException("DNSCrypt archive entry expanded beyond its declared size.");
                output.Write(buffer, 0, read);
            }

            if (copied != declaredLength)
                throw new InvalidDataException("DNSCrypt archive entry length does not match its declared size.");
        }

        private static bool TryParseActivationBackupVersion(string directoryName, out Version version)
        {
            const string prefix = ".activate-backup-";
            version = null;
            if (!directoryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            string remainder = directoryName[prefix.Length..];
            int separator = remainder.LastIndexOf('-');
            if (separator <= 0)
                return false;

            string versionText = remainder[..separator];
            version = ParseStoredVersion(versionText);
            return version is not null;
        }

        private static string EnsureTrailingSeparator(string path) =>
            path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;

        private static void ValidateComponentVersion(Version version)
        {
            if (version.Build < 0 || version.Revision >= 0)
                throw new ArgumentException("DNSCrypt component versions must contain exactly three numeric parts.", nameof(version));
        }

        private static string FormatVersion(Version version) => $"{version.Major}.{version.Minor}.{version.Build}";

        private static Version ParseStoredVersion(string value) =>
            Version.TryParse(value, out Version version) && version.Build >= 0 && version.Revision < 0 ? version : null;

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch
            {
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            operationLock.Dispose();
            if (ownsHttpClient)
                httpClient.Dispose();
        }

        internal sealed class GitHubRelease
        {
            [JsonPropertyName("tag_name")]
            public string TagName { get; set; }

            [JsonPropertyName("draft")]
            public bool Draft { get; set; }

            [JsonPropertyName("prerelease")]
            public bool Prerelease { get; set; }

            [JsonPropertyName("assets")]
            public GitHubAsset[] Assets { get; set; }
        }

        internal sealed class GitHubAsset
        {
            [JsonPropertyName("name")]
            public string Name { get; set; }

            [JsonPropertyName("browser_download_url")]
            public string DownloadUrl { get; set; }

            [JsonPropertyName("size")]
            public long Size { get; set; }

            [JsonPropertyName("digest")]
            public string Digest { get; set; }
        }

        private sealed class ComponentMetadata
        {
            public string ComponentId { get; set; }
            public string ActiveVersion { get; set; }
            public string PreviousVersion { get; set; }
            public string Repository { get; set; }
            public bool AutoUpdate { get; set; } = true;
            public DateTimeOffset? LastUpdateCheckUtc { get; set; }
        }
    }
}
