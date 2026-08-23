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
            ArgumentNullException.ThrowIfNull(configurationProvider);
            _strategies =
            [
                new BalancingStrategy(configurationProvider),
                new HighAvailabilityStrategy(configurationProvider),
            ];
        }
        public IList<IStrategy> GetStrategies()
        {
            return _strategies;
        }
    }
}
