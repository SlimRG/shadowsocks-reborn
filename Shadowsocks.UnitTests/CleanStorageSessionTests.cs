#nullable enable
using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Core.Storage;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class CleanStorageSessionTests
    {
        [TestMethod]
        public void SessionOwnsExclusiveLockAndDeletesTreeOnDispose()
        {
            string sessionsRoot = Path.Combine(
                Path.GetTempPath(),
                "Shadowsocks.UnitTests",
                "CleanSessions",
                Guid.NewGuid().ToString("N"));
            DateTime nowUtc = new(2026, 8, 18, 12, 34, 56, DateTimeKind.Utc);
            Guid sessionId = Guid.Parse("9f14432d-a114-47e6-a120-29511dc39887");

            try
            {
                CleanStorageSession session = CleanStorageSession.Create(
                    sessionsRoot,
                    nowUtc,
                    4242,
                    sessionId);
                string sessionRoot = session.Root;
                string expectedName = "20260818123456-4242-9f14432da11447e6a12029511dc39887";
                Assert.AreEqual(expectedName, Path.GetFileName(sessionRoot));
                Assert.IsTrue(File.Exists(Path.Combine(sessionRoot, CleanStorageSession.LockFileName)));
                Assert.IsTrue(CleanStorageSession.IsActive(sessionRoot));

                string nested = Path.Combine(sessionRoot, "Runtime", "DNSCryptProxy");
                Directory.CreateDirectory(nested);
                File.WriteAllText(Path.Combine(nested, "dnscrypt-proxy.toml"), "test");

                session.Dispose();
                session.Dispose(); // Cleanup is idempotent during duplicate exit notifications.
                Assert.IsFalse(Directory.Exists(sessionRoot));
            }
            finally
            {
                CleanStorageSession.TryDeleteDirectory(sessionsRoot);
            }
        }

        [TestMethod]
        public void UnlockedSessionDirectoryIsNotReportedAsActive()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "Shadowsocks.UnitTests",
                "CleanUnlocked",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, CleanStorageSession.LockFileName), string.Empty);

            try
            {
                Assert.IsFalse(CleanStorageSession.IsActive(directory));
            }
            finally
            {
                CleanStorageSession.TryDeleteDirectory(directory);
            }
        }
    }
}
