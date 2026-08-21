using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Shadowsocks.Controller;
using Shadowsocks.Model;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class UpdateCheckerTests
    {
        [TestMethod]
        public void StableReleaseWinsOverPrereleaseWithSameNumericVersion()
        {
            JArray releases = JArray.Parse("""
            [
              { "tag_name": "v5.3.0-beta.1", "prerelease": true, "draft": false },
              { "tag_name": "v5.3.0", "prerelease": false, "draft": false }
            ]
            """);
            var configuration = new Configuration { checkPreRelease = true };

            JToken selected = UpdateChecker.SelectLatestEligibleRelease(
                releases,
                configuration,
                new Version(5, 2, 1, 0),
                out Version version);

            Assert.IsNotNull(selected);
            Assert.AreEqual("v5.3.0", (string)selected["tag_name"]);
            Assert.AreEqual(new Version(5, 3, 0, 0), version);
        }

        [TestMethod]
        public void SkippedReleaseDoesNotStopSearchForNewerVersion()
        {
            JArray releases = JArray.Parse("""
            [
              { "tag_name": "v5.4.0", "prerelease": false, "draft": false },
              { "tag_name": "v5.3.0", "prerelease": false, "draft": false }
            ]
            """);
            var configuration = new Configuration { skippedUpdateVersion = "v5.3.0" };

            JToken selected = UpdateChecker.SelectLatestEligibleRelease(
                releases,
                configuration,
                new Version(5, 2, 1, 0),
                out Version version);

            Assert.IsNotNull(selected);
            Assert.AreEqual("v5.4.0", (string)selected["tag_name"]);
            Assert.AreEqual(new Version(5, 4, 0, 0), version);
        }

        [TestMethod]
        public void CanonicalAssetNameIsRequiredExactly()
        {
            JArray assets = JArray.Parse("""
            [
              { "name": "folder/Shadowsocks-win-x64.zip" },
              { "name": "other-win-x64.zip" }
            ]
            """);

            Assert.IsNull(UpdateChecker.SelectReleaseZipAsset(assets));

            assets.Add(new JObject { ["name"] = UpdateChecker.PreferredReleaseZipFilename });
            Assert.IsNotNull(UpdateChecker.SelectReleaseZipAsset(assets));
        }

        [TestMethod]
        public void ReleaseAssetOriginMustBeCanonicalRepository()
        {
            Assert.IsTrue(UpdateChecker.IsAllowedReleaseDownloadUrl(
                "https://github.com/SlimRG/shadowsocks-reborn/releases/download/v5.2.2/Shadowsocks-win-x64.zip"));
            Assert.IsFalse(UpdateChecker.IsAllowedReleaseDownloadUrl(
                "https://example.com/SlimRG/shadowsocks-reborn/releases/download/v5.2.2/Shadowsocks-win-x64.zip"));
            Assert.IsFalse(UpdateChecker.IsAllowedReleaseDownloadUrl(
                "https://github.com/Other/repo/releases/download/v5.2.2/Shadowsocks-win-x64.zip"));
        }

        [TestMethod]
        public async Task Sha256SidecarAcceptsPlainDigestAndValidatesOptionalCanonicalFileName()
        {
            string root = CreateTempDirectory();
            try
            {
                string payload = Path.Combine(root, UpdateChecker.PreferredReleaseZipFilename);
                await File.WriteAllBytesAsync(payload, new byte[] { 1, 2, 3, 4 });
                string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(payload))).ToLowerInvariant();
                string checksum = payload + ".sha256";

                await File.WriteAllTextAsync(checksum, $"{hash}  folder/{UpdateChecker.PreferredReleaseZipFilename}");
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                    UpdateChecker.VerifySha256Async(payload, checksum, UpdateChecker.PreferredReleaseZipFilename));

                await File.WriteAllTextAsync(checksum, $"{hash}  {UpdateChecker.PreferredReleaseZipFilename}");
                await UpdateChecker.VerifySha256Async(payload, checksum, UpdateChecker.PreferredReleaseZipFilename);

                await File.WriteAllTextAsync(checksum, hash);
                await UpdateChecker.VerifySha256Async(payload, checksum, UpdateChecker.PreferredReleaseZipFilename);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task DownloadedAssetIsClosedBeforePromotion()
        {
            string root = CreateTempDirectory();
            try
            {
                string destination = Path.Combine(root, UpdateChecker.PreferredReleaseZipFilename);
                await File.WriteAllTextAsync(destination, "old");
                byte[] expected = { 0x53, 0x53, 0x2D, 0x55, 0x50, 0x44, 0x41, 0x54, 0x45 };
                using var content = new System.Net.Http.ByteArrayContent(expected);

                await UpdateChecker.WriteDownloadedAssetAsync(content, destination);

                CollectionAssert.AreEqual(expected, await File.ReadAllBytesAsync(destination));
                Assert.IsFalse(File.Exists(destination + ".download"));

                using FileStream exclusiveProbe = new(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.AreEqual(expected.Length, exclusiveProbe.Length);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public void UpdateZipMustContainOneRootExecutableOnly()
        {
            string root = CreateTempDirectory();
            try
            {
                string validZip = Path.Combine(root, "valid.zip");
                using (ZipArchive archive = ZipFile.Open(validZip, ZipArchiveMode.Create))
                {
                    ZipArchiveEntry entry = archive.CreateEntry("Shadowsocks.exe");
                    using Stream output = entry.Open();
                    output.WriteByte(0x4D);
                    output.WriteByte(0x5A);
                }

                string extracted = Path.Combine(root, "Shadowsocks.Update.exe");
                UpdateChecker.ExtractCanonicalExecutable(validZip, extracted);
                Assert.IsTrue(File.Exists(extracted));

                string invalidZip = Path.Combine(root, "invalid.zip");
                using (ZipArchive archive = ZipFile.Open(invalidZip, ZipArchiveMode.Create))
                {
                    ZipArchiveEntry entry = archive.CreateEntry("bin/Shadowsocks.exe");
                    using Stream output = entry.Open();
                    output.WriteByte(1);
                }

                Assert.ThrowsExactly<InvalidDataException>(() =>
                    UpdateChecker.ExtractCanonicalExecutable(invalidZip, Path.Combine(root, "bad.exe")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "Shadowsocks.UnitTests", "UpdateChecker", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
