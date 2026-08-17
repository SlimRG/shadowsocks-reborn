using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Model;

namespace Shadowsocks.UnitTests;

[TestClass]
public sealed class ConfigurationServerNamingTests
{
    [TestMethod]
    public void EnsureServerNames_AssignsUniqueNamesWithoutOverwritingExplicitNames()
    {
        var servers = new List<Server>
        {
            new() { server = "one.example", server_port = 8388, remarks = "Primary" },
            new() { server = "two.example", server_port = 8388 },
            new() { server = "three.example", server_port = 8388, remarks = "Server 1" },
            new() { server = "four.example", server_port = 8388 },
        };

        Configuration.EnsureServerNames(servers);

        Assert.AreEqual("Primary", servers[0].remarks);
        Assert.AreEqual("Server 2", servers[1].remarks);
        Assert.AreEqual("Server 1", servers[2].remarks);
        Assert.AreEqual("Server 3", servers[3].remarks);
    }

    [TestMethod]
    public void EnsureServerNames_DoesNotNameBlankPlaceholderServer()
    {
        var servers = new List<Server> { new() };

        Configuration.EnsureServerNames(servers);

        Assert.AreEqual(string.Empty, servers[0].remarks);
    }
}
