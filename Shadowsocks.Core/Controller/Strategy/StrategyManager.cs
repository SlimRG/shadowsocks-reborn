using System;
using System.Collections.Generic;
using Shadowsocks.Model;

namespace Shadowsocks.Controller.Strategy
{
    class StrategyManager
    {
        private readonly List<IStrategy> _strategies;
        public StrategyManager(Func<Configuration> configurationProvider)
        {
            if (configurationProvider == null) throw new ArgumentNullException(nameof(configurationProvider));
            _strategies = new List<IStrategy>();
            _strategies.Add(new BalancingStrategy(configurationProvider));
            _strategies.Add(new HighAvailabilityStrategy(configurationProvider));
            // TODO: load DLL plugins
        }
        public IList<IStrategy> GetStrategies()
        {
            return _strategies;
        }
    }
}
