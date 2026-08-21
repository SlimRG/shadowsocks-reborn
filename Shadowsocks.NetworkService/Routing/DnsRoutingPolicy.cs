using Shadowsocks.NetworkService.Ipc;

namespace Shadowsocks.NetworkService.Routing;

internal enum DnsRouteDecision
{
    Direct = 0,
    Redirect = 1,
    Block = 2,
    Proxy = 3,
}

internal static class DnsRoutingPolicy
{
    public static DnsRouteDecision Evaluate(DnsPolicyDto? policy, bool dnsInterceptionExempt)
    {
        if (dnsInterceptionExempt || policy is null)
            return DnsRouteDecision.Direct;

        return policy.Mode switch
        {
            DnsPolicyMode.System => DnsRouteDecision.Direct,
            DnsPolicyMode.Direct => policy.DirectDnsReady
                ? DnsRouteDecision.Redirect
                : policy.DirectDnsRouteThroughShadowsocks ? DnsRouteDecision.Proxy : DnsRouteDecision.Direct,
            DnsPolicyMode.Proxy => DnsRouteDecision.Proxy,
            DnsPolicyMode.CustomDoh => policy.CustomDohReady
                ? DnsRouteDecision.Redirect
                : policy.FailClosed ? DnsRouteDecision.Block : DnsRouteDecision.Direct,
            // DNSCrypt is always fail-closed. Falling back to Direct here would leak
            // plaintext DNS during startup/restart windows and contradict the selected mode.
            DnsPolicyMode.DnsCrypt => policy.DnsCryptReady
                ? DnsRouteDecision.Redirect
                : DnsRouteDecision.Block,
            _ => DnsRouteDecision.Direct,
        };
    }
}
