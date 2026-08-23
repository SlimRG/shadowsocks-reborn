using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Shadowsocks.Controller.Service;
using Shadowsocks.Model;
using Shadowsocks.Core.Storage;

namespace Shadowsocks.Controller.Traffic
{
    /// <summary>
    /// Downloads the official WinDivert runtime only when Admin Mode is requested.
    /// The driver is not embedded in Shadowsocks and is not touched by User Mode.
    /// </summary>
    internal sealed class WinDivertInstaller : IDisposable
    {
        public const string Version = "2.2.2";
        public const string PackageUrl = "https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip";
        public const string DllSha256 = "c1e060ee19444a259b2162f8af0f3fe8c4428a1c6f694dce20de194ac8d7d9a2";
        public const string DriverSha256 = "8da085332782708d8767bcace5327a6ec7283c17cfb85e40b03cd2323a90ddc2";

        private const int MaxPackageBytes = 8 * 1024 * 1024;
        private const long MaxRuntimeEntryBytes = 4 * 1024 * 1024;
        private const ushort ImageFileMachineAmd64 = 0x8664;

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly SemaphoreSlim _installLock = new(1, 1);
        private bool _disposed;

        public async Task<string> EnsureInstalledAsync(Configuration configuration, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(configuration);

            string installDirectory = GetInstallDirectory(configuration);
            string dllPath = Path.Combine(installDirectory, "WinDivert.dll");
            string driverPath = Path.Combine(installDirectory, "WinDivert64.sys");
            if (TryValidateInstalledRuntime(dllPath, driverPath))
            {
                return installDirectory;
            }

            await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (TryValidateInstalledRuntime(dllPath, driverPath))
                {
                    return installDirectory;
                }

                TryDeleteDirectory(installDirectory);
                Directory.CreateDirectory(installDirectory);
                try
                {
                    byte[] package = await DownloadPackageAsync(configuration, cancellationToken).ConfigureAwait(false);
                    ExtractRuntime(package, installDirectory);

                    if (!File.Exists(dllPath) || !File.Exists(driverPath))
                    {
                        throw new InvalidDataException("WinDivert package did not contain the x64 runtime files.");
                    }

                    ValidatePinnedRuntimeFile(dllPath, DllSha256);
                    ValidatePinnedRuntimeFile(driverPath, DriverSha256);

                    string marker = Path.Combine(installDirectory, "source.txt");
                    File.WriteAllText(
                        marker,
                        $"WinDivert {Version}{Environment.NewLine}" +
                        $"{PackageUrl}{Environment.NewLine}" +
                        $"WinDivert.dll SHA-256: {DllSha256}{Environment.NewLine}" +
                        $"WinDivert64.sys SHA-256: {DriverSha256}{Environment.NewLine}");
                    return installDirectory;
                }
                catch
                {
                    TryDeleteDirectory(installDirectory);
                    throw;
                }
            }
            finally
            {
                _installLock.Release();
            }
        }

        public static string GetInstallDirectory(Configuration configuration)
        {
            return Path.Combine(AppStoragePaths.WinDivertRuntimeRoot, Version);
        }

