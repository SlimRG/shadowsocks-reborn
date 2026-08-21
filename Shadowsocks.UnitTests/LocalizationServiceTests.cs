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
            "Traffic Routing", "Servers", "Share Server Config", "DNS", "System DNS", "DNSCrypt",
            "DNS Settings", "Check DNSCrypt Update", "DNSCrypt: Not installed", "DNSCrypt: Starting…", "DNSCrypt: Updating…",
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
            string value = service[key];
            Assert.IsFalse(string.IsNullOrWhiteSpace(value), $"Missing Russian tray translation for '{key}'.");
            if (key is "DNS" or "DNSCrypt")
                continue;

            Assert.AreNotEqual(key, value, $"Missing Russian tray translation for '{key}'.");
        }
    }
    [TestMethod]
    public void EmbeddedRussianCatalogCoversDnsCryptManagement()
    {
        var service = new CsvLocalizationService(Shadowsocks.Core.EmbeddedResources.I18nCsv, CultureInfo.GetCultureInfo("ru-RU"));
        string[] keys =
        [
            "DNS", "DNS status", "Active adapters", "Windows DNS servers", "Shadowsocks DNS policy", "Transparent interception",
            "DNS mode", "System DNS", "Direct DNS", "DNS through Shadowsocks", "Custom DoH", "DNSCrypt",
            "System DNS settings", "Direct DNS settings", "DNS through Shadowsocks settings", "Custom DoH settings",
            "Original DNS destination", "DNS provider", "Primary DNS server", "Fallback DNS server", "Route Direct DNS through Shadowsocks",
            "DNSCrypt Proxy", "Installed version", "Latest version", "Local port", "Coverage", "Not installed",
            "Install DNSCrypt Proxy", "Check for DNSCrypt updates", "Update", "Restart", "Reinstall", "Remove",
            "Automatically update DNSCrypt Proxy", "Require DNSSEC", "Require no-log resolvers",
            "Require unfiltered resolvers", "Use IPv6 resolvers", "Route DNSCrypt through Shadowsocks",
            "In Administrator Mode, DNSCrypt always blocks intercepted plaintext DNS while starting, restarting, or unavailable.", "Resolver selection", "Automatic", "Manual selection",
            "Automatically selected resolvers",
            "Available resolvers", "Load resolver list", "Search resolvers",
            "Resolver list is loaded from DNSCrypt Proxy's signed upstream catalog.",
            "Loading resolver list", "Resolver list loaded.", "Loaded {0} upstream resolvers.",
            "Showing {0} matching resolver(s).", "Select at least one resolver.",
            "DNS settings are saved and applied automatically.", "DNS settings applied.", "DNS server settings applied.", "Custom DoH settings applied.",
            "Enter a valid primary IPv4 or IPv6 DNS server address.", "Enter a valid fallback IPv4 or IPv6 DNS server address.",
            "Enter a primary DNS server before configuring a fallback server.",
            "System DNS information is read from active Windows network adapters; Shadowsocks does not change adapter DNS addresses.",
            "Windows DNS servers from active network adapters are used without modification.",
            "Captured DNS queries are sent directly to their original destination and bypass Shadowsocks.",
            "Captured DNS queries use the active Shadowsocks route; no separate resolver endpoint is required.",
            "Direct DNS queries are routed to the selected resolver through Shadowsocks.",
            "DNS queries are sent as DNS-over-HTTPS to the configured endpoint.",
            "Use DNS servers configured by Windows without Shadowsocks DNS redirection.",
            "Send captured DNS traffic directly to its original destination. Transparent enforcement requires Administrator Mode.",
            "Route captured DNS traffic through the active Shadowsocks server. Transparent enforcement requires Administrator Mode.",
            "Convert captured DNS queries to DNS-over-HTTPS using the configured HTTPS endpoint.",
            "Use DNSCrypt Proxy for Shadowsocks-managed DNS and transparent DNS capture when available.",
            "Enter an absolute HTTPS DNS-over-HTTPS endpoint. Changes are applied automatically when you leave the field.",
            "Protocol",
            "All protocols",
            "Automatically select a resolver from DNSCrypt Proxy's signed catalog using the DNSSEC, no-log, unfiltered and address-family settings.",
            "Choose one or more DNSCrypt or DoH resolvers manually from the signed upstream catalog.",
            "Automatic mode selects a filtered resolver from DNSCrypt Proxy's signed catalog.",
            "Automatic filtered selection is active ({0} resolver(s)).",
            "Automatic filtered resolver is starting.",
            "Automatic mode selects a resolver from the signed catalog using the DNSSEC, no-log, unfiltered and address-family settings above.",
            "Filter the signed resolver catalog by transport protocol: DNSCrypt or DNS-over-HTTPS.",
            "Select the DNSCrypt or DoH resolvers DNSCrypt Proxy may use in manual-selection mode. Ping is measured asynchronously; the active resolver is replaced with DNSCrypt Proxy's actual RTT when available.",
            "Choose one or more DNSCrypt resolvers manually from the signed upstream catalog.",
            "Filter resolvers by name, protocol, country code, country name, or description.",
            "Select the resolvers DNSCrypt Proxy may use in manual-selection mode.",
            "Checking latest version",
            "Downloading DNSCrypt Proxy", "Verifying signature", "Validating DNSCrypt runtime",
            "DNSCrypt Proxy is not installed.", "Download and enable", "DNSCrypt operation failed. See Logs for details.",
            "DNS operation failed. See Logs for details.", "Enter a valid HTTPS DoH URL to enable Custom DoH.",
            "Administrator Mode transparently redirects captured UDP/TCP DNS traffic to DNSCrypt. User and Game modes do not provide system-wide interception.",
            "Transparent DNS interception is active for captured applications.",
            "DNS is blocked (fail-closed) because DNSCrypt is unavailable.",
            "DNSCrypt is running, but system-wide interception is paused in Game Mode.",
            "DNSCrypt is running; full DNS interception requires Administrator Mode.",
            "System-wide DNS interception is paused in Game Mode.",
            "DNSCrypt is unavailable; full DNS interception requires Administrator Mode.",
        ];

        foreach (string key in keys)
        {
            string value = service[key];
            Assert.IsFalse(string.IsNullOrWhiteSpace(value), $"Missing Russian DNSCrypt translation for '{key}'.");
            if (key is "DNS" or "DNSCrypt" or "DNSCrypt Proxy")
                continue;

            Assert.AreNotEqual(key, value, $"Missing Russian DNSCrypt translation for '{key}'.");
        }
    }

}
