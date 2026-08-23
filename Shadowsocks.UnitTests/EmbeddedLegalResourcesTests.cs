using System;
using System.Globalization;
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
            "Adblock Plus-derived network routing",
        ];

        foreach (string component in required)
        {
            StringAssert.Contains(text, component);
        }
    }


    [TestMethod]
    public void LegalViewerUsesSelectedUiCultureAndPreservesAuthoritativeOriginalTerms()
    {
        (string Culture, string LicensePrefix, string NoticesPrefix)[] cases =
        [
            ("en-US", "Shadowsocks Reborn License", "Third-party component notices"),
            ("ru-RU", "Лицензия Shadowsocks Reborn", "Уведомления сторонних компонентов"),
            ("zh-CN", "Shadowsocks Reborn 许可证", "第三方组件声明"),
            ("zh-TW", "Shadowsocks Reborn 授權條款", "第三方元件聲明"),
            ("ja-JP", "Shadowsocks Reborn ライセンス", "サードパーティコンポーネント通知"),
            ("ko-KR", "Shadowsocks Reborn 라이선스", "서드파티 구성 요소 고지"),
            ("fr-FR", "Licence de Shadowsocks Reborn", "Mentions relatives aux composants tiers"),
        ];

        foreach ((string cultureName, string licensePrefix, string noticesPrefix) in cases)
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
            string license = EmbeddedResources.LocalizedProductLicense(culture);
            string notices = EmbeddedResources.LocalizedThirdPartyNotices(culture);

            Assert.IsTrue(license.StartsWith(licensePrefix, StringComparison.Ordinal), cultureName);
            Assert.IsTrue(notices.StartsWith(noticesPrefix, StringComparison.Ordinal), cultureName);
            StringAssert.Contains(license, "GNU GENERAL PUBLIC LICENSE");
            StringAssert.Contains(license, "Version 3, 29 June 2007");
            StringAssert.Contains(notices, "Bouncy Castle Cryptography for .NET");
            StringAssert.Contains(notices, "Adblock Plus-derived network routing");
            Assert.IsFalse(notices.StartsWith("# Third-party notices", StringComparison.Ordinal));
        }
    }

}
