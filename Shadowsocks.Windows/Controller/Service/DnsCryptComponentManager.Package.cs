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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shadowsocks.Core;

namespace Shadowsocks.Controller.Service
{
    public sealed partial class DnsCryptComponentManager
    {
        internal static DnsCryptReleaseInfo SelectRelease(GitHubRelease release)
        {
            ArgumentNullException.ThrowIfNull(release);
            if (release.Draft)
                throw new InvalidDataException("Draft DNSCrypt releases cannot be installed.");
            if (release.Prerelease)
                throw new InvalidDataException("Prerelease DNSCrypt releases cannot be installed by the stable component channel.");

            Version tagVersion = ParseReleaseTag(release.TagName)
                ?? throw new InvalidDataException($"Unsupported DNSCrypt release tag '{release.TagName}'.");
            GitHubAsset[] assets = release.Assets ?? [];
            GitHubAsset archive = null;
            Version assetVersion = null;

            foreach (GitHubAsset candidate in assets)
            {
                Match match = ReleaseAssetRegex.Match(candidate?.Name ?? string.Empty);
                if (!match.Success)
                    continue;

                if (!Version.TryParse(match.Groups["version"].Value, out Version parsed))
                    continue;
                if (archive is not null)
                    throw new InvalidDataException("The DNSCrypt release contains multiple matching Windows x64 ZIP assets.");

                archive = candidate;
                assetVersion = parsed;
            }

            if (archive is null || assetVersion is null)
                throw new InvalidDataException("The DNSCrypt release does not contain the expected Windows x64 ZIP asset.");
            if (!assetVersion.Equals(tagVersion))
            {
                throw new InvalidDataException(
                    $"DNSCrypt release tag {FormatVersion(tagVersion)} does not match asset version {FormatVersion(assetVersion)}.");
            }

            string signatureName = archive.Name + ".minisig";
            GitHubAsset[] signatures = assets.Where(candidate =>
                string.Equals(candidate?.Name, signatureName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (signatures.Length != 1)
                throw new InvalidDataException($"The DNSCrypt release must contain exactly one signature asset '{signatureName}'.");
            GitHubAsset signature = signatures[0];

            Uri archiveUri = ValidateOfficialReleaseDownloadUri(archive.DownloadUrl, archive.Name, tagVersion);
            Uri signatureUri = ValidateOfficialReleaseDownloadUri(signature.DownloadUrl, signature.Name, tagVersion);
            string digest = NormalizeSha256Digest(archive.Digest);
            if (archive.Size < 0 || archive.Size > MaxArchiveDownloadBytes)
                throw new InvalidDataException("The DNSCrypt archive size reported by GitHub is outside the allowed range.");

            return new DnsCryptReleaseInfo(
                tagVersion,
                release.TagName,
                archive.Name,
                archiveUri,
                signature.Name,
                signatureUri,
                archive.Size,
                digest);
        }

        internal static void ExtractZipSafely(string zipPath, string destinationDirectory)
        {
            if (!File.Exists(zipPath))
                throw new FileNotFoundException("DNSCrypt archive was not found.", zipPath);

            Directory.CreateDirectory(destinationDirectory);
            string root = EnsureTrailingSeparator(Path.GetFullPath(destinationDirectory));
            long totalExtracted = 0;
            int entryCount = 0;

            using FileStream stream = new(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                entryCount++;
                if (entryCount > MaxArchiveEntries)
                    throw new InvalidDataException($"DNSCrypt archive contains more than {MaxArchiveEntries} entries.");
                ValidateZipEntryType(entry);

                string name = entry.FullName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (Path.IsPathRooted(name)
                    || name.StartsWith('/')
                    || name.StartsWith('\\')
                    || name.Contains(':'))
                {
                    throw new InvalidDataException($"Unsafe DNSCrypt archive entry '{name}'.");
                }

                string normalizedName = name
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                string target = Path.GetFullPath(Path.Combine(root, normalizedName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"DNSCrypt archive entry escapes the extraction directory: '{name}'.");

                bool directoryEntry = name.EndsWith('/')
                    || name.EndsWith('\\');
                if (directoryEntry)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                if (entry.Length < 0 || entry.Length > MaxSingleExtractedFileBytes)
                    throw new InvalidDataException($"DNSCrypt archive entry '{name}' is too large.");
                totalExtracted = checked(totalExtracted + entry.Length);
                if (totalExtracted > MaxExtractedBytes)
                    throw new InvalidDataException("DNSCrypt archive expands beyond the allowed size.");

                string parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(parent))
                    Directory.CreateDirectory(parent);

                using Stream input = entry.Open();
                using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CopyWithLimit(input, output, entry.Length, MaxSingleExtractedFileBytes);
            }
        }

        private async Task<DnsCryptPreparedComponent> PrepareReleaseCoreAsync(
            DnsCryptReleaseInfo release,
            IProgress<DnsCryptComponentProgress> progress,
            bool forceDownload,
            CancellationToken cancellationToken)
        {
            string existingExecutable = GetExecutablePath(release.Version);
            if (!forceDownload && File.Exists(existingExecutable))
            {
                progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.Prepared));
                return new DnsCryptPreparedComponent(
                    release.Version,
                    GetVersionDirectory(release.Version),
                    existingExecutable);
            }

            Directory.CreateDirectory(updateDirectory);
            Directory.CreateDirectory(componentDirectory);
            string operationDirectory = Path.Combine(updateDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(operationDirectory);
            string archivePath = Path.Combine(operationDirectory, release.ArchiveName);
            string signaturePath = Path.Combine(operationDirectory, release.SignatureName);
            string extractionDirectory = Path.Combine(operationDirectory, "extract");

            try
            {
                progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.DownloadingArchive));
                await DownloadFileAsync(
                    release.ArchiveDownloadUri,
                    archivePath,
                    MaxArchiveDownloadBytes,
                    DnsCryptComponentStage.DownloadingArchive,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                if (release.ArchiveSize > 0 && new FileInfo(archivePath).Length != release.ArchiveSize)
                    throw new InvalidDataException("Downloaded DNSCrypt archive size does not match GitHub release metadata.");

                progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.DownloadingSignature));
                await DownloadFileAsync(
                    release.SignatureDownloadUri,
                    signaturePath,
                    MaxSignatureDownloadBytes,
                    DnsCryptComponentStage.DownloadingSignature,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.Verifying));
                VerifySha256IfAvailable(archivePath, release.Sha256Digest);
                string signatureText = await File.ReadAllTextAsync(signaturePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                bool validSignature = trustedReleasePublicKeys.Any(key =>
                {
                    try
                    {
                        return MinisignVerifier.VerifyFile(archivePath, signatureText, key);
                    }
                    catch (InvalidDataException)
                    {
                        return false;
                    }
                });
                if (!validSignature)
                    throw new CryptographicException("DNSCrypt Proxy release signature verification failed.");

                progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.Installing));
                Directory.CreateDirectory(extractionDirectory);
                ExtractZipSafely(archivePath, extractionDirectory);
                string[] executables = Directory
                    .EnumerateFiles(extractionDirectory, ExpectedExecutableName, SearchOption.AllDirectories)
                    .ToArray();
                if (executables.Length != 1)
                {
                    throw new InvalidDataException(
                        $"DNSCrypt archive must contain exactly one '{ExpectedExecutableName}' file, but {executables.Length} were found.");
                }

                string versionDirectory = GetVersionDirectory(release.Version);
                bool preserveExistingVersion = forceDownload && Directory.Exists(versionDirectory);
                string preparedDirectory = preserveExistingVersion
                    ? Path.Combine(componentDirectory, $".prepared-{FormatVersion(release.Version)}-{Guid.NewGuid():N}")
                    : versionDirectory;
                string installStaging = Path.Combine(componentDirectory, $".install-{Guid.NewGuid():N}");
                try
                {
                    Directory.CreateDirectory(installStaging);
                    string stagedExecutable = Path.Combine(installStaging, ExpectedExecutableName);
                    File.Copy(executables[0], stagedExecutable, overwrite: false);

                    if (Directory.Exists(preparedDirectory))
                        throw new IOException($"DNSCrypt prepared directory already exists: {preparedDirectory}");
                    Directory.Move(installStaging, preparedDirectory);
                }
                catch
                {
                    TryDeleteDirectory(installStaging);
                    if (preserveExistingVersion)
                        TryDeleteDirectory(preparedDirectory);
                    throw;
                }

                string preparedExecutable = Path.Combine(preparedDirectory, ExpectedExecutableName);
                if (!File.Exists(preparedExecutable))
                    throw new InvalidDataException("DNSCrypt component preparation did not produce the expected executable.");

                progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.Prepared));
                return new DnsCryptPreparedComponent(release.Version, preparedDirectory, preparedExecutable);
            }
            finally
            {
                TryDeleteDirectory(operationDirectory);
            }
        }

        private void ActivateVersionCore(Version version)
        {
            string executable = GetExecutablePath(version);
            if (!File.Exists(executable))
                throw new InvalidOperationException($"DNSCrypt Proxy {FormatVersion(version)} is not prepared.");

            ComponentMetadata metadata = ReadMetadata() ?? CreateDefaultMetadata();
            Version active = ParseStoredVersion(metadata.ActiveVersion);
            if (active is null || active != version)
            {
                metadata.PreviousVersion = active is null ? null : FormatVersion(active);
                metadata.ActiveVersion = FormatVersion(version);
            }
            WriteMetadata(metadata);
            CleanupUnusedVersions(metadata.ActiveVersion, metadata.PreviousVersion);
        }

        private async Task DownloadFileAsync(
            Uri uri,
            string destination,
            long maxBytes,
            DnsCryptComponentStage stage,
            IProgress<DnsCryptComponentProgress> progress,
            CancellationToken cancellationToken)
        {
            try
            {
                using HttpRequestMessage request = CreateDownloadRequest(HttpMethod.Get, uri);
                using HttpResponseMessage response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                long? contentLength = response.Content.Headers.ContentLength;
                if (contentLength.HasValue && contentLength.Value > maxBytes)
                    throw new InvalidDataException($"DNSCrypt download exceeds the {maxBytes}-byte safety limit.");

                await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[64 * 1024];
                long transferred = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    transferred = checked(transferred + read);
                    if (transferred > maxBytes)
                        throw new InvalidDataException($"DNSCrypt download exceeds the {maxBytes}-byte safety limit.");

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(new DnsCryptComponentProgress(stage, transferred, contentLength));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or DnsCryptBootstrapException)
            {
                Logger.Error(exception, "DNSCrypt release asset download failed through DoH-over-Shadowsocks: {0}", uri);
                throw new DnsCryptComponentNetworkException(
                    $"DNSCrypt component asset '{uri.Host}' could not be downloaded through DoH-over-Shadowsocks.",
                    exception);
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5),
            };
            return client;
        }

        private static HttpRequestMessage CreateGitHubApiRequest(HttpMethod method, Uri uri)
        {
            HttpRequestMessage request = CreateDownloadRequest(method, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            return request;
        }

        private static HttpRequestMessage CreateDownloadRequest(HttpMethod method, Uri uri)
        {
            var request = new HttpRequestMessage(method, uri);
            request.Headers.UserAgent.ParseAdd($"Shadowsocks-Reborn/{ApplicationInfo.Version}");
            return request;
        }

        private static async Task<byte[]> ReadContentBytesWithLimitAsync(
            HttpContent content,
            long maxBytes,
            CancellationToken cancellationToken)
        {
            long? contentLength = content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > maxBytes)
                throw new InvalidDataException($"HTTP response exceeds the {maxBytes}-byte safety limit.");

            await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            int initialCapacity = contentLength.HasValue && contentLength.Value > 0 && contentLength.Value <= int.MaxValue
                ? (int)contentLength.Value
                : 0;
            using var output = new MemoryStream(initialCapacity);
            byte[] buffer = new byte[32 * 1024];
            long transferred = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                transferred = checked(transferred + read);
                if (transferred > maxBytes)
                    throw new InvalidDataException($"HTTP response exceeds the {maxBytes}-byte safety limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            return output.ToArray();
        }

        private static Uri ValidateOfficialReleaseDownloadUri(string value, string assetName, Version releaseVersion)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new InvalidDataException($"DNSCrypt asset '{assetName}' has an untrusted download URL.");
            }

            string[] segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToArray();
            bool officialPath = segments.Length == 6
                && string.Equals(segments[0], "DNSCrypt", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[1], "dnscrypt-proxy", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[2], "releases", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[3], "download", StringComparison.OrdinalIgnoreCase)
                && ParseReleaseTag(segments[4]) is Version urlVersion
                && urlVersion.Equals(releaseVersion)
                && string.Equals(segments[5], assetName, StringComparison.OrdinalIgnoreCase);
            if (!officialPath)
                throw new InvalidDataException($"DNSCrypt asset '{assetName}' points outside the expected official release path.");

            return uri;
        }

        private static Version ParseReleaseTag(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName))
                return null;
            string normalized = tagName.Trim();
            if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(1);

            string[] parts = normalized.Split('.');
            if (parts.Length != 3 || parts.Any(part => !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                return null;
            return Version.TryParse(normalized, out Version version) ? version : null;
        }

        private static string NormalizeSha256Digest(string digest)
        {
            if (string.IsNullOrWhiteSpace(digest))
                return null;

            const string prefix = "sha256:";
            if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DNSCrypt release contains an unsupported GitHub asset digest.");

            string hex = digest.Substring(prefix.Length).Trim();
            if (hex.Length != 64 || !hex.All(Uri.IsHexDigit))
                throw new InvalidDataException("DNSCrypt release contains a malformed SHA-256 digest.");
            return hex.ToLowerInvariant();
        }

        private static void VerifySha256IfAvailable(string filePath, string expectedHex)
        {
            if (string.IsNullOrWhiteSpace(expectedHex))
                return;

            byte[] expected = Convert.FromHexString(expectedHex);
            byte[] actual;
            using (FileStream input = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha256 = SHA256.Create())
            {
                actual = sha256.ComputeHash(input);
            }

            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                throw new CryptographicException("DNSCrypt Proxy archive SHA-256 digest does not match GitHub release metadata.");
        }

    }
}
