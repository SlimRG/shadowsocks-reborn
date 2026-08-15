using Shadowsocks.Encryption;
using Shadowsocks.Controller.Service;
using Shadowsocks.Model;
using Shadowsocks.Util;
using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NLog;

namespace Shadowsocks.Controller
{
    public class PACServer : Listener.Service
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public const string RESOURCE_NAME = "pac";

        private string PacSecret
        {
            get
            {
                if (string.IsNullOrEmpty(_cachedPacSecret))
                {
                    var rd = new byte[32];
                    RNG.GetBytes(rd);
                    _cachedPacSecret = HttpServerUtilityUrlToken.Encode(rd);
                }
                return _cachedPacSecret;
            }
        }

        private string _cachedPacSecret = "";
        public string PacUrl { get; private set; } = "";

        private Configuration _config;
        private readonly PACDaemon _pacDaemon;

        public PACServer(PACDaemon pacDaemon)
        {
            _pacDaemon = pacDaemon;
        }

        public void UpdatePACURL(Configuration config)
        {
            _config = config;
            string usedSecret = _config.secureLocalPac ? $"&secret={PacSecret}" : "";
            string contentHash = GetHash(GetContentForHash());
            PacUrl = $"http://{config.LocalHost}:{config.localPort}/{RESOURCE_NAME}?hash={contentHash}{usedSecret}";
            logger.Debug("Set PAC URL:" + PacUrl);
        }

        private string GetContentForHash()
        {
            if (UseOnlinePac())
            {
                if (OnlinePacCache.TryGetContent(_config.pacUrl, out string cached))
                    return cached;

                string proxy = $"PROXY {_config.LocalHost}:{_config.localPort};";
                return BuildProxyAllPac(proxy);
            }

            return _pacDaemon.GetPACContent();
        }

        private static string GetHash(string content)
            => HttpServerUtilityUrlToken.Encode(MD5.HashData(Encoding.UTF8.GetBytes(content)));

        public override bool Handle(byte[] firstPacket, int length, Socket socket, object state)
        {
            if (socket.ProtocolType != ProtocolType.Tcp)
                return false;

            try
            {
                string request = Encoding.UTF8.GetString(firstPacket, 0, length);
                string[] lines = request.Split('\r', '\n');
                bool hostMatch = false, pathMatch = false;
                bool secretMatch = !_config.secureLocalPac;

                if (lines.Length < 2)
                    return false;

                string requestLine = lines[0];
                string[] requestItems = requestLine.Split(' ');
                if (requestItems.Length == 3 && requestItems[0] == "GET")
                {
                    int index = requestItems[1].IndexOf('?');
                    if (index < 0)
                        index = requestItems[1].Length;

                    string resourceString = requestItems[1].Substring(0, index).Remove(0, 1);
                    if (string.Equals(resourceString, RESOURCE_NAME, StringComparison.OrdinalIgnoreCase))
                    {
                        pathMatch = true;
                        if (!secretMatch)
                        {
                            string queryString = requestItems[1].Substring(index);
                            if (queryString.Contains(PacSecret))
                                secretMatch = true;
                        }
                    }
                }

                for (int i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrEmpty(lines[i]))
                        continue;

                    string[] kv = lines[i].Split(new[] { ':' }, 2);
                    if (kv.Length == 2 && kv[0] == "Host" &&
                        kv[1].Trim() == ((IPEndPoint)socket.LocalEndPoint).ToString())
                    {
                        hostMatch = true;
                    }
                }

                if (!hostMatch || !pathMatch)
                    return false;

                if (!secretMatch)
                    socket.Close();
                else
                    SendResponse(socket);

                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private void SendResponse(Socket socket)
        {
            try
            {
                IPEndPoint localEndPoint = (IPEndPoint)socket.LocalEndPoint;
                string proxy = GetPACAddress(localEndPoint);
                string pacContent;

                if (UseOnlinePac())
                {
                    // Serve the remote PAC byte-for-byte logically from the local cache.
                    // If this is the first run and the cache is not ready yet, proxy-all is
                    // a safe bootstrap that avoids a direct-network leak.
                    pacContent = OnlinePacCache.TryGetContent(_config.pacUrl, out string cached)
                        ? cached
                        : BuildProxyAllPac(proxy);
                }
                else
                {
                    pacContent = $"var __PROXY__ = '{proxy}';\n" + _pacDaemon.GetPACContent();
                }

                byte[] body = Encoding.UTF8.GetBytes(pacContent);
                string responseHead =
                    $"HTTP/1.1 200 OK\r\n" +
                    $"Server: ShadowsocksWindows/{UpdateChecker.Version}\r\n" +
                    "Content-Type: application/x-ns-proxy-autoconfig; charset=utf-8\r\n" +
                    $"Content-Length: {body.Length}\r\n" +
                    "Cache-Control: no-cache\r\n" +
                    "Connection: Close\r\n\r\n";
                byte[] head = Encoding.UTF8.GetBytes(responseHead);
                byte[] response = new byte[head.Length + body.Length];
                Buffer.BlockCopy(head, 0, response, 0, head.Length);
                Buffer.BlockCopy(body, 0, response, head.Length, body.Length);
                socket.BeginSend(response, 0, response.Length, 0, SendCallback, socket);
            }
            catch (Exception e)
            {
                logger.LogUsefulException(e);
                socket.Close();
            }
        }

        private static void SendCallback(IAsyncResult ar)
        {
            Socket conn = (Socket)ar.AsyncState;
            try
            {
                conn.Shutdown(SocketShutdown.Send);
            }
            catch
            {
            }
        }

        private bool UseOnlinePac()
            => _config != null && _config.useOnlinePac && !string.IsNullOrWhiteSpace(_config.pacUrl);

        private static string BuildProxyAllPac(string proxy)
            => $"function FindProxyForURL(url, host) {{ return '{proxy}'; }}\n";

        private string GetPACAddress(IPEndPoint localEndPoint)
            => localEndPoint.AddressFamily == AddressFamily.InterNetworkV6
                ? $"PROXY [{localEndPoint.Address}]:{_config.localPort};"
                : $"PROXY {localEndPoint.Address}:{_config.localPort};";
    }
}
