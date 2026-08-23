using System;
using System.Net;
using System.Linq;
using Shadowsocks.Model;

namespace Shadowsocks.Controller.Strategy
{
    class BalancingStrategy : IStrategy
    {
        private readonly Func<Configuration> _configurationProvider;
        private readonly Random _random;

        public BalancingStrategy(Func<Configuration> configurationProvider)
        {
            ArgumentNullException.ThrowIfNull(configurationProvider);
            _configurationProvider = configurationProvider;
            _random = new Random();
        }

        public string Name
        {
            get { return I18N.GetString("Load Balance"); }
        }

        public string ID
        {
            get { return "com.shadowsocks.strategy.balancing"; }
        }

        public void ReloadServers()
        {
            // do nothing
        }

        public Server GetAServer(IStrategyCallerType type, IPEndPoint localIPEndPoint, EndPoint destEndPoint)
        {
            Server[] configs = (_configurationProvider().configs ?? [])
                .Where(server => server?.IsConfigured == true)
                .ToArray();
            if (configs.Length == 0)
            {
                return null;
            }

            if (type == IStrategyCallerType.TCP)
            {
                return configs[_random.Next(configs.Length)];
            }

            int index = localIPEndPoint.GetHashCode();
            int selectedIndex = (int)((uint)index % (uint)configs.Length);
            return configs[selectedIndex];
        }

        public void UpdateLatency(Model.Server server, TimeSpan latency)
        {
            // do nothing
        }

        public void UpdateLastRead(Model.Server server)
        {
            // do nothing
        }

        public void UpdateLastWrite(Model.Server server)
        {
            // do nothing
        }

        public void SetFailure(Model.Server server)
        {
            // do nothing
        }
    }
}
