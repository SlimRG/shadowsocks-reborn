using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Core.Settings;

namespace Shadowsocks.UnitTests;

[TestClass]
public class AppSettingsTest
{
    [TestMethod]
    public void EmbeddedLoggingSettingsAreAvailable()
    {
        LoggingSettings logging = AppSettings.Logging;

        Assert.IsFalse(string.IsNullOrWhiteSpace(logging.FilePath));
        Assert.IsFalse(string.IsNullOrWhiteSpace(logging.FallbackFilePath));
        Assert.IsFalse(string.IsNullOrWhiteSpace(logging.MinimumLevel));
        Assert.IsFalse(string.IsNullOrWhiteSpace(logging.VerboseMinimumLevel));
        Assert.IsFalse(string.IsNullOrWhiteSpace(logging.Layout));
    }
}
