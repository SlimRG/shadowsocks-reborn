using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Shadowsocks.Controller.Service;
using Shadowsocks.Model;

namespace Shadowsocks.Controller.Traffic
{
    /// <summary>
    /// Downloads the official WinDivert runtime only when Admin Mode is requested.
    /// The driver is not embedded in Shadowsocks and is not touched by User Mode.
    /// </summary>
    internal sealed class WinDivertInstaller
    {
        public const string Version = "2.2.2";
        public const string PackageUrl = "https://reqrypt.org/download/WinDivert-2.2.2-A.zip";

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly SemaphoreSlim _installLock = new(1, 1);

        public async Task<string> EnsureInstalledAsync(Configuration configuration, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            string installDirectory = GetInstallDirectory(configuration);
            string dllPath = Path.Combine(installDirectory, "WinDivert.dll");
            string driverPath = Path.Combine(installDirectory, "WinDivert64.sys");
            if (File.Exists(dllPath) && File.Exists(driverPath))
            {
                return installDirectory;
            }

            await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(dllPath) && File.Exists(driverPath))
                {
                    return installDirectory;
                }

                Directory.CreateDirectory(installDirectory);
                byte[] package = await DownloadPackageAsync(configuration, cancellationToken).ConfigureAwait(false);
                ExtractRuntime(package, installDirectory);

                if (!File.Exists(dllPath) || !File.Exists(driverPath))
                {
                    throw new InvalidDataException("WinDivert package did not contain the x64 runtime files.");
                }

                string marker = Path.Combine(installDirectory, "source.txt");
                File.WriteAllText(marker, $"WinDivert {Version}{Environment.NewLine}{PackageUrl}{Environment.NewLine}");
                return installDirectory;
            }
            finally
            {
                _installLock.Release();
            }
        }

        public static string GetInstallDirectory(Configuration configuration)
        {
            string root = configuration.portableMode
                ? Path.Combine(Shadowsocks.Engine.RuntimeEnvironment.WorkingDirectory, "runtime")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Shadowsocks",
                    "runtime");
            return Path.Combine(root, "windivert", Version);
        }

        private static async Task<byte[]> DownloadPackageAsync(Configuration configuration, CancellationToken cancellationToken)
        {
            Exception proxiedFailure = null;
            try
            {
                using HttpClient proxiedClient = LocalProxyHttpClient.Create(configuration);
                return await proxiedClient.GetByteArrayAsync(PackageUrl, cancellationToken).ConfigureAwait(false);
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
                return await directClient.GetByteArrayAsync(PackageUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception directFailure) when (directFailure is not OperationCanceledException)
            {
                throw new HttpRequestException(
                    "Unable to download the official WinDivert package through Shadowsocks or directly.",
                    new AggregateException(proxiedFailure, directFailure));
            }
        }

        private static void ExtractRuntime(byte[] package, string installDirectory)
        {
            using MemoryStream packageStream = new(package, writable: false);
            using ZipArchive archive = new(packageStream, ZipArchiveMode.Read, leaveOpen: false);

            ExtractRequiredEntry(archive, "/x64/WinDivert.dll", Path.Combine(installDirectory, "WinDivert.dll"));
            ExtractRequiredEntry(archive, "/x64/WinDivert64.sys", Path.Combine(installDirectory, "WinDivert64.sys"));
        }

        private static void ExtractRequiredEntry(ZipArchive archive, string suffix, string destination)
        {
            ZipArchiveEntry entry = archive.Entries.FirstOrDefault(candidate =>
                candidate.FullName.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                throw new InvalidDataException($"WinDivert package is missing {suffix}.");
            }

            string temporary = destination + ".new";
            try
            {
                using (Stream input = entry.Open())
                using (FileStream output = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }

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
    }
}
