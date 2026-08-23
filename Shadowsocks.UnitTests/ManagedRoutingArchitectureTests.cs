using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller;
using Shadowsocks.Controller.Service;
using Shadowsocks.Model;

namespace Shadowsocks.UnitTests;

[TestClass]
public class ManagedRoutingArchitectureTests
{
    [TestMethod]
    public void LocalPacIsOnlyMinimalManagedProxyFunnel()
    {
        string pac = PACDaemon.GetManagedFunnelPac();

        Assert.AreEqual("function FindProxyForURL(url, host) { return __PROXY__; }\n", pac);
        Assert.IsFalse(pac.Contains("__RULES__", StringComparison.Ordinal));
        Assert.IsFalse(pac.Contains("RegExp", StringComparison.Ordinal));
        Assert.IsFalse(pac.Contains("CombinedMatcher", StringComparison.Ordinal));
        Assert.IsFalse(pac.Contains("RegExpFilter", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LocalManagedSnapshotAlwaysUsesCSharpAuthority()
    {
        var configuration = new Configuration
        {
            Enabled = true,
            global = false,
            useOnlinePac = false,
        };

        var snapshot = ManagedRoutingSnapshotBuilder.Build(configuration);

        Assert.IsTrue(snapshot.IsEnabled);
        Assert.IsFalse(snapshot.Source.Contains("compatibility", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(snapshot.Source.Contains("JavaScript", StringComparison.OrdinalIgnoreCase));
    }
}
