using System;
using System.IO;
using System.IO.Compression;
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
        public void ManualZipInstallsUnderPluginStorageAndResolvesExecutable()
        {
            string zip = CreateZip("custom-plugin.zip",
                ("custom-plugin.exe", new byte[] { 0x4D, 0x5A }),
                ("data/config.json", new byte[] { 0x7B, 0x7D }));

            InstalledPlugin installed = PluginManager.InstallManualZip(zip);

            Assert.AreEqual("custom-plugin", installed.Id);
            Assert.AreEqual("Manual ZIP", installed.Source);
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
        public void ManualImportRejectsTarGzPathTraversal()
        {
            string archive = CreateTarGz("unsafe.tar.gz", ("../escaped.exe", new byte[] { 0x4D, 0x5A }));

            Assert.ThrowsExactly<InvalidDataException>(() => PluginManager.InstallManualArchive(archive));
            Assert.IsFalse(File.Exists(Path.Combine(_pluginsRoot, "escaped.exe")));
            Assert.AreEqual(0, PluginManager.GetInstalledPlugins().Count);
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
