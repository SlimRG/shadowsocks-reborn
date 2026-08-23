#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shadowsocks.Controller.Service;

namespace Shadowsocks.Controller
{
    public sealed partial class ShadowsocksController
    {
        private static readonly TimeSpan PluginMaintenanceStartupDelay = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan PluginMaintenancePollInterval = TimeSpan.FromHours(1);
        private readonly object _pluginMaintenanceLock = new();
        private CancellationTokenSource? _pluginMaintenanceCancellation;
        private Task? _pluginMaintenanceTask;

        public Task<PluginUpdateSummary> CheckPluginUpdatesAsync(
            bool force = false,
            CancellationToken cancellationToken = default)
            => PluginManager.UpdateCatalogPluginsAsync(GetPluginIdsInUse(), force, cancellationToken);

        private void StartPluginMaintenance()
        {
            lock (_pluginMaintenanceLock)
            {
                if (_pluginMaintenanceTask is { IsCompleted: false })
                    return;

                _pluginMaintenanceCancellation?.Dispose();
                _pluginMaintenanceCancellation = new CancellationTokenSource();
                CancellationToken token = _pluginMaintenanceCancellation.Token;
                _pluginMaintenanceTask = RunPluginMaintenanceLoopAsync(token);
            }
        }

        private void StopPluginMaintenance()
        {
            CancellationTokenSource? cancellation;
            Task? task;
            lock (_pluginMaintenanceLock)
            {
                cancellation = _pluginMaintenanceCancellation;
                task = _pluginMaintenanceTask;
                _pluginMaintenanceCancellation = null;
                _pluginMaintenanceTask = null;
            }

            if (cancellation is null)
                return;

            try
            {
                cancellation.Cancel();
                task?.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Normal during Stop(), suspend, or application shutdown.
            }
            catch (Exception exception)
            {
                logger.Warn(exception, "SIP003 plugin automatic maintenance did not stop cleanly.");
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        private async Task RunPluginMaintenanceLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(PluginMaintenanceStartupDelay, cancellationToken).ConfigureAwait(false);
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        PluginUpdateSummary summary = await CheckPluginUpdatesAsync(
                            force: false,
                            cancellationToken: cancellationToken).ConfigureAwait(false);

                        if (summary.UpdatedCount > 0)
                        {
                            logger.Info(
                                "SIP003 plugins | UPDATE | Automatically updated {0} plugin(s).",
                                summary.UpdatedCount);
                        }

                        if (summary.SkippedInUseCount > 0)
                        {
                            logger.Debug(
                                "SIP003 plugins | UPDATE | Deferred {0} plugin(s) because they are active in this session.",
                                summary.SkippedInUseCount);
                        }

                        foreach (string error in summary.Errors)
                            logger.Warn("SIP003 plugins | UPDATE | {0}", error);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        logger.Warn(exception, "SIP003 plugin automatic maintenance iteration failed.");
                    }

                    await Task.Delay(PluginMaintenancePollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal lifecycle cancellation.
            }
        }

        private string[] GetPluginIdsInUse()
            => _pluginsByServer.Keys
                .Select(server => server?.plugin)
                .Where(plugin => !string.IsNullOrWhiteSpace(plugin))
                .Select(plugin => plugin!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }
}
