using Shadowsocks.NetworkService.Ipc;

namespace Shadowsocks.NetworkService;

/// <summary>
/// Owns the elevated broker's capture child lifecycle and asynchronous recovery state.
/// The broker protocol stays responsive while restart backoff runs in the background.
/// </summary>
internal sealed class CaptureChildSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan StableRunThreshold = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan[] RestartDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10),
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource _recoveryCancellation = new();
    private CaptureChild? _child;
    private StartRequest? _lastRequest;
    private Task? _recoveryTask;
    private bool _gameMode;
    private bool _captureFaulted;
    private int _automaticRestartAttempts;
    private bool _disposed;

    public async Task<ServiceResponse> StartOrRestartAsync(StartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            CancelRecoveryLocked();
            _lastRequest = request;
            _captureFaulted = false;
            _automaticRestartAttempts = 0;

            if (_gameMode)
            {
                return NetworkServiceResponses.Success("Configuration stored; Game Mode remains active.");
            }

            await StopChildLockedAsync().ConfigureAwait(false);
            try
            {
                CaptureChild child = await CaptureChild.StartAsync(request, cancellationToken).ConfigureAwait(false);
                _child = child;
                return NetworkServiceResponses.CaptureStatus(child, "Admin capture active.");
            }
            catch (Exception exception)
            {
                _captureFaulted = true;
                return NetworkServiceResponses.Failure(exception.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServiceResponse> EnterGameModeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            CancelRecoveryLocked();
            _gameMode = true;
            _captureFaulted = false;
            _automaticRestartAttempts = 0;
            await StopChildLockedAsync().ConfigureAwait(false);

            bool removed = DriverServiceCleaner.TryRemove();
            ServiceResponse response = NetworkServiceResponses.Success(
                removed
                    ? "Game Mode active; WinDivert capture and driver removed."
                    : "Game Mode active; capture stopped, driver cleanup reported a warning.");
            response.DriverRemoved = removed;
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServiceResponse> ExitGameModeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            CancelRecoveryLocked();
            _gameMode = false;
            _captureFaulted = false;
            _automaticRestartAttempts = 0;

            if (_lastRequest is null)
            {
                return NetworkServiceResponses.Failure("No Admin Mode configuration is available.");
            }

            await StopChildLockedAsync().ConfigureAwait(false);
            try
            {
                StartRequest request = _lastRequest;
                CaptureChild child = await CaptureChild.StartAsync(request, cancellationToken).ConfigureAwait(false);
                _child = child;
                return NetworkServiceResponses.CaptureStatus(child, "Admin capture restored.");
            }
            catch (Exception exception)
            {
                _captureFaulted = true;
                return NetworkServiceResponses.Failure(exception.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServiceResponse> PingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_child is not null && _child.IsAlive
                && DateTime.UtcNow - _child.StartedUtc >= StableRunThreshold)
            {
                _automaticRestartAttempts = 0;
            }

            if (_child is not null && !_child.IsAlive)
            {
                await StopChildLockedAsync().ConfigureAwait(false);
            }

            if (_child is not null && _child.IsAlive)
            {
                return NetworkServiceResponses.CaptureStatus(_child, "pong");
            }

            EnsureRecoveryScheduledLocked();
            return NetworkServiceResponses.Success(
                _captureFaulted
                    ? "capture-faulted"
                    : _recoveryTask is { IsCompleted: false }
                        ? "capture-recovering"
                        : "pong");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServiceResponse> StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return NetworkServiceResponses.Success("Stopping.");
            }

            CancelRecoveryLocked();
            await StopChildLockedAsync().ConfigureAwait(false);
            bool removed = DriverServiceCleaner.TryRemove();
            ServiceResponse response = NetworkServiceResponses.Success("Stopping.");
            response.DriverRemoved = removed;
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureRecoveryScheduledLocked()
    {
        if (_gameMode
            || _captureFaulted
            || _lastRequest is null
            || _automaticRestartAttempts >= RestartDelays.Length
            || _recoveryTask is { IsCompleted: false })
        {
            return;
        }

        StartRequest request = _lastRequest;
        CancellationToken token = _recoveryCancellation.Token;
        _recoveryTask = RecoverAsync(request, token);
    }

    private async Task RecoverAsync(StartRequest request, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            int attempt;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!CanRecoverLocked(request))
                {
                    return;
                }

                attempt = _automaticRestartAttempts;
                if (attempt >= RestartDelays.Length)
                {
                    _captureFaulted = true;
                    return;
                }
                _automaticRestartAttempts++;
            }
            finally
            {
                _gate.Release();
            }

            await Task.Delay(RestartDelays[attempt], cancellationToken).ConfigureAwait(false);

            CaptureChild? candidate = null;
            try
            {
                // Starting a child can take up to the handshake timeout. Do it outside
                // the supervisor gate so ping/stop/game-mode commands remain responsive.
                candidate = await CaptureChild.StartAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (!CanRecoverLocked(request))
                    {
                        return;
                    }

                    if (_automaticRestartAttempts >= RestartDelays.Length)
                    {
                        _captureFaulted = true;
                        return;
                    }
                }
                finally
                {
                    _gate.Release();
                }

                continue;
            }

            bool accepted = false;
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (candidate is not null
                    && !cancellationToken.IsCancellationRequested
                    && CanRecoverLocked(request))
                {
                    _child = candidate;
                    candidate = null;
                    accepted = true;
                }
            }
            finally
            {
                _gate.Release();
            }

            if (candidate is not null)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
            }

            if (accepted || cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private bool CanRecoverLocked(StartRequest request)
    {
        return !_disposed
            && !_gameMode
            && !_captureFaulted
            && ReferenceEquals(_lastRequest, request)
            && _child is null;
    }

    private async Task StopChildLockedAsync()
    {
        CaptureChild? child = _child;
        _child = null;
        if (child is not null)
        {
            await child.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void CancelRecoveryLocked()
    {
        CancellationTokenSource previousCancellation = _recoveryCancellation;
        Task? previousTask = _recoveryTask;
        previousCancellation.Cancel();
        _recoveryCancellation = new CancellationTokenSource();
        _recoveryTask = null;
        _ = DisposeRecoveryResourcesAsync(previousTask, previousCancellation);
    }

    private static async Task DisposeRecoveryResourcesAsync(
        Task? recoveryTask,
        CancellationTokenSource cancellation)
    {
        if (recoveryTask is not null)
        {
            try
            {
                await recoveryTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // Recovery failures are reflected through capture state; observing the
                // task here prevents an unobserved background exception.
            }
        }

        cancellation.Dispose();
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        Task? recoveryTask;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _recoveryCancellation.Cancel();
            recoveryTask = _recoveryTask;
            await StopChildLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (recoveryTask is not null)
        {
            try
            {
                await recoveryTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _recoveryCancellation.Dispose();
        _gate.Dispose();
        DriverServiceCleaner.TryRemove();
    }
}
