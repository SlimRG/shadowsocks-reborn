using System;

namespace Shadowsocks.Controller.Traffic
{
    /// <summary>
    /// DNS routing contract. The current revision persists and exposes policy only;
    /// DNS interception/resolution is intentionally implemented in a later stage.
    /// </summary>
    [Serializable]
    public sealed class DnsPolicyConfig
    {
        public DnsPolicyMode mode = DnsPolicyMode.System;
        public string customDohUrl = string.Empty;
    }
}
