using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Shadowsocks.Controller;

namespace Shadowsocks.Model
{
    [Serializable]
    public class Server
    {
        public const string DefaultMethod = "chacha20-ietf-poly1305";
        public const int DefaultPort = 8388;

        private const int DefaultServerTimeoutSec = 5;
        public const int MaxServerTimeoutSec = 20;

        public string server { get; set; }
        [JsonProperty("server_port")]
        public int ServerPort { get; set; }
        public string password { get; set; }
        public string method { get; set; }
        // optional fields
        [DefaultValue("")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        public string plugin { get; set; }
        [DefaultValue("")]
        [JsonProperty("plugin_opts", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        public string PluginOptions { get; set; }
        [DefaultValue("")]
        [JsonProperty("plugin_args", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        public string PluginArguments { get; set; }
        [DefaultValue("")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        public string remarks { get; set; }

        [DefaultValue("")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        public string group { get; set; }

        public int timeout { get; set; }

        // Credentials originating from a link/subscription stay reveal-protected in the UI.
        // The flag is persisted so a direct SIP002 import remains protected after restart.
        [DefaultValue(false)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        public bool importedFromUrl { get; set; }

        [JsonIgnore]
        public bool IsConfigured => !string.IsNullOrWhiteSpace(server)
            && ServerPort > 0
            && ServerPort <= 65535
            && !string.IsNullOrEmpty(password)
            && !string.IsNullOrWhiteSpace(method)
            && timeout > 0
            && timeout <= MaxServerTimeoutSec;

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(server ?? string.Empty) ^ ServerPort;
        }

        public override bool Equals(object obj) => obj is Server o2 && server == o2.server && ServerPort == o2.ServerPort;

        public override string ToString()
        {
            if (string.IsNullOrEmpty(server))
            {
                return I18N.GetString("New server");
            }

            string serverStr = $"{FormalHostName}:{ServerPort}";
            return string.IsNullOrEmpty(remarks)
                ? serverStr
                : $"{remarks} ({serverStr})";
        }

        public string GetURL()
        {
            string parts = $"{method}:{password}";
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(parts));
            string websafeBase64 = base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');

            string url = $"{websafeBase64}@{FormalHostName}:{ServerPort}/";

            if (!string.IsNullOrWhiteSpace(plugin))
            {
                string pluginPart = plugin;
                if (!string.IsNullOrWhiteSpace(PluginOptions))
                {
                    pluginPart += ";" + PluginOptions;
                }
                url += "?plugin=" + UrlEncodeShareComponent(pluginPart);
            }

            string tag = string.IsNullOrEmpty(remarks)
                ? string.Empty
                : $"#{UrlEncodeShareComponent(remarks)}";
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
            ServerPort = DefaultPort;
            method = DefaultMethod;
            plugin = "";
            PluginOptions = "";
            PluginArguments = "";
            password = "";
            remarks = "";
            timeout = DefaultServerTimeoutSec;
        }

        public static Server ParseURL(string serverUrl)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                return null;
            }

            string normalized = serverUrl.Trim();
            if (!normalized.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            Uri parsedUrl;
            try
            {
                parsedUrl = new Uri(normalized);
            }
            catch (UriFormatException)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(parsedUrl.IdnHost) || parsedUrl.Port <= 0)
            {
                return null;
            }

            string rawUserInfo = parsedUrl.GetComponents(UriComponents.UserInfo, UriFormat.Unescaped);
            string base64 = rawUserInfo.Replace('-', '+').Replace('_', '/');
            string userInfo;
            try
            {
                userInfo = Encoding.UTF8.GetString(Convert.FromBase64String(
                    base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=')));
            }
            catch (FormatException)
            {
                return null;
            }

            string[] userInfoParts = userInfo.Split(':', 2);
            if (userInfoParts.Length != 2)
            {
                return null;
            }

            var server = new Server
            {
                remarks = WebUtility.UrlDecode(parsedUrl.GetComponents(UriComponents.Fragment, UriFormat.Unescaped)),
                server = parsedUrl.IdnHost,
                ServerPort = parsedUrl.Port,
                method = userInfoParts[0],
                password = userInfoParts[1],
                importedFromUrl = true,
            };

            string pluginValue = GetQueryParameter(parsedUrl, "plugin") ?? string.Empty;
            string[] pluginParts = pluginValue.Split(';', 2);
            server.plugin = pluginParts.Length > 0 ? pluginParts[0] ?? string.Empty : string.Empty;
            server.PluginOptions = pluginParts.Length > 1 ? pluginParts[1] ?? string.Empty : string.Empty;
            return server;
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
            return server + ':' + ServerPort;
        }
    }
}
