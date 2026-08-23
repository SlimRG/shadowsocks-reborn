using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Shadowsocks.Controller.Service;
using Shadowsocks.Controller.Traffic;
using Shadowsocks.Core;
using Shadowsocks.Model;

namespace Shadowsocks.Controller
{
    public sealed partial class ShadowsocksController
    {


        private void RememberAutomaticDnsCryptRuntimeResolver()
        {
            if (_config.dnsPolicy?.dnsCrypt?.automaticResolvers != true)
                return;

            IReadOnlyList<string> runtimeNames = _dnsCryptRuntimeManager.GetActiveResolverNames();
            if (runtimeNames.Count > 0)
                _dnsCryptAutomaticServerNames = runtimeNames.ToArray();
        }

        public DnsCryptManagementStatus GetDnsCryptManagementStatus()
        {
            DnsCryptComponentStatus component = _dnsCryptComponentManager.GetStatus();
            DnsCryptRuntimeStatus runtime = _dnsCryptRuntimeManager.GetStatus();
            DnsCryptConfig config = _config.dnsPolicy?.dnsCrypt ?? new DnsCryptConfig();
            bool automaticResolvers = config.automaticResolvers;
            IReadOnlyList<string> runtimeNames = _dnsCryptRuntimeManager.GetActiveResolverNames();
            IReadOnlyList<string> activeNames = automaticResolvers
                ? (runtimeNames.Count > 0 ? runtimeNames : _dnsCryptAutomaticServerNames)
                : config.serverNames;
            IReadOnlyDictionary<string, int> runtimeLatencies = _dnsCryptRuntimeManager.GetResolverLatencies();
            DnsCryptResolverInfo[] activeResolvers = activeNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name =>
                {
                    DnsCryptResolverInfo resolver = _dnsCryptResolverCatalog.FirstOrDefault(
                        item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? CreateResolverPlaceholder(name);
                    return runtimeLatencies.TryGetValue(name, out int latencyMs)
                        ? resolver with { LatencyMs = latencyMs }
                        : resolver;
                })
                .ToArray();
            return new DnsCryptManagementStatus(
                _config.dnsPolicy?.mode ?? DnsPolicyMode.System,
                component,
                runtime,
                _latestDnsCryptVersion,
                _lastDnsCryptMaintenanceError,
                automaticResolvers,
                activeResolvers);
        }

        public Task<DnsCryptReleaseInfo> CheckDnsCryptUpdateAsync(CancellationToken cancellationToken = default)
        {
            return _dnsCryptCoordinator.ExecuteExclusiveAsync(
                "Check DNSCrypt update",
                CheckDnsCryptUpdateCoreAsync,
                cancellationToken);
        }

        private async Task<DnsCryptReleaseInfo> CheckDnsCryptUpdateCoreAsync(CancellationToken cancellationToken)
        {
            DnsCryptReleaseInfo release = await _dnsCryptComponentManager.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            _latestDnsCryptVersion = release.Version;
            _lastDnsCryptMaintenanceError = null;
            _dnsCryptComponentManager.RecordUpdateCheck(DateTimeOffset.UtcNow);
            DnsCryptStatusChanged?.Invoke(this, EventArgs.Empty);
            return release;
        }

        public Task<IReadOnlyList<DnsCryptResolverInfo>> GetDnsCryptResolversAsync(CancellationToken cancellationToken = default)
        {
            return _dnsCryptCoordinator.ExecuteExclusiveAsync(
                "List DNSCrypt resolvers",
                GetDnsCryptResolversCoreAsync,
                cancellationToken);
        }

        private async Task<IReadOnlyList<DnsCryptResolverInfo>> GetDnsCryptResolversCoreAsync(CancellationToken cancellationToken)
        {
            DnsCryptComponentStatus component = _dnsCryptComponentManager.GetStatus();
            if (!component.IsInstalled)
                throw new InvalidOperationException("DNSCrypt Proxy is not installed.");

            DnsCryptConfig config = CloneDnsCryptConfig(_config.dnsPolicy?.dnsCrypt ?? new DnsCryptConfig());
            // -list-all reads the signed resolver catalog. Manual mode intentionally exposes
            // DNSCrypt and DoH entries; ODoH remains excluded.
            config.serverNames = [];
            config.automaticResolvers = false;
            config.routeThroughShadowsocks = false;
            IReadOnlyList<DnsCryptResolverInfo> resolvers = await GetAndCacheDnsCryptResolversAsync(config, cancellationToken).ConfigureAwait(false);
            return resolvers.Where(IsSupportedDnsCryptProxyResolver).ToArray();
        }

