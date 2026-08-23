using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Shadowsocks.Model;
using Shadowsocks.Routing;

namespace Shadowsocks.Controller.Service
{
    /// <summary>
    /// Builds one immutable managed-routing snapshot from the same Local PAC inputs used by
    /// GeositeUpdater. Network/download work is intentionally excluded: callers publish a
    /// bootstrap proxy-all snapshot until GeoSite has a complete local database, then rebuild.
    /// </summary>
    public static class ManagedRoutingSnapshotBuilder
    {
        public static ManagedRoutingSnapshot Build(Configuration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            if (!configuration.Enabled)
            {
                return ManagedRoutingSnapshot.Disabled("system proxy is disabled");
            }

            if (configuration.global)
            {
                return ManagedRoutingSnapshot.Disabled("global mode already routes every request through Shadowsocks");
            }

            if (configuration.useOnlinePac)
            {
                // An arbitrary downloaded PAC program cannot be losslessly reduced to ABP
                // network filters. Keep the PAC decision authoritative in Online PAC mode.
                return ManagedRoutingSnapshot.Disabled("online PAC is the selected external routing mode");
            }

            IReadOnlyList<string> userRules = ReadUserRules();
            if (!GeositeUpdater.IsDatabaseAvailable || GeositeUpdater.NeedsRefresh || GeositeUpdater.IsSourceSetDirty)
            {
                IReadOnlyList<string> bootstrapRules = new[] { "/.*/" };
                FilterEngine bootstrap = FilterEngine.Compile(
                    bootstrapRules,
                    Array.Empty<string>(),
                    out FilterCompilationReport bootstrapReport);
                return ManagedRoutingSnapshot.ProxyAllBootstrap(
                    bootstrap,
                    bootstrapReport,
                    "GeoSite is not ready; matching Local PAC proxy-all bootstrap",
                    bootstrapRules,
                    Array.Empty<string>());
            }

            IReadOnlyList<string> defaultRules = GeositeUpdater.BuildManagedFilterRules(
                configuration.geositeDirectGroups,
                configuration.geositeProxiedGroups,
                configuration.geositePreferDirect);
            FilterEngine engine = FilterEngine.Compile(defaultRules, userRules, out FilterCompilationReport report);
            return ManagedRoutingSnapshot.LocalRules(
                engine,
                report,
                "GeoSite + user rules",
                defaultRules,
                userRules);
        }

        private static IReadOnlyList<string> ReadUserRules()
        {
            try
            {
                if (!File.Exists(PACDaemon.UserRuleFile))
                {
                    return FilterEngine.ParseRuleLines(Shadowsocks.Core.EmbeddedResources.UserRule);
                }

                string content = Shadowsocks.Controller.FileManager.NonExclusiveReadAllText(PACDaemon.UserRuleFile, Encoding.UTF8);
                return FilterEngine.ParseRuleLines(content);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Do not replace a known-good live snapshot with incomplete rules merely
                // because an editor still owns the file. The controller keeps the previous
                // immutable snapshot and retries on the next file-system/configuration event.
                throw new IOException("Managed routing could not read user-rule.txt while rebuilding the snapshot.", exception);
            }
        }
    }
}
