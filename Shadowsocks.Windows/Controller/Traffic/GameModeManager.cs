using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Shadowsocks.Model;

namespace Shadowsocks.Controller.Traffic
{
    /// <summary>
    /// Coordinates desired capture mode with automatic Game Mode. A configured
    /// application entering the process list temporarily disables the WinDivert
    /// capture child and driver; Admin Mode is restored automatically after it exits.
    /// </summary>
    internal sealed class GameModeManager : IAsyncDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
        private readonly AdminCaptureManager _adminCapture;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Timer _timer;

        private Configuration _configuration;
        private int[] _excludedProcessIds = [];
        private DnsCaptureRuntimeState _dnsRuntime = DnsCaptureRuntimeState.Unavailable;
        private string[] _runningGameApplications = [];
        private bool _automaticGameMode;
        private bool _disposed;

        public GameModeManager(AdminCaptureManager adminCapture)
        {
            _adminCapture = adminCapture ?? throw new ArgumentNullException(nameof(adminCapture));
            _adminCapture.StatusChanged += AdminCapture_StatusChanged;
            _timer = new Timer(static state => ((GameModeManager)state).QueuePoll(), this, Timeout.Infinite, Timeout.Infinite);
        }

        public TrafficRuntimeMode RuntimeMode
        {
            get
            {
                if (_configuration?.trafficCaptureMode != TrafficCaptureMode.Admin)
                {
                    return TrafficRuntimeMode.User;
                }

                if (_automaticGameMode || _adminCapture.IsGameMode)
                {
                    return TrafficRuntimeMode.Game;
                }

                return _adminCapture.IsCaptureActive ? TrafficRuntimeMode.Admin : TrafficRuntimeMode.User;
            }
        }

        public bool AutomaticGameMode => _automaticGameMode;
        public IReadOnlyList<string> RunningGameApplications => _runningGameApplications;

        public event EventHandler StatusChanged;

        private void AdminCapture_StatusChanged(object sender, EventArgs e)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task ApplyConfigurationAsync(
            Configuration configuration,
            IEnumerable<int> excludedProcessIds,
            DnsCaptureRuntimeState dnsRuntime,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                _configuration = configuration;
                _excludedProcessIds = (excludedProcessIds ?? []).Where(pid => pid > 0).Distinct().ToArray();
                _dnsRuntime = dnsRuntime;

                _runningGameApplications = configuration.trafficCaptureMode == TrafficCaptureMode.Admin
                    ? FindRunningConfiguredGames(configuration.gameModeApplications)
                    : [];
                _automaticGameMode = _runningGameApplications.Length > 0;

                await ApplyStateLockedAsync(cancellationToken).ConfigureAwait(false);
                if (configuration.trafficCaptureMode == TrafficCaptureMode.Admin)
                {
                    _timer.Change(PollInterval, PollInterval);
                }
                else
                {
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task UpdateDnsRuntimeAsync(
            DnsCaptureRuntimeState dnsRuntime,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                _dnsRuntime = dnsRuntime;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                _automaticGameMode = false;
                _runningGameApplications = [];
                await _adminCapture.StopAsync(cancellationToken).ConfigureAwait(false);
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                _gate.Release();
            }
        }

        private void QueuePoll()
        {
            if (_disposed || _configuration?.trafficCaptureMode != TrafficCaptureMode.Admin)
            {
                return;
            }

            _ = PollAsync();
        }

        private async Task PollAsync()
        {
            if (!await _gate.WaitAsync(0).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                if (_disposed || _configuration?.trafficCaptureMode != TrafficCaptureMode.Admin)
                {
                    return;
                }

                string[] running = FindRunningConfiguredGames(_configuration.gameModeApplications);
                bool detected = running.Length > 0;
                bool processSetChanged = !_runningGameApplications.SequenceEqual(running, StringComparer.OrdinalIgnoreCase);
                if (detected == _automaticGameMode && !processSetChanged)
                {
                    return;
                }

                _runningGameApplications = running;
                bool modeChanged = detected != _automaticGameMode;
                _automaticGameMode = detected;

                if (modeChanged)
                {
                    await ApplyStateLockedAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    StatusChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception exception)
            {
                Logger.Warn(exception, "Unable to update automatic Game Mode state.");
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task ApplyStateLockedAsync(CancellationToken cancellationToken)
        {
            if (_configuration is null || _configuration.trafficCaptureMode != TrafficCaptureMode.Admin)
            {
                await _adminCapture.StopAsync(cancellationToken).ConfigureAwait(false);
                StatusChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (_automaticGameMode)
            {
                // Keep the elevated broker's pending configuration current while the
                // capture child is paused. Do not create a broker/UAC prompt merely
                // because DNSCrypt restarted while a game was already running.
                if (_adminCapture.IsGameMode && _adminCapture.IsBrokerRunning)
                {
                    await _adminCapture.StartOrUpdateAsync(
                        _configuration,
                        _excludedProcessIds,
                        _dnsRuntime,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _adminCapture.EnterGameModeAsync(cancellationToken).ConfigureAwait(false);
                }
                StatusChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (_adminCapture.IsGameMode)
            {
                // Update the broker's stored request before resuming capture so a DNSCrypt
                // restart that happened during Game Mode cannot restore a stale PID/port.
                await _adminCapture.StartOrUpdateAsync(
                    _configuration,
                    _excludedProcessIds,
                    _dnsRuntime,
                    cancellationToken).ConfigureAwait(false);
                await _adminCapture.ExitGameModeAsync(cancellationToken).ConfigureAwait(false);
                StatusChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            await _adminCapture.StartOrUpdateAsync(
                _configuration,
                _excludedProcessIds,
                _dnsRuntime,
                cancellationToken).ConfigureAwait(false);

            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private static string[] FindRunningConfiguredGames(IReadOnlyCollection<string> patterns)
        {
            if (patterns is null || patterns.Count == 0)
            {
                return [];
            }

            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch
            {
                return [];
            }

            var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (Process process in processes)
                {
                    string name = null;
                    string path = null;
                    try
                    {
                        name = process.ProcessName;
                    }
                    catch
                    {
                    }

                    try
                    {
                        path = process.MainModule?.FileName;
                    }
                    catch
                    {
                    }

                    foreach (string pattern in patterns)
                    {
                        if (!ApplicationPatternMatcher.Matches(pattern, path, name))
                        {
                            continue;
                        }

                        string displayName = !string.IsNullOrWhiteSpace(path)
                            ? System.IO.Path.GetFileName(path)
                            : string.IsNullOrWhiteSpace(name)
                                ? pattern
                                : name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
                        matches.Add(displayName);
                    }
                }
            }
            finally
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        process.Dispose();
                    }
                    catch
                    {
                    }
                }
            }

            return matches.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            await StopAsync().ConfigureAwait(false);
            _disposed = true;
            _timer.Dispose();
            _adminCapture.StatusChanged -= AdminCapture_StatusChanged;
            await _adminCapture.DisposeAsync().ConfigureAwait(false);
            _gate.Dispose();
        }
    }
}
