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
            new() { server = "one.example", ServerPort = 8388, password = "test", remarks = "Primary" },
            new() { server = "two.example", ServerPort = 8388, password = "test" },
            new() { server = "three.example", ServerPort = 8388, password = "test", remarks = "Server 1" },
            new() { server = "four.example", ServerPort = 8388, password = "test" },
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
