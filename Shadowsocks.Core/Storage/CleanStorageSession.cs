#nullable enable
using System;
using System.IO;
using System.Threading;

namespace Shadowsocks.Core.Storage
{
    /// <summary>
    /// Owns one Clean Mode storage session and its exclusive lifetime lock.
    /// Disposing the session releases the lock before removing the entire session tree.
    /// </summary>
    internal sealed class CleanStorageSession : IDisposable
    {
        internal const string LockFileName = ".session.lock";

        private FileStream? sessionLock;
        private int disposed;

        private CleanStorageSession(string root, FileStream sessionLock)
        {
            Root = root;
            this.sessionLock = sessionLock;
        }

        public string Root { get; }

        public static CleanStorageSession Create(
            string sessionsRoot,
            DateTime utcNow,
            int processId,
            Guid sessionId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionsRoot);
            string sessionName = $"{utcNow:yyyyMMddHHmmss}-{processId}-{sessionId:N}";
            string root = Path.Combine(sessionsRoot, sessionName);
            Directory.CreateDirectory(root);

            try
            {
                string lockPath = Path.Combine(root, LockFileName);
                FileStream sessionLock = new(
                    lockPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
                return new CleanStorageSession(root, sessionLock);
            }
            catch
            {
                TryDeleteDirectory(root);
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            FileStream? ownedLock = Interlocked.Exchange(ref sessionLock, null);
            try
            {
                ownedLock?.Dispose();
            }
            catch
            {
            }

            TryDeleteDirectory(Root);
        }

        internal static bool IsActive(string directory)
        {
            string lockPath = Path.Combine(directory, LockFileName);
            if (!File.Exists(lockPath))
                return false;

            try
            {
                using FileStream _ = new(
                    lockPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        internal static void TryDeleteDirectory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (Directory.Exists(path))
                        Directory.Delete(path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
                catch
                {
                    return;
                }
            }
        }
    }
}
