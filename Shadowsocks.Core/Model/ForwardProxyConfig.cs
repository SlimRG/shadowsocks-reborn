using System;

namespace Shadowsocks.Model
{
    [Serializable]
    public class ForwardProxyConfig
    {
        public const int ProxySocks5 = 0;
        public const int ProxyHttp = 1;

        public const int MaxProxyTimeoutSec = 10;
        private const int DefaultProxyTimeoutSec = 3;

        public bool useProxy { get; set; }
        public int proxyType { get; set; }
        public string proxyServer { get; set; }
        public int proxyPort { get; set; }
        public int proxyTimeout { get; set; }
        public bool useAuth { get; set; }
        public string authUser { get; set; }
        public string authPwd { get; set; }

        public ForwardProxyConfig()
        {
            useProxy = false;
            proxyType = ProxySocks5;
            proxyServer = "";
            proxyPort = 0;
            proxyTimeout = DefaultProxyTimeoutSec;
            useAuth = false;
            authUser = "";
            authPwd = "";
        }

        public void CheckConfig()
        {
            if (proxyType < ProxySocks5 || proxyType > ProxyHttp)
            {
                proxyType = ProxySocks5;
            }
        }
    }
}
