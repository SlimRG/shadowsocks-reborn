using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using NLog;
using Shadowsocks.Core;
using Shadowsocks.Core.Storage;

namespace Shadowsocks.Controller.Traffic
{
    internal static class NetworkServiceRuntime
    {
        internal const string EmbeddedResourceName = "Shadowsocks.WinUI.Embedded.Shadowsocks.NetworkService.exe";
        private const string ExtractionMutexName = @"Local\Shadowsocks.Reborn.NetworkService.Extraction";
        private static readonly TimeSpan StaleRuntimeAge = TimeSpan.FromHours(24);
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        internal sealed class HelperLease : IDisposable
        {
            private FileStream guardStream;
            private readonly string runDirectory;
            private readonly bool deleteOnDispose;
            private bool disposed;

            internal HelperLease(
                string path,
                string expectedSha256,
                FileStream guardStream,
                string runDirectory,
                bool deleteOnDispose)
            {
                ArgumentNullException.ThrowIfNull(path);
                Path = path;
                ExpectedSha256 = expectedSha256 ?? string.Empty;
                this.guardStream = guardStream;
                this.runDirectory = runDirectory;
                this.deleteOnDispose = deleteOnDispose;
            }

            public string Path { get; }
            public string ExpectedSha256 { get; }

            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;

                try
                {
                    guardStream?.Dispose();
                }
                finally
                {
                    guardStream = null;
                    if (deleteOnDispose)
                    {
                        TryDeleteDirectory(runDirectory);
                        TryPruneEmptyParents(System.IO.Path.GetDirectoryName(runDirectory), AppStoragePaths.TempNetworkServiceRoot);
                    }
                }
            }
        }

