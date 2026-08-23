using System;
using System.Collections.Generic;
using System.Threading;

namespace Shadowsocks.Routing
{
    /// <summary>
    /// Describes how the managed network-filter engine participates in routing.
    /// Disabled snapshots deliberately preserve the PAC/system-proxy decision.
    /// </summary>
    public enum ManagedRoutingSnapshotMode
    {
        Disabled = 0,
        LocalRules = 1,
        ProxyAllBootstrap = 2,
    }

    /// <summary>
    /// Immutable routing snapshot published atomically to live proxy connections.
    /// Existing connections keep their already selected route; every new request reads
    /// the latest complete snapshot without observing a partially rebuilt rule set.
    /// </summary>
    public sealed class ManagedRoutingSnapshot
    {
        private static long _nextGeneration;
        private long _directDecisionCount;
        private long _proxyDecisionCount;

        private ManagedRoutingSnapshot(
            ManagedRoutingSnapshotMode mode,
            FilterEngine engine,
            FilterCompilationReport report,
            string source,
            DateTime createdUtc,
            IReadOnlyList<string> defaultRules,
            IReadOnlyList<string> userRules)
        {
            Mode = mode;
            Engine = engine;
            Report = report;
            Source = source ?? string.Empty;
            CreatedUtc = createdUtc;
            DefaultRules = defaultRules ?? Array.Empty<string>();
            UserRules = userRules ?? Array.Empty<string>();
            Generation = Interlocked.Increment(ref _nextGeneration);
        }

        public long Generation { get; }
        public ManagedRoutingSnapshotMode Mode { get; }
        public FilterEngine Engine { get; }
        public FilterCompilationReport Report { get; }
        public string Source { get; }
        public DateTime CreatedUtc { get; }
        public IReadOnlyList<string> DefaultRules { get; }
        public IReadOnlyList<string> UserRules { get; }
        public bool IsEnabled => Engine is not null && Mode != ManagedRoutingSnapshotMode.Disabled;
        public long DirectDecisionCount => Interlocked.Read(ref _directDecisionCount);
        public long ProxyDecisionCount => Interlocked.Read(ref _proxyDecisionCount);

        internal void RecordDecision(FilterRoutingAction action)
        {
            if (action == FilterRoutingAction.Direct)
            {
                Interlocked.Increment(ref _directDecisionCount);
            }
            else
            {
                Interlocked.Increment(ref _proxyDecisionCount);
            }
        }

        public static ManagedRoutingSnapshot Disabled(string source)
            => new(
                ManagedRoutingSnapshotMode.Disabled,
                null,
                null,
                source,
                DateTime.UtcNow,
                Array.Empty<string>(),
                Array.Empty<string>());

        public static ManagedRoutingSnapshot LocalRules(
            FilterEngine engine,
            FilterCompilationReport report,
            string source = "GeoSite + user rules",
            IReadOnlyList<string> defaultRules = null,
            IReadOnlyList<string> userRules = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(report);
            return new ManagedRoutingSnapshot(
                ManagedRoutingSnapshotMode.LocalRules,
                engine,
                report,
                source,
                DateTime.UtcNow,
                defaultRules ?? Array.Empty<string>(),
                userRules ?? Array.Empty<string>());
        }

        public static ManagedRoutingSnapshot ProxyAllBootstrap(
            FilterEngine engine,
            FilterCompilationReport report,
            string source,
            IReadOnlyList<string> defaultRules = null,
            IReadOnlyList<string> userRules = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(report);
            return new ManagedRoutingSnapshot(
                ManagedRoutingSnapshotMode.ProxyAllBootstrap,
                engine,
                report,
                source,
                DateTime.UtcNow,
                defaultRules ?? Array.Empty<string>(),
                userRules ?? Array.Empty<string>());
        }
    }
}
