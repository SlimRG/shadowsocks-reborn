using System;
using System.Collections.Generic;
using System.Net;
using NLog;
using Shadowsocks.Model;

namespace Shadowsocks.Controller.Strategy
{
    class HighAvailabilityStrategy : IStrategy
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private ServerStatus _currentServer;
        private Dictionary<Server, ServerStatus> _serverStatus;
        private readonly Func<Configuration> _configurationProvider;

        public class ServerStatus
        {
            // time interval between SYN and SYN+ACK
            public TimeSpan Latency { get; set; }
            public DateTime LastTimeDetectLatency { get; set; }

            // last time anything received
            public DateTime LastRead { get; set; }

            // last time anything sent
            public DateTime LastWrite { get; set; }

            // connection refused or closed before anything received
            public DateTime LastFailure { get; set; }

            public Server Server { get; set; }

            public double Score { get; set; }
        }

        public HighAvailabilityStrategy(Func<Configuration> configurationProvider)
        {
            ArgumentNullException.ThrowIfNull(configurationProvider);
            _configurationProvider = configurationProvider;
            _serverStatus = new Dictionary<Server, ServerStatus>(ReferenceEqualityComparer.Instance);
        }

        public string Name
        {
            get { return I18N.GetString("High Availability"); }
        }

        public string ID
        {
            get { return "com.shadowsocks.strategy.ha"; }
        }

        public void ReloadServers()
        {
            Dictionary<Server, ServerStatus> previous = _serverStatus;
            Dictionary<Server, ServerStatus> current = new(ReferenceEqualityComparer.Instance);
            DateTime now = DateTime.Now;

            foreach (Server server in _configurationProvider().configs ?? [])
            {
                if (server?.IsConfigured != true)
                {
                    continue;
                }

                if (!previous.TryGetValue(server, out ServerStatus status))
                {
                    status = new ServerStatus
                    {
                        Server = server,
                        LastFailure = DateTime.MinValue,
                        LastRead = now,
                        LastWrite = now,
                        Latency = TimeSpan.FromMilliseconds(10),
                        LastTimeDetectLatency = now,
                    };
                }
                else
                {
                    status.Server = server;
                }

                current[server] = status;
            }

            _serverStatus = current;
            if (_currentServer is not null && !current.ContainsValue(_currentServer))
            {
                _currentServer = null;
            }

            ChooseNewServer();
        }

        public Server GetAServer(IStrategyCallerType type, System.Net.IPEndPoint localIPEndPoint, EndPoint destEndPoint)
        {
            if (type == IStrategyCallerType.TCP)
            {
                ChooseNewServer();
            }
            if (_currentServer == null)
            {
                return null;
            }
            return _currentServer.Server;
        }

        /**
         * once failed, try after 5 min
         * and (last write - last read) < 5s
         * and (now - last read) <  5s  // means not stuck
         * and latency < 200ms, try after 30s
         */
        public void ChooseNewServer()
        {
            DateTime now = DateTime.Now;
            ServerStatus best = null;

            foreach (ServerStatus status in _serverStatus.Values)
            {
                status.Score =
                    100 * 1000 * Math.Min(5 * 60, (now - status.LastFailure).TotalSeconds)
                    - 2 * 5 * (Math.Min(2000, status.Latency.TotalMilliseconds) /
                               (1 + (now - status.LastTimeDetectLatency).TotalSeconds / 300))
                    + 0.5 * 200 * Math.Min(5, (status.LastWrite - status.LastRead).TotalSeconds);

                logger.Debug($"server: {status.Server} latency:{status.Latency} score: {status.Score}");
                if (best is null || status.Score >= best.Score)
                {
                    best = status;
                }
            }

            if (best is not null && (_currentServer is null || best.Score - _currentServer.Score > 200))
            {
                _currentServer = best;
                logger.Info($"HA switching to server: {_currentServer.Server}");
            }
        }

        public void UpdateLatency(Model.Server server, TimeSpan latency)
        {
            logger.Debug($"latency: {server.ToString()} {latency}");

            ServerStatus status;
            if (_serverStatus.TryGetValue(server, out status))
            {
                status.Latency = latency;
                status.LastTimeDetectLatency = DateTime.Now;
            }
        }

        public void UpdateLastRead(Model.Server server)
        {
            logger.Debug($"last read: {server.ToString()}");

            ServerStatus status;
            if (_serverStatus.TryGetValue(server, out status))
            {
                status.LastRead = DateTime.Now;
            }
        }

        public void UpdateLastWrite(Model.Server server)
        {
            logger.Debug($"last write: {server.ToString()}");

            ServerStatus status;
            if (_serverStatus.TryGetValue(server, out status))
            {
                status.LastWrite = DateTime.Now;
            }
        }

        public void SetFailure(Model.Server server)
        {
            logger.Debug($"failure: {server.ToString()}");

            ServerStatus status;
            if (_serverStatus.TryGetValue(server, out status))
            {
                status.LastFailure = DateTime.Now;
            }
        }
    }
}
