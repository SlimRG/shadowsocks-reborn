using NLog;
using Shadowsocks.Model;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

        public event EventHandler StatusChanged;

        private void AdminCapture_StatusChanged(object sender, EventArgs e)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task ApplyConfigurationAsync(
            Configuration configuration,
            IEnumerable<int> excludedProcessIds,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                _configuration = configuration;
                _excludedProcessIds = (excludedProcessIds ?? []).Where(pid => pid > 0).Distinct().ToArray();

                bool gameDetected = configuration.trafficCaptureMode == TrafficCaptureMode.Admin
                    && IsConfiguredGameRunning(configuration.gameModeApplications);
                _automaticGameMode = gameDetected;

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

                bool detected = IsConfiguredGameRunning(_configuration.gameModeApplications);
                if (detected == _automaticGameMode)
                {
                    return;
                }

                _automaticGameMode = detected;

                await ApplyStateLockedAsync(CancellationToken.None).ConfigureAwait(false);
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
                await _adminCapture.EnterGameModeAsync(cancellationToken).ConfigureAwait(false);
                StatusChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (_adminCapture.IsGameMode)
            {
                // If the broker survived Game Mode, exiting it resumes capture without UAC.
                // When no broker exists (e.g. app started while a game was already open),
                // StartOrUpdate below performs the first elevation now that the game ended.
                await _adminCapture.ExitGameModeAsync(cancellationToken).ConfigureAwait(false);
            }

            await _adminCapture.StartOrUpdateAsync(
                _configuration,
                _excludedProcessIds,
                cancellationToken).ConfigureAwait(false);

            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private static bool IsConfiguredGameRunning(IReadOnlyCollection<string> patterns)
        {
            if (patterns is null || patterns.Count == 0)
            {
                return false;
            }

            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch
            {
                return false;
            }

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
                        if (ApplicationPatternMatcher.Matches(pattern, path, name))
                        {
                            return true;
                        }
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

            return false;
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
