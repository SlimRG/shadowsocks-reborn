using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Core.Storage;
using Shadowsocks.Controller;
using Shadowsocks.Model;
using Shadowsocks.Windows.Shell;

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
        public void ConfigurationSchemaMigrationCanonicalizesUnknownProperties()
        {
            MemorySettingsStore store = new();
            Configuration.ConfigureSettingsStore(store);
            store.SetString(
                Configuration.SettingsValueName,
                "{\"localPort\":1088,\"obsoleteProperty\":42,\"configs\":[],\"onlineConfigSource\":[]}");
            store.SetString(
                Configuration.SettingsBackupValueName,
                "{\"localPort\":1087,\"obsoleteProperty\":41,\"configs\":[],\"onlineConfigSource\":[]}");
            store.SetInt32(Configuration.SettingsSchemaVersionName, 1);

            Configuration loaded = Configuration.Load();

            Assert.AreEqual(1088, loaded.localPort);
            Assert.IsTrue(store.TryGetInt32(Configuration.SettingsSchemaVersionName, out int schemaVersion));
            Assert.AreEqual(Configuration.SettingsSchemaVersion, schemaVersion);
            Assert.IsTrue(store.TryGetString(Configuration.SettingsValueName, out string primary));
            Assert.IsTrue(store.TryGetString(Configuration.SettingsBackupValueName, out string backup));
            Assert.IsFalse(primary.Contains("obsoleteProperty", StringComparison.Ordinal));
            Assert.IsFalse(backup.Contains("obsoleteProperty", StringComparison.Ordinal));
        }

        [TestMethod]
        public void SchemaV2PersistedDnsCryptDefaultsArePreservedExactly()
        {
            MemorySettingsStore store = new();
            Configuration.ConfigureSettingsStore(store);
            store.SetString(
                Configuration.SettingsValueName,
                "{\"configs\":[],\"dnsPolicy\":{\"mode\":0,\"dnsCrypt\":{\"autoUpdate\":true,\"requireDnssec\":true,\"requireNoLog\":true,\"requireNoFilter\":false,\"ipv4Servers\":true,\"ipv6Servers\":false,\"routeThroughShadowsocks\":false,\"automaticResolvers\":true,\"failClosed\":true,\"serverNames\":[]}}}");
            store.SetInt32(Configuration.SettingsSchemaVersionName, 2);

            Configuration loaded = Configuration.Load();

            Assert.IsTrue(loaded.dnsPolicy.dnsCrypt.autoUpdate);
            Assert.IsTrue(loaded.dnsPolicy.dnsCrypt.requireDnssec);
            Assert.IsTrue(loaded.dnsPolicy.dnsCrypt.requireNoLog);
            Assert.IsFalse(loaded.dnsPolicy.dnsCrypt.requireNoFilter);
            Assert.IsTrue(loaded.dnsPolicy.dnsCrypt.ipv4Servers);
            Assert.IsFalse(loaded.dnsPolicy.dnsCrypt.ipv6Servers);
            Assert.IsFalse(loaded.dnsPolicy.dnsCrypt.routeThroughShadowsocks);
            Assert.IsTrue(loaded.dnsPolicy.dnsCrypt.automaticResolvers);
            Assert.IsTrue(loaded.dnsPolicy.dnsCrypt.failClosed);
            Assert.IsTrue(store.TryGetInt32(Configuration.SettingsSchemaVersionName, out int schemaVersion));
            Assert.AreEqual(Configuration.SettingsSchemaVersion, schemaVersion);
        }

        [TestMethod]
        public void SchemaV2CustomizedDnsCryptSettingsArePreserved()
        {
            MemorySettingsStore store = new();
            Configuration.ConfigureSettingsStore(store);
            store.SetString(
                Configuration.SettingsValueName,
                "{\"configs\":[],\"dnsPolicy\":{\"mode\":0,\"dnsCrypt\":{\"autoUpdate\":false,\"requireDnssec\":true,\"requireNoLog\":true,\"requireNoFilter\":false,\"ipv4Servers\":true,\"ipv6Servers\":false,\"routeThroughShadowsocks\":false,\"automaticResolvers\":true,\"failClosed\":false,\"serverNames\":[]}}}");
            store.SetInt32(Configuration.SettingsSchemaVersionName, 2);

            Configuration loaded = Configuration.Load();
            Configuration.Process(ref loaded);

            Assert.IsFalse(loaded.dnsPolicy.dnsCrypt.autoUpdate);
            Assert.IsFalse(loaded.dnsPolicy.dnsCrypt.requireNoFilter);
            Assert.IsFalse(loaded.dnsPolicy.dnsCrypt.routeThroughShadowsocks);
            Assert.IsFalse(loaded.dnsPolicy.dnsCrypt.ipv6Servers);
            Assert.IsFalse(loaded.dnsPolicy.dnsCrypt.failClosed);
        }

        [TestMethod]
        public void SaveWithoutServerPreservesDnsPreferenceButDisablesSystemProxy()
        {
            MemorySettingsStore store = new();
            Configuration.ConfigureSettingsStore(store);
            Configuration configuration = new()
            {
                Enabled = true,
                dnsPolicy = new Shadowsocks.Controller.Traffic.DnsPolicyConfig
                {
                    mode = Shadowsocks.Controller.Traffic.DnsPolicyMode.DnsCrypt,
                },
            };

            Configuration.Save(configuration);
            Configuration loaded = Configuration.Load();
            Configuration.Process(ref loaded);

            Assert.IsFalse(loaded.Enabled);
            Assert.IsFalse(loaded.HasConfiguredServer);
            Assert.AreEqual(
                Shadowsocks.Controller.Traffic.DnsPolicyMode.DnsCrypt,
                loaded.dnsPolicy.mode);
        }

        [TestMethod]
        public void SavePreservesSelectedServerWhenOnlineGroupsAreSortedAfterLocalServers()
        {
            MemorySettingsStore store = new();
            Configuration.ConfigureSettingsStore(store);

            Server selectedSubscriptionServer = new()
            {
                server = "198.51.100.2",
                ServerPort = 443,
                password = "subscription-secret",
                method = Server.DefaultMethod,
                remarks = "Subscription server",
                group = "https://example.invalid/subscription",
            };
            Server localServer = new()
            {
                // Deliberately use the same endpoint as the selected subscription server.
                // Server.Equals compares only host/port, so Save must track object identity.
                server = "198.51.100.2",
                ServerPort = 443,
                password = "local-secret",
                method = Server.DefaultMethod,
                remarks = "Local server",
                group = string.Empty,
            };
            Configuration configuration = new()
            {
                configs = [selectedSubscriptionServer, localServer],
                index = 0,
            };

            Configuration.Save(configuration);

            Assert.AreSame(localServer, configuration.configs[0]);
            Assert.AreSame(selectedSubscriptionServer, configuration.configs[1]);
            Assert.AreEqual(1, configuration.index);
            Assert.AreSame(selectedSubscriptionServer, configuration.GetCurrentServer());
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
                    new[] { AutoStartup.StartupHiddenOption, AutoStartup.StartupOriginOption, original });

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

        [TestMethod]
        public void StartupCommandMatcherRecognizesCanonicalAndLegacyRunEntries()
        {
            string path = Path.Combine(Path.GetTempPath(), "Shadowsocks Tests", "Shadowsocks.exe");

            Assert.IsTrue(AutoStartup.CommandTargetsExecutable($"\"{path}\" --start-hidden", path));
            Assert.IsTrue(AutoStartup.CommandTargetsExecutable($"{path} --start-hidden", path));
            Assert.IsFalse(AutoStartup.CommandTargetsExecutable($"\"{path}.other\" --start-hidden", path));
        }

        [TestMethod]
        public void RestartManagerRestoresHiddenState()
        {
            string commandLine = AutoStartup.BuildRestartCommandLine(Array.Empty<string>(), windowVisible: false);
            string[] arguments = WindowsCommandLine.ParseArguments(commandLine);

            CollectionAssert.Contains(arguments, AutoStartup.StartupHiddenOption);
            CollectionAssert.DoesNotContain(arguments, AutoStartup.StartupVisibleOption);
        }

        [TestMethod]
        public void RestartManagerRestoresVisibleState()
        {
            string commandLine = AutoStartup.BuildRestartCommandLine(Array.Empty<string>(), windowVisible: true);
            string[] arguments = WindowsCommandLine.ParseArguments(commandLine);

            CollectionAssert.Contains(arguments, AutoStartup.StartupVisibleOption);
            CollectionAssert.DoesNotContain(arguments, AutoStartup.StartupHiddenOption);
        }

        [TestMethod]
        public void StartupStateDetectionRecognizesBootArguments()
        {
            Assert.IsTrue(AutoStartup.IsHiddenStartup(new[] { AutoStartup.StartupHiddenOption }));
            Assert.IsFalse(AutoStartup.IsHiddenStartup(new[] { AutoStartup.StartupVisibleOption }));
            Assert.IsTrue(AutoStartup.IsVisibleStartup(new[] { AutoStartup.StartupVisibleOption }));
            Assert.IsFalse(AutoStartup.IsVisibleStartup(new[] { AutoStartup.StartupHiddenOption }));
            Assert.IsFalse(AutoStartup.IsHiddenStartup(Array.Empty<string>()));
            Assert.IsFalse(AutoStartup.IsVisibleStartup(Array.Empty<string>()));
        }

        [TestMethod]
        public void RestartManagerReplacesStaleUiStateWithoutDuplication()
        {
            string commandLine = AutoStartup.BuildRestartCommandLine(
                new[]
                {
                    AutoStartup.StartupHiddenOption,
                    AutoStartup.StartupVisibleOption,
                    "value with spaces",
                },
                windowVisible: true);
            string[] arguments = WindowsCommandLine.ParseArguments(commandLine);

            Assert.AreEqual(0, arguments.Count(argument => string.Equals(argument, AutoStartup.StartupHiddenOption, StringComparison.OrdinalIgnoreCase)));
            Assert.AreEqual(1, arguments.Count(argument => string.Equals(argument, AutoStartup.StartupVisibleOption, StringComparison.OrdinalIgnoreCase)));
            CollectionAssert.Contains(arguments, "value with spaces");
        }

    }
}
