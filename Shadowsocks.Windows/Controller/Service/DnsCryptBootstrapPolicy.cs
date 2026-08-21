#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Shadowsocks.Controller.Traffic;
using Shadowsocks.Model;

namespace Shadowsocks.Controller.Service
{
    internal static class DnsCryptBootstrapPolicy
    {
        internal static IReadOnlyList<string> GetUnsafeEndpoints(
            Configuration? configuration,
            DnsCryptConfig? dnsCryptConfig = null)
        {
            DnsCryptConfig? effectiveConfig = dnsCryptConfig ?? configuration?.dnsPolicy?.dnsCrypt;
            if (effectiveConfig?.routeThroughShadowsocks != true)
                return Array.Empty<string>();

            var unsafeEndpoints = new List<string>();
            foreach (Server server in configuration?.configs ?? [])
            {
                if (server?.IsConfigured != true)
                    continue;

                string host = server.server?.Trim() ?? string.Empty;
                if (!IPAddress.TryParse(host, out _))
                    unsafeEndpoints.Add(host);
            }

            ForwardProxyConfig? forwardProxy = configuration?.proxy;
            if (forwardProxy?.useProxy == true)
            {
                string proxyHost = forwardProxy.proxyServer?.Trim() ?? string.Empty;
                if (!IPAddress.TryParse(proxyHost, out _))
                    unsafeEndpoints.Add(proxyHost);
            }

            return unsafeEndpoints
                .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(endpoint => endpoint, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        internal static void Validate(
            Configuration? configuration,
            DnsCryptConfig? dnsCryptConfig = null)
        {
            IReadOnlyList<string> unsafeEndpoints = GetUnsafeEndpoints(configuration, dnsCryptConfig);
            if (unsafeEndpoints.Count == 0)
                return;

            throw new InvalidOperationException(
                "Routing DNSCrypt through Shadowsocks requires IP-address Shadowsocks/forward-proxy endpoints " +
                "to prevent a DNS bootstrap recursion loop. Hostname endpoint(s): " +
                string.Join(", ", unsafeEndpoints));
        }
    }
}
