using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Shadowsocks.Controller.Service;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    [DoNotParallelize]
    public class DnsCryptComponentManagerTests
    {
        [TestMethod]
        public void SecurityPinsMatchOfficialDnsCryptProject()
        {
            Assert.AreEqual("DNSCrypt/dnscrypt-proxy", ReadDnsCryptConstant(nameof(DnsCryptComponentManager.Repository)));
            Assert.AreEqual("dnscrypt-proxy.exe", ReadDnsCryptConstant(nameof(DnsCryptComponentManager.ExpectedExecutableName)));

            byte[] minisignKey = Convert.FromBase64String(DnsCryptComponentManager.ReleaseSigningPublicKey);
            Assert.AreEqual(42, minisignKey.Length);
        }

        private static string ReadDnsCryptConstant(string fieldName)
        {
            System.Reflection.FieldInfo field = typeof(DnsCryptComponentManager).GetField(
                fieldName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(field);
            return (string)field.GetRawConstantValue();
        }

        private string root;
        private string componentRoot;
        private string updateRoot;

        [TestInitialize]
        public void Initialize()
        {
            root = Path.Combine(Path.GetTempPath(), "Shadowsocks.UnitTests", "DnsCrypt", Guid.NewGuid().ToString("N"));
            componentRoot = Path.Combine(root, "Components", "DNSCryptProxy");
            updateRoot = Path.Combine(root, "Temp", "Updates", "DNSCryptProxy");
            Directory.CreateDirectory(root);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        [TestMethod]
        public void ReleaseSelectionAcceptsExactWindowsX64ArchiveAndSignature()
        {
            DnsCryptComponentManager.GitHubRelease release = CreateRelease(
                "2.1.18",
                CreateAsset("dnscrypt-proxy-win64-2.1.18.zip", 1024, new string('a', 64)),
                CreateAsset("dnscrypt-proxy-win64-2.1.18.zip.minisig", 512));

            DnsCryptReleaseInfo selected = DnsCryptComponentManager.SelectRelease(release);

            Assert.AreEqual(new Version(2, 1, 18), selected.Version);
            Assert.AreEqual("dnscrypt-proxy-win64-2.1.18.zip", selected.ArchiveName);
            Assert.AreEqual("dnscrypt-proxy-win64-2.1.18.zip.minisig", selected.SignatureName);
            Assert.AreEqual(new string('a', 64), selected.Sha256Digest);
        }

        [TestMethod]
        public void ReleaseSelectionAcceptsVPrefixedTagForUpstreamCompatibility()
        {
            DnsCryptComponentManager.GitHubAsset archive = CreateAsset("dnscrypt-proxy-win64-2.1.18.zip", 1024);
            archive.DownloadUrl = OfficialAssetUrl("v2.1.18", archive.Name);
            DnsCryptComponentManager.GitHubAsset signature = CreateAsset("dnscrypt-proxy-win64-2.1.18.zip.minisig", 512);
            signature.DownloadUrl = OfficialAssetUrl("v2.1.18", signature.Name);
            DnsCryptComponentManager.GitHubRelease release = CreateRelease("v2.1.18", archive, signature);

            DnsCryptReleaseInfo selected = DnsCryptComponentManager.SelectRelease(release);

            Assert.AreEqual(new Version(2, 1, 18), selected.Version);
        }

        [TestMethod]
        public void ReleaseSelectionRejectsWrongArchitecture()
        {
            DnsCryptComponentManager.GitHubRelease release = CreateRelease(
                "2.1.18",
                CreateAsset("dnscrypt-proxy-win32-2.1.18.zip", 1024),
                CreateAsset("dnscrypt-proxy-win32-2.1.18.zip.minisig", 512));

            Assert.ThrowsExactly<InvalidDataException>(() => DnsCryptComponentManager.SelectRelease(release));
        }

        [TestMethod]
        public void ReleaseSelectionRejectsAssetVersionDifferentFromTag()
        {
            DnsCryptComponentManager.GitHubRelease release = CreateRelease(
                "2.1.18",
                CreateAsset("dnscrypt-proxy-win64-2.1.17.zip", 1024),
                CreateAsset("dnscrypt-proxy-win64-2.1.17.zip.minisig", 512));

            Assert.ThrowsExactly<InvalidDataException>(() => DnsCryptComponentManager.SelectRelease(release));
        }

        [TestMethod]
        public void ReleaseSelectionRequiresMinisignAsset()
        {
            DnsCryptComponentManager.GitHubRelease release = CreateRelease(
                "2.1.18",
                CreateAsset("dnscrypt-proxy-win64-2.1.18.zip", 1024));

            Assert.ThrowsExactly<InvalidDataException>(() => DnsCryptComponentManager.SelectRelease(release));
        }

        [TestMethod]
        public void ReleaseSelectionRejectsDownloadOutsideOfficialRepository()
        {
            DnsCryptComponentManager.GitHubAsset archive = CreateAsset("dnscrypt-proxy-win64-2.1.18.zip", 1024);
            archive.DownloadUrl = "https://example.com/dnscrypt-proxy-win64-2.1.18.zip";
            DnsCryptComponentManager.GitHubRelease release = CreateRelease(
                "2.1.18",
                archive,
                CreateAsset("dnscrypt-proxy-win64-2.1.18.zip.minisig", 512));

            Assert.ThrowsExactly<InvalidDataException>(() => DnsCryptComponentManager.SelectRelease(release));
        }

        [TestMethod]
        public void SafeZipExtractionAcceptsNormalEntries()
        {
            string zip = CreateZipFile(
                "normal.zip",
                ("dnscrypt-proxy/dnscrypt-proxy.exe", new byte[] { 0x4D, 0x5A }),
                ("dnscrypt-proxy/LICENSE", Encoding.UTF8.GetBytes("ISC")));
            string destination = Path.Combine(root, "extract-normal");

            DnsCryptComponentManager.ExtractZipSafely(zip, destination);

            Assert.IsTrue(File.Exists(Path.Combine(destination, "dnscrypt-proxy", "dnscrypt-proxy.exe")));
        }

        [TestMethod]
        public void SafeZipExtractionRejectsParentTraversal()
        {
            string zip = CreateZipFile("traversal.zip", ("../outside.exe", new byte[] { 1, 2, 3 }));
            string destination = Path.Combine(root, "extract-traversal");

            Assert.ThrowsExactly<InvalidDataException>(() =>
                DnsCryptComponentManager.ExtractZipSafely(zip, destination));
            Assert.IsFalse(File.Exists(Path.Combine(root, "outside.exe")));
        }

        [TestMethod]
        public void SafeZipExtractionRejectsWindowsAbsolutePath()
        {
            string zip = CreateZipFile("absolute.zip", ("C:/Windows/evil.exe", new byte[] { 1 }));

            Assert.ThrowsExactly<InvalidDataException>(() =>
                DnsCryptComponentManager.ExtractZipSafely(zip, Path.Combine(root, "extract-absolute")));
        }

        [TestMethod]
        public void SafeZipExtractionRejectsUnixSymlinkEntry()
        {
            string zip = Path.Combine(root, "symlink.zip");
            using (FileStream stream = new(zip, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = archive.CreateEntry("dnscrypt-proxy.exe");
                entry.ExternalAttributes = unchecked((int)(0xA000 << 16));
                using Stream output = entry.Open();
                output.WriteByte(0);
            }

            Assert.ThrowsExactly<InvalidDataException>(() =>
                DnsCryptComponentManager.ExtractZipSafely(zip, Path.Combine(root, "extract-symlink")));
        }

        [TestMethod]
        public void MinisignVerifierAcceptsValidModernSignatureAndRejectsTampering()
        {
            byte[] payload = Encoding.UTF8.GetBytes("dnscrypt-proxy archive fixture");
            string signedFile = Path.Combine(root, "signed.zip");
            File.WriteAllBytes(signedFile, payload);
            MinisignFixture fixture = CreateMinisignFixture(payload);

            Assert.IsTrue(MinisignVerifier.VerifyFile(signedFile, fixture.SignatureText, fixture.PublicKey));

            File.AppendAllText(signedFile, "tampered", Encoding.UTF8);
            Assert.IsFalse(MinisignVerifier.VerifyFile(signedFile, fixture.SignatureText, fixture.PublicKey));
        }

        [TestMethod]
        public void MinisignVerifierAcceptsLegacyResolverCatalogSignatureAndRejectsTampering()
        {
            byte[] payload = Encoding.UTF8.GetBytes("dnscrypt resolver catalog fixture");
            string signedFile = Path.Combine(root, "public-resolvers.md");
            File.WriteAllBytes(signedFile, payload);
            MinisignFixture fixture = CreateMinisignFixture(payload, prehashed: false);

            Assert.IsTrue(MinisignVerifier.VerifyFileAllowLegacy(signedFile, fixture.SignatureText, fixture.PublicKey));

            File.AppendAllText(signedFile, "tampered", Encoding.UTF8);
            Assert.IsFalse(MinisignVerifier.VerifyFileAllowLegacy(signedFile, fixture.SignatureText, fixture.PublicKey));
        }

        [TestMethod]
        public void MinisignVerifierRecognizesOfficialResolverLegacySignatureFormat()
        {
            string signedFile = Path.Combine(root, "official-resolver-format.md");
            File.WriteAllText(signedFile, "intentionally not the signed upstream catalog", Encoding.UTF8);
            string signatureText = string.Join("\n", new[]
            {
                "untrusted comment: signature from minisign secret key",
                "RWQf6LRCGA9i59dR/JthmdbOIHVSepvwImbIu8RNVy4drRsi1YPHp5bLTqvDVu3BgNy7/eYDZWDnKzxl+aovrP1VNU6qBzD9CQs=",
                "trusted comment: timestamp:1786712947\tfile:public-resolvers.md",
                "zyX1ZY3vlnMtEY4vditqPW3+XnMcqvgVWXa7FPvWv0VDcVXs1d2rIXtBpj2bZO8xHQUIqlotHNlmvdi9F2GMBA==",
                string.Empty,
            });

            Assert.IsFalse(MinisignVerifier.VerifyFileAllowLegacy(
                signedFile,
                signatureText,
                DnsCryptTomlGenerator.PublicResolversMinisignKey));
        }

        [TestMethod]
        public void MinisignVerifierDefaultPathRejectsLegacySignature()
        {
            byte[] payload = Encoding.UTF8.GetBytes("legacy release fixture");
            string signedFile = Path.Combine(root, "legacy-release.zip");
            File.WriteAllBytes(signedFile, payload);
            MinisignFixture fixture = CreateMinisignFixture(payload, prehashed: false);

            Assert.ThrowsExactly<InvalidDataException>(() =>
                MinisignVerifier.VerifyFile(signedFile, fixture.SignatureText, fixture.PublicKey));
        }

        [TestMethod]
        public void MinisignVerifierRejectsTamperedTrustedCommentForLegacySignature()
        {
            byte[] payload = Encoding.UTF8.GetBytes("dnscrypt resolver catalog fixture");
            string signedFile = Path.Combine(root, "public-resolvers-comment.md");
            File.WriteAllBytes(signedFile, payload);
            MinisignFixture fixture = CreateMinisignFixture(payload, prehashed: false);
            string tamperedSignature = fixture.SignatureText.Replace(
                "file:public-resolvers.md",
                "file:tampered-resolvers.md",
                StringComparison.Ordinal);

            Assert.IsFalse(MinisignVerifier.VerifyFileAllowLegacy(signedFile, tamperedSignature, fixture.PublicKey));
        }

        [TestMethod]
        public void MinisignVerifierRejectsDifferentTrustedKey()
        {
            byte[] payload = Encoding.UTF8.GetBytes("signed content");
            string signedFile = Path.Combine(root, "wrong-key.zip");
            File.WriteAllBytes(signedFile, payload);
            MinisignFixture signature = CreateMinisignFixture(payload, seedOffset: 0);
            MinisignFixture otherKey = CreateMinisignFixture(payload, seedOffset: 33);

            Assert.IsFalse(MinisignVerifier.VerifyFile(signedFile, signature.SignatureText, otherKey.PublicKey));
        }

        [TestMethod]
        public async Task PrepareLatestDownloadsAndVerifiesOfficialArchiveWithoutActivation()
        {
            byte[] executable = { 0x4D, 0x5A, 0x90, 0x00 };
            byte[] archive = CreateZipBytes(("dnscrypt-proxy-win64/dnscrypt-proxy.exe", executable));
            MinisignFixture signature = CreateMinisignFixture(archive);
            string digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            string archiveName = "dnscrypt-proxy-win64-2.1.18.zip";
            string signatureName = archiveName + ".minisig";
            string archiveUrl = OfficialAssetUrl("2.1.18", archiveName);
            string signatureUrl = OfficialAssetUrl("2.1.18", signatureName);
            string releaseJson = CreateReleaseJson("2.1.18", archiveName, archiveUrl, archive.Length, digest, signatureName, signatureUrl);

            using HttpClient client = CreateHttpClient(new Dictionary<string, Func<HttpResponseMessage>>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://api.github.com/repos/DNSCrypt/dnscrypt-proxy/releases/latest"] = () => JsonResponse(releaseJson),
                [archiveUrl] = () => BytesResponse(archive),
                [signatureUrl] = () => BytesResponse(Encoding.UTF8.GetBytes(signature.SignatureText)),
            });
            using var manager = new DnsCryptComponentManager(client, componentRoot, updateRoot, [signature.PublicKey]);

            DnsCryptPreparedComponent installed = await manager.PrepareLatestAsync();
            DnsCryptComponentStatus status = manager.GetStatus();

            Assert.AreEqual(new Version(2, 1, 18), installed.Version);
            Assert.IsTrue(File.Exists(installed.ExecutablePath));
            CollectionAssert.AreEqual(executable, File.ReadAllBytes(installed.ExecutablePath));
            Assert.IsFalse(status.IsInstalled);
            Assert.IsNull(status.ActiveVersion);
            Assert.IsTrue(status.AutoUpdate);
            Assert.IsFalse(File.Exists(Path.Combine(componentRoot, "component.json")), "Prepare must not persist active metadata before validation/activation.");
            Assert.IsFalse(Directory.Exists(updateRoot) && Directory.EnumerateFileSystemEntries(updateRoot).Any());
        }

        [TestMethod]
        public async Task PrepareLatestDoesNotChangeActiveVersionBeforeActivation()
        {
            byte[] archive = CreateZipBytes(("dnscrypt-proxy.exe", new byte[] { 0x4D, 0x5A }));
            MinisignFixture signature = CreateMinisignFixture(archive);
            string archiveName = "dnscrypt-proxy-win64-2.1.18.zip";
            string signatureName = archiveName + ".minisig";
            string archiveUrl = OfficialAssetUrl("2.1.18", archiveName);
            string signatureUrl = OfficialAssetUrl("2.1.18", signatureName);
            string releaseJson = CreateReleaseJson("2.1.18", archiveName, archiveUrl, archive.Length, null, signatureName, signatureUrl);

            using HttpClient client = CreateHttpClient(new Dictionary<string, Func<HttpResponseMessage>>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://api.github.com/repos/DNSCrypt/dnscrypt-proxy/releases/latest"] = () => JsonResponse(releaseJson),
                [archiveUrl] = () => BytesResponse(archive),
                [signatureUrl] = () => BytesResponse(Encoding.UTF8.GetBytes(signature.SignatureText)),
            });
            using var manager = new DnsCryptComponentManager(client, componentRoot, updateRoot, [signature.PublicKey]);

            DnsCryptPreparedComponent prepared = await manager.PrepareLatestAsync();

            Assert.IsTrue(File.Exists(prepared.ExecutablePath));
            Assert.IsFalse(manager.GetStatus().IsInstalled);

            DnsCryptActivationLease activation = manager.ActivatePrepared(prepared);
            manager.CommitActivation(activation);
            Assert.IsTrue(manager.GetStatus().IsInstalled);
        }

        [TestMethod]
        public async Task ForcePrepareOfActiveVersionKeepsOldExecutableUntilActivation()
        {
            byte[] oldExecutable = { 0x4D, 0x5A, 0x01 };
            byte[] newExecutable = { 0x4D, 0x5A, 0x02 };
            CreatePreparedVersion("2.1.18", oldExecutable);

            byte[] archive = CreateZipBytes(("dnscrypt-proxy.exe", newExecutable));
            MinisignFixture signature = CreateMinisignFixture(archive);
            string archiveName = "dnscrypt-proxy-win64-2.1.18.zip";
            string signatureName = archiveName + ".minisig";
            string archiveUrl = OfficialAssetUrl("2.1.18", archiveName);
            string signatureUrl = OfficialAssetUrl("2.1.18", signatureName);
            string releaseJson = CreateReleaseJson("2.1.18", archiveName, archiveUrl, archive.Length, null, signatureName, signatureUrl);

            using HttpClient client = CreateHttpClient(new Dictionary<string, Func<HttpResponseMessage>>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://api.github.com/repos/DNSCrypt/dnscrypt-proxy/releases/latest"] = () => JsonResponse(releaseJson),
                [archiveUrl] = () => BytesResponse(archive),
                [signatureUrl] = () => BytesResponse(Encoding.UTF8.GetBytes(signature.SignatureText)),
            });
            using var manager = new DnsCryptComponentManager(client, componentRoot, updateRoot, [signature.PublicKey]);
            manager.ActivateVersion(new Version(2, 1, 18));
            string activeExecutable = manager.GetStatus().ExecutablePath;

            DnsCryptPreparedComponent prepared = await manager.PrepareLatestAsync(forceDownload: true);

            Assert.AreNotEqual(Path.GetFullPath(activeExecutable), Path.GetFullPath(prepared.ExecutablePath));
            CollectionAssert.AreEqual(oldExecutable, File.ReadAllBytes(activeExecutable), "Prepare must not replace the active executable.");
            CollectionAssert.AreEqual(newExecutable, File.ReadAllBytes(prepared.ExecutablePath));

            DnsCryptActivationLease activation = manager.ActivatePrepared(prepared);

            Assert.IsNotNull(activation);
            Assert.IsTrue(Directory.Exists(activation.BackupDirectory));
            CollectionAssert.AreEqual(newExecutable, File.ReadAllBytes(manager.GetStatus().ExecutablePath));

            manager.CommitActivation(activation);
            Assert.IsFalse(Directory.Exists(activation.BackupDirectory));
        }

        [TestMethod]
        public async Task SameVersionActivationCanRollbackUntilPostActivationCommit()
        {
            byte[] oldExecutable = { 0x4D, 0x5A, 0x11 };
            byte[] newExecutable = { 0x4D, 0x5A, 0x22 };
            CreatePreparedVersion("2.1.18", oldExecutable);

            byte[] archive = CreateZipBytes(("dnscrypt-proxy.exe", newExecutable));
            MinisignFixture signature = CreateMinisignFixture(archive);
            string archiveName = "dnscrypt-proxy-win64-2.1.18.zip";
            string signatureName = archiveName + ".minisig";
            string archiveUrl = OfficialAssetUrl("2.1.18", archiveName);
            string signatureUrl = OfficialAssetUrl("2.1.18", signatureName);
            string releaseJson = CreateReleaseJson("2.1.18", archiveName, archiveUrl, archive.Length, null, signatureName, signatureUrl);

            using HttpClient client = CreateHttpClient(new Dictionary<string, Func<HttpResponseMessage>>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://api.github.com/repos/DNSCrypt/dnscrypt-proxy/releases/latest"] = () => JsonResponse(releaseJson),
                [archiveUrl] = () => BytesResponse(archive),
                [signatureUrl] = () => BytesResponse(Encoding.UTF8.GetBytes(signature.SignatureText)),
            });
            using var manager = new DnsCryptComponentManager(client, componentRoot, updateRoot, [signature.PublicKey]);
            manager.ActivateVersion(new Version(2, 1, 18));

            DnsCryptPreparedComponent prepared = await manager.PrepareLatestAsync(forceDownload: true);
            DnsCryptActivationLease activation = manager.ActivatePrepared(prepared);

            Assert.IsNotNull(activation);
            CollectionAssert.AreEqual(newExecutable, File.ReadAllBytes(manager.GetStatus().ExecutablePath));
            Assert.IsTrue(Directory.Exists(activation.BackupDirectory));

            manager.RollbackActivation(activation);

            CollectionAssert.AreEqual(oldExecutable, File.ReadAllBytes(manager.GetStatus().ExecutablePath));
            Assert.AreEqual(new Version(2, 1, 18), manager.GetStatus().ActiveVersion);
            Assert.IsFalse(Directory.Exists(activation.BackupDirectory));
        }

        [TestMethod]
        public async Task PrepareLatestRejectsReportedArchiveSizeMismatch()
        {
            byte[] archive = CreateZipBytes(("dnscrypt-proxy.exe", new byte[] { 1, 2, 3 }));
            MinisignFixture signature = CreateMinisignFixture(archive);
            string archiveName = "dnscrypt-proxy-win64-2.1.18.zip";
            string signatureName = archiveName + ".minisig";
            string archiveUrl = OfficialAssetUrl("2.1.18", archiveName);
            string signatureUrl = OfficialAssetUrl("2.1.18", signatureName);
            string releaseJson = CreateReleaseJson("2.1.18", archiveName, archiveUrl, archive.Length + 1, null, signatureName, signatureUrl);

            using HttpClient client = CreateHttpClient(new Dictionary<string, Func<HttpResponseMessage>>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://api.github.com/repos/DNSCrypt/dnscrypt-proxy/releases/latest"] = () => JsonResponse(releaseJson),
                [archiveUrl] = () => BytesResponse(archive),
                [signatureUrl] = () => BytesResponse(Encoding.UTF8.GetBytes(signature.SignatureText)),
            });
            using var manager = new DnsCryptComponentManager(client, componentRoot, updateRoot, [signature.PublicKey]);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => manager.PrepareLatestAsync());

            Assert.IsFalse(manager.GetStatus().IsInstalled);
        }

        [TestMethod]
        public async Task PrepareLatestRejectsSha256MismatchBeforeInstallation()
        {
            byte[] archive = CreateZipBytes(("dnscrypt-proxy.exe", new byte[] { 1, 2, 3 }));
            MinisignFixture signature = CreateMinisignFixture(archive);
            string archiveName = "dnscrypt-proxy-win64-2.1.18.zip";
            string signatureName = archiveName + ".minisig";
            string archiveUrl = OfficialAssetUrl("2.1.18", archiveName);
            string signatureUrl = OfficialAssetUrl("2.1.18", signatureName);
            string wrongDigest = new string('0', 64);
            string releaseJson = CreateReleaseJson("2.1.18", archiveName, archiveUrl, archive.Length, wrongDigest, signatureName, signatureUrl);

            using HttpClient client = CreateHttpClient(new Dictionary<string, Func<HttpResponseMessage>>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://api.github.com/repos/DNSCrypt/dnscrypt-proxy/releases/latest"] = () => JsonResponse(releaseJson),
                [archiveUrl] = () => BytesResponse(archive),
                [signatureUrl] = () => BytesResponse(Encoding.UTF8.GetBytes(signature.SignatureText)),
            });
            using var manager = new DnsCryptComponentManager(client, componentRoot, updateRoot, [signature.PublicKey]);

            await Assert.ThrowsExactlyAsync<CryptographicException>(() => manager.PrepareLatestAsync());

            Assert.IsFalse(manager.GetStatus().IsInstalled);
            Assert.IsFalse(Directory.Exists(componentRoot) && Directory.EnumerateDirectories(componentRoot).Any());
        }

        [TestMethod]
        public void ActivationKeepsOnePreviousVersionAndRollbackSwapsThem()
        {
            using var manager = new DnsCryptComponentManager(new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, Func<HttpResponseMessage>>())), componentRoot, updateRoot, [DnsCryptComponentManager.ReleaseSigningPublicKey]);
            CreatePreparedVersion("2.1.16", new byte[] { 16 });
            CreatePreparedVersion("2.1.17", new byte[] { 17 });

            manager.ActivateVersion(new Version(2, 1, 17));
            CreatePreparedVersion("2.1.18", new byte[] { 18 });
            manager.ActivateVersion(new Version(2, 1, 18));

            DnsCryptComponentStatus before = manager.GetStatus();
            Assert.AreEqual(new Version(2, 1, 18), before.ActiveVersion);
            Assert.AreEqual(new Version(2, 1, 17), before.PreviousVersion);
            Assert.IsFalse(Directory.Exists(Path.Combine(componentRoot, "2.1.16")));

            DnsCryptPreparedComponent rolledBack = manager.Rollback();
            DnsCryptComponentStatus after = manager.GetStatus();
            Assert.AreEqual(new Version(2, 1, 17), rolledBack.Version);
            Assert.AreEqual(new Version(2, 1, 17), after.ActiveVersion);
            Assert.AreEqual(new Version(2, 1, 18), after.PreviousVersion);
        }

        [TestMethod]
        public void DiscardPreparedVersionNeverDeletesActiveOrPreviousVersions()
        {
            using var manager = new DnsCryptComponentManager(new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, Func<HttpResponseMessage>>())), componentRoot, updateRoot, [DnsCryptComponentManager.ReleaseSigningPublicKey]);
            CreatePreparedVersion("2.1.17", new byte[] { 17 });
            manager.ActivateVersion(new Version(2, 1, 17));
            CreatePreparedVersion("2.1.18", new byte[] { 18 });
            manager.ActivateVersion(new Version(2, 1, 18));
            CreatePreparedVersion("2.1.19", new byte[] { 19 });

            manager.DiscardPreparedVersion(new Version(2, 1, 17));
            manager.DiscardPreparedVersion(new Version(2, 1, 18));
            manager.DiscardPreparedVersion(new Version(2, 1, 19));

            Assert.IsTrue(Directory.Exists(Path.Combine(componentRoot, "2.1.17")));
            Assert.IsTrue(Directory.Exists(Path.Combine(componentRoot, "2.1.18")));
            Assert.IsFalse(Directory.Exists(Path.Combine(componentRoot, "2.1.19")));
        }

        [TestMethod]
        public void RemoveDeletesComponentAndUpdateState()
        {
            using var manager = new DnsCryptComponentManager(new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, Func<HttpResponseMessage>>())), componentRoot, updateRoot, [DnsCryptComponentManager.ReleaseSigningPublicKey]);
            CreatePreparedVersion("2.1.18", new byte[] { 18 });
            Directory.CreateDirectory(updateRoot);
            File.WriteAllText(Path.Combine(updateRoot, "temporary"), "x");
            manager.ActivateVersion(new Version(2, 1, 18));

            manager.Remove();

            Assert.IsFalse(Directory.Exists(componentRoot));
            Assert.IsFalse(Directory.Exists(updateRoot));
            Assert.IsFalse(manager.GetStatus().IsInstalled);
        }

        [TestMethod]
        public void ComponentMetadataPersistsAutoUpdateAndLastCheck()
        {
            DateTimeOffset checkedAt = new(2026, 8, 18, 12, 30, 0, TimeSpan.Zero);
            using (var manager = new DnsCryptComponentManager(new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, Func<HttpResponseMessage>>())), componentRoot, updateRoot, [DnsCryptComponentManager.ReleaseSigningPublicKey]))
            {
                manager.SetAutoUpdate(false);
                manager.RecordUpdateCheck(checkedAt);
            }

            using var reloaded = new DnsCryptComponentManager(new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, Func<HttpResponseMessage>>())), componentRoot, updateRoot, [DnsCryptComponentManager.ReleaseSigningPublicKey]);
            DnsCryptComponentStatus status = reloaded.GetStatus();

            Assert.IsFalse(status.AutoUpdate);
            Assert.AreEqual(checkedAt, status.LastUpdateCheckUtc);
        }

        private void CreatePreparedVersion(string version, byte[] content)
        {
            string directory = Path.Combine(componentRoot, version);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, DnsCryptComponentManager.ExpectedExecutableName), content);
        }

        private static DnsCryptComponentManager.GitHubRelease CreateRelease(
            string tag,
            params DnsCryptComponentManager.GitHubAsset[] assets) => new()
        {
            TagName = tag,
            Draft = false,
            Prerelease = false,
            Assets = assets,
        };

        private static DnsCryptComponentManager.GitHubAsset CreateAsset(string name, long size, string digest = null) => new()
        {
            Name = name,
            Size = size,
            Digest = digest is null ? null : "sha256:" + digest,
            DownloadUrl = OfficialAssetUrl("2.1.18", name),
        };

        private static string OfficialAssetUrl(string version, string name) =>
            $"https://github.com/DNSCrypt/dnscrypt-proxy/releases/download/{version}/{name}";

        private string CreateZipFile(string name, params (string Name, byte[] Content)[] entries)
        {
            string path = Path.Combine(root, name);
            File.WriteAllBytes(path, CreateZipBytes(entries));
            return path;
        }

        [TestMethod]
        public void ConstructorRecoversInterruptedActivationBackupWhenTargetIsMissing()
        {
            string backup = Path.Combine(componentRoot, ".activate-backup-2.1.18-0123456789abcdef0123456789abcdef");
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "dnscrypt-proxy.exe"), "old-binary");

            using var manager = new DnsCryptComponentManager(
                CreateHttpClient(new Dictionary<string, Func<HttpResponseMessage>>()),
                componentRoot,
                updateRoot,
                [DnsCryptComponentManager.ReleaseSigningPublicKey]);

            string restored = Path.Combine(componentRoot, "2.1.18", "dnscrypt-proxy.exe");
            Assert.IsTrue(File.Exists(restored));
            Assert.AreEqual("old-binary", File.ReadAllText(restored));
            Assert.IsFalse(Directory.Exists(backup));
        }

        [TestMethod]
        public void ConstructorCleansStalePreparedAndDownloadDirectories()
        {
            Directory.CreateDirectory(Path.Combine(componentRoot, ".prepared-2.1.18-stale"));
            Directory.CreateDirectory(Path.Combine(componentRoot, ".install-stale"));
            Directory.CreateDirectory(Path.Combine(componentRoot, "2.1.17"));
            Directory.CreateDirectory(Path.Combine(updateRoot, "stale-operation"));
            File.WriteAllText(Path.Combine(updateRoot, "stale.tmp"), "stale");

            using var manager = new DnsCryptComponentManager(
                CreateHttpClient(new Dictionary<string, Func<HttpResponseMessage>>()),
                componentRoot,
                updateRoot,
                [DnsCryptComponentManager.ReleaseSigningPublicKey]);

            Assert.IsFalse(Directory.Exists(Path.Combine(componentRoot, ".prepared-2.1.18-stale")));
            Assert.IsFalse(Directory.Exists(Path.Combine(componentRoot, ".install-stale")));
            Assert.IsTrue(Directory.Exists(Path.Combine(componentRoot, "2.1.17")));
            Assert.IsFalse(Directory.EnumerateFileSystemEntries(updateRoot).Any());
        }

        private static byte[] CreateZipBytes(params (string Name, byte[] Content)[] entries)
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach ((string entryName, byte[] content) in entries)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
                    using Stream output = entry.Open();
                    output.Write(content, 0, content.Length);
                }
            }
            return stream.ToArray();
        }

        private static MinisignFixture CreateMinisignFixture(
            byte[] payload,
            int seedOffset = 0,
            bool prehashed = true)
        {
            byte[] seed = Enumerable.Range(0, 32).Select(value => (byte)(value + seedOffset)).ToArray();
            byte[] keyId = Enumerable.Range(1, 8).Select(value => (byte)(value + seedOffset)).ToArray();
            var privateKey = new Ed25519PrivateKeyParameters(seed);
            byte[] publicKeyBytes = privateKey.GeneratePublicKey().GetEncoded();

            byte[] signedPayload;
            string algorithm;
            string trustedComment;
            if (prehashed)
            {
                var digest = new Blake2bDigest(512);
                digest.BlockUpdate(payload, 0, payload.Length);
                signedPayload = new byte[64];
                digest.DoFinal(signedPayload, 0);
                algorithm = "ED";
                trustedComment = "timestamp:1700000000\tfile:dnscrypt-proxy.zip\thashed";
            }
            else
            {
                signedPayload = payload;
                algorithm = "Ed";
                trustedComment = "timestamp:1700000000\tfile:public-resolvers.md";
            }

            byte[] signature = Sign(privateKey, signedPayload);
            byte[] trustedCommentBytes = Encoding.UTF8.GetBytes(trustedComment);
            byte[] globalMessage = new byte[signature.Length + trustedCommentBytes.Length];
            Buffer.BlockCopy(signature, 0, globalMessage, 0, signature.Length);
            Buffer.BlockCopy(trustedCommentBytes, 0, globalMessage, signature.Length, trustedCommentBytes.Length);
            byte[] globalSignature = Sign(privateKey, globalMessage);

            byte[] publicKeyRecord = Concat(Encoding.ASCII.GetBytes("Ed"), keyId, publicKeyBytes);
            byte[] signatureRecord = Concat(Encoding.ASCII.GetBytes(algorithm), keyId, signature);
            string signatureText = string.Join("\n", new[]
            {
                "untrusted comment: signature from test key",
                Convert.ToBase64String(signatureRecord),
                "trusted comment: " + trustedComment,
                Convert.ToBase64String(globalSignature),
                string.Empty,
            });

            return new MinisignFixture(Convert.ToBase64String(publicKeyRecord), signatureText);
        }

        private static byte[] Sign(Ed25519PrivateKeyParameters privateKey, byte[] message)
        {
            var signer = new Ed25519Signer();
            signer.Init(true, privateKey);
            signer.BlockUpdate(message, 0, message.Length);
            return signer.GenerateSignature();
        }

        private static byte[] Concat(params byte[][] arrays)
        {
            byte[] result = new byte[arrays.Sum(array => array.Length)];
            int offset = 0;
            foreach (byte[] array in arrays)
            {
                Buffer.BlockCopy(array, 0, result, offset, array.Length);
                offset += array.Length;
            }
            return result;
        }

        private static string CreateReleaseJson(
            string tag,
            string archiveName,
            string archiveUrl,
            int archiveSize,
            string digest,
            string signatureName,
            string signatureUrl)
        {
            string digestProperty = digest is null ? string.Empty : $",\"digest\":\"sha256:{digest}\"";
            return "{" +
                $"\"tag_name\":\"{tag}\",\"draft\":false,\"prerelease\":false,\"assets\":[" +
                $"{{\"name\":\"{archiveName}\",\"browser_download_url\":\"{archiveUrl}\",\"size\":{archiveSize}{digestProperty}}}," +
                $"{{\"name\":\"{signatureName}\",\"browser_download_url\":\"{signatureUrl}\",\"size\":1024}}" +
                "]}";
        }

        private static HttpClient CreateHttpClient(Dictionary<string, Func<HttpResponseMessage>> responses) =>
            new(new FakeHttpMessageHandler(responses));

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        private static HttpResponseMessage BytesResponse(byte[] bytes) => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };

        private sealed record MinisignFixture(string PublicKey, string SignatureText);

        private sealed class FakeHttpMessageHandler : HttpMessageHandler
        {
            private readonly IReadOnlyDictionary<string, Func<HttpResponseMessage>> responses;

            public FakeHttpMessageHandler(IReadOnlyDictionary<string, Func<HttpResponseMessage>> responses)
            {
                this.responses = responses;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string url = request.RequestUri?.AbsoluteUri ?? string.Empty;
                if (!responses.TryGetValue(url, out Func<HttpResponseMessage> responseFactory))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                return Task.FromResult(responseFactory());
            }
        }
    }
}
