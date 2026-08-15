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
