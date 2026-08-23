using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class ProtocolHandlerTests
    {
        [TestMethod]
        public void ShellOpenCommandQuotesExecutableAndProtocolUrl()
        {
            const string executablePath = @"C:\Users\Example User\My Apps\Shadowsocks.exe";

            string command = ProtocolHandler.BuildShellOpenCommand(executablePath);

            Assert.AreEqual(
                "\"C:\\Users\\Example User\\My Apps\\Shadowsocks.exe\" --open-url \"%1\"",
                command);
        }
    }
}
