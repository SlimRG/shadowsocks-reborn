#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Controller.Service
{
    /// <summary>
    /// Serializes DNSCrypt management transactions that span component, runtime, configuration,
    /// and capture state. Individual managers keep their own internal locks; this gate protects
    /// the higher-level transaction from interleaving with another user or background operation.
    /// The lifecycle token additionally cancels queued work during suspend/application shutdown.
    /// </summary>
    internal sealed class DnsCryptCoordinator
    {
        private readonly SemaphoreSlim _managementGate = new(1, 1);
        private readonly object _stateLock = new();
        private CancellationTokenSource _lifecycleCancellation = new();
        private string? _currentOperation;
        private CancellationTokenSource? _currentCancellation;
        private bool _acceptingOperations = true;

        public string? CurrentOperation
        {
            get
            {
                lock (_stateLock)
                {
                    return _currentOperation;
                }
            }
        }

        public bool IsBusy => CurrentOperation is not null;

        public bool IsSuspended
        {
            get
            {
                lock (_stateLock)
                {
                    return !_acceptingOperations;
                }
            }
        }

        public async Task<T> ExecuteExclusiveAsync<T>(
            string operation,
            Func<CancellationToken, Task<T>> action,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operation);
            ArgumentNullException.ThrowIfNull(action);

            CancellationToken lifecycleToken;
            lock (_stateLock)
            {
                if (!_acceptingOperations)
                    throw new InvalidOperationException("DNSCrypt coordinator is suspended.");
                lifecycleToken = _lifecycleCancellation.Token;
            }

            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifecycleToken);
            bool gateHeld = false;
            try
            {
                await _managementGate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
                gateHeld = true;
                operationCancellation.Token.ThrowIfCancellationRequested();

                lock (_stateLock)
                {
                    if (!_acceptingOperations || lifecycleToken != _lifecycleCancellation.Token)
                        throw new OperationCanceledException("DNSCrypt coordinator lifecycle changed.", operationCancellation.Token);

                    _currentOperation = operation;
                    _currentCancellation = operationCancellation;
                }

                return await action(operationCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_stateLock)
                {
                    if (ReferenceEquals(_currentCancellation, operationCancellation))
                    {
                        _currentOperation = null;
                        _currentCancellation = null;
                    }
                }

                if (gateHeld)
                    _managementGate.Release();
            }
        }

        public void CancelCurrentOperation()
        {
            CancellationTokenSource? cancellation;
            lock (_stateLock)
            {
                cancellation = _currentCancellation;
            }

            TryCancel(cancellation);
        }

        /// <summary>
        /// Atomically stops accepting DNS management work, cancels active and queued transactions,
        /// waits for the gate, then executes teardown while the coordinator remains suspended.
        /// Call <see cref="Resume"/> from the next controller Start() lifecycle.
        /// </summary>
        public async Task SuspendAndExecuteAsync(
            string operation,
            Func<CancellationToken, Task> action)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operation);
            ArgumentNullException.ThrowIfNull(action);

            CancellationTokenSource lifecycleCancellation;
            CancellationTokenSource? currentCancellation;
            lock (_stateLock)
            {
                _acceptingOperations = false;
                lifecycleCancellation = _lifecycleCancellation;
                currentCancellation = _currentCancellation;
            }

            TryCancel(lifecycleCancellation);
            TryCancel(currentCancellation);

            await _managementGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                lock (_stateLock)
                {
                    _currentOperation = operation;
                    _currentCancellation = null;
                }

                await action(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                lock (_stateLock)
                {
                    _currentOperation = null;
                    _currentCancellation = null;
                }
                _managementGate.Release();
            }
        }

        public void Resume()
        {
            CancellationTokenSource? oldCancellation = null;
            lock (_stateLock)
            {
                if (_acceptingOperations)
                    return;

                oldCancellation = _lifecycleCancellation;
                _lifecycleCancellation = new CancellationTokenSource();
                _acceptingOperations = true;
            }

            oldCancellation.Dispose();
        }

        public Task ExecuteExclusiveAsync(
            string operation,
            Func<CancellationToken, Task> action,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            return ExecuteExclusiveAsync<object?>(
                operation,
                async token =>
                {
                    await action(token).ConfigureAwait(false);
                    return null;
                },
                cancellationToken);
        }

        private static void TryCancel(CancellationTokenSource? cancellation)
        {
            try
            {
                cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The transaction completed between observing and cancelling its token source.
            }
        }
    }
}
