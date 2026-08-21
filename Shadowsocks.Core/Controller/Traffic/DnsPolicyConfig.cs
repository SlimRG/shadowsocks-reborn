using System;
using System.Collections.Generic;

namespace Shadowsocks.Controller.Traffic
{
    /// <summary>
    /// DNS routing contract. DNSCrypt runtime configuration is persisted here;
    /// Admin Mode consumes the live DNSCrypt port/PID and managed component root through the NetworkService IPC.
    /// </summary>
    [Serializable]
    public sealed class DnsPolicyConfig
    {
        public DnsPolicyMode mode = DnsPolicyMode.System;
        public string directDnsServer = string.Empty;
        public string directDnsFallbackServer = string.Empty;
        public bool directDnsRouteThroughShadowsocks = false;
        public string customDohUrl = string.Empty;
        public bool customDohRouteThroughShadowsocks = false;
        public DnsCryptConfig dnsCrypt = new();
    }

    [Serializable]
    public sealed class DnsCryptConfig
    {
        public bool autoUpdate = true;
        public bool requireDnssec = true;
        public bool requireNoLog = true;
        public bool requireNoFilter = false;
        public bool ipv4Servers = true;
        public bool ipv6Servers = false;
        public bool routeThroughShadowsocks = false;
        // Explicit resolver selection mode. Automatic mode keeps serverNames runtime-managed.
        public bool automaticResolvers = true;
        public bool failClosed = true;
        public List<string> serverNames = new();
    }
}
