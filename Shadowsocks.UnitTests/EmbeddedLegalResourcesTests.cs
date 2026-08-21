using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Core;

namespace Shadowsocks.UnitTests;

[TestClass]
public class EmbeddedLegalResourcesTests
{
    [TestMethod]
    public void ProductLicenseIsEmbeddedAndCurrent()
    {
        string text = EmbeddedResources.ProductLicense;

        StringAssert.Contains(text, "Shadowsocks Reborn");
        StringAssert.Contains(text, "GNU GENERAL PUBLIC LICENSE");
        StringAssert.Contains(text, "Version 3, 29 June 2007");
        StringAssert.Contains(text, "GPL-3.0-or-later");
        Assert.IsFalse(text.Contains("Privoxy", System.StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(text.Contains("mbed TLS", System.StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ThirdPartyNoticesAreEmbeddedAndCoverRuntimeDependencies()
    {
        string text = EmbeddedResources.ThirdPartyNotices;
        string[] required =
        [
            "Bouncy Castle Cryptography for .NET",
            "Google.Protobuf",
            "Newtonsoft.Json",
            "NLog",
            "WinUIEx",
            "ZXing.Net",
            "System.Management",
            "System.Drawing.Common",
            "Windows App SDK",
            "dnscrypt-proxy",
            "WinDivert",
            "ByteCircularBuffer",
            "Adblock Plus-derived PAC helper",
        ];

        foreach (string component in required)
        {
            StringAssert.Contains(text, component);
        }
    }
}
