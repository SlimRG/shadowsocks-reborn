using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Formats.Tar;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller.Service;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    [DoNotParallelize]
    public class PluginManagerTests
    {
        private string _root;
        private string _pluginsRoot;

        [TestInitialize]
        public void Initialize()
        {
            _root = Path.Combine(Path.GetTempPath(), "Shadowsocks.UnitTests", Guid.NewGuid().ToString("N"));
            _pluginsRoot = Path.Combine(_root, "Plugins");
            Directory.CreateDirectory(_root);
            PluginManager.PluginsDirectoryOverride = _pluginsRoot;
        }

        [TestCleanup]
        public void Cleanup()
        {
            PluginManager.PluginsDirectoryOverride = null;
            PluginManager.HttpClientOverride = null;
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [TestMethod]
        public void CatalogContainsSupportedWindowsPlugins()
        {
            CollectionAssert.AreEquivalent(
                new[] { "xray-plugin", "v2ray-plugin", "qtun" },
                PluginManager.Catalog.Select(entry => entry.Id).ToArray());
        }

        [TestMethod]
        public async Task QtunCatalogMatchesUpstreamWindowsReleaseArchive()
        {
            PluginCatalogEntry entry = PluginManager.Catalog.Single(candidate => candidate.Id == "qtun");
            Assert.AreEqual("x86_64-pc-windows-msvc", entry.WindowsAssetMarker);

            using var client = new HttpClient(new QtunReleaseHandler());
            PluginManager.HttpClientOverride = client;

            InstalledPlugin installed = await PluginManager.InstallCatalogPluginAsync(entry);

            Assert.AreEqual("qtun", installed.Id);
            Assert.AreEqual("v0.2.0", installed.ReleaseTag);
            Assert.AreEqual("shadowsocks-v0.2.0.x86_64-pc-windows-msvc.zip", installed.AssetName);
            Assert.AreEqual("qtun-client.exe", Path.GetFileName(installed.ExecutablePath));
        }

        [TestMethod]
        public void CatalogLookupRecognizesShareLinkPluginExecutableNames()
        {
            Assert.AreEqual("xray-plugin", PluginManager.FindCatalogEntry("xray-plugin.exe")?.Id);
            Assert.AreEqual("v2ray-plugin", PluginManager.FindCatalogEntry("v2ray-plugin.exe")?.Id);
            Assert.AreEqual("qtun", PluginManager.FindCatalogEntry("qtun-client.exe")?.Id);
            Assert.IsNull(PluginManager.FindCatalogEntry("unknown-plugin.exe"));
        }

        [TestMethod]
        public void ManualZipInstallsUnderPluginStorageAndResolvesExecutable()
        {
            string zip = CreateZip("custom-plugin.zip",
                ("custom-plugin.exe", new byte[] { 0x4D, 0x5A }),
                ("data/config.json", new byte[] { 0x7B, 0x7D }));

            InstalledPlugin installed = PluginManager.InstallManualZip(zip);

            Assert.AreEqual("custom-plugin", installed.Id);
            Assert.AreEqual("Manual ZIP", installed.Source);
            Assert.IsFalse(installed.CanAutoUpdate);
            Assert.IsFalse(installed.AutoUpdate);
            Assert.IsTrue(File.Exists(installed.ExecutablePath));
            Assert.IsTrue(installed.ExecutablePath.StartsWith(Path.GetFullPath(_pluginsRoot), StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(installed.ExecutablePath, PluginManager.ResolveExecutable("custom-plugin"));
            Assert.AreEqual(installed.ExecutablePath, PluginManager.ResolveExecutable("custom-plugin.exe"));
            Assert.AreEqual(1, PluginManager.GetInstalledPlugins().Count);
        }

        [TestMethod]
        public void ManualTarGzInstallsUnderPluginStorageAndResolvesExecutable()
        {
            string archive = CreateTarGz("custom-tar-plugin.tar.gz",
                ("custom-tar-plugin.exe", new byte[] { 0x4D, 0x5A }),
                ("data/config.json", new byte[] { 0x7B, 0x7D }));

            InstalledPlugin installed = PluginManager.InstallManualArchive(archive);

            Assert.AreEqual("custom-tar-plugin", installed.Id);
            Assert.AreEqual("Manual TAR.GZ", installed.Source);
            Assert.IsFalse(installed.CanAutoUpdate);
            Assert.IsFalse(installed.AutoUpdate);
            Assert.IsTrue(File.Exists(installed.ExecutablePath));
            Assert.IsTrue(installed.ExecutablePath.StartsWith(Path.GetFullPath(_pluginsRoot), StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(installed.ExecutablePath, PluginManager.ResolveExecutable("custom-tar-plugin"));
            Assert.AreEqual(1, PluginManager.GetInstalledPlugins().Count);
        }

        [TestMethod]
        public void ManualZipSelectsSingleClientExecutableWhenPackageContainsClientAndServer()
        {
            string zip = CreateZip("qtun.zip",
                ("bin/qtun-client.exe", new byte[] { 0x4D, 0x5A }),
                ("bin/qtun-server.exe", new byte[] { 0x4D, 0x5A }));

            InstalledPlugin installed = PluginManager.InstallManualZip(zip);

            Assert.AreEqual("qtun", installed.Id);
            Assert.AreEqual("qtun-client.exe", Path.GetFileName(installed.ExecutablePath));
        }

        [TestMethod]
        public void ManualImportRejectsUnsupportedArchiveFiles()
        {
            Assert.ThrowsExactly<InvalidDataException>(() => PluginManager.InstallManualArchive(Path.Combine(_root, "plugin.exe")));
        }

        [TestMethod]
        public void ManualImportRejectsZipWithoutExecutable()
        {
            string zip = CreateZip("no-exe.zip", ("README.txt", new byte[] { 1, 2, 3 }));

            Assert.ThrowsExactly<InvalidDataException>(() => PluginManager.InstallManualZip(zip));
            Assert.AreEqual(0, PluginManager.GetInstalledPlugins().Count);
        }

        [TestMethod]
        public void ManualImportRejectsZipPathTraversal()
        {
            string zip = CreateZip("unsafe.zip", ("../escaped.exe", new byte[] { 0x4D, 0x5A }));

            Assert.ThrowsExactly<InvalidDataException>(() => PluginManager.InstallManualZip(zip));
            Assert.IsFalse(File.Exists(Path.Combine(_pluginsRoot, "escaped.exe")));
            Assert.AreEqual(0, PluginManager.GetInstalledPlugins().Count);
        }

        [TestMethod]
        public void ManualImportRejectsWindowsAlternateDataStreamPath()
        {
            string zip = CreateZip("unsafe-ads.zip", ("plugin.exe:payload", new byte[] { 0x4D, 0x5A }));

            Assert.ThrowsExactly<InvalidDataException>(() => PluginManager.InstallManualZip(zip));
            Assert.AreEqual(0, PluginManager.GetInstalledPlugins().Count);
        }

        [TestMethod]
        public void ManualImportRejectsTarGzPathTraversal()
        {
            string archive = CreateTarGz("unsafe.tar.gz", ("../escaped.exe", new byte[] { 0x4D, 0x5A }));

            Assert.ThrowsExactly<InvalidDataException>(() => PluginManager.InstallManualArchive(archive));
            Assert.IsFalse(File.Exists(Path.Combine(_pluginsRoot, "escaped.exe")));
            Assert.AreEqual(0, PluginManager.GetInstalledPlugins().Count);
        }

        [TestMethod]
        public void ExtractionBudgetTracksActualBytesAndRejectsOverflow()
        {
            const long Limit = 512L * 1024 * 1024;

            Assert.AreEqual(Limit, PluginManager.AccumulateExtractedBytes(Limit - 1, 1, "ZIP"));
            Assert.ThrowsExactly<InvalidDataException>(() =>
                PluginManager.AccumulateExtractedBytes(Limit - 1, 2, "ZIP"));
        }

        [TestMethod]
        public async Task CatalogPluginStoresReleaseMetadataAndAutoUpdates()
        {
            PluginCatalogEntry entry = PluginManager.Catalog.Single(candidate => candidate.Id == "xray-plugin");
            using var firstClient = new HttpClient(new FakeReleaseHandler("v1.0.0", 0x01));
            PluginManager.HttpClientOverride = firstClient;

            InstalledPlugin installed = await PluginManager.InstallCatalogPluginAsync(entry);

            Assert.IsTrue(installed.CanAutoUpdate);
            Assert.IsTrue(installed.AutoUpdate);
            Assert.AreEqual(entry.Repository, installed.Repository);
            Assert.AreEqual("v1.0.0", installed.ReleaseTag);
            CollectionAssert.AreEqual(new byte[] { 0x4D, 0x5A, 0x01 }, File.ReadAllBytes(installed.ExecutablePath));

            using var secondClient = new HttpClient(new FakeReleaseHandler("v1.1.0", 0x02));
            PluginManager.HttpClientOverride = secondClient;
            PluginUpdateSummary summary = await PluginManager.UpdateCatalogPluginsAsync(force: true);

            Assert.AreEqual(1, summary.CheckedCount);
            Assert.AreEqual(1, summary.UpdatedCount);
            Assert.AreEqual(0, summary.Errors.Count);
            InstalledPlugin updated = PluginManager.GetInstalledPlugins().Single();
            Assert.AreEqual("v1.1.0", updated.ReleaseTag);
            CollectionAssert.AreEqual(new byte[] { 0x4D, 0x5A, 0x02 }, File.ReadAllBytes(updated.ExecutablePath));
        }

        [TestMethod]
        public async Task CatalogPluginRejectsMismatchedGitHubDigest()
        {
            PluginCatalogEntry entry = PluginManager.Catalog.Single(candidate => candidate.Id == "xray-plugin");
            using var client = new HttpClient(new FakeReleaseHandler("v1.0.0", 0x01, useInvalidDigest: true));
            PluginManager.HttpClientOverride = client;

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PluginManager.InstallCatalogPluginAsync(entry));

            Assert.AreEqual(0, PluginManager.GetInstalledPlugins().Count);
        }

        [TestMethod]
        public async Task FailedAutomaticCheckDoesNotPostponeRetryWindow()
        {
            PluginCatalogEntry entry = PluginManager.Catalog.Single(candidate => candidate.Id == "xray-plugin");
            using var installClient = new HttpClient(new FakeReleaseHandler("v1.0.0", 0x01));
            PluginManager.HttpClientOverride = installClient;
            await PluginManager.InstallCatalogPluginAsync(entry);
            DateTimeOffset? lastSuccessfulCheck = PluginManager.GetInstalledPlugins().Single().LastUpdateCheckUtc;

            using var failingClient = new HttpClient(new StatusCodeHandler(HttpStatusCode.ServiceUnavailable));
            PluginManager.HttpClientOverride = failingClient;
            PluginUpdateSummary summary = await PluginManager.UpdateCatalogPluginsAsync(force: true);

            Assert.AreEqual(1, summary.CheckedCount);
            Assert.AreEqual(0, summary.UpdatedCount);
            Assert.AreEqual(1, summary.Errors.Count);
            Assert.AreEqual(lastSuccessfulCheck, PluginManager.GetInstalledPlugins().Single().LastUpdateCheckUtc);
        }

        [TestMethod]
        public async Task SuccessfulNoOpCheckAdvancesRetryWindow()
        {
            PluginCatalogEntry entry = PluginManager.Catalog.Single(candidate => candidate.Id == "xray-plugin");
            using var installClient = new HttpClient(new FakeReleaseHandler("v1.0.0", 0x01));
            PluginManager.HttpClientOverride = installClient;
            await PluginManager.InstallCatalogPluginAsync(entry);
            DateTimeOffset? before = PluginManager.GetInstalledPlugins().Single().LastUpdateCheckUtc;

            using var checkClient = new HttpClient(new FakeReleaseHandler("v1.0.0", 0x01));
            PluginManager.HttpClientOverride = checkClient;
            PluginUpdateSummary summary = await PluginManager.UpdateCatalogPluginsAsync(force: true);
            DateTimeOffset? after = PluginManager.GetInstalledPlugins().Single().LastUpdateCheckUtc;

            Assert.AreEqual(1, summary.CheckedCount);
            Assert.AreEqual(0, summary.UpdatedCount);
            Assert.AreEqual(0, summary.Errors.Count);
            Assert.IsTrue(after >= before);
        }

        [TestMethod]
        public async Task CatalogAutoUpdateRejectsVersionDowngrade()
        {
            PluginCatalogEntry entry = PluginManager.Catalog.Single(candidate => candidate.Id == "xray-plugin");
            using var installClient = new HttpClient(new FakeReleaseHandler("v1.1.0", 0x02));
            PluginManager.HttpClientOverride = installClient;
            InstalledPlugin installed = await PluginManager.InstallCatalogPluginAsync(entry);
            DateTimeOffset? before = installed.LastUpdateCheckUtc;

            using var downgradeClient = new HttpClient(new FakeReleaseHandler("v1.0.0", 0x01));
            PluginManager.HttpClientOverride = downgradeClient;
            PluginUpdateSummary summary = await PluginManager.UpdateCatalogPluginsAsync(force: true);
            InstalledPlugin after = PluginManager.GetInstalledPlugins().Single();

            Assert.AreEqual(1, summary.CheckedCount);
            Assert.AreEqual(0, summary.UpdatedCount);
            Assert.AreEqual(0, summary.Errors.Count);
            Assert.AreEqual("v1.1.0", after.ReleaseTag);
            Assert.IsTrue(after.LastUpdateCheckUtc >= before);
            CollectionAssert.AreEqual(new byte[] { 0x4D, 0x5A, 0x02 }, File.ReadAllBytes(after.ExecutablePath));
        }

        [TestMethod]
        public async Task DisabledCatalogAutoUpdateDoesNotCallGitHub()
        {
            PluginCatalogEntry entry = PluginManager.Catalog.Single(candidate => candidate.Id == "xray-plugin");
            using var installClient = new HttpClient(new FakeReleaseHandler("v1.0.0", 0x01));
            PluginManager.HttpClientOverride = installClient;
            await PluginManager.InstallCatalogPluginAsync(entry);
            Assert.IsTrue(PluginManager.SetAutoUpdate(entry.Id, false));

            using var throwingClient = new HttpClient(new ThrowingHandler());
            PluginManager.HttpClientOverride = throwingClient;
            PluginUpdateSummary summary = await PluginManager.UpdateCatalogPluginsAsync(force: true);

            Assert.AreEqual(0, summary.CheckedCount);
            Assert.AreEqual(0, summary.UpdatedCount);
            Assert.IsFalse(PluginManager.GetInstalledPlugins().Single().AutoUpdate);
        }

        [TestMethod]
        public async Task ManualPluginWithCatalogIdIsNotAutoUpdated()
        {
            string zip = CreateZip("xray-plugin.zip", ("xray-plugin.exe", new byte[] { 0x4D, 0x5A }));
            InstalledPlugin installed = PluginManager.InstallManualZip(zip);
            Assert.AreEqual("xray-plugin", installed.Id);
            Assert.IsFalse(installed.CanAutoUpdate);

            using var throwingClient = new HttpClient(new ThrowingHandler());
            PluginManager.HttpClientOverride = throwingClient;
            PluginUpdateSummary summary = await PluginManager.UpdateCatalogPluginsAsync(force: true);

            Assert.AreEqual(0, summary.CheckedCount);
            Assert.AreEqual(0, summary.UpdatedCount);
        }

        [TestMethod]
        public async Task CatalogPluginInUseIsDeferredWithoutNetworkAccess()
        {
            PluginCatalogEntry entry = PluginManager.Catalog.Single(candidate => candidate.Id == "xray-plugin");
            using var installClient = new HttpClient(new FakeReleaseHandler("v1.0.0", 0x01));
            PluginManager.HttpClientOverride = installClient;
            await PluginManager.InstallCatalogPluginAsync(entry);

            using var throwingClient = new HttpClient(new ThrowingHandler());
            PluginManager.HttpClientOverride = throwingClient;
            PluginUpdateSummary summary = await PluginManager.UpdateCatalogPluginsAsync(new[] { entry.Id }, force: true);

            Assert.AreEqual(0, summary.CheckedCount);
            Assert.AreEqual(0, summary.UpdatedCount);
            Assert.AreEqual(1, summary.SkippedInUseCount);
        }

        [TestMethod]
        public void AutomaticCheckPolicyUsesTwentyFourHourIntervalAndRecoversFromClockRollback()
        {
            DateTimeOffset now = new(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);

            Assert.IsTrue(PluginManager.IsAutomaticCheckDue(null, now));
            Assert.IsFalse(PluginManager.IsAutomaticCheckDue(now.AddHours(-23), now));
            Assert.IsTrue(PluginManager.IsAutomaticCheckDue(now.AddHours(-24), now));
            Assert.IsTrue(PluginManager.IsAutomaticCheckDue(now.AddMinutes(1), now));
        }

        [TestMethod]
        public void InterruptedSwapRestoresOrphanedPluginBackupOnNextStorageRead()
        {
            string zip = CreateZip("recovery-plugin.zip", ("recovery-plugin.exe", new byte[] { 0x4D, 0x5A }));
            InstalledPlugin installed = PluginManager.InstallManualZip(zip);
            string destination = Path.Combine(_pluginsRoot, installed.Id);
            string backup = Path.Combine(_pluginsRoot, $".plugin-backup-{installed.Id}-{Guid.NewGuid():N}");
            Directory.Move(destination, backup);

            Assert.IsFalse(Directory.Exists(destination));
            InstalledPlugin recovered = PluginManager.GetInstalledPlugins().Single();

            Assert.AreEqual(installed.Id, recovered.Id);
            Assert.IsTrue(Directory.Exists(destination));
            Assert.IsFalse(Directory.Exists(backup));
            Assert.IsTrue(File.Exists(recovered.ExecutablePath));
        }

        [TestMethod]
        public void RemoveDeletesInstalledPlugin()
        {
            string zip = CreateZip("remove-me.zip", ("remove-me.exe", new byte[] { 0x4D, 0x5A }));
            InstalledPlugin installed = PluginManager.InstallManualZip(zip);

            PluginManager.Remove(installed.Id);

            Assert.IsFalse(Directory.Exists(Path.Combine(_pluginsRoot, installed.Id)));
            Assert.IsNull(PluginManager.ResolveExecutable(installed.Id));
        }

        private sealed class FakeReleaseHandler : HttpMessageHandler
        {
            private readonly string _tag;
            private readonly byte[] _payload;
            private readonly bool _useInvalidDigest;

            public FakeReleaseHandler(string tag, byte payloadVersion, bool useInvalidDigest = false)
            {
                _tag = tag;
                _payload = CreateZipBytes(payloadVersion);
                _useInvalidDigest = useInvalidDigest;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri?.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) == true)
                {
                    string digest = _useInvalidDigest
                        ? new string('0', 64)
                        : Convert.ToHexString(SHA256.HashData(_payload)).ToLowerInvariant();
                    string json = "{\"tag_name\":\"" + _tag + "\",\"assets\":[{"
                        + "\"name\":\"xray-plugin-windows-amd64.zip\","
                        + "\"browser_download_url\":\"https://downloads.test/xray-plugin.zip\","
                        + "\"digest\":\"sha256:" + digest + "\"}]}";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    });
                }

                if (request.RequestUri?.Host.Equals("downloads.test", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(_payload),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            private static byte[] CreateZipBytes(byte payloadVersion)
            {
                using var stream = new MemoryStream();
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    ZipArchiveEntry entry = archive.CreateEntry("xray-plugin.exe");
                    using Stream output = entry.Open();
                    output.Write(new byte[] { 0x4D, 0x5A, payloadVersion });
                }
                return stream.ToArray();
            }
        }

        private sealed class QtunReleaseHandler : HttpMessageHandler
        {
            private readonly byte[] _payload = CreateQtunZipBytes();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri?.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) == true)
                {
                    string digest = Convert.ToHexString(SHA256.HashData(_payload)).ToLowerInvariant();
                    const string assetName = "shadowsocks-v0.2.0.x86_64-pc-windows-msvc.zip";
                    string json = "{\"tag_name\":\"v0.2.0\",\"assets\":[{"
                        + "\"name\":\"" + assetName + "\","
                        + "\"browser_download_url\":\"https://downloads.test/qtun.zip\","
                        + "\"digest\":\"sha256:" + digest + "\"}]}";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    });
                }

                if (request.RequestUri?.Host.Equals("downloads.test", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(_payload),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            private static byte[] CreateQtunZipBytes()
            {
                using var stream = new MemoryStream();
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (string fileName in new[] { "qtun-client.exe", "qtun-server.exe" })
                    {
                        ZipArchiveEntry entry = archive.CreateEntry(fileName);
                        using Stream output = entry.Open();
                        output.Write(new byte[] { 0x4D, 0x5A, 0x20 });
                    }
                }
                return stream.ToArray();
            }
        }

        private sealed class StatusCodeHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _statusCode;

            public StatusCodeHandler(HttpStatusCode statusCode)
            {
                _statusCode = statusCode;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(_statusCode));
        }

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => throw new AssertFailedException($"Unexpected network request: {request.RequestUri}");
        }

        private string CreateTarGz(string name, params (string Name, byte[] Content)[] entries)
        {
            string path = Path.Combine(_root, name);
            using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var gzip = new GZipStream(stream, CompressionLevel.SmallestSize);
            using var writer = new TarWriter(gzip, leaveOpen: false);
            foreach ((string entryName, byte[] content) in entries)
            {
                using var data = new MemoryStream(content, writable: false);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
                {
                    DataStream = data,
                };
                writer.WriteEntry(entry);
            }
            return path;
        }

        private string CreateZip(string name, params (string Name, byte[] Content)[] entries)
        {
            string path = Path.Combine(_root, name);
            using FileStream stream = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach ((string entryName, byte[] content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(entryName);
                using Stream target = entry.Open();
                target.Write(content, 0, content.Length);
            }
            return path;
        }
    }
}
