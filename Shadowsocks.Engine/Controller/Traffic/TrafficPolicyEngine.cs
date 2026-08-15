using System;
using System.Collections.Generic;
using System.Threading;
using Shadowsocks.Model;

namespace Shadowsocks.Controller.Traffic
{
    /// <summary>
    /// Shared routing policy used by both user-mode proxying and the elevated
    /// transparent-capture backend. Capture and routing are deliberately separate.
    /// </summary>
    public sealed class TrafficPolicyEngine
    {
        private readonly Lock _sync = new();
        private List<ApplicationRouteRule> _applicationRules = [];

        public TrafficPolicyEngine(Configuration configuration)
        {
            UpdateConfiguration(configuration);
        }

        public void UpdateConfiguration(Configuration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            lock (_sync)
            {
                _applicationRules = configuration.applicationRules is null
                    ? []
                    : [.. configuration.applicationRules];
            }
        }

        public RouteDecision EvaluateUserMode(TrafficContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            // Internal maintenance requests (Online PAC / GeoSite / update checks that
            // explicitly enter the local proxy) must never be turned DIRECT by an app rule.
            if (context.IsInternal)
            {
                return RouteDecision.Proxy("internal Shadowsocks request");
            }

            TrafficRouteAction applicationAction = MatchApplication(context.ProcessPath, context.ProcessName);
            return applicationAction switch
            {
                TrafficRouteAction.Direct => RouteDecision.Direct("application rule"),
                TrafficRouteAction.Block => RouteDecision.Block("application rule"),
                TrafficRouteAction.Proxy => RouteDecision.Proxy("application rule"),
                _ => RouteDecision.Proxy("system proxy/PAC selected this request"),
            };
        }

        public RouteDecision EvaluateAdminMode(TrafficContext context, TrafficRouteAction fallback)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (context.IsInternal)
            {
                return RouteDecision.Direct("Shadowsocks transport exclusion");
            }

            TrafficRouteAction applicationAction = MatchApplication(context.ProcessPath, context.ProcessName);
            TrafficRouteAction action = applicationAction == TrafficRouteAction.Default ? fallback : applicationAction;
            return action switch
            {
                TrafficRouteAction.Block => RouteDecision.Block("application/admin rule"),
                TrafficRouteAction.Direct => RouteDecision.Direct("application/admin rule"),
                _ => RouteDecision.Proxy("application/admin rule"),
            };
        }

        private TrafficRouteAction MatchApplication(string processPath, string processName)
        {
            List<ApplicationRouteRule> rules;
            lock (_sync)
            {
                rules = [.. _applicationRules];
            }

            foreach (ApplicationRouteRule rule in rules)
            {
                if (rule is null || !rule.enabled || string.IsNullOrWhiteSpace(rule.application))
                {
                    continue;
                }

                if (MatchesApplication(rule.application.Trim(), processPath, processName))
                {
                    return rule.action;
                }
            }

            return TrafficRouteAction.Default;
        }

        private static bool MatchesApplication(string pattern, string processPath, string processName)
        {
            return ApplicationPatternMatcher.Matches(pattern, processPath, processName);
        }
    }
}
