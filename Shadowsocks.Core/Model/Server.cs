using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Shadowsocks.Controller;

namespace Shadowsocks.Model
{
    [Serializable]
    public class Server
    {
        public const string DefaultMethod = "chacha20-ietf-poly1305";
        public const int DefaultPort = 8388;

        #region ParseLegacyURL
        private static readonly Regex UrlFinder = new Regex(@"ss://(?<base64>[A-Za-z0-9+-/=_]+)(?:#(?<tag>\S+))?", RegexOptions.IgnoreCase);
        private static readonly Regex DetailsParser = new Regex(@"^((?<method>.+?):(?<password>.*)@(?<hostname>.+?):(?<port>\d+?))$", RegexOptions.IgnoreCase);
        #endregion ParseLegacyURL

        private const int DefaultServerTimeoutSec = 5;
        public const int MaxServerTimeoutSec = 20;

        public string server;
        public int server_port;
        public string password;
        public string method;
        // optional fields
        [DefaultValue("")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        public string plugin;
        [DefaultValue("")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        public string plugin_opts;
        [DefaultValue("")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        public string plugin_args;
        [DefaultValue("")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        public string remarks;

        [DefaultValue("")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        public string group;

        public int timeout;

        // Set to true when imported from a legacy ss:// URL.
        public bool warnLegacyUrl;

        // Credentials originating from a link/subscription stay reveal-protected in the UI.
        // The flag is persisted so a direct SIP002 import remains protected after restart.
        [DefaultValue(false)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        public bool importedFromUrl;

        [JsonIgnore]
        public bool IsConfigured => !string.IsNullOrWhiteSpace(server) && server_port > 0 && server_port <= 65535;

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(server ?? string.Empty) ^ server_port;
        }

        public override bool Equals(object obj) => obj is Server o2 && server == o2.server && server_port == o2.server_port;

        public override string ToString()
        {
            if (string.IsNullOrEmpty(server))
            {
                return I18N.GetString("New server");
            }

            string serverStr = $"{FormalHostName}:{server_port}";
            return string.IsNullOrEmpty(remarks)
                ? serverStr
                : $"{remarks} ({serverStr})";
        }

        public string GetURL(bool legacyUrl = false)
        {
            string tag = string.Empty;
            string url = string.Empty;

            if (legacyUrl && string.IsNullOrWhiteSpace(plugin))
            {
                // For backwards compatiblity, if no plugin, use old url format
                string parts = $"{method}:{password}@{server}:{server_port}";
                string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(parts));
                url = base64;
            }
            else
            {
                // SIP002
                string parts = $"{method}:{password}";
                string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(parts));
                string websafeBase64 = base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');

                url = string.Format(
                    "{0}@{1}:{2}/",
                    websafeBase64,
                    FormalHostName,
                    server_port
                    );

                if (!string.IsNullOrWhiteSpace(plugin))
                {

                    string pluginPart = plugin;
                    if (!string.IsNullOrWhiteSpace(plugin_opts))
                    {
                        pluginPart += ";" + plugin_opts;
                    }
                    string pluginQuery = "?plugin=" + UrlEncodeShareComponent(pluginPart);
                    url += pluginQuery;
                }
            }

            if (!string.IsNullOrEmpty(remarks))
            {
                tag = $"#{UrlEncodeShareComponent(remarks)}";
            }
            return $"ss://{url}{tag}";
        }

        private static string UrlEncodeShareComponent(string value)
        {
            string encoded = WebUtility.UrlEncode(value);
            int percentIndex = encoded.IndexOf('%');
            if (percentIndex < 0)
            {
                return encoded;
            }

            char[] chars = encoded.ToCharArray();
            for (int i = percentIndex; i + 2 < chars.Length; i++)
            {
                if (chars[i] != '%')
                {
                    continue;
                }

                // Keep the historical Shadowsocks-Windows share-URL form.
                // Modern .NET emits uppercase hexadecimal digits in percent escapes,
                // while existing links/tests use lowercase escapes (%3b, %3d, ...).
                chars[i + 1] = char.ToLowerInvariant(chars[i + 1]);
                chars[i + 2] = char.ToLowerInvariant(chars[i + 2]);
                i += 2;
            }

            return new string(chars);
        }

        [JsonIgnore]
        public string FormalHostName
        {
            get
            {
                // CheckHostName() won't do a real DNS lookup
                switch (Uri.CheckHostName(server))
                {
                    case UriHostNameType.IPv6:  // Add square bracket when IPv6 (RFC3986)
                        return $"[{server}]";
                    default:    // IPv4 or domain name
                        return server;
                }
            }
        }

        public Server()
        {
            server = "";
            server_port = DefaultPort;
            method = DefaultMethod;
            plugin = "";
            plugin_opts = "";
            plugin_args = "";
            password = "";
            remarks = "";
            timeout = DefaultServerTimeoutSec;
        }

        private static Server ParseLegacyURL(string ssURL)
        {
            var match = UrlFinder.Match(ssURL);
            if (!match.Success)
                return null;

            Server server = new Server();
            var base64 = match.Groups["base64"].Value.TrimEnd('/');
            var tag = match.Groups["tag"].Value;
            if (!string.IsNullOrEmpty(tag))
            {
                server.remarks = WebUtility.UrlDecode(tag);
            }
            Match details = null;
            try
            {
                details = DetailsParser.Match(Encoding.UTF8.GetString(Convert.FromBase64String(
                base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='))));
            }
            catch (FormatException)
            {
                return null;
            }
            if (!details.Success)
                return null;
            server.method = details.Groups["method"].Value;
            server.password = details.Groups["password"].Value;
            server.server = details.Groups["hostname"].Value;
            server.server_port = int.Parse(details.Groups["port"].Value);
            server.warnLegacyUrl = true;
            server.importedFromUrl = true;
            return server;
        }

        public static Server ParseURL(string serverUrl)
        {
            string _serverUrl = serverUrl.Trim();
            if (!_serverUrl.StartsWith("ss://", StringComparison.InvariantCultureIgnoreCase))
            {
                return null;
            }

            Server legacyServer = ParseLegacyURL(serverUrl);
            if (legacyServer != null)   //legacy
            {
                return legacyServer;
            }
            else   //SIP002
            {
                Uri parsedUrl;
                try
                {
                    parsedUrl = new Uri(serverUrl);
                }
                catch (UriFormatException)
                {
                    return null;
                }
                Server server = new Server
                {
                    remarks = WebUtility.UrlDecode(parsedUrl.GetComponents(
                        UriComponents.Fragment, UriFormat.Unescaped)),
                    server = parsedUrl.IdnHost,
                    server_port = parsedUrl.Port,
                };

                // parse base64 UserInfo
                string rawUserInfo = parsedUrl.GetComponents(UriComponents.UserInfo, UriFormat.Unescaped);
                string base64 = rawUserInfo.Replace('-', '+').Replace('_', '/');    // Web-safe base64 to normal base64
                string userInfo = "";
                try
                {
                    userInfo = Encoding.UTF8.GetString(Convert.FromBase64String(
                    base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=')));
                }
                catch (FormatException)
                {
                    return null;
                }
                string[] userInfoParts = userInfo.Split(new char[] { ':' }, 2);
                if (userInfoParts.Length != 2)
                {
                    return null;
                }
                server.method = userInfoParts[0];
                server.password = userInfoParts[1];
                server.importedFromUrl = true;

                string[] pluginParts = (GetQueryParameter(parsedUrl, "plugin") ?? "").Split(new[] { ';' }, 2);
                if (pluginParts.Length > 0)
                {
                    server.plugin = pluginParts[0] ?? "";
                }

                if (pluginParts.Length > 1)
                {
                    server.plugin_opts = pluginParts[1] ?? "";
                }

                return server;
            }
        }

        private static string GetQueryParameter(Uri uri, string name)
        {
            string query = uri?.Query;
            if (string.IsNullOrEmpty(query)) return null;

            foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = pair.Split('=', 2);
                string key = WebUtility.UrlDecode(parts[0]);
                if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) continue;
                return parts.Length == 2 ? WebUtility.UrlDecode(parts[1]) : string.Empty;
            }

            return null;
        }

        public static List<Server> GetServers(string ssURL)
        {
            return ssURL
                .Split('\r', '\n', ' ')
                .Select(u => ParseURL(u))
                .Where(s => s != null)
                .ToList();
        }

        public string Identifier()
        {
            return server + ':' + server_port;
        }
    }
}