        /// <summary>
        /// Materializes the embedded elevated helper on demand. This method is called only by
        /// AdminCaptureManager while entering Admin Mode; User Mode never extracts the helper.
        /// The returned lease keeps a read-only file handle open for the entire broker lifetime,
        /// preventing another process from replacing/deleting the validated executable before or
        /// while it is used for elevation.
        /// </summary>
        public static HelperLease AcquireHelper()
        {
            Assembly entryAssembly = Assembly.GetEntryAssembly();
            using Stream resource = entryAssembly?.GetManifestResourceStream(EmbeddedResourceName);
            if (resource is null)
                return AcquireDevelopmentHelper();

            using MemoryStream buffer = new();
            resource.CopyTo(buffer);
            byte[] helperBytes = buffer.ToArray();
            if (helperBytes.Length == 0)
                throw new InvalidDataException("Embedded Shadowsocks.NetworkService.exe is empty.");

            string hash = Convert.ToHexString(SHA256.HashData(helperBytes));
            string version = SanitizePathComponent(ApplicationInfo.Version);
            string versionDirectory = System.IO.Path.Combine(AppStoragePaths.TempNetworkServiceRoot, version);
            string hashDirectory = System.IO.Path.Combine(versionDirectory, hash[..16]);

            using Mutex mutex = new(initiallyOwned: false, ExtractionMutexName);
            bool lockTaken = false;
            try
            {
                try
                {
                    lockTaken = mutex.WaitOne(TimeSpan.FromSeconds(30));
                }
                catch (AbandonedMutexException)
                {
                    lockTaken = true;
                }

                if (!lockTaken)
                    throw new TimeoutException("Timed out waiting for the NetworkService extraction lock.");

                Directory.CreateDirectory(hashDirectory);
                CleanupStaleHelpers(keepDirectory: hashDirectory);

                // Keep one stable helper path per application version + embedded SHA-256.
                // Windows Defender Firewall keys application consent to the executable path;
                // the previous PID/GUID directory made the same signed/hashed helper look like
                // a brand-new application on every Admin Mode start and repeatedly triggered
                // the public/private network access prompt. The SHA directory already gives us
                // immutable versioning, so another per-run directory is unnecessary.
                string runDirectory = hashDirectory;
                string targetPath = System.IO.Path.Combine(runDirectory, "Shadowsocks.NetworkService.exe");
                string temporaryPath = targetPath + ".new";

                try
                {
                    if (!IsFileHashValid(targetPath, hash))
                    {
                        TryDeleteFile(targetPath);
                        TryDeleteFile(temporaryPath);
                        using (FileStream output = new(
                            temporaryPath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: 1024 * 64,
                            FileOptions.WriteThrough))
                        {
                            output.Write(helperBytes, 0, helperBytes.Length);
                            output.Flush(flushToDisk: true);
                        }

                        if (!IsFileHashValid(temporaryPath, hash))
                            throw new InvalidDataException("Extracted NetworkService staging hash validation failed.");

                        File.Move(temporaryPath, targetPath, overwrite: false);
                    }

                    if (!IsFileHashValid(targetPath, hash))
                        throw new InvalidDataException("Extracted NetworkService hash validation failed.");

                    // Keep the file guarded for the whole elevated broker lifetime. FileShare.Read
                    // lets Windows load the image while denying replacement/write/delete access.
                    FileStream guard = new(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    Logger.Info(
                        "NetworkService helper ready at stable path: {0} (version {1}, SHA256 {2})",
                        targetPath,
                        ApplicationInfo.Version,
                        hash);
                    return new HelperLease(targetPath, hash, guard, runDirectory, deleteOnDispose: false);
                }
                catch
                {
                    TryDeleteFile(temporaryPath);
                    throw;
                }
            }
            finally
            {
                if (lockTaken)
                    mutex.ReleaseMutex();
            }
        }

        private static HelperLease AcquireDevelopmentHelper()
        {
            string[] candidates =
            [
                System.IO.Path.Combine(AppContext.BaseDirectory, "Shadowsocks.NetworkService.exe"),
                System.IO.Path.Combine(RuntimeEnvironment.WorkingDirectory, "Shadowsocks.NetworkService.exe"),
            ];
            string path = candidates.FirstOrDefault(File.Exists);
            if (path is null)
            {
                throw new FileNotFoundException(
                    "Shadowsocks.NetworkService.exe is neither embedded nor available in the development output.",
                    candidates[0]);
            }

            Logger.Debug("Using development NetworkService helper: {0}", path);
            return new HelperLease(path, string.Empty, guardStream: null, runDirectory: null, deleteOnDispose: false);
        }

        private static bool IsFileHashValid(string path, string expectedHash)
        {
            if (!File.Exists(path))
                return false;
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                string actual = Convert.ToHexString(SHA256.HashData(stream));
                return string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal static void CleanupStaleRuntime()
        {
            CleanupStaleHelpers(keepDirectory: null);
        }

        private static void CleanupStaleHelpers(string keepDirectory)
        {
            try
            {
                string root = AppStoragePaths.TempNetworkServiceRoot;
                if (!Directory.Exists(root))
                    return;

                DateTime cutoffUtc = DateTime.UtcNow - StaleRuntimeAge;
                foreach (string versionDirectory in Directory.EnumerateDirectories(root))
                {
                    foreach (string hashDirectory in SafeEnumerateDirectories(versionDirectory))
                    {
                        if (!string.IsNullOrWhiteSpace(keepDirectory)
                            && string.Equals(hashDirectory, keepDirectory, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string stableHelper = System.IO.Path.Combine(hashDirectory, "Shadowsocks.NetworkService.exe");
                        if (File.Exists(stableHelper))
                        {
                            try
                            {
                                DateTime lastWriteUtc = File.GetLastWriteTimeUtc(stableHelper);
                                if (lastWriteUtc <= cutoffUtc)
                                    Directory.Delete(hashDirectory, recursive: true);
                            }
                            catch
                            {
                                // The helper may still be guarded by a live broker.
                            }
                        }

                        TryDeleteEmptyDirectory(hashDirectory);
                    }

                    TryDeleteEmptyDirectory(versionDirectory);
                }
            }
            catch (Exception exception)
            {
                Logger.Debug(exception, "Unable to clean all stale NetworkService helper directories.");
            }
        }

        private static string[] SafeEnumerateDirectories(string path)
        {
            try
            {
                return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
            }
            catch
            {
                return [];
            }
        }

        private static string SanitizePathComponent(string value)
        {
            string result = value ?? "unknown";
            foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
                result = result.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(result) ? "unknown" : result.Replace('.', '_');
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
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
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch
            {
            }
        }

        private static void TryDeleteEmptyDirectory(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path)
                    && Directory.Exists(path)
                    && !Directory.EnumerateFileSystemEntries(path).Any())
                {
                    Directory.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void TryPruneEmptyParents(string directory, string stopAt)
        {
            try
            {
                string current = directory;
                while (!string.IsNullOrWhiteSpace(current)
                    && current.StartsWith(stopAt, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(current, stopAt, StringComparison.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
                        break;
                    Directory.Delete(current);
                    current = System.IO.Path.GetDirectoryName(current);
                }
            }
            catch
            {
            }
        }
    }
}
