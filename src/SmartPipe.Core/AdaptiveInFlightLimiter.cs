#nullable enable

namespace SmartPipe.Core;

internal sealed class AdaptiveInFlightLimiter : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Queue<Waiter> _waiters = new();
    private int _currentLimit;
    private int _inUse;
    private int _pendingWaiters;
    private bool _disposed;

    public AdaptiveInFlightLimiter(int initialLimit)
    {
        if (initialLimit <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(initialLimit),
                initialLimit,
                "Initial limit must be greater than zero."
            );

        _currentLimit = initialLimit;
    }

    public int CurrentLimit
    {
        get
        {
            lock (_gate)
                return _currentLimit;
        }
    }

    public int InUse
    {
        get
        {
            lock (_gate)
                return _inUse;
        }
    }

    public int PendingWaiters
    {
        get
        {
            lock (_gate)
                return _pendingWaiters;
        }
    }

    public ValueTask<AdaptiveInFlightLease> AcquireAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return ValueTask.FromCanceled<AdaptiveInFlightLease>(ct);

        Waiter? waiter = null;
        lock (_gate)
        {
            ThrowIfDisposed();

            if (_inUse < _currentLimit)
            {
                _inUse++;
                return ValueTask.FromResult(new AdaptiveInFlightLease(this));
            }

            waiter = new Waiter(this, ct);
            _waiters.Enqueue(waiter);
            _pendingWaiters++;
        }

        waiter.RegisterCancellation();
        return new ValueTask<AdaptiveInFlightLease>(waiter.Task);
    }

    public void UpdateLimit(int newLimit)
    {
        if (newLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(newLimit), newLimit, "Limit must be greater than zero.");

        List<WaiterGrant>? grants;
        lock (_gate)
        {
            ThrowIfDisposed();
            _currentLimit = newLimit;
            grants = DrainWaitersUnderLock();
        }

        CompleteGrants(grants);
    }

    public ValueTask DisposeAsync()
    {
        List<Waiter>? waiters = null;
        lock (_gate)
        {
            if (_disposed)
                return ValueTask.CompletedTask;

            _disposed = true;
            if (_waiters.Count > 0)
            {
                waiters = new List<Waiter>(_waiters.Count);
                while (_waiters.Count > 0)
                {
                    var waiter = _waiters.Dequeue();
                    if (waiter.TryCompleteUnderLock())
                    {
                        _pendingWaiters--;
                        waiters.Add(waiter);
                    }
                }
            }
        }

        if (waiters is not null)
        {
            foreach (var waiter in waiters)
            {
                waiter.DisposeRegistration();
                waiter.TrySetException(new ObjectDisposedException(nameof(AdaptiveInFlightLimiter)));
            }
        }

        return ValueTask.CompletedTask;
    }

    private void Release()
    {
        List<WaiterGrant>? grants;
        lock (_gate)
        {
            if (_inUse <= 0)
                return;

            _inUse--;
            grants = _disposed ? null : DrainWaitersUnderLock();
        }

        CompleteGrants(grants);
    }

    private void Cancel(Waiter waiter, CancellationToken ct)
    {
        var shouldCancel = false;
        lock (_gate)
        {
            if (waiter.TryCompleteUnderLock())
            {
                _pendingWaiters--;
                shouldCancel = true;
            }
        }

        if (shouldCancel)
        {
            waiter.TrySetCanceled(ct);
        }
    }

    private List<WaiterGrant>? DrainWaitersUnderLock()
    {
        List<WaiterGrant>? grants = null;

        while (_inUse < _currentLimit && _waiters.Count > 0)
        {
            var waiter = _waiters.Dequeue();
            if (!waiter.TryCompleteUnderLock())
                continue;

            _pendingWaiters--;
            _inUse++;
            grants ??= [];
            grants.Add(new WaiterGrant(waiter, new AdaptiveInFlightLease(this)));
        }

        return grants;
    }

    private static void CompleteGrants(List<WaiterGrant>? grants)
    {
        if (grants is null)
            return;

        foreach (var grant in grants)
        {
            grant.Waiter.DisposeRegistration();
            grant.Waiter.TrySetResult(grant.Lease);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AdaptiveInFlightLimiter));
    }

    private sealed class Waiter(AdaptiveInFlightLimiter owner, CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource<AdaptiveInFlightLease> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AdaptiveInFlightLimiter _owner = owner;
        private readonly CancellationToken _cancellationToken = cancellationToken;
        private CancellationTokenRegistration _registration;
        private bool _completed;

        public Task<AdaptiveInFlightLease> Task => _completion.Task;

        public void RegisterCancellation()
        {
            if (_cancellationToken.CanBeCanceled)
                _registration = _cancellationToken.Register(static state =>
                {
                    var waiter = (Waiter)state!;
                    waiter._owner.Cancel(waiter, waiter._cancellationToken);
                }, this);
        }

        public bool TryCompleteUnderLock()
        {
            if (_completed)
                return false;

            _completed = true;
            return true;
        }

        public void DisposeRegistration() => _registration.Dispose();

        public void TrySetResult(AdaptiveInFlightLease lease) => _completion.TrySetResult(lease);

        public void TrySetCanceled(CancellationToken ct) => _completion.TrySetCanceled(ct);

        public void TrySetException(Exception exception) => _completion.TrySetException(exception);
    }

    private sealed record WaiterGrant(Waiter Waiter, AdaptiveInFlightLease Lease);

    internal void ReleaseLease() => Release();
}

internal sealed class AdaptiveInFlightLease : IAsyncDisposable
{
    private readonly AdaptiveInFlightLimiter _owner;
    private int _disposed;

    internal AdaptiveInFlightLease(AdaptiveInFlightLimiter owner) => _owner = owner;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _owner.ReleaseLease();

        return ValueTask.CompletedTask;
    }
}
