using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Shadowsocks.Model;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class ConfigurationStateTest
    {
        [TestMethod]
        public void EmptyConfigurationHasNoSyntheticServer()
        {
            Configuration configuration = new();
            Configuration.Process(ref configuration);

            Assert.AreEqual(0, configuration.configs.Count);
            Assert.AreEqual(-1, configuration.index);
            Assert.IsFalse(configuration.Enabled);
            Assert.IsFalse(configuration.GetCurrentServer().IsConfigured);
            Assert.IsFalse(configuration.HasConfiguredServer);
        }

        [TestMethod]
        public void LegacyBlankPlaceholderIsRemovedWithoutChangingSelectedRealServer()
        {
            Configuration configuration = new()
            {
                index = 1,
                configs =
                [
                    new Server(),
                    new Server
                    {
                        server = "127.0.0.1",
                        ServerPort = 8388,
                        password = "test",
                        method = Server.DefaultMethod,
                    },
                ],
            };

            Configuration.Process(ref configuration);

            Assert.AreEqual(1, configuration.configs.Count);
            Assert.AreEqual(0, configuration.index);
            Assert.AreEqual("127.0.0.1", configuration.GetCurrentServer().server);
            Assert.IsTrue(configuration.HasConfiguredServer);
        }

        [TestMethod]
        public void IncompleteServerCredentialsAreNotConfigured()
        {
            Server server = new()
            {
                server = "127.0.0.1",
                ServerPort = 8388,
                password = string.Empty,
                method = Server.DefaultMethod,
            };

            Assert.IsFalse(server.IsConfigured);
            server.password = "test";
            Assert.IsTrue(server.IsConfigured);
        }


        [TestMethod]
        public void IncompleteSelectedServerDisablesProxyButPreservesDnsPreference()
        {
            Configuration configuration = new()
            {
                Enabled = true,
                dnsPolicy = new Shadowsocks.Controller.Traffic.DnsPolicyConfig
                {
                    mode = Shadowsocks.Controller.Traffic.DnsPolicyMode.DnsCrypt,
                },
            };
            configuration.configs.Add(new Server
            {
                server = "127.0.0.1",
                ServerPort = 8388,
                password = string.Empty,
            });

            Configuration.Process(ref configuration);

            Assert.IsFalse(configuration.HasConfiguredServer);
            Assert.IsFalse(configuration.Enabled);
            Assert.AreEqual(Shadowsocks.Controller.Traffic.DnsPolicyMode.DnsCrypt, configuration.dnsPolicy.mode);
        }

        [TestMethod]
        [DataRow("System", "System")]
        [DataRow("light", "Light")]
        [DataRow(" DARK ", "Dark")]
        [DataRow("unexpected", "System")]
        public void UiThemeIsNormalized(string input, string expected)
        {
            Configuration configuration = new();
            configuration.uiTheme = input;

            Configuration.Process(ref configuration);

            Assert.AreEqual(expected, configuration.uiTheme);
        }

        [TestMethod]
        public void UpdateCheckAtStartupIsEnabledByDefault()
        {
            Configuration configuration = new();

            Assert.IsTrue(configuration.autoCheckUpdate);
        }

        [TestMethod]
        public void DnsDiagnosticLoggingIsDisabledByDefault()
        {
            Configuration configuration = new();

            Assert.IsFalse(configuration.showDnsLogs);
        }


        [TestMethod]
        public void EnabledUsesLegacyJsonPropertyName()
        {
            Configuration configuration = new() { Enabled = true };

            string json = JsonConvert.SerializeObject(configuration);
            Configuration roundTrip = JsonConvert.DeserializeObject<Configuration>(json);

            StringAssert.Contains(json, "\"enabled\":true");
            Assert.IsFalse(json.Contains("\"Enabled\"", StringComparison.Ordinal));
            Assert.IsNotNull(roundTrip);
            Assert.IsTrue(roundTrip.Enabled);
        }

        [TestMethod]
        public void RealServerIsConfigured()
        {
            Configuration configuration = new();
            configuration.configs.Add(new Server
            {
                server = "127.0.0.1",
                ServerPort = 8388,
                password = "test",
                method = Server.DefaultMethod,
            });

            Configuration.Process(ref configuration);

            Assert.IsTrue(configuration.GetCurrentServer().IsConfigured);
            Assert.IsTrue(configuration.HasConfiguredServer);
        }
    }
}