        public async Task<IReadOnlyDictionary<string, int>> ProbeDnsCryptResolverLatenciesAsync(
            IEnumerable<string> resolverNames,
            CancellationToken cancellationToken = default)
        {
            string[] names = (resolverNames ?? Array.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (names.Length == 0)
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<DnsCryptResolverInfo> catalog = _dnsCryptResolverCatalog;
            DnsCryptResolverInfo[] targets = catalog
                .Where(resolver => names.Contains(resolver.Name, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            IReadOnlyDictionary<string, int> measured = await DnsCryptResolverLatencyProbe
                .ProbeAsync(targets, cancellationToken)
                .ConfigureAwait(false);
            if (measured.Count == 0)
                return measured;

            IReadOnlyDictionary<string, int> runtimeLatencies = _dnsCryptRuntimeManager.GetResolverLatencies();
            _dnsCryptResolverCatalog = _dnsCryptResolverCatalog
                .Select(resolver => runtimeLatencies.TryGetValue(resolver.Name, out int runtimeLatency)
                    ? resolver with { LatencyMs = runtimeLatency }
                    : measured.TryGetValue(resolver.Name, out int latencyMs)
                        ? resolver with { LatencyMs = latencyMs }
                        : resolver)
                .ToArray();
            // The DNS page updates its local list directly from this return value. Raising the
            // global status event for every background latency batch caused duplicate page refreshes.
            return measured;
        }

        public Task<DnsCryptManagementStatus> InstallDnsCryptAsync(
            IProgress<DnsCryptComponentProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            return _dnsCryptCoordinator.ExecuteExclusiveAsync(
                "Install DNSCrypt",
                token => InstallDnsCryptCoreAsync(progress, token),
                cancellationToken);
        }

        private async Task<DnsCryptManagementStatus> InstallDnsCryptCoreAsync(
            IProgress<DnsCryptComponentProgress> progress,
            CancellationToken cancellationToken)
        {
            DnsCryptComponentStatus existing = _dnsCryptComponentManager.GetStatus();
            if (existing.IsInstalled)
                return GetDnsCryptManagementStatus();

            DnsCryptPreparedComponent prepared = null;
            DnsCryptActivationLease activation = null;
            bool activated = false;
            try
            {
                prepared = await _dnsCryptComponentManager.PrepareLatestAsync(progress, cancellationToken: cancellationToken).ConfigureAwait(false);
                progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.ValidatingRuntime));
                await _dnsCryptRuntimeManager.ValidatePreparedAsync(
                    prepared, CreateDnsCryptRuntimeOptions(_config.dnsPolicy?.dnsCrypt), cancellationToken).ConfigureAwait(false);

                progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.Activating));
                activation = _dnsCryptComponentManager.ActivatePrepared(prepared);
                activated = true;
                _dnsCryptComponentManager.SetAutoUpdate(_config.dnsPolicy?.dnsCrypt?.autoUpdate ?? true);
                if (IsDnsCryptRuntimeRequired())
                {
                    progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.Starting));
                    await StartDnsCryptWithResolvedResolversAsync(
                        _config.dnsPolicy.dnsCrypt, cancellationToken).ConfigureAwait(false);
                }
                _dnsCryptComponentManager.CommitActivation(activation);
                _latestDnsCryptVersion = prepared.Version;
                _dnsCryptComponentManager.RecordUpdateCheck(DateTimeOffset.UtcNow);
                progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.Ready));
                DnsCryptStatusChanged?.Invoke(this, EventArgs.Empty);
                return GetDnsCryptManagementStatus();
            }
            catch (Exception exception)
            {
                logger.Error(exception, "DNSCrypt Proxy installation failed.");
                if (activation is not null && !activation.Completed)
                {
                    try
                    {
                        _dnsCryptComponentManager.RollbackActivation(activation);
                    }
                    catch (Exception rollbackException)
                    {
                        logger.Error(rollbackException, "Failed to roll back DNSCrypt Proxy activation after install failure.");
                    }
                }
                else if (prepared is not null && !activated)
                {
                    _dnsCryptComponentManager.DiscardPrepared(prepared);
                }
                throw;
            }
        }

        public Task<DnsCryptManagementStatus> UpdateDnsCryptAsync(
            IProgress<DnsCryptComponentProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            return _dnsCryptCoordinator.ExecuteExclusiveAsync(
                "Update DNSCrypt",
                token => UpdateDnsCryptCoreAsync(progress, token),
                cancellationToken);
        }

        private async Task<DnsCryptManagementStatus> UpdateDnsCryptCoreAsync(
            IProgress<DnsCryptComponentProgress> progress,
            CancellationToken cancellationToken)
        {
            DnsCryptComponentStatus before = _dnsCryptComponentManager.GetStatus();
            if (!before.IsInstalled)
                return await InstallDnsCryptCoreAsync(progress, cancellationToken).ConfigureAwait(false);

            DnsCryptReleaseInfo release = await CheckDnsCryptUpdateCoreAsync(cancellationToken).ConfigureAwait(false);
            return await UpdateDnsCryptReleaseCoreAsync(before, release, progress, cancellationToken).ConfigureAwait(false);
        }

        private async Task<DnsCryptManagementStatus> UpdateDnsCryptReleaseCoreAsync(
            DnsCryptComponentStatus before,
            DnsCryptReleaseInfo release,
            IProgress<DnsCryptComponentProgress> progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(before);
            ArgumentNullException.ThrowIfNull(release);
            if (!before.IsInstalled || before.ActiveVersion is null)
                throw new InvalidOperationException("DNSCrypt Proxy is not installed.");

            if (release.Version <= before.ActiveVersion)
            {
                progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.Ready));
                return GetDnsCryptManagementStatus();
            }

            DnsCryptPreparedComponent prepared = null;
            DnsCryptActivationLease activation = null;
            bool activated = false;
            DnsCryptRuntimeStatus runtimeBefore = _dnsCryptRuntimeManager.GetStatus();
            bool wasRunning = runtimeBefore.IsRunning && IsDnsCryptRuntimeRequired();
            if (!IsDnsCryptRuntimeRequired() && runtimeBefore.IsServing)
                await StopDnsCryptRuntimeWhenNotRequiredAsync(cancellationToken).ConfigureAwait(false);

            // Validate the complete launch topology before publishing the Updating state. If this
            // throws, the runtime state remains untouched.
            DnsCryptRuntimeStartOptions options = CreateDnsCryptRuntimeOptions(_config.dnsPolicy?.dnsCrypt);
            DnsCryptRuntimeStatus updateSnapshot = _dnsCryptRuntimeManager.BeginUpdate();
            bool updateSucceeded = false;
            try
            {
                // Use the exact release object that was already selected and version-checked.
                // This avoids a second GitHub "latest" request changing underneath the
                // transaction and keeps automatic checks to their 24-hour cadence.
                prepared = await _dnsCryptComponentManager
                    .PrepareReleaseAsync(release, progress, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.ValidatingRuntime));
                await _dnsCryptRuntimeManager.ValidatePreparedAsync(prepared, options, cancellationToken).ConfigureAwait(false);

                if (wasRunning)
                    await _dnsCryptRuntimeManager.StopAsync(cancellationToken).ConfigureAwait(false);

                progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.Activating));
                activation = _dnsCryptComponentManager.ActivatePrepared(prepared);
                activated = true;
                if (wasRunning || IsDnsCryptRuntimeRequired())
                {
                    progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.Starting));
                    await StartDnsCryptWithResolvedResolversAsync(
                        _config.dnsPolicy?.dnsCrypt, cancellationToken).ConfigureAwait(false);
                }

                _dnsCryptComponentManager.CommitActivation(activation);
                _latestDnsCryptVersion = prepared.Version;
                _lastDnsCryptMaintenanceError = null;
                updateSucceeded = true;
                progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.Ready));
                DnsCryptStatusChanged?.Invoke(this, EventArgs.Empty);
                return GetDnsCryptManagementStatus();
            }
            catch (Exception exception)
            {
                logger.Error(exception, "DNSCrypt Proxy update failed.");
                bool componentRolledBack = false;
                if (activation is not null && !activation.Completed)
                {
                    try
                    {
                        _dnsCryptComponentManager.RollbackActivation(activation);
                        componentRolledBack = true;
                    }
                    catch (Exception rollbackException)
                    {
                        logger.Error(rollbackException, "DNSCrypt Proxy activation rollback failed after update failure.");
                    }
                }
                else if (prepared is not null && !activated)
                {
                    _dnsCryptComponentManager.DiscardPrepared(prepared);
                }
                else if (prepared is not null)
                {
                    DnsCryptComponentStatus current = _dnsCryptComponentManager.GetStatus();
                    if (current.ActiveVersion == prepared.Version && current.PreviousVersion is not null)
                    {
                        try
                        {
                            _dnsCryptComponentManager.Rollback();
                            componentRolledBack = true;
                        }
                        catch (Exception rollbackException)
                        {
                            logger.Error(rollbackException, "DNSCrypt Proxy rollback failed after update failure.");
                        }
                    }
                }

                if (componentRolledBack && (wasRunning || IsDnsCryptRuntimeRequired())
                    && !_dnsCryptRuntimeManager.GetStatus().IsServing)
                {
                    try
                    {
                        await StartDnsCryptWithResolvedResolversAsync(
                            _config.dnsPolicy?.dnsCrypt, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception restartException)
                    {
                        logger.Error(restartException, "Failed to restart the previous DNSCrypt Proxy after update rollback.");
                    }
                }

                if (wasRunning && !_dnsCryptRuntimeManager.GetStatus().IsServing)
                {
                    try
                    {
                        await StartDnsCryptWithResolvedResolversAsync(
                            _config.dnsPolicy?.dnsCrypt, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception restartException)
                    {
                        logger.Error(restartException, "Failed to restart DNSCrypt Proxy after update failure.");
                    }
                }

                DnsCryptStatusChanged?.Invoke(this, EventArgs.Empty);
                throw;
            }
            finally
            {
                _dnsCryptRuntimeManager.EndUpdate(updateSnapshot, updateSucceeded);
            }
        }

        public Task<DnsCryptManagementStatus> ReinstallDnsCryptAsync(
            IProgress<DnsCryptComponentProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            return _dnsCryptCoordinator.ExecuteExclusiveAsync(
                "Reinstall DNSCrypt",
                token => ReinstallDnsCryptCoreAsync(progress, token),
                cancellationToken);
        }

        private async Task<DnsCryptManagementStatus> ReinstallDnsCryptCoreAsync(
            IProgress<DnsCryptComponentProgress> progress,
            CancellationToken cancellationToken)
        {
            DnsCryptComponentStatus before = _dnsCryptComponentManager.GetStatus();
            if (!before.IsInstalled)
                return await InstallDnsCryptCoreAsync(progress, cancellationToken).ConfigureAwait(false);

            DnsCryptRuntimeStatus runtimeBefore = _dnsCryptRuntimeManager.GetStatus();
            bool wasRunning = runtimeBefore.IsRunning && IsDnsCryptRuntimeRequired();
            if (!IsDnsCryptRuntimeRequired() && runtimeBefore.IsServing)
                await StopDnsCryptRuntimeWhenNotRequiredAsync(cancellationToken).ConfigureAwait(false);

            // Validate before BeginUpdate() so invalid bootstrap/settings never leave the runtime
            // presentation stuck in Updating.
            DnsCryptRuntimeStartOptions options = CreateDnsCryptRuntimeOptions(_config.dnsPolicy?.dnsCrypt);
            DnsCryptRuntimeStatus updateSnapshot = _dnsCryptRuntimeManager.BeginUpdate();
            bool updateSucceeded = false;
            DnsCryptPreparedComponent prepared = null;
            DnsCryptActivationLease activation = null;
            bool activated = false;
            try
            {
                // Keep the active runtime serving DNS while the replacement is downloaded and
                // validated on its isolated loopback port. Downtime begins only at activation.
                prepared = await _dnsCryptComponentManager.PrepareLatestAsync(
                    progress, forceDownload: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.ValidatingRuntime));
                await _dnsCryptRuntimeManager.ValidatePreparedAsync(prepared, options, cancellationToken).ConfigureAwait(false);

                if (wasRunning)
                    await _dnsCryptRuntimeManager.StopAsync(cancellationToken).ConfigureAwait(false);

                progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.Activating));
                activation = _dnsCryptComponentManager.ActivatePrepared(prepared);
                activated = true;
                if (wasRunning || IsDnsCryptRuntimeRequired())
                {
                    progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.Starting));
                    await StartDnsCryptWithResolvedResolversAsync(
                        _config.dnsPolicy?.dnsCrypt, cancellationToken).ConfigureAwait(false);
                }
                _dnsCryptComponentManager.CommitActivation(activation);
                _latestDnsCryptVersion = prepared.Version;
                _dnsCryptComponentManager.RecordUpdateCheck(DateTimeOffset.UtcNow);
                updateSucceeded = true;
                progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.Ready));
                DnsCryptStatusChanged?.Invoke(this, EventArgs.Empty);
                return GetDnsCryptManagementStatus();
            }
            catch (Exception exception)
            {
                logger.Error(exception, "DNSCrypt Proxy reinstall failed.");
                bool componentRolledBack = false;
                if (activation is not null && !activation.Completed)
                {
                    try
                    {
                        _dnsCryptComponentManager.RollbackActivation(activation);
                        componentRolledBack = true;
                    }
                    catch (Exception rollbackException)
                    {
                        logger.Error(rollbackException, "Failed to roll back DNSCrypt Proxy activation after reinstall failure.");
                    }
                }
                else if (prepared is not null && !activated)
                {
                    _dnsCryptComponentManager.DiscardPrepared(prepared);
                }
                else if (activated && prepared is not null && prepared.Version != before.ActiveVersion)
                {
                    try
                    {
                        DnsCryptComponentStatus current = _dnsCryptComponentManager.GetStatus();
                        if (current.PreviousVersion is not null)
                        {
                            _dnsCryptComponentManager.Rollback();
                            componentRolledBack = true;
                        }
                    }
                    catch (Exception rollbackException)
                    {
                        logger.Error(rollbackException, "Failed to roll back DNSCrypt Proxy after reinstall failure.");
                    }
                }

                if (componentRolledBack && (wasRunning || IsDnsCryptRuntimeRequired())
                    && !_dnsCryptRuntimeManager.GetStatus().IsServing)
                {
                    try
                    {
                        await StartDnsCryptWithResolvedResolversAsync(
                            _config.dnsPolicy?.dnsCrypt, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception restartException)
                    {
                        logger.Error(restartException, "Failed to restart the previous DNSCrypt Proxy after reinstall rollback.");
                    }
                }

                if (wasRunning && !_dnsCryptRuntimeManager.GetStatus().IsServing)
                {
                    try { await StartDnsCryptWithResolvedResolversAsync(
                        _config.dnsPolicy?.dnsCrypt, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception restartException) { logger.Error(restartException, "Failed to restart DNSCrypt Proxy after reinstall failure."); }
                }
                DnsCryptStatusChanged?.Invoke(this, EventArgs.Empty);
                throw;
            }
            finally
            {
                _dnsCryptRuntimeManager.EndUpdate(updateSnapshot, updateSucceeded);
            }
        }

        private void EnsureConfiguredServerForDnsPolicy()
        {
            if (!_config.HasConfiguredServer)
            {
                throw new InvalidOperationException(
                    "A configured Shadowsocks server is required before enabling a non-system DNS policy.");
            }
        }

        public Task SetDnsPolicyAsync(DnsPolicyMode mode, CancellationToken cancellationToken = default)
        {
            if (mode != DnsPolicyMode.System && !_config.HasConfiguredServer)
            {
                return Task.FromException(new InvalidOperationException(
                    "A configured Shadowsocks server is required before enabling a non-system DNS policy."));
            }

            return _dnsCryptCoordinator.ExecuteExclusiveAsync(
                "Set DNS policy",
                token => SetDnsPolicyCoreAsync(mode, token),
                cancellationToken);
        }

        private async Task SetDnsPolicyCoreAsync(DnsPolicyMode mode, CancellationToken cancellationToken)
        {
            _config.dnsPolicy ??= new DnsPolicyConfig();
            _config.dnsPolicy.dnsCrypt ??= new DnsCryptConfig();
            DnsPolicyMode previous = _config.dnsPolicy.mode;
            bool startedForNewPolicy = false;

            try
            {
                if (mode == DnsPolicyMode.DnsCrypt)
                {
                    DnsCryptComponentStatus component = _dnsCryptComponentManager.GetStatus();
                    if (!component.IsInstalled)
                        throw new InvalidOperationException("DNSCrypt Proxy is not installed.");

                    // Resolve and pin the signed resolver catalog while the previous DNS policy
                    // is still active. Catalog maintenance resolves source hosts with Cloudflare DoH
                    // over the local Shadowsocks tunnel and falls back to Google DoH. It must finish
                    // before DNSCrypt fail-closed capture is armed. The active runtime itself receives
                    // only local [static.*] stamps and performs no source refresh or plaintext DNS.
                    DnsCryptRuntimeStartOptions startupOptions = null;
                    if (!_dnsCryptRuntimeManager.GetStatus().IsServing)
                    {
                        startupOptions = await CreateDnsCryptRuntimeOptionsForStartAsync(
                            _config.dnsPolicy.dnsCrypt, cancellationToken).ConfigureAwait(false);
                    }

                    // Start and health-check the pinned runtime while the previous DNS policy is
                    // still active. Only after it is serving do we arm DNSCrypt fail-closed capture.
                    // This avoids both bootstrap deadlocks and an unnecessary DNS outage during start.
                    if (!_dnsCryptRuntimeManager.GetStatus().IsServing)
                    {
                        Interlocked.Increment(ref _suppressDnsCaptureRefresh);
                        try
                        {
                            await _dnsCryptRuntimeManager.StartAsync(
                                startupOptions ?? throw new InvalidOperationException("DNSCrypt startup options were not prepared."),
                                cancellationToken).ConfigureAwait(false);
                            if (_config.dnsPolicy.dnsCrypt.automaticResolvers)
                                RememberAutomaticDnsCryptRuntimeResolver();
                            startedForNewPolicy = true;
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _suppressDnsCaptureRefresh);
                        }
                    }
                }

                _config.dnsPolicy.mode = mode;
                _trafficPolicyEngine.UpdateConfiguration(_config);
                Configuration.Save(_config);

                // Entering DNSCrypt: runtime is healthy before Admin redirect is enabled.
                // Leaving DNSCrypt: Admin redirect is disabled before the runtime stops.
                await ApplyTrafficCaptureConfigurationCoreAsync(
                    _config,
                    ensureDnsRuntime: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                await StopDnsCryptRuntimeWhenNotRequiredAsync(cancellationToken).ConfigureAwait(false);


                ConfigChanged?.Invoke(this, EventArgs.Empty);
                DnsCryptStatusChanged?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                _config.dnsPolicy.mode = previous;
                _trafficPolicyEngine.UpdateConfiguration(_config);
                Configuration.Save(_config);
                try
                {
                    await ApplyTrafficCaptureConfigurationCoreAsync(
                        _config,
                        ensureDnsRuntime: false,
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception restoreException)
                {
                    logger.Warn(restoreException, "Failed to restore previous DNS capture policy.");
                }

                if (startedForNewPolicy && previous != DnsPolicyMode.DnsCrypt)
                {
                    try { await _dnsCryptRuntimeManager.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception stopException) { logger.Warn(stopException, "Failed to stop DNSCrypt after DNS policy rollback."); }
                }
                throw;
            }
        }

        public Task SetDirectDnsPolicyAsync(string serverAddress, CancellationToken cancellationToken = default)
            => SetDirectDnsPolicyAsync(serverAddress, string.Empty, _config.dnsPolicy?.directDnsRouteThroughShadowsocks ?? false, cancellationToken);

        public Task SetDirectDnsPolicyAsync(
            string primaryServerAddress,
            string fallbackServerAddress,
            CancellationToken cancellationToken = default)
            => SetDirectDnsPolicyAsync(
                primaryServerAddress,
                fallbackServerAddress,
                _config.dnsPolicy?.directDnsRouteThroughShadowsocks ?? false,
                cancellationToken);

        public Task SetDirectDnsPolicyAsync(
            string primaryServerAddress,
            string fallbackServerAddress,
            bool routeThroughShadowsocks,
            CancellationToken cancellationToken = default)
        {
            EnsureConfiguredServerForDnsPolicy();
            string primary = NormalizeDirectDnsServer(primaryServerAddress, nameof(primaryServerAddress));
            string fallback = NormalizeDirectDnsServer(fallbackServerAddress, nameof(fallbackServerAddress));
            if (primary.Length == 0 && fallback.Length > 0)
                throw new ArgumentException("A primary DNS server is required when a fallback DNS server is configured.", nameof(primaryServerAddress));
            if (string.Equals(primary, fallback, StringComparison.OrdinalIgnoreCase))
                fallback = string.Empty;

            return _dnsCryptCoordinator.ExecuteExclusiveAsync(
                "Set direct DNS policy",
                token => SetDirectDnsPolicyCoreAsync(primary, fallback, routeThroughShadowsocks, token),
                cancellationToken);
        }

        private async Task SetDirectDnsPolicyCoreAsync(
            string primaryServerAddress,
            string fallbackServerAddress,
            bool routeThroughShadowsocks,
            CancellationToken cancellationToken)
        {
            _config.dnsPolicy ??= new DnsPolicyConfig();
            string previousPrimary = _config.dnsPolicy.directDnsServer;
            string previousFallback = _config.dnsPolicy.directDnsFallbackServer;
            bool previousRoute = _config.dnsPolicy.directDnsRouteThroughShadowsocks;
            _config.dnsPolicy.directDnsServer = primaryServerAddress;
            _config.dnsPolicy.directDnsFallbackServer = fallbackServerAddress;
            _config.dnsPolicy.directDnsRouteThroughShadowsocks = routeThroughShadowsocks;
            try
            {
                await SetDnsPolicyCoreAsync(DnsPolicyMode.Direct, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _config.dnsPolicy.directDnsServer = previousPrimary;
                _config.dnsPolicy.directDnsFallbackServer = previousFallback;
                _config.dnsPolicy.directDnsRouteThroughShadowsocks = previousRoute;
                Configuration.Save(_config);
                throw;
            }
        }

        private static string NormalizeDirectDnsServer(string serverAddress, string parameterName)
        {
            string value = serverAddress?.Trim().TrimStart('[').TrimEnd(']') ?? string.Empty;
            if (value.Length == 0)
                return string.Empty;
            if (!IPAddress.TryParse(value, out IPAddress address))
                throw new ArgumentException("DNS server address must be an IPv4 or IPv6 address.", parameterName);
            return address.ToString();
        }

        public Task SetCustomDohPolicyAsync(string url, CancellationToken cancellationToken = default)
            => SetCustomDohPolicyAsync(
                url,
                _config.dnsPolicy?.customDohRouteThroughShadowsocks ?? false,
                cancellationToken);

        public Task SetCustomDohPolicyAsync(
            string url,
            bool routeThroughShadowsocks,
            CancellationToken cancellationToken = default)
        {
            EnsureConfiguredServerForDnsPolicy();
            if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Custom DoH URL must be an absolute HTTPS URL.", nameof(url));
            }

            string normalized = uri.AbsoluteUri;
            return _dnsCryptCoordinator.ExecuteExclusiveAsync(
                "Set custom DoH policy",
                token => SetCustomDohPolicyCoreAsync(normalized, routeThroughShadowsocks, token),
                cancellationToken);
        }

        private async Task SetCustomDohPolicyCoreAsync(
            string url,
            bool routeThroughShadowsocks,
            CancellationToken cancellationToken)
        {
            _config.dnsPolicy ??= new DnsPolicyConfig();
            string previousUrl = _config.dnsPolicy.customDohUrl;
            bool previousRoute = _config.dnsPolicy.customDohRouteThroughShadowsocks;
            _config.dnsPolicy.customDohUrl = url;
            _config.dnsPolicy.customDohRouteThroughShadowsocks = routeThroughShadowsocks;
            try
            {
                await SetDnsPolicyCoreAsync(DnsPolicyMode.CustomDoh, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _config.dnsPolicy.customDohUrl = previousUrl;
                _config.dnsPolicy.customDohRouteThroughShadowsocks = previousRoute;
                Configuration.Save(_config);
                throw;
            }
        }

        public Task SaveDnsCryptSettingsAsync(DnsCryptConfig config, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            DnsCryptConfig snapshot = CloneDnsCryptConfig(config);
            return _dnsCryptCoordinator.ExecuteExclusiveAsync(
                "Save DNSCrypt settings",
                token => SaveDnsCryptSettingsCoreAsync(snapshot, token),
                cancellationToken);
        }

        private async Task SaveDnsCryptSettingsCoreAsync(DnsCryptConfig config, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);
            DnsCryptConfig next = await SanitizeDnsCryptConfigForSupportedResolversAsync(config, cancellationToken).ConfigureAwait(false);
            // Validate bootstrap topology even in User Mode, where no runtime is started. This
            // prevents persisting a configuration that can only fail when Admin Mode is enabled.
            DnsCryptBootstrapPolicy.Validate(_config, next);

            if (!next.ipv4Servers && !next.ipv6Servers)
                throw new ArgumentException("At least one DNSCrypt address family must be enabled.", nameof(config));

            _config.dnsPolicy ??= new DnsPolicyConfig();
            DnsCryptConfig previous = CloneDnsCryptConfig(_config.dnsPolicy.dnsCrypt ?? new DnsCryptConfig());
            bool wasRunning = IsDnsCryptRuntimeRequired() && _dnsCryptRuntimeManager.GetStatus().IsRunning;
            // Resolve Automatic mode against the signed catalog before persistence when the
            // managed component is available, so impossible filter combinations are rejected
            // immediately instead of failing on the next start. Settings can still be prepared
            // before installation; install/start performs the same filtered selection later.
            bool componentInstalled = _dnsCryptComponentManager.GetStatus().IsInstalled;
            DnsCryptRuntimeStartOptions nextOptions = componentInstalled
                ? await CreateDnsCryptRuntimeOptionsForStartAsync(next, cancellationToken).ConfigureAwait(false)
                : CreateDnsCryptRuntimeOptions(next);
            if (componentInstalled && !next.automaticResolvers)
            {
                await _dnsCryptRuntimeManager.ValidateSettingsAsync(
                    nextOptions,
                    TimeSpan.FromSeconds(8),
                    cancellationToken).ConfigureAwait(false);
            }

            if (wasRunning)
            {
                try
                {
                    using var applyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    applyTimeout.CancelAfter(TimeSpan.FromSeconds(next.automaticResolvers ? 25 : 12));
                    // Both modes start from a fully resolved, bounded server set. Automatic
                    // mode has already applied DNSSEC/no-log/no-filter/address-family constraints.
                    await _dnsCryptRuntimeManager.StartAsync(nextOptions, applyTimeout.Token).ConfigureAwait(false);
                    if (next.automaticResolvers)
                        RememberAutomaticDnsCryptRuntimeResolver();
                }
                catch (Exception applyException)
                {
                    string upstreamError = _dnsCryptRuntimeManager.GetLastRuntimeUpstreamError();
                    try
                    {
                        using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                        if (previous.automaticResolvers)
                        {
                            await StartDnsCryptWithResolvedResolversAsync(previous, rollbackTimeout.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            await _dnsCryptRuntimeManager.StartAsync(
                                await CreateDnsCryptRuntimeOptionsForStartAsync(previous, rollbackTimeout.Token).ConfigureAwait(false),
                                rollbackTimeout.Token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception rollbackException)
                    {
                        logger.Error(rollbackException, "Failed to restore DNSCrypt runtime after settings apply failed.");
                    }

                    if (applyException is OperationCanceledException)
                    {
                        string message = "DNSCrypt settings could not be activated before the operation timeout; the previous runtime was restored.";
                        if (!string.IsNullOrWhiteSpace(upstreamError))
                            message += $" Last dnscrypt-proxy error: {upstreamError}";
                        throw new TimeoutException(message, applyException);
                    }
                    throw;
                }
            }

            _config.dnsPolicy.dnsCrypt = next;
            _dnsCryptComponentManager.SetAutoUpdate(next.autoUpdate);
            Configuration.Save(_config);
            await StopDnsCryptRuntimeWhenNotRequiredAsync(cancellationToken).ConfigureAwait(false);
            if (_config.trafficCaptureMode == TrafficCaptureMode.Admin && _adminCaptureManager.IsBrokerRunning)
            {
                await ApplyTrafficCaptureConfigurationCoreAsync(
                    _config,
                    ensureDnsRuntime: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            ConfigChanged?.Invoke(this, EventArgs.Empty);
            DnsCryptStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task<DnsCryptRuntimeStatus> RestartDnsCryptAsync(CancellationToken cancellationToken = default)
        {
            return _dnsCryptCoordinator.ExecuteExclusiveAsync(
                "Restart DNSCrypt",
                RestartDnsCryptCoreAsync,
                cancellationToken);
        }

        private Task<DnsCryptRuntimeStatus> RestartDnsCryptCoreAsync(CancellationToken cancellationToken)
        {
            if (!IsDnsCryptRuntimeRequired())
                throw new InvalidOperationException("DNSCrypt runtime is active only while DNSCrypt mode is selected.");

            return RestartDnsCryptWithAutomaticResolversAsync(cancellationToken);
        }

        public Task RemoveDnsCryptAsync(
            IProgress<DnsCryptComponentProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            return _dnsCryptCoordinator.ExecuteExclusiveAsync(
                "Remove DNSCrypt",
                token => RemoveDnsCryptCoreAsync(progress, token),
                cancellationToken);
        }

        private async Task RemoveDnsCryptCoreAsync(
            IProgress<DnsCryptComponentProgress> progress,
            CancellationToken cancellationToken)
        {
            if (_config.dnsPolicy?.mode == DnsPolicyMode.DnsCrypt)
            {
                await SetDnsPolicyCoreAsync(DnsPolicyMode.System, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _dnsCryptRuntimeManager.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.Removing));
            _dnsCryptComponentManager.Remove();
            try
            {
                if (Directory.Exists(Shadowsocks.Core.Storage.AppStoragePaths.DnsCryptRuntimeDirectory))
                    Directory.Delete(Shadowsocks.Core.Storage.AppStoragePaths.DnsCryptRuntimeDirectory, recursive: true);
            }
            catch (Exception exception)
            {
                logger.Warn(exception, "Failed to remove DNSCrypt runtime directory.");
            }
            _latestDnsCryptVersion = null;
            progress?.Report(new DnsCryptComponentProgress(DnsCryptComponentStage.Ready));
            ConfigChanged?.Invoke(this, EventArgs.Empty);
            DnsCryptStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private bool IsDnsCryptRuntimeRequired()
        {
            return _config.HasConfiguredServer
                && ShouldRunDnsCryptRuntime(
                    _config.trafficCaptureMode,
                    _config.dnsPolicy?.mode ?? DnsPolicyMode.System);
        }

        internal static bool ShouldRunDnsCryptRuntime(
            TrafficCaptureMode captureMode,
            DnsPolicyMode dnsMode)
        {
            _ = captureMode; // capture mode controls coverage, not DNSCrypt process lifetime.
            return dnsMode == DnsPolicyMode.DnsCrypt;
        }

        internal IPEndPoint ResolveOutboundEndpoint(string host, int port)
        {
            if (port is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("An outbound host is required.", nameof(host));
            string normalizedHost = host.Trim().TrimStart('[').TrimEnd(']');
            if (IPAddress.TryParse(normalizedHost, out IPAddress parsed))
                return new IPEndPoint(parsed, port);

            if (_config.dnsPolicy?.mode == DnsPolicyMode.DnsCrypt)
            {
                DnsCryptRuntimeStatus runtime = _dnsCryptRuntimeManager.GetStatus();
                if (!runtime.IsServing)
                {
                    throw new InvalidOperationException(
                        $"DNSCrypt is selected but unavailable; refusing plaintext system-DNS resolution for '{normalizedHost}'.");
                }

                IReadOnlyList<IPAddress> resolved = _dnsCryptRuntimeManager
                    .ResolveHostAsync(normalizedHost)
                    .GetAwaiter()
                    .GetResult();
                IPAddress address = resolved.FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
                    ?? (resolved.Count > 0
                        ? resolved[0]
                        : throw new SocketException((int)SocketError.HostNotFound));
                return new IPEndPoint(address, port);
            }

            IPAddress[] systemAddresses = Dns.GetHostAddresses(normalizedHost);
            IPAddress selected = systemAddresses.FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
                ?? (systemAddresses.Length > 0
                    ? systemAddresses[0]
                    : throw new SocketException((int)SocketError.HostNotFound));
            return new IPEndPoint(selected, port);
        }

#nullable enable
        public static Task<IpCountryInfo?> GetServerCountryAsync(Server server, CancellationToken cancellationToken = default)
        {
            if (server is null || string.IsNullOrWhiteSpace(server.server))
                return Task.FromResult<IpCountryInfo?>(null);
            return IpCountryService.ResolveHostAsync(server.server, cancellationToken);
        }
#nullable restore

        public Task<bool> TestDnsCryptAsync(CancellationToken cancellationToken = default)
        {
            return _dnsCryptCoordinator.ExecuteExclusiveAsync(
                "Test DNSCrypt",
                token => _dnsCryptRuntimeManager.HealthCheckAsync(token),
                cancellationToken);
        }

        public Task<DnsPrivacySelfTestResult> TestDnsPrivacyAsync(CancellationToken cancellationToken = default)
        {
            return _dnsCryptCoordinator.ExecuteExclusiveAsync(
                "Test DNS privacy",
                TestDnsPrivacyCoreAsync,
                cancellationToken);
        }

        private async Task<DnsPrivacySelfTestResult> TestDnsPrivacyCoreAsync(CancellationToken cancellationToken)
        {
            DnsCryptManagementStatus management = GetDnsCryptManagementStatus();
            TrafficCaptureStatus traffic = GetTrafficCaptureStatus();
            DnsCryptConfig config = _config.dnsPolicy?.dnsCrypt ?? new DnsCryptConfig();

            bool runtimeHealthy = management.Runtime.IsServing
                && await _dnsCryptRuntimeManager.HealthCheckAsync(cancellationToken).ConfigureAwait(false);
            bool adminInterception = traffic.RuntimeMode == TrafficRuntimeMode.Admin
                && traffic.WinDivertActive
                && traffic.DnsInterceptionActive;
            // Administrator-mode DNSCrypt interception is an effective fail-closed
            // security invariant even when an older persisted preference contains false.
            bool plaintextFallbackBlocked = (_config.dnsPolicy?.mode ?? DnsPolicyMode.System) == DnsPolicyMode.DnsCrypt
                || config.failClosed;

            bool bootstrapDisabled = false;
            bool systemDnsIgnored = false;
            bool automaticResolverPinned = !config.automaticResolvers;
            if (!string.IsNullOrWhiteSpace(management.Runtime.ConfigPath)
                && File.Exists(management.Runtime.ConfigPath))
            {
                string toml = await File.ReadAllTextAsync(management.Runtime.ConfigPath, cancellationToken).ConfigureAwait(false);
                bootstrapDisabled = !DnsCryptTomlGenerator.RuntimeUsesPlaintextBootstrap(toml)
                    && !toml.Contains("[sources.public-resolvers]", StringComparison.Ordinal);
                systemDnsIgnored = toml.Contains("ignore_system_dns = true", StringComparison.Ordinal);
                if (config.automaticResolvers)
                {
                    automaticResolverPinned = toml.Contains("server_names = [", StringComparison.Ordinal)
                        && toml.Contains("[static.'", StringComparison.Ordinal)
                        && toml.Contains("stamp = 'sdns://", StringComparison.Ordinal)
                        && !toml.Contains("[sources.public-resolvers]", StringComparison.Ordinal)
                        && !toml.Contains("9.9.9.11:53", StringComparison.Ordinal)
                        && !toml.Contains("8.8.8.8:53", StringComparison.Ordinal);
                }
            }

            IReadOnlyList<DnsCryptResolverInfo> activeResolvers = management.ActiveResolvers ?? Array.Empty<DnsCryptResolverInfo>();
            string transport = string.Join(
                ", ",
                activeResolvers
                    .Select(resolver => resolver.Protocol?.Trim())
                    .Where(protocol => !string.IsNullOrWhiteSpace(protocol))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(transport))
                transport = "—";

            string upstream = string.Join(
                ", ",
                activeResolvers.Select(resolver => resolver.Name).Where(name => !string.IsNullOrWhiteSpace(name)));
            if (string.IsNullOrWhiteSpace(upstream))
                upstream = "—";

            var failures = new List<string>();
            if ((_config.dnsPolicy?.mode ?? DnsPolicyMode.System) != DnsPolicyMode.DnsCrypt)
                failures.Add("DNSCrypt mode is not selected.");
            if (!runtimeHealthy)
                failures.Add("DNSCrypt runtime health-check failed.");
            if (!adminInterception)
                failures.Add("Administrator DNS interception is not active.");
            if (!plaintextFallbackBlocked)
                failures.Add("Plaintext DNS fallback is not blocked.");
            if (!bootstrapDisabled)
                failures.Add("The active DNS runtime still permits plaintext bootstrap DNS or remote resolver-source refresh.");
            if (!systemDnsIgnored)
                failures.Add("The active DNS runtime does not ignore system DNS.");
            if (!automaticResolverPinned)
                failures.Add("Automatic mode is not pinned to a filtered resolver set from the signed catalog.");

            return new DnsPrivacySelfTestResult(
                failures.Count == 0,
                runtimeHealthy,
                adminInterception,
                plaintextFallbackBlocked,
                bootstrapDisabled,
                systemDnsIgnored,
                automaticResolverPinned,
                config.routeThroughShadowsocks,
                upstream,
                transport,
                failures);
        }

        private async Task<DnsCryptRuntimeStatus> StartDnsCryptWithResolvedResolversAsync(
            DnsCryptConfig config, CancellationToken cancellationToken)
        {
            DnsCryptConfig source = config ?? new DnsCryptConfig();
            DnsCryptRuntimeStartOptions options = await CreateDnsCryptRuntimeOptionsForStartAsync(
                source, cancellationToken).ConfigureAwait(false);

            // Automatic mode resolves a concrete filtered resolver set before runtime start.
            // dnscrypt-proxy remains constrained to that set and never falls back to an
            // unrelated provider when the selected automatic resolver is unavailable.
            DnsCryptRuntimeStatus status = await _dnsCryptRuntimeManager.StartAsync(
                options, cancellationToken).ConfigureAwait(false);
            if (source.automaticResolvers)
                RememberAutomaticDnsCryptRuntimeResolver();
            return status;
        }

        private Task<DnsCryptRuntimeStatus> RestartDnsCryptWithAutomaticResolversAsync(CancellationToken cancellationToken)
            => StartDnsCryptWithResolvedResolversAsync(_config.dnsPolicy?.dnsCrypt, cancellationToken);

#nullable enable annotations
        private async Task<IReadOnlyList<DnsCryptResolverInfo>> GetAndCacheDnsCryptResolversAsync(
            DnsCryptConfig config, CancellationToken cancellationToken)
        {
            if (_dnsCryptResolverCatalog.Length > 0
                && DateTimeOffset.UtcNow - _dnsCryptResolverCatalogLoadedUtc < TimeSpan.FromHours(6))
                return _dnsCryptResolverCatalog;

            IReadOnlyList<DnsCryptResolverInfo> resolvers;
            try
            {
                resolvers = await _dnsCryptRuntimeManager
                    .ListResolversAsync(CreateDnsCryptRuntimeOptions(config), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.Warn(exception, "DNSCrypt signed resolver catalog could not be refreshed or loaded.");
                throw new DnsCryptBootstrapException(
                    "DNSCrypt could not refresh or load the signed resolver catalog.",
                    exception);
            }
            resolvers = await EnrichResolverCountriesAsync(resolvers, cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, int> runtimeLatencies = _dnsCryptRuntimeManager.GetResolverLatencies();
            DnsCryptResolverInfo[] enrichedResolvers = resolvers
                .Select(resolver => runtimeLatencies.TryGetValue(resolver.Name, out int latencyMs)
                    ? resolver with { LatencyMs = latencyMs }
                    : resolver)
                .ToArray();
            _dnsCryptResolverCatalog = enrichedResolvers;
            _dnsCryptResolverCatalogLoadedUtc = DateTimeOffset.UtcNow;
            return enrichedResolvers;
        }

        private async Task<IReadOnlyList<DnsCryptResolverInfo>> EnrichResolverCountriesAsync(
            IReadOnlyList<DnsCryptResolverInfo> resolvers,
            CancellationToken cancellationToken)
        {
            if (resolvers.Count == 0)
                return resolvers;

            // dnscrypt-proxy -list-all exposes resolver endpoint addresses. Resolve their
            // actual IPs and use GeoIP as the sole source of country metadata. Resolver
            // names/descriptions are deliberately ignored: city/provider naming is not
            // authoritative geography and Anycast is not a country.
            var resolverIps = new Dictionary<string, IReadOnlyList<System.Net.IPAddress>>(StringComparer.OrdinalIgnoreCase);
            var allIps = new List<System.Net.IPAddress>();
            using var catalogDohResolver = new ShadowsocksDohResolver(_config.LocalHost, _config.localPort);

            foreach (DnsCryptResolverInfo resolver in resolvers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var literalAddresses = new List<System.Net.IPAddress>();
                var hostNames = new List<string>();
                foreach (string endpoint in resolver.Addresses ?? Array.Empty<string>())
                {
                    string host = IpCountryService.NormalizeHost(endpoint);
                    if (string.IsNullOrWhiteSpace(host))
                        continue;

                    if (System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? ip))
                    {
                        if (IpCountryService.IsPublicAddress(ip))
                            literalAddresses.Add(ip);
                    }
                    else
                    {
                        hostNames.Add(host);
                    }
                }

                var resolvedHostAddresses = new List<System.Net.IPAddress>();
                if (literalAddresses.Count == 0)
                {
                    foreach (string host in hostNames.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            System.Net.IPAddress[] resolved = await catalogDohResolver
                                .ResolveAsync(host, cancellationToken)
                                .ConfigureAwait(false);
                            resolvedHostAddresses.AddRange(resolved.Where(IpCountryService.IsPublicAddress));
                        }
                        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                        {
                            logger.Debug(exception, $"DNSCrypt resolver endpoint could not be resolved for GeoIP: {host}");
                        }
                    }
                }

                // A literal address from the DNS stamp is the authoritative endpoint.
                // Hostname resolution is used only when the stamp has no fixed address.
                System.Net.IPAddress[] unique = literalAddresses
                    .Concat(resolvedHostAddresses)
                    .Distinct()
                    .ToArray();
                resolverIps[resolver.Name] = unique;
                allIps.AddRange(unique);
            }

            IReadOnlyDictionary<string, IpCountryInfo> countries = await IpCountryService
                .LookupAddressesAsync(allIps.Distinct(), cancellationToken)
                .ConfigureAwait(false);

            return resolvers.Select(resolver =>
            {
                if (!resolverIps.TryGetValue(resolver.Name, out IReadOnlyList<System.Net.IPAddress>? ips))
                    return resolver with { CountryCode = string.Empty, CountryName = string.Empty, FlagEmoji = string.Empty };

                IpCountryInfo[] located = ips
                    .Select(ip => countries.TryGetValue(ip.ToString(), out IpCountryInfo? country) ? country : null)
                    .Where(country => country is not null)
                    .Cast<IpCountryInfo>()
                    .ToArray();
                string[] countryCodes = located
                    .Select(country => country.CountryCode)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                // A resolver with endpoints geolocated to different countries has no
                // single country. Do not choose one arbitrarily just to fill the UI.
                if (countryCodes.Length != 1)
                    return resolver with { CountryCode = string.Empty, CountryName = string.Empty, FlagEmoji = string.Empty };

                IpCountryInfo country = located.First(item =>
                    string.Equals(item.CountryCode, countryCodes[0], StringComparison.OrdinalIgnoreCase));
                return resolver with
                {
                    CountryCode = country.CountryCode,
                    CountryName = country.CountryName,
                    FlagEmoji = country.FlagEmoji,
                };
            }).ToArray();
        }

#nullable restore annotations

        private async Task<DnsCryptRuntimeStartOptions> CreateDnsCryptRuntimeOptionsForStartAsync(
            DnsCryptConfig config, CancellationToken cancellationToken)
        {
            DnsCryptConfig snapshot = CloneDnsCryptConfig(config ?? new DnsCryptConfig());
            IReadOnlyList<DnsCryptResolverInfo> catalog;
            if (snapshot.automaticResolvers)
            {
                var listingConfig = CloneDnsCryptConfig(snapshot);
                listingConfig.serverNames = [];
                listingConfig.routeThroughShadowsocks = false;
                catalog = await GetAndCacheDnsCryptResolversAsync(
                    listingConfig, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<IpCountryInfo> targetCountries = await GetDnsCryptTargetCountriesAsync(
                    cancellationToken).ConfigureAwait(false);
                IReadOnlyList<string> selected = targetCountries.Count > 0
                    ? await DnsCryptCountrySelector.SelectAsync(
                        catalog,
                        targetCountries,
                        snapshot,
                        IpCountryService.ResolveHostAsync,
                        cancellationToken).ConfigureAwait(false)
                    : Array.Empty<string>();
                if (selected.Count == 0)
                    selected = DnsCryptCountrySelector.SelectFallback(catalog, snapshot);
                if (selected.Count == 0)
                {
                    throw new InvalidOperationException(
                        "No DNSCrypt/DoH resolver matches the selected automatic filters (DNSSEC, logging, filtering and address family)."
                    );
                }

                snapshot.serverNames = selected.ToList();
                _dnsCryptAutomaticServerNames = snapshot.serverNames.ToArray();
                DnsCryptStatusChanged?.Invoke(this, EventArgs.Empty);
                logger.Info(
                    "DNSCrypt automatic resolver selection: {0} (DNSSEC={1}, NoLog={2}, NoFilter={3}, IPv4={4}, IPv6={5}).",
                    string.Join(", ", snapshot.serverNames),
                    snapshot.requireDnssec,
                    snapshot.requireNoLog,
                    snapshot.requireNoFilter,
                    snapshot.ipv4Servers,
                    snapshot.ipv6Servers);
            }
            else
            {
                _dnsCryptAutomaticServerNames = Array.Empty<string>();
                List<string> originalNames = snapshot.serverNames.ToList();
                snapshot = await SanitizeDnsCryptConfigForSupportedResolversAsync(snapshot, cancellationToken).ConfigureAwait(false);
                if (!originalNames.SequenceEqual(snapshot.serverNames, StringComparer.OrdinalIgnoreCase)
                    && _config.dnsPolicy?.dnsCrypt is not null)
                {
                    _config.dnsPolicy.dnsCrypt.serverNames = snapshot.serverNames.ToList();
                    Configuration.Save(_config);
                    DnsCryptStatusChanged?.Invoke(this, EventArgs.Empty);
                }

                var listingConfig = CloneDnsCryptConfig(snapshot);
                listingConfig.serverNames = [];
                listingConfig.routeThroughShadowsocks = false;
                catalog = await GetAndCacheDnsCryptResolversAsync(listingConfig, cancellationToken).ConfigureAwait(false);
            }

            if (snapshot.routeThroughShadowsocks)
                DnsCryptBootstrapPolicy.Validate(_config, snapshot);

            Dictionary<string, string> stamps = DnsCryptRuntimeManager.BuildStaticResolverStampMap(
                catalog, snapshot.serverNames);
            return new DnsCryptRuntimeStartOptions(snapshot, _config.localPort)
            {
                ShadowsocksSocks5Host = _config.LocalHost,
                StaticResolverStamps = stamps,
            };
        }

        private async Task<IReadOnlyList<IpCountryInfo>> GetDnsCryptTargetCountriesAsync(CancellationToken cancellationToken)
        {
            IEnumerable<Server> servers = !string.IsNullOrWhiteSpace(_config.strategy)
                ? _config.configs?.Where(server => server?.IsConfigured == true) ?? []
                : _config.GetCurrentServer() is Server current && current.IsConfigured ? [current] : [];

            var countries = new List<IpCountryInfo>();
            foreach (Server server in servers.GroupBy(item => item.server, StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IpCountryInfo country = await IpCountryService.ResolveHostAsync(server.server, cancellationToken).ConfigureAwait(false);
                if (country is not null && !countries.Any(item => string.Equals(item.CountryCode, country.CountryCode, StringComparison.OrdinalIgnoreCase)))
                    countries.Add(country);
            }
            return countries;
        }

        private void QueueDnsCryptAutomaticResolverRefresh()
        {
            if (_config.dnsPolicy?.dnsCrypt?.automaticResolvers != true || !IsDnsCryptRuntimeRequired())
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _dnsCryptCoordinator.ExecuteExclusiveAsync(
                        "Refresh automatic DNSCrypt resolver",
                        RestartDnsCryptWithAutomaticResolversAsync,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.Warn(exception, "Automatic DNSCrypt resolver refresh failed after the active Shadowsocks server changed.");
                }
            });
        }

        private DnsCryptRuntimeStartOptions CreateDnsCryptRuntimeOptions(DnsCryptConfig config)
        {
            DnsCryptConfig snapshot = CloneDnsCryptConfig(config ?? new DnsCryptConfig());
            if (snapshot.routeThroughShadowsocks)
                DnsCryptBootstrapPolicy.Validate(_config, snapshot);
            return new DnsCryptRuntimeStartOptions(snapshot, _config.localPort)
            {
                ShadowsocksSocks5Host = _config.LocalHost,
            };
        }

        private async Task<DnsCryptConfig> SanitizeDnsCryptConfigForSupportedResolversAsync(
            DnsCryptConfig source,
            CancellationToken cancellationToken)
        {
            DnsCryptConfig snapshot = CloneDnsCryptConfig(source ?? new DnsCryptConfig());
            if (snapshot.automaticResolvers)
            {
                // Persisted Automatic mode keeps serverNames empty. Runtime startup derives
                // a fresh resolver set from the signed catalog and the persisted filters.
                snapshot.serverNames = [];
                return snapshot;
            }
            if (snapshot.serverNames.Count == 0)
                throw new ArgumentException("Manual DNSCrypt resolver selection requires at least one resolver.", nameof(source));

            IReadOnlyList<DnsCryptResolverInfo> catalog = _dnsCryptResolverCatalog;
            if (catalog.Count == 0)
            {
                try
                {
                    var listingConfig = CloneDnsCryptConfig(snapshot);
                    listingConfig.serverNames = [];
                    listingConfig.routeThroughShadowsocks = false;
                    catalog = await GetAndCacheDnsCryptResolversAsync(listingConfig, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // ODoH is hard-disabled in generated TOML. Preserve names while the signed
                    // catalog is temporarily unavailable instead of silently changing Manual mode;
                    // candidate validation still proves the selected runtime before persistence.
                    logger.Warn(exception, "Could not verify manual resolver names against the signed catalog.");
                    return snapshot;
                }
            }

            var supportedNames = catalog
                .Where(IsSupportedDnsCryptProxyResolver)
                .Select(resolver => resolver.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<string> sanitized = snapshot.serverNames
                .Where(supportedNames.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (sanitized.Count != snapshot.serverNames.Count)
                logger.Warn("Removed unsupported or unknown resolver names from the DNSCrypt configuration.");
            if (sanitized.Count == 0)
                throw new ArgumentException("Manual resolver selection must contain at least one DNSCrypt or DoH resolver.", nameof(source));
            snapshot.serverNames = sanitized;
            return snapshot;
        }

        private static bool IsSupportedDnsCryptProxyResolver(DnsCryptResolverInfo resolver)
        {
            if (resolver is null || string.IsNullOrWhiteSpace(resolver.Protocol))
                return false;

            return resolver.Protocol.Contains("DNSCrypt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(resolver.Protocol.Trim(), "DoH", StringComparison.OrdinalIgnoreCase);
        }

        private static DnsCryptResolverInfo CreateResolverPlaceholder(string name)
        {
            // Runtime can report an active server name before the signed catalog has been loaded
            // into the UI cache. Do not infer protocol, address family or privacy capabilities from
            // provider/server names; those properties are authoritative only when catalog metadata
            // is available.
            return new DnsCryptResolverInfo(
                name,
                string.Empty,
                false,
                null,
                false,
                false,
                string.Empty,
                Array.Empty<string>());
        }

        private static DnsCryptConfig CloneDnsCryptConfig(DnsCryptConfig source)
        {
            source ??= new DnsCryptConfig();
            return new DnsCryptConfig
            {
                autoUpdate = source.autoUpdate,
                requireDnssec = source.requireDnssec,
                requireNoLog = source.requireNoLog,
                requireNoFilter = source.requireNoFilter,
                ipv4Servers = source.ipv4Servers,
                ipv6Servers = source.ipv6Servers,
                routeThroughShadowsocks = source.routeThroughShadowsocks,
                automaticResolvers = source.automaticResolvers,
                failClosed = source.failClosed,
                serverNames = source.serverNames?
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? [],
            };
        }

    }
}
