using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Localization;

namespace Shadowsocks.UnitTests;

[TestClass]
public class LocalizationServiceTests
{
    private const string Csv = "en,ru-RU,zh-CN,zh-TW,ja,ko,fr\r\n"
        + "Hello,Привет,,,,,\r\n"
        + "Port {0},Порт {0},,,,,\r\n";

    [TestMethod]
    public void UsesRequestedLocale()
    {
        var service = new CsvLocalizationService(Csv, CultureInfo.GetCultureInfo("ru-RU"));

        Assert.AreEqual("Привет", service["Hello"]);
        Assert.AreEqual("Порт 1080", service.Format("Port {0}", 1080));
    }

    [TestMethod]
    public void FallsBackToSameLanguageRegion()
    {
        var service = new CsvLocalizationService(Csv, CultureInfo.GetCultureInfo("ru-UA"));

        Assert.AreEqual("Привет", service["Hello"]);
    }

    [TestMethod]
    public void MissingTranslationFallsBackToEnglishKey()
    {
        var service = new CsvLocalizationService(Csv, CultureInfo.GetCultureInfo("fr-FR"));

        Assert.AreEqual("Hello", service["Hello"]);
        Assert.AreEqual("Unknown", service["Unknown"]);
    }


    [TestMethod]
    public void EmbeddedCatalogHasCompleteTranslations()
    {
        string csv = Shadowsocks.Core.EmbeddedResources.I18nCsv;
        string[] locales = ["ru-RU", "zh-CN", "zh-TW", "ja", "ko", "fr"];
        string[][] rows = ParseCsv(csv)
            .Where(row => row.Length >= 7 && !string.IsNullOrWhiteSpace(row[0]) && !row[0].TrimStart().StartsWith('#'))
            .ToArray();

        foreach (string[] row in rows)
        {
            for (int i = 0; i < locales.Length; i++)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(row[i + 1]), $"Missing {locales[i]} translation for '{row[0]}'.");
            }
        }
    }

    private static IEnumerable<string[]> ParseCsv(string csv)
    {
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < csv.Length; i++)
        {
            char c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                {
                    i++;
                }
                row.Add(field.ToString());
                field.Clear();
                if (row.Count > 1 || row.Any(value => value.Length > 0))
                {
                    yield return row.ToArray();
                }
                row.Clear();
            }
            else
            {
                field.Append(c);
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row.ToArray();
        }
    }


    [TestMethod]
    public void EmbeddedRussianCatalogCoversPluginManagement()
    {
        var service = new CsvLocalizationService(Shadowsocks.Core.EmbeddedResources.I18nCsv, CultureInfo.GetCultureInfo("ru-RU"));
        string[] pluginKeys =
        [
            "Plugins", "Plugin", "Add plugin", "Choose a plugin", "Install", "Import ZIP/TAR.GZ",
            "Installed plugins", "No plugins installed.", "Plugin removed.",
            "Install and manage SIP003 plugins.",
            "Install SIP003 plugins from the built-in list or import a plugin ZIP or TAR.GZ package.",
            "Optional SIP003 plugin installed on the Plugins page.",
            "Choose a supported Windows x64 SIP003 plugin to install.",
            "Download the latest Windows x64 release archive and install the selected plugin.",
            "Import a local ZIP or TAR.GZ package containing a Windows plugin executable.",
            "Remove this installed plugin from Shadowsocks storage.",
            "{0} installed.",
        ];

        foreach (string key in pluginKeys)
        {
            Assert.AreNotEqual(key, service[key], $"Missing Russian plugin translation for '{key}'.");
        }
    }

    [TestMethod]
    public void EmbeddedRussianCatalogCoversTrayCommands()
    {
        var service = new CsvLocalizationService(Shadowsocks.Core.EmbeddedResources.I18nCsv, CultureInfo.GetCultureInfo("ru-RU"));
        string[] trayKeys =
        [
            "System Proxy", "Disable", "PAC", "Global", "Traffic Mode", "User Mode", "Admin Mode",
            "Traffic Routing", "Servers", "Share Server Config",
            "Local PAC", "Online PAC", "Edit Local PAC File",
            "Update Local PAC from Geosite", "GeoSite Sources", "Edit User Rule for Geosite", "Require secret for local PAC URL",
            "Regenerate Local PAC after application updates", "Edit Online PAC URL",
            "Forward Proxy", "Online Config", "Start on Boot", "Associate ss:// Links",
            "Allow other devices to connect", "Hotkeys", "Help", "Logs", "Updates",
            "Check for Updates", "Include prerelease versions", "About", "Quit",
            "More than 20 servers (total: {0})",
        ];

        foreach (string key in trayKeys)
        {
            Assert.AreNotEqual(key, service[key], $"Missing Russian tray translation for '{key}'.");
        }
    }
}
