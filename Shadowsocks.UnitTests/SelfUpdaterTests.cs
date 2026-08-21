using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller.Service;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class SelfUpdaterTests
    {
        [TestMethod]
        public void InternalUpdateArgumentsAreRemovedBeforeNormalStartup()
        {
            string[] arguments =
            {
                "--start-hidden",
                SelfUpdater.CleanupSwitch,
                SelfUpdater.ResumeHiddenSwitch,
                SelfUpdater.TransactionOption,
                @"C:\Temp\Shadowsocks\Updates\tx",
                SelfUpdater.BackupOption + @"=C:\Apps\.Shadowsocks.exe.tx.update-backup",
                SelfUpdater.UpdaterPidOption,
                "1234",
                SelfUpdater.PayloadSha256Option,
                new string('a', 64),
                "--open-url",
                "ss://example",
            };

            string[] publicArguments = SelfUpdater.RemoveInternalArguments(arguments);

            CollectionAssert.AreEqual(
                new[] { "--start-hidden", "--open-url", "ss://example" },
                publicArguments);
        }

        [TestMethod]
        public void TransactionDirectoryMustBeBelowSystemTempUpdateRoot()
        {
            string child = Path.Combine(SelfUpdater.UpdatesRoot, "test-transaction");
            Assert.IsTrue(SelfUpdater.IsCanonicalTransactionDirectory(child));
            Assert.IsFalse(SelfUpdater.IsCanonicalTransactionDirectory(SelfUpdater.UpdatesRoot));
            Assert.IsFalse(SelfUpdater.IsCanonicalTransactionDirectory(Path.GetTempPath()));
        }

        [TestMethod]
        public void UpdaterPathUsesSingleCanonicalExecutableName()
        {
            string transaction = Path.Combine(SelfUpdater.UpdatesRoot, "abc");
            string updater = SelfUpdater.GetTemporaryUpdaterPath(transaction);
            Assert.AreEqual(SelfUpdater.TemporaryUpdaterFileName, Path.GetFileName(updater));
        }

        [TestMethod]
        public void StagedPayloadHashRejectsTampering()
        {
            string path = Path.Combine(Path.GetTempPath(), "shadowsocks-updater-hash-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
                string expected = SelfUpdater.ComputeSha256Hex(path);
                Assert.AreEqual(64, expected.Length);

                using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    SelfUpdater.VerifySha256(stream, expected);
                }

                File.WriteAllBytes(path, new byte[] { 1, 2, 3, 5 });
                using FileStream tampered = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Assert.ThrowsExactly<InvalidDataException>(() => SelfUpdater.VerifySha256(tampered, expected));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
