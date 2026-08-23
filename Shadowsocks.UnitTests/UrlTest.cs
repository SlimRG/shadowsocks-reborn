using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Shadowsocks.Model;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class UrlTest
    {
        private const string BaseUserInfo = "Y2hhY2hhMjAtaWV0Zi1wb2x5MTMwNTp0ZXN0";

        [TestMethod]
        public void Sip002UrlRoundTripsWithoutPlugin()
        {
            var expected = new Server
            {
                server = "192.168.100.1",
                ServerPort = 8888,
                password = "test",
                method = Server.DefaultMethod,
                remarks = "example-server 1",
            };

            string url = $"ss://{BaseUserInfo}@192.168.100.1:8888/#example-server+1";
            Server actual = Server.ParseURL(url);

            AssertServerEquals(expected, actual);
            Assert.IsTrue(actual.importedFromUrl);
            Assert.AreEqual(url, actual.GetURL());
        }

        [TestMethod]
        public void Sip002UrlRoundTripsWithPlugin()
        {
            var expected = new Server
            {
                server = "192.168.1.1",
                ServerPort = 8388,
                password = "test",
                method = Server.DefaultMethod,
                plugin = "v2ray-plugin",
                PluginOptions = "mode=websocket;host=example.com",
            };

            string url = $"ss://{BaseUserInfo}@192.168.1.1:8388/?plugin=v2ray-plugin%3bmode%3dwebsocket%3bhost%3dexample.com";
            Server actual = Server.ParseURL(url);

            AssertServerEquals(expected, actual);
            Assert.AreEqual(url, actual.GetURL());
        }


        [TestMethod]
        public void ServerUsesStableJsonPropertyNames()
        {
            Server server = new()
            {
                ServerPort = 8388,
                PluginOptions = "mode=websocket",
                PluginArguments = "--fast-open",
            };

            string json = JsonConvert.SerializeObject(server);
            Server roundTrip = JsonConvert.DeserializeObject<Server>(json);

            StringAssert.Contains(json, "\"server_port\":8388");
            StringAssert.Contains(json, "\"plugin_opts\":\"mode=websocket\"");
            StringAssert.Contains(json, "\"plugin_args\":\"--fast-open\"");
            Assert.IsFalse(json.Contains("\"ServerPort\"", StringComparison.Ordinal));
            Assert.IsFalse(json.Contains("\"PluginOptions\"", StringComparison.Ordinal));
            Assert.IsFalse(json.Contains("\"PluginArguments\"", StringComparison.Ordinal));
            Assert.IsNotNull(roundTrip);
            Assert.AreEqual(8388, roundTrip.ServerPort);
            Assert.AreEqual("mode=websocket", roundTrip.PluginOptions);
            Assert.AreEqual("--fast-open", roundTrip.PluginArguments);
        }

        [TestMethod]
        public void MultipleSip002UrlsAreParsed()
        {
            string first = $"ss://{BaseUserInfo}@192.168.100.1:8888/";
            string second = $"ss://{BaseUserInfo}@192.168.1.1:8388/";

            var servers = Server.GetServers(first + "\r\n" + second);

            Assert.AreEqual(2, servers.Count);
            Assert.AreEqual("192.168.100.1", servers[0].server);
            Assert.AreEqual("192.168.1.1", servers[1].server);
        }

        [TestMethod]
        public void PreSip002UrlIsRejected()
        {
            Assert.IsNull(Server.ParseURL("ss://YmYtY2ZiOnRlc3RAMTkyLjE2OC4xMDAuMTo4ODg4"));
        }

        private static void AssertServerEquals(Server expected, Server actual)
        {
            Assert.IsNotNull(actual);
            Assert.AreEqual(expected.server, actual.server);
            Assert.AreEqual(expected.ServerPort, actual.ServerPort);
            Assert.AreEqual(expected.password, actual.password);
            Assert.AreEqual(expected.method, actual.method);
            Assert.AreEqual(expected.plugin, actual.plugin);
            Assert.AreEqual(expected.PluginOptions, actual.PluginOptions);
            Assert.AreEqual(expected.remarks, actual.remarks);
        }
    }
}
