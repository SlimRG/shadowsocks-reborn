using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Core.Storage;
using Shadowsocks.Controller;
using Shadowsocks.Model;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    [DoNotParallelize]
    public class StoragePolicyTest
    {
        [TestMethod]
        public void NormalStorageUsesLocalAppDataForAllApplicationState()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string temp = Path.GetTempPath();

            Assert.IsTrue(AppStoragePaths.LocalAppDataRoot.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(AppStoragePaths.StorageRoot.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(AppStoragePaths.TempRoot.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(AppStoragePaths.CleanSessionsRoot.StartsWith(temp, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(AppStoragePaths.SettingsFile.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(AppStoragePaths.PluginsDirectory.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(Path.Combine(AppStoragePaths.StorageRoot, "Plugins"), AppStoragePaths.PluginsDirectory);
            Assert.AreEqual(Path.Combine(AppStoragePaths.StorageRoot, "Components"), AppStoragePaths.ComponentsDirectory);
            Assert.AreEqual(Path.Combine(AppStoragePaths.ComponentsDirectory, "DNSCryptProxy"), AppStoragePaths.DnsCryptComponentDirectory);
            Assert.AreEqual(Path.Combine(AppStoragePaths.RuntimeRoot, "DNSCryptProxy"), AppStoragePaths.DnsCryptRuntimeDirectory);
            Assert.AreEqual(Path.Combine(AppStoragePaths.TempUpdatesRoot, "DNSCryptProxy"), AppStoragePaths.DnsCryptUpdateDirectory);
            Assert.IsTrue(AppStoragePaths.DnsCryptComponentDirectory.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(AppStoragePaths.StartupExecutableFile.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual("Shadowsocks.exe", Path.GetFileName(AppStoragePaths.StartupExecutableFile));
        }

        [TestMethod]
        public void CleanModeUsesRufusStylePSuffix()
        {
            Assert.IsTrue(AppStoragePaths.IsCleanModeExecutableName(@"C:\Tools\Shadowsocksp.exe"));
            Assert.IsTrue(AppStoragePaths.IsCleanModeExecutableName(@"C:\Tools\Shadowsocks-5.0p.exe"));
            Assert.IsTrue(AppStoragePaths.IsCleanModeExecutableName(@"C:\Tools\SHADOWSOCKSP.EXE"));
            Assert.IsFalse(AppStoragePaths.IsCleanModeExecutableName(@"C:\Tools\Shadowsocks.exe"));
            Assert.IsFalse(AppStoragePaths.IsCleanModeExecutableName(@"C:\Tools\Shadowsocks-portable.exe"));
        }

        [TestMethod]
        public void JsonFileSettingsStorePersistsStructuredConfigurationAndBackup()
        {
            string root = Path.Combine(Path.GetTempPath(), "Shadowsocks.UnitTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string file = Path.Combine(root, "settings.json");
                string backup = Path.Combine(root, "settings.backup.json");
                var store = new JsonFileSettingsStore(file, backup);
                Configuration.ConfigureSettingsStore(store);

                Configuration first = new() { localPort = 1081, uiTheme = "Light" };
                Configuration.Save(first);
                Configuration second = new() { localPort = 1082, uiTheme = "Dark" };
                Configuration.Save(second);

                string persisted = File.ReadAllText(file);
                StringAssert.Contains(persisted, "\"Configuration\"");
                StringAssert.Contains(persisted, "\"localPort\": 1082");
                StringAssert.Contains(persisted, "\"ConfigurationBackup\"");
                Assert.IsTrue(File.Exists(backup));

                Configuration loaded = Configuration.Load();
                Assert.AreEqual(1082, loaded.localPort);
                Assert.AreEqual("Dark", loaded.uiTheme);
            }
            finally
            {
                Configuration.ConfigureSettingsStore(new MemorySettingsStore());
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [TestMethod]
        public void JsonFileSettingsStoreFallsBackToDurableBackupWhenPrimaryIsCorrupt()
        {
            string root = Path.Combine(Path.GetTempPath(), "Shadowsocks.UnitTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string file = Path.Combine(root, "settings.json");
                string backup = Path.Combine(root, "settings.backup.json");
                var store = new JsonFileSettingsStore(file, backup);

                store.SetString("Value", "\"first\"");
                store.SetString("Value", "\"second\"");
                File.WriteAllText(file, "{ invalid json");

                Assert.IsTrue(store.TryGetString("Value", out string recovered));
                Assert.AreEqual("first", recovered);
                StringAssert.Contains(File.ReadAllText(file), "first");
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [TestMethod]
        public void ConfigurationRoundTripsThroughSettingsStoreAndRollsBackCorruption()
        {
            MemorySettingsStore store = new();
            Configuration.ConfigureSettingsStore(store);

            Configuration first = new() { localPort = 1081, uiTheme = "Light" };
            Configuration.Save(first);

            Configuration second = new() { localPort = 1082, uiTheme = "Dark" };
            Configuration.Save(second);

            Configuration loaded = Configuration.Load();
            Assert.AreEqual(1082, loaded.localPort);
            Assert.AreEqual("Dark", loaded.uiTheme);
            Assert.IsTrue(store.TryGetString(Configuration.SettingsBackupValueName, out string backup));
            Assert.IsTrue(Configuration.TryDeserialize(backup, out Configuration backupConfig));
            Assert.AreEqual(1081, backupConfig.localPort);

            store.SetString(Configuration.SettingsValueName, "{ invalid json");
            Configuration rolledBack = Configuration.Load();
            Assert.AreEqual(1081, rolledBack.localPort);
            Assert.IsTrue(store.TryGetString(Configuration.SettingsValueName, out string restoredPrimary));
            Assert.IsTrue(Configuration.TryDeserialize(restoredPrimary, out Configuration restoredConfig));
            Assert.AreEqual(1081, restoredConfig.localPort);
        }

        private sealed class MemorySettingsStore : ISettingsStore
        {
            private readonly Dictionary<string, object> values = new(StringComparer.Ordinal);

            public bool TryGetString(string name, out string value)
            {
                if (values.TryGetValue(name, out object raw) && raw is string text)
                {
                    value = text;
                    return true;
                }
                value = null;
                return false;
            }

            public void SetString(string name, string value) => values[name] = value;

            public bool TryGetInt32(string name, out int value)
            {
                if (values.TryGetValue(name, out object raw) && raw is int number)
                {
                    value = number;
                    return true;
                }
                value = default;
                return false;
            }

            public void SetInt32(string name, int value) => values[name] = value;

            public void DeleteValue(string name) => values.Remove(name);
        }
        [TestMethod]
        public void StartupCopyResolvesOriginalExecutableForSelfUpdate()
        {
            string root = Path.Combine(Path.GetTempPath(), "ShadowsocksTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string startup = Path.Combine(root, "Startup", "Shadowsocks.exe");
                string original = Path.Combine(root, "Shadowsocks-main.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(startup)!);
                File.WriteAllText(startup, "startup");
                File.WriteAllText(original, "original");

                string resolved = AutoStartup.ResolvePrimaryExecutablePath(
                    startup,
                    startup,
                    new[] { "--start-hidden", AutoStartup.StartupOriginOption, original });

                Assert.AreEqual(Path.GetFullPath(original), resolved);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [TestMethod]
        public void StartupCopyRejectsMissingOriginForSelfUpdate()
        {
            string startup = Path.Combine(Path.GetTempPath(), "ShadowsocksTests", Guid.NewGuid().ToString("N"), "Shadowsocks.exe");
            string missing = Path.Combine(Path.GetDirectoryName(startup)!, "Shadowsocks-missing.exe");

            string resolved = AutoStartup.ResolvePrimaryExecutablePath(
                startup,
                startup,
                new[] { AutoStartup.StartupOriginOption, missing });

            Assert.AreEqual(Path.GetFullPath(startup), resolved);
        }

    }
}
