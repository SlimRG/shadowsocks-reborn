#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Shadowsocks.Controller.Service;
using Shadowsocks.Controller.Traffic;

namespace Shadowsocks.Controller
{
    public sealed partial class ShadowsocksController
    {
        private readonly object _dnsCryptMaintenanceLock = new();
        private CancellationTokenSource? _dnsCryptMaintenanceCancellation;
        private Task? _dnsCryptMaintenanceTask;
        private string? _lastDnsCryptMaintenanceError;

        private void StartDnsCryptMaintenance()
        {
            lock (_dnsCryptMaintenanceLock)
            {
                if (_dnsCryptMaintenanceTask is { IsCompleted: false })
                    return;

                _dnsCryptMaintenanceCancellation?.Dispose();
                _dnsCryptMaintenanceCancellation = new CancellationTokenSource();
                CancellationToken token = _dnsCryptMaintenanceCancellation.Token;
                _dnsCryptMaintenanceTask = RunDnsCryptMaintenanceLoopAsync(token);
            }
        }

        private void StopDnsCryptMaintenance()
        {
            CancellationTokenSource? cancellation;
            Task? task;
            lock (_dnsCryptMaintenanceLock)
            {
                cancellation = _dnsCryptMaintenanceCancellation;
                task = _dnsCryptMaintenanceTask;
                _dnsCryptMaintenanceCancellation = null;
                _dnsCryptMaintenanceTask = null;
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
                logger.Warn(exception, "DNSCrypt automatic maintenance did not stop cleanly.");
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        private async Task RunDnsCryptMaintenanceLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(DnsCryptMaintenancePolicy.StartupDelay, cancellationToken).ConfigureAwait(false);
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        DnsCryptComponentStatus component = _dnsCryptComponentManager.GetStatus();
                        DnsCryptConfig config = CloneDnsCryptConfig(
                            _config.dnsPolicy?.dnsCrypt ?? new DnsCryptConfig());

                        if (component.IsInstalled && component.AutoUpdate != config.autoUpdate)
                        {
                            // settings.json is the user-facing source of truth. Keep component.json
                            // synchronized so recovery/tools see the same preference.
                            _dnsCryptComponentManager.SetAutoUpdate(config.autoUpdate);
                            component = _dnsCryptComponentManager.GetStatus();
                        }

                        TimeSpan delay = DnsCryptMaintenancePolicy.GetDelayUntilNextAutomaticCheck(
                            component,
                            config,
                            DateTimeOffset.UtcNow);
                        if (delay > TimeSpan.Zero)
                        {
                            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        await _dnsCryptCoordinator.ExecuteExclusiveAsync(
                            "Automatic DNSCrypt update",
                            AutomaticDnsCryptMaintenanceCoreAsync,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        // A transient local metadata/configuration fault must not permanently disable
                        // maintenance for the rest of this application session. Surface the error and
                        // retry only after the local idle interval; no GitHub request is made here.
                        _lastDnsCryptMaintenanceError = exception.Message;
                        logger.Warn(exception, "DNSCrypt automatic maintenance iteration failed.");
                        DnsCryptStatusChanged?.Invoke(this, EventArgs.Empty);
                        await Task.Delay(
                            DnsCryptMaintenancePolicy.IdleRecheckInterval,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal lifecycle cancellation.
            }
        }

        private async Task AutomaticDnsCryptMaintenanceCoreAsync(CancellationToken cancellationToken)
        {
            DnsCryptComponentStatus before = _dnsCryptComponentManager.GetStatus();
            DnsCryptConfig config = CloneDnsCryptConfig(
                _config.dnsPolicy?.dnsCrypt ?? new DnsCryptConfig());
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

            if (!DnsCryptMaintenancePolicy.IsAutomaticCheckDue(before, config, nowUtc))
                return;

            // Persist the attempt before touching the network. A failed/cancelled automatic
            // request therefore cannot create a retry storm across resume or process restart;
            // manual Check for Update remains available at any time.
            _dnsCryptComponentManager.RecordUpdateCheck(nowUtc);
            try
            {
                DnsCryptReleaseInfo release = await _dnsCryptComponentManager
                    .GetLatestReleaseAsync(cancellationToken)
                    .ConfigureAwait(false);
                _latestDnsCryptVersion = release.Version;
                _lastDnsCryptMaintenanceError = null;
                DnsCryptStatusChanged?.Invoke(this, EventArgs.Empty);

                if (before.ActiveVersion is not null && release.Version > before.ActiveVersion)
                {
                    logger.Info(
                        "DNSCryptProxy | UPDATE | Automatic update {0} -> {1} is available.",
                        before.ActiveVersion,
                        release.Version);
                    await UpdateDnsCryptReleaseCoreAsync(
                        before,
                        release,
                        progress: null,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _lastDnsCryptMaintenanceError = exception.Message;
                logger.Warn(exception, "DNSCrypt automatic update check/update failed.");
                DnsCryptStatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
