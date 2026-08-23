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
        public DnsPolicyMode mode { get; set; } = DnsPolicyMode.System;
        public string directDnsServer { get; set; } = string.Empty;
        public string directDnsFallbackServer { get; set; } = string.Empty;
        public bool directDnsRouteThroughShadowsocks { get; set; }
        public string customDohUrl { get; set; } = string.Empty;
        public bool customDohRouteThroughShadowsocks { get; set; }
        public DnsCryptConfig dnsCrypt { get; set; } = new();
    }

    [Serializable]
    public sealed class DnsCryptConfig
    {
        public bool autoUpdate { get; set; } = true;
        public bool requireDnssec { get; set; } = true;
        public bool requireNoLog { get; set; } = true;
        public bool requireNoFilter { get; set; } = true;
        public bool ipv4Servers { get; set; } = true;
        public bool ipv6Servers { get; set; }
        public bool routeThroughShadowsocks { get; set; } = true;
        // Explicit resolver selection mode. Automatic mode keeps serverNames runtime-managed.
        public bool automaticResolvers { get; set; } = true;
        public bool failClosed { get; set; } = true;
        public List<string> serverNames { get; set; } = new();
    }
}
