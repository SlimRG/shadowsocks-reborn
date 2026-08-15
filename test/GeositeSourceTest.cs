using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller;
using Shadowsocks.Controller.Service;
using Shadowsocks.Model;
using System.Collections.Generic;

namespace Shadowsocks.Test
{
    [TestClass]
    public class GeositeSourceTest
    {
        [TestMethod]
        public void ChecksumUrlIsAdjacentToDatabase()
        {
            const string source = "https://raw.githubusercontent.com/runetfreedom/russia-blocked-geosite/release/geosite.dat";
            Assert.AreEqual(source + ".sha256sum", GeositeUpdater.GetChecksumUrl(source));
        }

        [TestMethod]
        public void GeositeSourcesAreTrimmedAndDeduplicated()
        {
            var sources = Configuration.NormalizeGeositeSourceList(new List<string>
            {
                " https://example.com/a.dat ",
                "https://example.com/a.dat",
                "https://example.com/b.dat",
            });

            CollectionAssert.AreEqual(new List<string>
            {
                "https://example.com/a.dat",
                "https://example.com/b.dat",
            }, sources);
        }
    }
}
