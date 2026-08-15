using System;

namespace Shadowsocks.Controller.Traffic
{
    [Serializable]
    public sealed class ApplicationRouteRule
    {
        public bool enabled = true;
        public string application = string.Empty;
        public TrafficRouteAction action = TrafficRouteAction.Proxy;
    }
}
