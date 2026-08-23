using System;
using Newtonsoft.Json;

namespace Shadowsocks.Controller.Traffic
{
    [Serializable]
    public sealed class ApplicationRouteRule
    {
        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonProperty("application")]
        public string Application { get; set; } = string.Empty;

        [JsonProperty("action")]
        public TrafficRouteAction Action { get; set; } = TrafficRouteAction.Proxy;
    }
}
