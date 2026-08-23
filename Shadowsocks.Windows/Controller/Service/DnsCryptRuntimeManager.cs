using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Shadowsocks.Controller.Traffic;
using Shadowsocks.Core.Storage;

namespace Shadowsocks.Controller.Service
{
    public sealed partial class DnsCryptRuntimeManager : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly TimeSpan VersionCheckTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ConfigurationCheckTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ResolverListTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);
        private static readonly JsonSerializerOptions s_resolverJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };
        private static readonly TimeSpan[] DefaultRestartDelays =
        [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(10),
        ];
        private readonly Func<DnsCryptComponentStatus> componentStatusProvider;
        private readonly DnsCryptComponentManager ownedComponentManager;
        private readonly string runtimeDirectory;
        private readonly IDnsCryptRuntimePlatform platform;
        private readonly Func<int> portAllocator;
        private readonly Func<int, TimeSpan, CancellationToken, Task<bool>> healthChecker;
        private readonly Func<bool> diagnosticLoggingEnabled;
        private readonly Func<bool> verboseLoggingEnabled;
        private readonly IDnsCryptResolverCatalogBootstrapper resolverCatalogBootstrapper;
        private readonly TimeSpan[] restartDelays;
        private readonly TimeSpan startupTimeout;
        private readonly SemaphoreSlim lifecycleLock = new(1, 1);
        private readonly object stateLock = new();
        private readonly ConcurrentDictionary<string, int> resolverLatencyCache = new(StringComparer.OrdinalIgnoreCase);

        private IDnsCryptRunningProcess process;
        private DnsCryptRuntimeStartOptions lastStartOptions;
        private DnsCryptRuntimeStatus status;
        private string lastRuntimeUpstreamError;
        private string selectedRuntimeResolverName;
        private CancellationTokenSource recoveryCancellation = new();
        private volatile bool stopRequested = true;
        private bool disposed;

        public DnsCryptRuntimeManager(
            DnsCryptComponentManager componentManager = null,
            Func<bool> diagnosticLoggingEnabled = null,
            Func<bool> verboseLoggingEnabled = null)
        {
            if (componentManager is null)
            {
                ownedComponentManager = new DnsCryptComponentManager();
                componentManager = ownedComponentManager;
            }

            componentStatusProvider = componentManager.GetStatus;
            runtimeDirectory = Path.GetFullPath(AppStoragePaths.DnsCryptRuntimeDirectory);
            platform = new SystemDnsCryptRuntimePlatform();
            portAllocator = AllocateLoopbackPort;
            healthChecker = DnsHealthCheckAsync;
            this.diagnosticLoggingEnabled = diagnosticLoggingEnabled ?? (() => true);
            this.verboseLoggingEnabled = verboseLoggingEnabled ?? (() => true);
            resolverCatalogBootstrapper = new DnsCryptResolverCatalogBootstrapper();
            restartDelays = DefaultRestartDelays;
            startupTimeout = StartupTimeout;
            CleanupTransientRuntimeDirectories();
            status = CreateStoppedStatus();
        }

        internal DnsCryptRuntimeManager(
            Func<DnsCryptComponentStatus> componentStatusProvider,
            string runtimeDirectory,
            IDnsCryptRuntimePlatform platform,
            Func<int> portAllocator,
            Func<int, TimeSpan, CancellationToken, Task<bool>> healthChecker,
            IReadOnlyList<TimeSpan> restartDelays = null,
            TimeSpan? startupTimeout = null,
            Func<bool> diagnosticLoggingEnabled = null,
            Func<bool> verboseLoggingEnabled = null,
            IDnsCryptResolverCatalogBootstrapper resolverCatalogBootstrapper = null)
        {
            ArgumentNullException.ThrowIfNull(componentStatusProvider);
            ArgumentNullException.ThrowIfNull(platform);
            ArgumentNullException.ThrowIfNull(portAllocator);
            ArgumentNullException.ThrowIfNull(healthChecker);

            this.componentStatusProvider = componentStatusProvider;
            if (string.IsNullOrWhiteSpace(runtimeDirectory))
                throw new ArgumentException("A DNSCrypt runtime directory is required.", nameof(runtimeDirectory));
            this.runtimeDirectory = Path.GetFullPath(runtimeDirectory);
            this.platform = platform;
            this.portAllocator = portAllocator;
            this.healthChecker = healthChecker;
            this.diagnosticLoggingEnabled = diagnosticLoggingEnabled ?? (() => true);
            this.verboseLoggingEnabled = verboseLoggingEnabled ?? (() => true);
            this.resolverCatalogBootstrapper = resolverCatalogBootstrapper ?? NoOpDnsCryptResolverCatalogBootstrapper.Instance;
            this.restartDelays = (restartDelays ?? DefaultRestartDelays).ToArray();
            if (this.restartDelays.Length == 0 || this.restartDelays.Any(delay => delay < TimeSpan.Zero))
                throw new ArgumentException("DNSCrypt restart delays must contain at least one non-negative delay.", nameof(restartDelays));
            this.startupTimeout = startupTimeout ?? StartupTimeout;
            if (this.startupTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(startupTimeout));
            CleanupTransientRuntimeDirectories();
            status = CreateStoppedStatus();
        }

        public event EventHandler<DnsCryptRuntimeStatusChangedEventArgs> StatusChanged;
        public event EventHandler ResolverMetricsChanged;

        public DnsCryptRuntimeStatus GetStatus()
        {
            ThrowIfDisposed();
            lock (stateLock)
            {
                if (process is null && status.State is DnsCryptRuntimeState.Stopped or DnsCryptRuntimeState.NotInstalled)
                    status = CreateStoppedStatus();
                return status;
            }
        }

        public IReadOnlyList<string> GetActiveResolverNames()
        {
            ThrowIfDisposed();
            lock (stateLock)
            {
                // Prefer the resolver dnscrypt-proxy actually selected. Both Automatic and
                // Manual mode are constrained to explicit DNSCrypt/DoH names at runtime.
                if (!string.IsNullOrWhiteSpace(selectedRuntimeResolverName))
                    return new[] { selectedRuntimeResolverName };
                string[] configured = lastStartOptions?.Config?.serverNames?
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray() ?? Array.Empty<string>();
                return configured;
            }
        }

        public IReadOnlyDictionary<string, int> GetResolverLatencies()
        {
            ThrowIfDisposed();
            return resolverLatencyCache.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        public string GetLastRuntimeUpstreamError()
        {
            ThrowIfDisposed();
            lock (stateLock)
                return lastRuntimeUpstreamError ?? string.Empty;
        }

        public async Task<DnsCryptRuntimeStatus> StartAsync(
            DnsCryptRuntimeStartOptions options,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            DnsCryptRuntimeStartOptions snapshot = SnapshotOptions(options);
            await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                stopRequested = false;
                ResetRecoveryCancellation();
                await StopProcessCoreAsync(updateStatus: false, CancellationToken.None).ConfigureAwait(false);
                try
                {
                    lock (stateLock)
                    {
                        lastStartOptions = snapshot;
                    }
                    return await StartProcessCoreAsync(snapshot, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    stopRequested = true;
                    SetStatus(CreateStoppedStatus());
                    throw;
                }
                catch (Exception exception)
                {
                    stopRequested = true;
                    DnsCryptRuntimeStatus currentStatus = GetStatus();
                    if (currentStatus.State != DnsCryptRuntimeState.NotInstalled)
                    {
                        SetStatus(new DnsCryptRuntimeStatus(
                            DnsCryptRuntimeState.Failed,
                            TryGetInstalledVersion(),
                            0,
                            0,
                            null,
                            GetConfigPath(),
                            exception.Message));
                    }
                    throw;
                }
            }
            finally
            {
                lifecycleLock.Release();
            }
        }

        public async Task<DnsCryptRuntimeStatus> RestartAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            DnsCryptRuntimeStartOptions options;
            lock (stateLock)
            {
                options = lastStartOptions;
            }
            if (options is null)
                throw new InvalidOperationException("DNSCrypt Proxy has not been started yet, so there is no runtime configuration to restart.");
            return await StartAsync(options, cancellationToken).ConfigureAwait(false);
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                stopRequested = true;
                recoveryCancellation.Cancel();
                await StopProcessCoreAsync(updateStatus: true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lifecycleLock.Release();
            }
        }

        /// <summary>
        /// Marks component maintenance as Updating without stopping a healthy runtime.
        /// </summary>
        public DnsCryptRuntimeStatus BeginUpdate()
        {
            ThrowIfDisposed();
            DnsCryptRuntimeStatus previous = GetStatus();
            if (previous.State == DnsCryptRuntimeState.NotInstalled)
                return previous;

            SetStatus(new DnsCryptRuntimeStatus(
                DnsCryptRuntimeState.Updating,
                previous.Version,
                previous.ProcessId,
                previous.Port,
                previous.StartedAtUtc,
                previous.ConfigPath,
                null));
            return previous;
        }

        /// <summary>
        /// Leaves Updating unless another runtime transition already replaced it.
        /// </summary>
        public void EndUpdate(DnsCryptRuntimeStatus previous, bool succeeded)
        {
            ThrowIfDisposed();
            DnsCryptRuntimeStatus current = GetStatus();
            if (current.State != DnsCryptRuntimeState.Updating)
                return;

            IDnsCryptRunningProcess currentProcess;
            lock (stateLock)
            {
                currentProcess = process;
            }

            if (currentProcess is not null && !currentProcess.HasExited)
            {
                SetStatus(new DnsCryptRuntimeStatus(
                    DnsCryptRuntimeState.Running,
                    TryGetInstalledVersion() ?? current.Version ?? previous?.Version,
                    currentProcess.Id,
                    current.Port,
                    current.StartedAtUtc,
                    current.ConfigPath ?? GetConfigPath(),
                    null));
                return;
            }

            if (!succeeded && previous?.State == DnsCryptRuntimeState.Failed)
            {
                SetStatus(previous);
                return;
            }

            if (!succeeded && previous?.IsRunning == true)
            {
                SetStatus(new DnsCryptRuntimeStatus(
                    DnsCryptRuntimeState.Failed,
                    TryGetInstalledVersion() ?? previous.Version,
                    0,
                    0,
                    null,
                    GetConfigPath(),
                    "DNSCrypt Proxy maintenance failed and the previous runtime could not be restored."));
                return;
            }

            SetStatus(CreateStoppedStatus());
        }

        public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            DnsCryptRuntimeStatus current = GetStatus();
            if (current.State != DnsCryptRuntimeState.Running || current.Port is < 1 or > 65535)
                return false;
            return await healthChecker(current.Port, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Uses dnscrypt-proxy's signed resolver source in an isolated maintenance profile and
        /// -list-all -json to obtain the current resolver catalog before active runtime startup.
        /// dnscrypt-proxy receives no remote source URL and no plaintext/system-DNS bootstrap path.
        /// </summary>
        public async Task<IReadOnlyList<DnsCryptResolverInfo>> ListResolversAsync(
            DnsCryptRuntimeStartOptions options,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            DnsCryptComponentStatus componentStatus = componentStatusProvider();
            if (componentStatus is null || !componentStatus.IsInstalled || componentStatus.ActiveVersion is null
                || string.IsNullOrWhiteSpace(componentStatus.ExecutablePath) || !File.Exists(componentStatus.ExecutablePath))
                throw new InvalidOperationException("DNSCrypt Proxy is not installed.");

            DnsCryptRuntimeStartOptions snapshot = SnapshotOptions(options);
            await EnsureResolverCatalogCacheAsync(snapshot, cancellationToken).ConfigureAwait(false);
            string listDirectory = Path.Combine(runtimeDirectory, $".list-{Guid.NewGuid():N}");
            Directory.CreateDirectory(listDirectory);
            SeedResolverSourceCache(listDirectory);
            try
            {
                int port = portAllocator();
                if (port is < 1 or > 65535)
                    throw new InvalidOperationException("DNSCrypt runtime port allocator returned an invalid port.");

                string configPath = Path.Combine(listDirectory, "dnscrypt-proxy.toml");
                string toml = DnsCryptTomlGenerator.Generate(
                    port,
                    snapshot.Config,
                    snapshot.ShadowsocksSocks5Port,
                    DnsCryptTomlPurpose.ResolverCatalog);
                await File.WriteAllTextAsync(configPath, toml, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

                DnsCryptCommandResult result = await platform.ExecuteAsync(
                    componentStatus.ExecutablePath,
                    listDirectory,
                    ["-list-all", "-json", "-config", configPath],
                    ResolverListTimeout,
                    cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    LogCommandOutput("LIST", result);
                    throw new InvalidOperationException($"dnscrypt-proxy -list-all failed with exit code {result.ExitCode}: {result.StandardError?.Trim()}");
                }

                IReadOnlyDictionary<string, int> commandLatencies = ParseResolverLatencies(result.StandardError);
                foreach ((string name, int latencyMs) in commandLatencies)
                    resolverLatencyCache[name] = latencyMs;
                DnsCryptResolverInfo[] resolvers = ParseResolverList(result.StandardOutput)
                    .Select(resolver => resolverLatencyCache.TryGetValue(resolver.Name, out int latencyMs)
                        ? resolver with { LatencyMs = latencyMs }
                        : resolver)
                    .ToArray();
                PersistResolverSourceCache(listDirectory);
                if (IsDiagnosticLoggingEnabled())
                {
                    Logger.Info(
                        "DNSCryptProxy | LIST | Loaded {0} resolvers; RTT available for {1}.",
                        resolvers.Length,
                        resolvers.Count(resolver => resolver.LatencyMs.HasValue));
                }
                return resolvers;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(listDirectory))
                        Directory.Delete(listDirectory, recursive: true);
                }
                catch (Exception exception)
                {
                    Logger.Warn(exception, "DNSCryptProxy | LIST | Failed to remove resolver-list runtime directory.");
                }
            }
        }

        internal static IReadOnlyList<DnsCryptResolverInfo> ParseResolverList(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<DnsCryptResolverInfo>();

            try
            {
                List<ResolverSummaryDto> summaries =
                    JsonSerializer.Deserialize<List<ResolverSummaryDto>>(json, s_resolverJsonOptions) ?? [];
                return summaries
                    .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                    .Select(item => new DnsCryptResolverInfo(
                        item.Name.Trim(),
                        item.Proto?.Trim() ?? string.Empty,
                        item.IPv6,
                        item.Dnssec,
                        item.NoLog,
                        item.NoFilter,
                        item.Description?.Trim() ?? string.Empty,
                        item.Addrs?.Where(address => !string.IsNullOrWhiteSpace(address)).Select(address => address.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [])
                    {
                        Ports = item.Ports?.Where(port => port is >= 1 and <= 65535).Distinct().ToArray() ?? Array.Empty<int>(),
                        Stamp = item.Stamp?.Trim() ?? string.Empty,
                    })
                    .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("dnscrypt-proxy returned an invalid resolver list JSON payload.", exception);
            }
        }

        /// <summary>
        /// Validates a prepared, not-yet-active DNSCrypt version without changing component metadata
        /// or replacing the currently running DNSCrypt instance. The candidate runs from an isolated
        /// temporary runtime directory on its own loopback port and is always terminated afterwards.
        /// </summary>
        public async Task ValidatePreparedAsync(
            DnsCryptPreparedComponent prepared,
            DnsCryptRuntimeStartOptions options,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(prepared);
            ArgumentNullException.ThrowIfNull(prepared.Version);
            ArgumentException.ThrowIfNullOrWhiteSpace(prepared.ExecutablePath);
            if (!File.Exists(prepared.ExecutablePath))
                throw new FileNotFoundException("Prepared DNSCrypt Proxy executable was not found.", prepared.ExecutablePath);

            DnsCryptRuntimeStartOptions snapshot = SnapshotOptions(options);
            string validationDirectory = Path.Combine(runtimeDirectory, $".validate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(validationDirectory);
            IDnsCryptRunningProcess candidate = null;
            try
            {
                // Verify the exact prepared binary before it is allowed to perform any catalog/network
                // operation. A version mismatch must fail closed before resolver discovery.
                DnsCryptCommandResult versionResult = await platform.ExecuteAsync(
                    prepared.ExecutablePath, validationDirectory, ["-version"], VersionCheckTimeout, cancellationToken).ConfigureAwait(false);
                LogCommandOutput("VALIDATE-VERSION", versionResult);
                if (versionResult.ExitCode != 0)
                    throw new InvalidOperationException($"dnscrypt-proxy -version failed with exit code {versionResult.ExitCode}.");
                Version reportedVersion = ParseReportedVersion(versionResult.StandardOutput);
                if (reportedVersion is null || !reportedVersion.Equals(prepared.Version))
                {
                    throw new InvalidDataException(
                        $"DNSCrypt executable version '{versionResult.StandardOutput?.Trim()}' does not match prepared component version {prepared.Version}.");
                }

                snapshot = await ResolvePreparedValidationOptionsAsync(
                    prepared.ExecutablePath, validationDirectory, snapshot, cancellationToken).ConfigureAwait(false);

                int port = portAllocator();
                if (port is < 1 or > 65535)
                    throw new InvalidOperationException("DNSCrypt runtime port allocator returned an invalid port.");

                string configPath = Path.Combine(validationDirectory, "dnscrypt-proxy.toml");
                string toml = DnsCryptTomlGenerator.Generate(
                    port,
                    snapshot.Config,
                    snapshot.ShadowsocksSocks5Port,
                    staticResolverStamps: snapshot.StaticResolverStamps);
                await File.WriteAllTextAsync(configPath, toml, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

                await CheckConfigurationAsync(
                    prepared.ExecutablePath,
                    validationDirectory,
                    configPath,
                    "VALIDATE-CHECK",
                    cancellationToken).ConfigureAwait(false);

                candidate = platform.Start(
                    prepared.ExecutablePath, validationDirectory, ["-config", configPath],
                    line => LogRuntimeLine(false, line), line => LogRuntimeLine(true, line));

                using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startupCts.CancelAfter(startupTimeout);
                while (!startupCts.IsCancellationRequested && !candidate.HasExited)
                {
                    if (await healthChecker(port, TimeSpan.FromSeconds(1), startupCts.Token).ConfigureAwait(false))
                    {
                        if (IsDiagnosticLoggingEnabled())
                            Logger.Info($"DNSCryptProxy | VALIDATE | Prepared version {prepared.Version} passed runtime DNS health-check on 127.0.0.1:{port}.");
                        return;
                    }
                    await Task.Delay(200, startupCts.Token).ConfigureAwait(false);
                }

                if (candidate.HasExited)
                    throw new InvalidOperationException("Prepared DNSCrypt Proxy exited before the DNS health-check succeeded.");
                throw new TimeoutException($"Prepared DNSCrypt Proxy did not pass the DNS health-check within {startupTimeout.TotalSeconds:0.###} seconds.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Prepared DNSCrypt Proxy did not pass the DNS health-check within {startupTimeout.TotalSeconds:0.###} seconds.");
            }
            finally
            {
                if (candidate is not null)
                    await TerminateProcessAsync(candidate, CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (Directory.Exists(validationDirectory))
                        Directory.Delete(validationDirectory, recursive: true);
                }
                catch (Exception exception)
                {
                    Logger.Warn(exception, "DNSCryptProxy | VALIDATE | Failed to remove validation runtime directory.");
                }
            }
        }

        /// <summary>
        /// Validates a DNSCrypt settings snapshot in an isolated candidate process while the
        /// current runtime remains online. This prevents a bad manual resolver from tearing down
        /// a healthy DNS path before reachability is known.
        /// </summary>
        public async Task ValidateSettingsAsync(
            DnsCryptRuntimeStartOptions options,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

            DnsCryptComponentStatus componentStatus = componentStatusProvider();
            if (componentStatus is null || !componentStatus.IsInstalled || componentStatus.ActiveVersion is null
                || string.IsNullOrWhiteSpace(componentStatus.ExecutablePath) || !File.Exists(componentStatus.ExecutablePath))
            {
                throw new InvalidOperationException("DNSCrypt Proxy is not installed.");
            }

            DnsCryptRuntimeStartOptions snapshot = SnapshotOptions(options);
            string validationDirectory = Path.Combine(runtimeDirectory, $".settings-{Guid.NewGuid():N}");
            Directory.CreateDirectory(validationDirectory);
            IDnsCryptRunningProcess candidate = null;
            string candidateError = string.Empty;
            object candidateErrorLock = new();
            try
            {
                int port = portAllocator();
                if (port is < 1 or > 65535)
                    throw new InvalidOperationException("DNSCrypt runtime port allocator returned an invalid port.");

                string configPath = Path.Combine(validationDirectory, "dnscrypt-proxy.toml");
                string toml = DnsCryptTomlGenerator.Generate(
                    port,
                    snapshot.Config,
                    snapshot.ShadowsocksSocks5Port,
                    staticResolverStamps: snapshot.StaticResolverStamps);
                await File.WriteAllTextAsync(configPath, toml, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

                candidate = platform.Start(
                    componentStatus.ExecutablePath,
                    validationDirectory,
                    ["-config", configPath],
                    line => LogSettingsValidationLine(false, line, value =>
                    {
                        lock (candidateErrorLock) candidateError = value;
                    }),
                    line => LogSettingsValidationLine(true, line, value =>
                    {
                        lock (candidateErrorLock) candidateError = value;
                    }));

                using var validationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                validationCts.CancelAfter(timeout);
                while (!validationCts.IsCancellationRequested && !candidate.HasExited)
                {
                    if (await healthChecker(port, TimeSpan.FromSeconds(1), validationCts.Token).ConfigureAwait(false))
                        return;
                    await Task.Delay(150, validationCts.Token).ConfigureAwait(false);
                }

                string lastError;
                lock (candidateErrorLock) lastError = candidateError;
                if (candidate.HasExited)
                    throw new InvalidOperationException(AppendDnsCryptUpstreamError(
                        "Selected DNSCrypt resolver exited before the DNS health-check succeeded.", lastError));
                throw new TimeoutException(AppendDnsCryptUpstreamError(
                    $"Selected DNSCrypt resolver did not become reachable within {timeout.TotalSeconds:0.###} seconds.", lastError));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                string lastError;
                lock (candidateErrorLock) lastError = candidateError;
                throw new TimeoutException(AppendDnsCryptUpstreamError(
                    $"Selected DNSCrypt resolver did not become reachable within {timeout.TotalSeconds:0.###} seconds.", lastError));
            }
            finally
            {
                if (candidate is not null)
                    await TerminateProcessAsync(candidate, CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (Directory.Exists(validationDirectory))
                        Directory.Delete(validationDirectory, recursive: true);
                }
                catch (Exception exception)
                {
                    Logger.Warn(exception, "DNSCryptProxy | SETTINGS-VALIDATE | Failed to remove validation runtime directory.");
                }
            }
        }

        private static string AppendDnsCryptUpstreamError(string message, string upstreamError)
            => string.IsNullOrWhiteSpace(upstreamError) ? message : $"{message} Last dnscrypt-proxy error: {upstreamError}";

        private async Task<DnsCryptRuntimeStatus> StartProcessCoreAsync(
            DnsCryptRuntimeStartOptions options,
            CancellationToken cancellationToken)
        {
            DnsCryptComponentStatus componentStatus = componentStatusProvider();
            if (componentStatus is null || !componentStatus.IsInstalled || componentStatus.ActiveVersion is null
                || string.IsNullOrWhiteSpace(componentStatus.ExecutablePath) || !File.Exists(componentStatus.ExecutablePath))
            {
                SetStatus(new DnsCryptRuntimeStatus(
                    DnsCryptRuntimeState.NotInstalled,
                    null,
                    0,
                    0,
                    null,
                    GetConfigPath(),
                    "DNSCrypt Proxy is not installed."));
                throw new InvalidOperationException("DNSCrypt Proxy is not installed.");
            }

            Directory.CreateDirectory(runtimeDirectory);
            int port = portAllocator();
            if (port is < 1 or > 65535)
                throw new InvalidOperationException("DNSCrypt runtime port allocator returned an invalid port.");

            string configPath = GetConfigPath();
            string toml = DnsCryptTomlGenerator.Generate(
                port,
                options.Config,
                options.ShadowsocksSocks5Port,
                staticResolverStamps: options.StaticResolverStamps);
            await File.WriteAllTextAsync(configPath, toml, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            SetStatus(new DnsCryptRuntimeStatus(
                DnsCryptRuntimeState.Starting,
                componentStatus.ActiveVersion,
                0,
                port,
                null,
                configPath,
                null));

            DnsCryptCommandResult versionResult = await platform.ExecuteAsync(
                componentStatus.ExecutablePath,
                runtimeDirectory,
                ["-version"],
                VersionCheckTimeout,
                cancellationToken).ConfigureAwait(false);
            LogCommandOutput("VERSION", versionResult);
            if (versionResult.ExitCode != 0)
                throw new InvalidOperationException($"dnscrypt-proxy -version failed with exit code {versionResult.ExitCode}.");

            Version reportedVersion = ParseReportedVersion(versionResult.StandardOutput);
            if (reportedVersion is null || !reportedVersion.Equals(componentStatus.ActiveVersion))
            {
                throw new InvalidDataException(
                    $"DNSCrypt executable version '{versionResult.StandardOutput?.Trim()}' does not match " +
                    $"installed component version {componentStatus.ActiveVersion}.");
            }

            IDnsCryptRunningProcess startedProcess = null;
            lock (stateLock)
            {
                lastRuntimeUpstreamError = null;
                selectedRuntimeResolverName = null;
            }
            try
            {
                startedProcess = platform.Start(
                    componentStatus.ExecutablePath,
                    runtimeDirectory,
                    ["-config", configPath],
                    line => LogRuntimeLine(false, line),
                    line => LogRuntimeLine(true, line));

                using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startupCts.CancelAfter(startupTimeout);
                bool healthy = false;
                while (!startupCts.IsCancellationRequested && !startedProcess.HasExited)
                {
                    if (await healthChecker(port, TimeSpan.FromSeconds(1), startupCts.Token).ConfigureAwait(false))
                    {
                        healthy = true;
                        break;
                    }
                    await Task.Delay(200, startupCts.Token).ConfigureAwait(false);
                }

                if (!healthy)
                {
                    if (startedProcess.HasExited)
                    {
                        string upstreamError;
                        lock (stateLock)
                            upstreamError = lastRuntimeUpstreamError;
                        throw new DnsCryptRuntimeStartupException(AppendDnsCryptUpstreamError(
                            "DNSCrypt Proxy exited before the DNS health-check succeeded.",
                            upstreamError));
                    }
                    throw new DnsCryptRuntimeStartupException(BuildStartupTimeoutMessage());
                }

                DnsCryptRuntimeStatus runningStatus = PromoteStartedProcessToRunning(
                    startedProcess,
                    componentStatus.ActiveVersion,
                    port,
                    configPath);
                if (IsDiagnosticLoggingEnabled())
                    Logger.Info($"DNSCryptProxy | RUNTIME | Started {componentStatus.ActiveVersion} on 127.0.0.1:{port} (PID {startedProcess.Id}).");
                return runningStatus;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (startedProcess is not null)
                    await TerminateProcessAsync(startedProcess, CancellationToken.None).ConfigureAwait(false);
                throw new DnsCryptRuntimeStartupException(BuildStartupTimeoutMessage());
            }
            catch
            {
                if (startedProcess is not null)
                    await TerminateProcessAsync(startedProcess, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        private async Task CheckConfigurationAsync(
            string executablePath,
            string workingDirectory,
            string configPath,
            string logPhase,
            CancellationToken cancellationToken)
        {
            try
            {
                DnsCryptCommandResult result = await platform.ExecuteAsync(
                    executablePath,
                    workingDirectory,
                    ["-check", "-config", configPath],
                    ConfigurationCheckTimeout,
                    cancellationToken).ConfigureAwait(false);
                LogCommandOutput(logPhase, result);
                if (result.ExitCode != 0)
                {
                    throw new InvalidDataException(
                        $"dnscrypt-proxy -check failed with exit code {result.ExitCode}: {result.StandardError?.Trim()}");
                }
            }
            catch (TimeoutException exception)
            {
                // dnscrypt-proxy -check may perform resolver certificate/network initialization.
                // A slow/filtered network must not produce a false
                // configuration failure: the real process startup + DNS probe below is the
                // authoritative readiness check. Syntax/config errors still fail immediately
                // because dnscrypt-proxy exits non-zero before this timeout.
                if (IsDiagnosticLoggingEnabled())
                {
                    Logger.Warn(exception,
                        $"DNSCryptProxy | {logPhase} | Configuration preflight timed out; continuing with runtime health-check.");
                }
            }
        }

        private DnsCryptRuntimeStatus PromoteStartedProcessToRunning(
            IDnsCryptRunningProcess startedProcess,
            Version version,
            int port,
            string configPath)
        {
            EventHandler<DnsCryptRuntimeStatusChangedEventArgs> handler;
            DnsCryptRuntimeStatus runningStatus;
            lock (stateLock)
            {
                if (startedProcess.HasExited)
                    throw new InvalidOperationException("DNSCrypt Proxy exited immediately after passing the DNS health-check.");

                process = startedProcess;
                runningStatus = new DnsCryptRuntimeStatus(
                    DnsCryptRuntimeState.Running,
                    version,
                    startedProcess.Id,
                    port,
                    DateTimeOffset.UtcNow,
                    configPath,
                    null);
                status = runningStatus;
                handler = StatusChanged;
            }

            // Publish Running before arming crash recovery. If the process exited in the
            // tiny gap before event subscription, the HasExited check below converts that
            // into the normal unexpected-exit path. This keeps status notifications ordered
            // as Running -> Failed/Recovered instead of allowing a stale Running event after
            // recovery has already started.
            RaiseStatusChanged(handler, runningStatus);
            startedProcess.Exited += OnProcessExited;
            if (startedProcess.HasExited)
                OnProcessExited(startedProcess, EventArgs.Empty);
            return runningStatus;
        }

        private async Task StopProcessCoreAsync(bool updateStatus, CancellationToken cancellationToken)
        {
            IDnsCryptRunningProcess current;
            lock (stateLock)
            {
                current = process;
                if (current is not null)
                {
                    current.Exited -= OnProcessExited;
                    if (ReferenceEquals(process, current))
                        process = null;
                }
            }

            if (current is not null)
            {
                await TerminateProcessAsync(current, cancellationToken).ConfigureAwait(false);
                if (IsDiagnosticLoggingEnabled())
                    Logger.Info("DNSCryptProxy | RUNTIME | Stopped.");
            }
            if (updateStatus)
                SetStatus(CreateStoppedStatus());
        }

        private static async Task TerminateProcessAsync(IDnsCryptRunningProcess target, CancellationToken cancellationToken)
        {
            try
            {
                if (!target.HasExited)
                    target.Kill();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(StopTimeout);
                await target.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception e)
            {
                Logger.Warn(e, "DNSCryptProxy | RUNTIME | Failed to terminate process cleanly.");
            }
            finally
            {
                target.Dispose();
            }
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            if (sender is not IDnsCryptRunningProcess exitedProcess)
                return;
            exitedProcess.Exited -= OnProcessExited;
            _ = RecoverFromUnexpectedExitAsync(exitedProcess);
        }

        private async Task RecoverFromUnexpectedExitAsync(IDnsCryptRunningProcess exitedProcess)
        {
            DnsCryptRuntimeStartOptions options;
            CancellationToken recoveryToken;
            lock (stateLock)
            {
                if (disposed || stopRequested || !ReferenceEquals(process, exitedProcess))
                    return;
                process = null;
                options = lastStartOptions;
                recoveryToken = recoveryCancellation.Token;
            }
            exitedProcess.Dispose();

            SetStatus(new DnsCryptRuntimeStatus(
                DnsCryptRuntimeState.Failed,
                TryGetInstalledVersion(),
                0,
                0,
                null,
                GetConfigPath(),
                "DNSCrypt Proxy exited unexpectedly."));
            Logger.Warn("DNSCryptProxy | RUNTIME | Process exited unexpectedly; starting recovery sequence.");

            for (int attempt = 0; attempt < restartDelays.Length; attempt++)
            {
                if (options is null || recoveryToken.IsCancellationRequested || stopRequested)
                    return;

                try
                {
                    await Task.Delay(restartDelays[attempt], recoveryToken).ConfigureAwait(false);
                    await lifecycleLock.WaitAsync(recoveryToken).ConfigureAwait(false);
                    try
                    {
                        if (stopRequested || process is not null)
                            return;
                        Logger.Warn($"DNSCryptProxy | RUNTIME | Restart attempt {attempt + 1}/{restartDelays.Length}.");
                        await StartProcessCoreAsync(options, recoveryToken).ConfigureAwait(false);
                        return;
                    }
                    finally
                    {
                        lifecycleLock.Release();
                    }
                }
                catch (OperationCanceledException) when (recoveryToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    Logger.Warn(exception, $"DNSCryptProxy | RUNTIME | Restart attempt {attempt + 1} failed.");
                }
            }

            SetStatus(new DnsCryptRuntimeStatus(
                DnsCryptRuntimeState.Failed,
                TryGetInstalledVersion(),
                0,
                0,
                null,
                GetConfigPath(),
                $"DNSCrypt Proxy failed after {restartDelays.Length} automatic restart attempts."));
        }

        private void CleanupTransientRuntimeDirectories()
        {
            if (!Directory.Exists(runtimeDirectory))
                return;

            foreach (string directory in Directory.EnumerateDirectories(runtimeDirectory))
            {
                string name = Path.GetFileName(directory);
                if (!name.StartsWith(".validate-", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith(".list-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception exception)
                {
                    Logger.Warn(exception, $"DNSCryptProxy | CLEANUP | Failed to remove stale runtime directory '{directory}'.");
                }
            }
        }

        private DnsCryptRuntimeStatus CreateStoppedStatus()
        {
            DnsCryptComponentStatus component = null;
            try { component = componentStatusProvider(); } catch { }
            bool installed = component?.IsInstalled == true;
            return new DnsCryptRuntimeStatus(
                installed ? DnsCryptRuntimeState.Stopped : DnsCryptRuntimeState.NotInstalled,
                installed ? component.ActiveVersion : null,
                0,
                0,
                null,
                GetConfigPath(),
                null);
        }

        private Version TryGetInstalledVersion()
        {
            try
            {
                DnsCryptComponentStatus component = componentStatusProvider();
                return component?.IsInstalled == true ? component.ActiveVersion : null;
            }
            catch
            {
                return null;
            }
        }

        private void SetStatus(DnsCryptRuntimeStatus newStatus)
        {
            EventHandler<DnsCryptRuntimeStatusChangedEventArgs> handler;
            lock (stateLock)
            {
                status = newStatus;
                handler = StatusChanged;
            }
            RaiseStatusChanged(handler, newStatus);
        }

        private void RaiseStatusChanged(
            EventHandler<DnsCryptRuntimeStatusChangedEventArgs> handler,
            DnsCryptRuntimeStatus newStatus)
        {
            try
            {
                handler?.Invoke(this, new DnsCryptRuntimeStatusChangedEventArgs(newStatus));
            }
            catch (Exception e)
            {
                Logger.Warn(e, "DNSCryptProxy | RUNTIME | StatusChanged subscriber failed.");
            }
        }

        private void ResetRecoveryCancellation()
        {
            CancellationTokenSource old = recoveryCancellation;
            recoveryCancellation = new CancellationTokenSource();
            try { old.Cancel(); } catch { }
            old.Dispose();
        }

        private async Task<DnsCryptRuntimeStartOptions> ResolvePreparedValidationOptionsAsync(
            string executablePath,
            string validationDirectory,
            DnsCryptRuntimeStartOptions options,
            CancellationToken cancellationToken)
        {
            string[] requestedNames = DnsCryptTomlGenerator.NormalizeServerNames(options.Config.serverNames);
            if (requestedNames.Length > 0 && HasStaticResolverStamps(options, requestedNames))
                return options;

            // Prepared-runtime validation is self-contained too: discover resolver stamps through
            // the signed catalog in the isolated maintenance directory, then validate the exact
            // selected resolver(s) as local [static.*] entries. The candidate runtime never
            // performs a remote resolver-source refresh.
            DnsCryptRuntimeStartOptions catalogOptions = SnapshotOptions(options);
            catalogOptions.Config.serverNames = [];
            catalogOptions.Config.routeThroughShadowsocks = false;

            int catalogPort = portAllocator();
            if (catalogPort is < 1 or > 65535)
                throw new InvalidOperationException("DNSCrypt runtime port allocator returned an invalid port.");

            await EnsureResolverCatalogCacheAsync(options, cancellationToken).ConfigureAwait(false);
            SeedResolverSourceCache(validationDirectory);

            string catalogConfigPath = Path.Combine(validationDirectory, "dnscrypt-proxy.catalog.toml");
            string catalogToml = DnsCryptTomlGenerator.Generate(
                catalogPort,
                catalogOptions.Config,
                purpose: DnsCryptTomlPurpose.ResolverCatalog);
            await File.WriteAllTextAsync(
                catalogConfigPath, catalogToml, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            DnsCryptCommandResult listResult;
            try
            {
                listResult = await platform.ExecuteAsync(
                    executablePath,
                    validationDirectory,
                    ["-list-all", "-json", "-config", catalogConfigPath],
                    ResolverListTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new DnsCryptBootstrapException(
                    "Prepared DNSCrypt could not refresh or load the signed resolver catalog.",
                    exception);
            }
            if (listResult.ExitCode != 0)
            {
                LogCommandOutput("VALIDATE-LIST", listResult);
                throw new DnsCryptBootstrapException(
                    $"Prepared dnscrypt-proxy -list-all failed with exit code {listResult.ExitCode}: {listResult.StandardError?.Trim()}");
            }

            IReadOnlyList<DnsCryptResolverInfo> resolvers;
            try
            {
                resolvers = ParseResolverList(listResult.StandardOutput);
            }
            catch (InvalidDataException exception)
            {
                throw new DnsCryptBootstrapException(
                    "Prepared DNSCrypt returned an invalid signed resolver catalog.",
                    exception);
            }
            IReadOnlyList<string> selected = requestedNames.Length > 0
                ? requestedNames
                : DnsCryptCountrySelector.SelectFallback(resolvers, options.Config);
            if (selected.Count == 0)
            {
                throw new InvalidOperationException(
                    "No DNSCrypt/DoH resolver in the signed catalog matches the selected automatic filters for prepared-runtime validation.");
            }

            Dictionary<string, string> stamps = BuildStaticResolverStampMap(resolvers, selected);
            PersistResolverSourceCache(validationDirectory);
            DnsCryptRuntimeStartOptions resolved = SnapshotOptions(options);
            resolved.Config.serverNames = selected.ToList();
            return resolved with { StaticResolverStamps = stamps };
        }

        private static bool HasStaticResolverStamps(
            DnsCryptRuntimeStartOptions options,
            string[] serverNames)
        {
            if (options.StaticResolverStamps is null)
                return false;
            return serverNames.All(name =>
                options.StaticResolverStamps.TryGetValue(name, out string stamp)
                && !string.IsNullOrWhiteSpace(stamp)
                && stamp.StartsWith("sdns://", StringComparison.Ordinal));
        }

        internal static Dictionary<string, string> BuildStaticResolverStampMap(
            IReadOnlyList<DnsCryptResolverInfo> resolvers,
            IReadOnlyList<string> serverNames)
        {
            ArgumentNullException.ThrowIfNull(resolvers);
            ArgumentNullException.ThrowIfNull(serverNames);
            var byName = resolvers
                .Where(resolver => resolver is not null && !string.IsNullOrWhiteSpace(resolver.Name))
                .GroupBy(resolver => resolver.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string serverName in serverNames)
            {
                if (!byName.TryGetValue(serverName, out DnsCryptResolverInfo resolver)
                    || string.IsNullOrWhiteSpace(resolver.Stamp)
                    || !resolver.Stamp.StartsWith("sdns://", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"The signed resolver catalog does not contain a usable stamp for '{serverName}'.");
                }
                result[serverName] = resolver.Stamp.Trim();
            }
            return result;
        }


        private async Task EnsureResolverCatalogCacheAsync(
            DnsCryptRuntimeStartOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (ReferenceEquals(resolverCatalogBootstrapper, NoOpDnsCryptResolverCatalogBootstrapper.Instance))
                return;
            if (!options.ShadowsocksSocks5Port.HasValue
                || options.ShadowsocksSocks5Port.Value is < 1 or > IPEndPoint.MaxPort)
            {
                throw new DnsCryptBootstrapException(
                    "A working local Shadowsocks SOCKS5 endpoint is required to refresh the DNSCrypt resolver catalog through DoH.");
            }

            string host = string.IsNullOrWhiteSpace(options.ShadowsocksSocks5Host)
                ? "127.0.0.1"
                : options.ShadowsocksSocks5Host;
            await resolverCatalogBootstrapper.EnsureFreshAsync(
                runtimeDirectory,
                host,
                options.ShadowsocksSocks5Port.Value,
                cancellationToken).ConfigureAwait(false);
        }

        private static DnsCryptRuntimeStartOptions SnapshotOptions(DnsCryptRuntimeStartOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(options.Config);
            DnsCryptConfig source = options.Config;
            var copy = new DnsCryptConfig
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
                serverNames = source.serverNames?.ToList() ?? [],
            };
            return new DnsCryptRuntimeStartOptions(copy, options.ShadowsocksSocks5Port)
            {
                ShadowsocksSocks5Host = options.ShadowsocksSocks5Host,
                StaticResolverStamps = options.StaticResolverStamps is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(options.StaticResolverStamps, StringComparer.OrdinalIgnoreCase),
            };
        }

        private void PersistResolverSourceCache(string sourceDirectory)
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory))
                return;

            try
            {
                // dnscrypt-proxy treats the resolver source as an authenticated pair. Persisting
                // only the markdown file makes the offline catalog cache unusable; the catalog
                // profile intentionally has no remote URLs or plaintext/system-DNS fallback.
                PersistResolverSourceCacheFile(sourceDirectory, "public-resolvers.md");
                PersistResolverSourceCacheFile(sourceDirectory, "public-resolvers.md.minisig");
            }
            catch (IOException exception)
            {
                Logger.Debug(exception, "DNSCryptProxy | LIST | Could not persist signed resolver cache pair for runtime reuse.");
            }
            catch (UnauthorizedAccessException exception)
            {
                Logger.Debug(exception, "DNSCryptProxy | LIST | Could not persist signed resolver cache pair for runtime reuse.");
            }
        }

        private void PersistResolverSourceCacheFile(string sourceDirectory, string fileName)
        {
            string source = Path.Combine(sourceDirectory, fileName);
            if (!File.Exists(source))
                return;

            Directory.CreateDirectory(runtimeDirectory);
            string destination = Path.Combine(runtimeDirectory, fileName);
            string temporary = destination + $".{Guid.NewGuid():N}.new";
            try
            {
                File.Copy(source, temporary, overwrite: false);
                File.Move(temporary, destination, overwrite: true);
                File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                catch
                {
                }
            }
        }

        private void SeedResolverSourceCache(string destinationDirectory)
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory))
                return;

            try
            {
                SeedResolverSourceCacheFile(destinationDirectory, "public-resolvers.md");
                SeedResolverSourceCacheFile(destinationDirectory, "public-resolvers.md.minisig");
            }
            catch (IOException exception)
            {
                Logger.Debug(exception, "DNSCryptProxy | LIST | Could not seed signed resolver cache pair.");
            }
            catch (UnauthorizedAccessException exception)
            {
                Logger.Debug(exception, "DNSCryptProxy | LIST | Could not seed signed resolver cache pair.");
            }
        }

        private void SeedResolverSourceCacheFile(string destinationDirectory, string fileName)
        {
            string source = Path.Combine(runtimeDirectory, fileName);
            if (!File.Exists(source))
                return;

            string destination = Path.Combine(destinationDirectory, fileName);
            if (File.Exists(destination))
                return;

            File.Copy(source, destination, overwrite: false);
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
        }

        private string GetConfigPath() => Path.Combine(runtimeDirectory, "dnscrypt-proxy.toml");

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            stopRequested = true;
            try { recoveryCancellation.Cancel(); } catch { }
            try
            {
                lifecycleLock.Wait();
                try { StopProcessCoreAsync(updateStatus: false, CancellationToken.None).GetAwaiter().GetResult(); }
                finally { lifecycleLock.Release(); }
            }
            catch
            {
            }
            recoveryCancellation.Dispose();
            lifecycleLock.Dispose();
            ownedComponentManager?.Dispose();
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }

        private sealed class ResolverSummaryDto
        {
            public string Name { get; set; }
            public string Proto { get; set; }
            public bool IPv6 { get; set; }
            public bool? Dnssec { get; set; }
            public bool NoLog { get; set; }
            public bool NoFilter { get; set; }
            public string Description { get; set; }
            public string[] Addrs { get; set; }
            public int[] Ports { get; set; }
            public string Stamp { get; set; }
        }
    }
}
