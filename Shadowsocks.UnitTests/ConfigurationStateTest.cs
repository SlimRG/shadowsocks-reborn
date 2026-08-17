using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Model;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class ConfigurationStateTest
    {
        [TestMethod]
        public void EmptyPlaceholderServerIsNotConfigured()
        {
            Configuration configuration = new();
            Configuration.Process(ref configuration);

            Assert.AreEqual(1, configuration.configs.Count);
            Assert.IsFalse(configuration.GetCurrentServer().IsConfigured);
            Assert.IsFalse(configuration.HasConfiguredServer);
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
        public void RealServerIsConfigured()
        {
            Configuration configuration = new();
            configuration.configs.Add(new Server
            {
                server = "127.0.0.1",
                server_port = 8388,
                password = "test",
                method = Server.DefaultMethod,
            });

            Configuration.Process(ref configuration);

            Assert.IsTrue(configuration.GetCurrentServer().IsConfigured);
            Assert.IsTrue(configuration.HasConfiguredServer);
        }
    }
}
