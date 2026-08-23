using System;
using System.Threading;

namespace Shadowsocks.Controller
{
    /// <summary>
    /// Process-wide single-instance gate that is independent of Windows App SDK
    /// application identity. This is required because the verified Start-with-Windows
    /// copy lives at a different executable path from the user-facing product EXE.
    /// </summary>
    internal sealed class ProcessSingleInstanceGuard : IDisposable
    {
        internal const string DefaultMutexName = @"Local\Shadowsocks.Reborn.WinUI.Process";

        private Mutex mutex;
        private bool ownsMutex;

        private ProcessSingleInstanceGuard(Mutex mutex)
        {
            this.mutex = mutex;
            ownsMutex = true;
        }

        internal static ProcessSingleInstanceGuard TryAcquire(string mutexName = DefaultMutexName)
        {
            if (string.IsNullOrWhiteSpace(mutexName))
            {
                throw new ArgumentException("A single-instance mutex name is required.", nameof(mutexName));
            }

            var candidate = new Mutex(initiallyOwned: false, mutexName);
            try
            {
                bool acquired;
                try
                {
                    acquired = candidate.WaitOne(0, exitContext: false);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    candidate.Dispose();
                    return null;
                }

                return new ProcessSingleInstanceGuard(candidate);
            }
            catch
            {
                candidate.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Mutex current = mutex;
            if (current is null)
            {
                return;
            }

            mutex = null;
            if (ownsMutex)
            {
                ownsMutex = false;
                current.ReleaseMutex();
            }
            current.Dispose();
        }
    }
}
