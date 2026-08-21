using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Shadowsocks.Controller.Service;
using Shadowsocks.Controller.Traffic;
using Shadowsocks.Core;
using Shadowsocks.Model;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class DnsCryptStage3Tests
    {
        [TestMethod]
        public void OlderConfigurationWithoutDnsCryptSettingsIsRepaired()
        {
            string json = $"{{\"version\":\"{ApplicationInfo.Version}\",\"dnsPolicy\":{{\"mode\":0,\"customDohUrl\":\"\"}}}}";
            Assert.IsTrue(Configuration.TryDeserialize(json, out Configuration configuration));

            Configuration.Process(ref configuration);

            Assert.IsNotNull(configuration.dnsPolicy);
            Assert.IsNotNull(configuration.dnsPolicy.dnsCrypt);
            Assert.IsTrue(configuration.dnsPolicy.dnsCrypt.autoUpdate);
            Assert.IsTrue(configuration.dnsPolicy.dnsCrypt.failClosed);
            Assert.IsNotNull(configuration.dnsPolicy.dnsCrypt.serverNames);
        }


        [TestMethod]
        public void LegacyDnsCryptConfigurationDefaultsToAutomaticResolvers()
        {
            string json = $"{{\"version\":\"{ApplicationInfo.Version}\",\"dnsPolicy\":{{\"mode\":4,\"dnsCrypt\":{{\"serverNames\":[\"quad9-dnscrypt-ip4-nofilter-pri\",\"quad9-dnscrypt-ip4-nofilter-ecs-pri\"]}}}}}}";
            Assert.IsTrue(Configuration.TryDeserialize(json, out Configuration configuration));
            Configuration.Process(ref configuration);

            Assert.IsTrue(configuration.dnsPolicy.dnsCrypt.automaticResolvers);
            Assert.AreEqual(0, configuration.dnsPolicy.dnsCrypt.serverNames.Count);
        }

        [TestMethod]
        public void DnsCryptModeAndSettingsRoundTripThroughConfigurationJson()
        {
            var source = new Configuration
            {
                dnsPolicy = new DnsPolicyConfig
                {
                    mode = DnsPolicyMode.DnsCrypt,
                    directDnsServer = "9.9.9.9",
                    directDnsFallbackServer = "149.112.112.112",
                    directDnsRouteThroughShadowsocks = true,
                    customDohUrl = "https://dns.example/dns-query",
                    customDohRouteThroughShadowsocks = true,
                    dnsCrypt = new DnsCryptConfig
                    {
                        autoUpdate = false,
                        requireDnssec = true,
                        requireNoLog = false,
                        requireNoFilter = true,
                        ipv6Servers = true,
                        routeThroughShadowsocks = true,
                        automaticResolvers = false,
                        failClosed = false,
                        serverNames = ["cloudflare", "quad9-dnscrypt-ip4-filter-pri"],
                    },
                },
            };

            string json = JsonConvert.SerializeObject(source);
            Assert.IsTrue(Configuration.TryDeserialize(json, out Configuration restored));
            Configuration.Process(ref restored);

            Assert.AreEqual(DnsPolicyMode.DnsCrypt, restored.dnsPolicy.mode);
            Assert.AreEqual("9.9.9.9", restored.dnsPolicy.directDnsServer);
            Assert.AreEqual("149.112.112.112", restored.dnsPolicy.directDnsFallbackServer);
            Assert.IsTrue(restored.dnsPolicy.directDnsRouteThroughShadowsocks);
            Assert.AreEqual("https://dns.example/dns-query", restored.dnsPolicy.customDohUrl);
            Assert.IsTrue(restored.dnsPolicy.customDohRouteThroughShadowsocks);
            Assert.IsFalse(restored.dnsPolicy.dnsCrypt.autoUpdate);
            Assert.IsTrue(restored.dnsPolicy.dnsCrypt.requireNoFilter);
            Assert.IsTrue(restored.dnsPolicy.dnsCrypt.ipv6Servers);
            Assert.IsTrue(restored.dnsPolicy.dnsCrypt.routeThroughShadowsocks);
            Assert.IsFalse(restored.dnsPolicy.dnsCrypt.automaticResolvers);
            Assert.IsTrue(restored.dnsPolicy.dnsCrypt.failClosed);
            CollectionAssert.AreEqual(new[] { "cloudflare", "quad9-dnscrypt-ip4-filter-pri" }, restored.dnsPolicy.dnsCrypt.serverNames);
        }

        [TestMethod]
        public void TrayPresentationCoversAllDnsCryptManagementStates()
        {
            Version installed = new(2, 1, 18);
            DnsCryptComponentStatus component = new(true, installed, null, @"C:\\dnscrypt-proxy.exe", true, null);

            AssertTray("DNSCrypt: Not installed", Status(false, null, DnsCryptRuntimeState.NotInstalled));
            AssertTray("DNSCrypt: Starting…", Status(true, installed, DnsCryptRuntimeState.Starting));
            AssertTray("DNSCrypt: Updating…", Status(true, installed, DnsCryptRuntimeState.Updating));
            AssertTray("DNSCrypt: Running · {0}", Status(true, installed, DnsCryptRuntimeState.Running), installed);
            AssertTray("DNSCrypt: Failed", Status(true, installed, DnsCryptRuntimeState.Failed));
            AssertTray("DNSCrypt: Stopped · {0}", Status(true, installed, DnsCryptRuntimeState.Stopped), installed);

            DnsCryptManagementStatus update = new(
                DnsPolicyMode.DnsCrypt,
                component,
                new DnsCryptRuntimeStatus(DnsCryptRuntimeState.Running, installed, 1, 5300, DateTimeOffset.UtcNow, "config", null),
                new Version(2, 1, 19));
            AssertTray("DNSCrypt: Update available · {0}", update, new Version(2, 1, 19));

            DnsCryptManagementStatus updatingWithKnownRelease = update with
            {
                Runtime = new DnsCryptRuntimeStatus(DnsCryptRuntimeState.Updating, installed, 1, 5300, DateTimeOffset.UtcNow, "config", null),
            };
            AssertTray("DNSCrypt: Updating…", updatingWithKnownRelease);
        }

        [TestMethod]
        public void UpdateAvailableRequiresNewerKnownVersion()
        {
            Version installed = new(2, 1, 18);
            DnsCryptComponentStatus component = new(true, installed, null, "dnscrypt-proxy.exe", true, null);
            DnsCryptRuntimeStatus runtime = new(DnsCryptRuntimeState.Stopped, installed, 0, 0, null, "config", null);

            Assert.IsFalse(new DnsCryptManagementStatus(DnsPolicyMode.System, component, runtime, null).UpdateAvailable);
            Assert.IsFalse(new DnsCryptManagementStatus(DnsPolicyMode.System, component, runtime, installed).UpdateAvailable);
            Assert.IsTrue(new DnsCryptManagementStatus(DnsPolicyMode.System, component, runtime, new Version(2, 1, 19)).UpdateAvailable);
        }

        private static DnsCryptManagementStatus Status(bool installed, Version version, DnsCryptRuntimeState state)
        {
            DnsCryptComponentStatus component = new(installed, installed ? version : null, null, installed ? "dnscrypt-proxy.exe" : null, true, null);
            DnsCryptRuntimeStatus runtime = new(
                state,
                version,
                state is DnsCryptRuntimeState.Running or DnsCryptRuntimeState.Updating ? 123 : 0,
                state is DnsCryptRuntimeState.Running or DnsCryptRuntimeState.Updating ? 5300 : 0,
                null,
                "config",
                null);
            return new DnsCryptManagementStatus(DnsPolicyMode.DnsCrypt, component, runtime, null);
        }

        private static void AssertTray(string key, DnsCryptManagementStatus status, Version version = null)
        {
            DnsCryptTrayPresentation presentation = DnsCryptPresentation.GetTrayPresentation(status);
            Assert.AreEqual(key, presentation.LocalizationKey);
            Assert.AreEqual(version, presentation.Version);
        }
    }
}