        private static async Task<byte[]> DownloadPackageAsync(Configuration configuration, CancellationToken cancellationToken)
        {
            Exception proxiedFailure = null;
            try
            {
                using HttpClient proxiedClient = LocalProxyHttpClient.Create(configuration);
                return await DownloadBoundedAsync(proxiedClient, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                proxiedFailure = exception;
                Logger.Warn(exception, "Unable to download WinDivert through the local Shadowsocks proxy; trying direct HTTPS.");
            }

            try
            {
                using HttpClient directClient = new()
                {
                    Timeout = TimeSpan.FromSeconds(30),
                };
                directClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", configuration.userAgentString);
                return await DownloadBoundedAsync(directClient, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception directFailure) when (directFailure is not OperationCanceledException)
            {
                throw new HttpRequestException(
                    "Unable to download the official WinDivert package through Shadowsocks or directly.",
                    new AggregateException(proxiedFailure, directFailure));
            }
        }

        internal static void ExtractRuntime(byte[] package, string installDirectory)
        {
            ExtractRuntime(package, installDirectory, DllSha256, DriverSha256);
        }

        internal static void ExtractRuntime(
            byte[] package,
            string installDirectory,
            string expectedDllSha256,
            string expectedDriverSha256)
        {
            ArgumentNullException.ThrowIfNull(package);
            if (package.Length == 0 || package.Length > MaxPackageBytes)
                throw new InvalidDataException("WinDivert package size is invalid.");

            using MemoryStream packageStream = new(package, writable: false);
            using ZipArchive archive = new(packageStream, ZipArchiveMode.Read, leaveOpen: false);

            string archiveRoot = $"WinDivert-{Version}-A/x64/";
            ExtractRequiredEntry(
                archive,
                archiveRoot + "WinDivert.dll",
                Path.Combine(installDirectory, "WinDivert.dll"),
                expectedDllSha256);
            ExtractRequiredEntry(
                archive,
                archiveRoot + "WinDivert64.sys",
                Path.Combine(installDirectory, "WinDivert64.sys"),
                expectedDriverSha256);
        }

        internal static void ValidateX64PortableExecutable(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 0x40)
                throw new InvalidDataException($"WinDivert runtime file '{Path.GetFileName(path)}' is not a valid PE image.");

            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0x5A4D)
                throw new InvalidDataException($"WinDivert runtime file '{Path.GetFileName(path)}' is missing the MZ header.");

            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            if (peOffset < 0x40 || peOffset > stream.Length - 6)
                throw new InvalidDataException($"WinDivert runtime file '{Path.GetFileName(path)}' has an invalid PE offset.");

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550)
                throw new InvalidDataException($"WinDivert runtime file '{Path.GetFileName(path)}' is missing the PE signature.");
            if (reader.ReadUInt16() != ImageFileMachineAmd64)
                throw new InvalidDataException($"WinDivert runtime file '{Path.GetFileName(path)}' is not an x64 PE image.");
        }

        internal static void ValidatePinnedRuntimeFile(string path, string expectedSha256)
        {
            ValidateX64PortableExecutable(path);
            byte[] expectedHash;
            try
            {
                expectedHash = Convert.FromHexString(expectedSha256);
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException("The pinned WinDivert SHA-256 value is invalid.", exception);
            }

            if (expectedHash.Length != 32)
                throw new InvalidOperationException("The pinned WinDivert SHA-256 value must contain 32 bytes.");

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] actualHash = SHA256.HashData(stream);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                throw new InvalidDataException(
                    $"WinDivert runtime file '{Path.GetFileName(path)}' did not match its pinned SHA-256 digest.");
            }
        }

        private static bool TryValidateInstalledRuntime(string dllPath, string driverPath)
        {
            if (!File.Exists(dllPath) || !File.Exists(driverPath))
                return false;

            try
            {
                ValidatePinnedRuntimeFile(dllPath, DllSha256);
                ValidatePinnedRuntimeFile(driverPath, DriverSha256);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Logger.Warn(exception, "Cached WinDivert runtime is invalid and will be downloaded again.");
                return false;
            }
        }

        private static async Task<byte[]> DownloadBoundedAsync(HttpClient client, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await client.GetAsync(
                PackageUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && (contentLength.Value <= 0 || contentLength.Value > MaxPackageBytes))
                throw new InvalidDataException("WinDivert package Content-Length is invalid.");

            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            int initialCapacity = contentLength.HasValue && contentLength.Value > 0 && contentLength.Value <= MaxPackageBytes
                ? (int)contentLength.Value
                : 0;
            using var output = new MemoryStream(initialCapacity);
            byte[] buffer = new byte[81920];
            int total = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                total = checked(total + read);
                if (total > MaxPackageBytes)
                    throw new InvalidDataException("WinDivert package exceeded the maximum allowed size.");
                output.Write(buffer, 0, read);
            }

            if (total == 0)
                throw new InvalidDataException("WinDivert package was empty.");
            return output.ToArray();
        }

        private static void ExtractRequiredEntry(
            ZipArchive archive,
            string expectedPath,
            string destination,
            string expectedSha256)
        {
            string normalizedExpected = expectedPath.Replace('\\', '/');
            ZipArchiveEntry[] matches = archive.Entries
                .Where(candidate => string.Equals(
                    candidate.FullName.Replace('\\', '/'),
                    normalizedExpected,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException($"WinDivert package must contain exactly one '{normalizedExpected}' entry.");

            ZipArchiveEntry entry = matches[0];
            if (entry.Length <= 0 || entry.Length > MaxRuntimeEntryBytes)
                throw new InvalidDataException($"WinDivert package entry '{normalizedExpected}' has an invalid size.");

            string temporary = destination + ".new";
            try
            {
                using (Stream input = entry.Open())
                using (FileStream output = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                    if (output.Length != entry.Length || output.Length > MaxRuntimeEntryBytes)
                        throw new InvalidDataException($"WinDivert package entry '{normalizedExpected}' extracted to an invalid size.");
                    output.Flush(flushToDisk: true);
                }

                ValidatePinnedRuntimeFile(temporary, expectedSha256);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                }
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Logger.Warn(exception, "Unable to clear the invalid WinDivert runtime directory before reinstalling.");
            }
        }
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _installLock.Dispose();
            GC.SuppressFinalize(this);
        }

    }
}
