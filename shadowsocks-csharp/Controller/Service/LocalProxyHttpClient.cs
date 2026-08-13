using System;
using System.Net;
using System.Net.Http;
using Shadowsocks.Model;

namespace Shadowsocks.Controller
{
    /// <summary>
    /// Creates HTTP clients that explicitly use the local Shadowsocks HTTP endpoint.
    /// The request enters the mixed local listener, is forwarded to Privoxy and then
    /// leaves through the currently selected Shadowsocks server.
    /// </summary>
    internal static class LocalProxyHttpClient
    {
        public static HttpClient Create(Configuration config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://{config.LocalHost}:{config.localPort}"),
                UseProxy = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30),
            };

            if (!string.IsNullOrWhiteSpace(config.userAgentString))
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", config.userAgentString);

            return client;
        }
    }
}
