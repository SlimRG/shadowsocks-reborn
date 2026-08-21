using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class ProcessSingleInstanceGuardTests
    {
        [TestMethod]
        public void NamedGateRejectsSecondThreadUntilOwnerReleasesIt()
        {
            string name = @"Local\Shadowsocks.Reborn.Tests." + Guid.NewGuid().ToString("N");
            using ProcessSingleInstanceGuard first = ProcessSingleInstanceGuard.TryAcquire(name);
            Assert.IsNotNull(first);

            bool secondAcquired = TryAcquireOnWorker(name);
            Assert.IsFalse(secondAcquired, "A concurrent process/thread must not acquire the same single-instance gate.");

            first.Dispose();

            bool acquiredAfterRelease = TryAcquireOnWorker(name);
            Assert.IsTrue(acquiredAfterRelease, "The single-instance gate must be reusable after the owner exits.");
        }

        private static bool TryAcquireOnWorker(string name)
        {
            bool acquired = false;
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using ProcessSingleInstanceGuard guard = ProcessSingleInstanceGuard.TryAcquire(name);
                    acquired = guard is not null;
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            });
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "Worker thread did not finish while probing the single-instance gate.");
            if (failure is not null)
            {
                throw failure;
            }
            return acquired;
        }
    }
}
