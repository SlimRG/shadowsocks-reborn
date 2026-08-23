using System;
using System.Collections.Generic;
using System.Threading;
using Shadowsocks.Model;
using Shadowsocks.Routing;

namespace Shadowsocks.Controller.Traffic
{
    /// <summary>
    /// Shared routing policy used by both user-mode proxying and the elevated
    /// transparent-capture backend. Capture and routing are deliberately separate.
    /// </summary>
    public sealed class TrafficPolicyEngine
    {
        private ApplicationRuleSnapshot[] _applicationRules = [];
        private ManagedRoutingSnapshot _managedRouting = ManagedRoutingSnapshot.Disabled("not initialized");

        public TrafficPolicyEngine(Configuration configuration)
        {
            UpdateConfiguration(configuration);
        }

        public void UpdateConfiguration(Configuration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            List<ApplicationRuleSnapshot> snapshots = [];
            if (configuration.applicationRules is not null)
            {
                foreach (ApplicationRouteRule rule in configuration.applicationRules)
                {
                    if (rule is null || !rule.Enabled || string.IsNullOrWhiteSpace(rule.Application))
                    {
                        continue;
                    }

                    if (ApplicationPatternMatcher.TryCompile(
                        rule.Application,
                        out ApplicationPatternMatcher.CompiledApplicationPattern pattern))
                    {
                        snapshots.Add(new ApplicationRuleSnapshot(pattern, rule.Action));
                    }
                }
            }

            Volatile.Write(ref _applicationRules, snapshots.ToArray());
        }

        public ManagedRoutingSnapshot CurrentManagedRoutingSnapshot => Volatile.Read(ref _managedRouting);
        public long ManagedDirectDecisionCount => CurrentManagedRoutingSnapshot.DirectDecisionCount;
        public long ManagedProxyDecisionCount => CurrentManagedRoutingSnapshot.ProxyDecisionCount;

        public void UpdateManagedRouting(ManagedRoutingSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            Volatile.Write(ref _managedRouting, snapshot);
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
            if (applicationAction != TrafficRouteAction.Default)
            {
                return applicationAction switch
                {
                    TrafficRouteAction.Direct => RouteDecision.Direct("application rule"),
                    TrafficRouteAction.Block => RouteDecision.Block("application rule"),
                    _ => RouteDecision.Proxy("application rule"),
                };
            }

            RouteDecision? managed = EvaluateManagedRouting(context);
            return managed ?? RouteDecision.Proxy("system proxy/PAC selected this request");
        }

        public RouteDecision EvaluateAdminMode(TrafficContext context, TrafficRouteAction fallback)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (context.IsInternal)
            {
                return RouteDecision.Direct("Shadowsocks transport exclusion");
            }

            TrafficRouteAction applicationAction = MatchApplication(context.ProcessPath, context.ProcessName);
            if (applicationAction != TrafficRouteAction.Default)
            {
                return applicationAction switch
                {
                    TrafficRouteAction.Block => RouteDecision.Block("application/admin rule"),
                    TrafficRouteAction.Direct => RouteDecision.Direct("application/admin rule"),
                    _ => RouteDecision.Proxy("application/admin rule"),
                };
            }

            // Admin SYN classification may not have a hostname yet. Ordinary TCP is deferred
            // to the transparent relay, where HTTP Host or TLS ClientHello SNI supplies
            // DestinationUrl and this exact managed decision path can be reused. UDP and
            // hostname-less traffic still use the deterministic fallback below.
            RouteDecision? managed = EvaluateManagedRouting(context);
            if (managed is not null)
            {
                return managed.Value;
            }

            return fallback switch
            {
                TrafficRouteAction.Block => RouteDecision.Block("admin fallback"),
                TrafficRouteAction.Direct => RouteDecision.Direct("admin fallback"),
                _ => RouteDecision.Proxy("admin fallback"),
            };
        }

        private RouteDecision? EvaluateManagedRouting(TrafficContext context)
        {
            ManagedRoutingSnapshot snapshot = CurrentManagedRoutingSnapshot;
            if (!snapshot.IsEnabled || string.IsNullOrWhiteSpace(context.DestinationUrl))
            {
                return null;
            }

            FilterRoutingDecision filter = snapshot.Engine.Evaluate(context.DestinationUrl, context.DestinationHost);
            string matched = string.IsNullOrWhiteSpace(filter.MatchedRule)
                ? filter.Source.ToString()
                : $"{filter.Source}: {filter.MatchedRule}";
            string reason = $"managed routing generation {snapshot.Generation} ({matched})";
            snapshot.RecordDecision(filter.Action);
            return filter.Action == FilterRoutingAction.Direct
                ? RouteDecision.Direct(reason)
                : RouteDecision.Proxy(reason);
        }

        private TrafficRouteAction MatchApplication(string processPath, string processName)
        {
            ApplicationRuleSnapshot[] rules = Volatile.Read(ref _applicationRules);
            ApplicationPatternMatcher.ApplicationMatchTarget target =
                ApplicationPatternMatcher.CreateTarget(processPath, processName);
            foreach (ApplicationRuleSnapshot rule in rules)
            {
                if (ApplicationPatternMatcher.Matches(rule.Pattern, target))
                {
                    return rule.Action;
                }
            }

            return TrafficRouteAction.Default;
        }

        private readonly record struct ApplicationRuleSnapshot(
            ApplicationPatternMatcher.CompiledApplicationPattern Pattern,
            TrafficRouteAction Action);
    }
}
