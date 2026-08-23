using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller.Service;
using Shadowsocks.Model;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    [DoNotParallelize]
    public class Sip003PluginTest
    {
        [TestInitialize]
        public void Initialize()
        {
            PluginManager.PluginsDirectoryOverride = Path.Combine(
                Path.GetTempPath(),
                "Shadowsocks.UnitTests",
                "Sip003",
                Guid.NewGuid().ToString("N"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            string root = PluginManager.PluginsDirectoryOverride;
            PluginManager.PluginsDirectoryOverride = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }

        [TestMethod]
        public void NoPluginReturnsNull()
        {
            Sip003Plugin plugin = Sip003Plugin.CreateIfConfigured(
                new Server
                {
                    server = "192.168.100.1",
                    ServerPort = 8888,
                    password = "test",
                    method = Server.DefaultMethod,
                },
                false);

            Assert.IsNull(plugin);
        }

        [TestMethod]
        public void PluginMustBeInstalledByPluginManager()
        {
            var server = new Server
            {
                server = "192.168.100.1",
                ServerPort = 8888,
                password = "test",
                method = Server.DefaultMethod,
                plugin = "missing-plugin.exe",
            };

            FileNotFoundException exception = Assert.ThrowsExactly<FileNotFoundException>(() =>
                Sip003Plugin.CreateIfConfigured(server, false));

            Assert.AreEqual("missing-plugin.exe", exception.FileName);
        }
    }
}
