#nullable enable

namespace SmartPipe.Core;

internal sealed class AdaptiveConcurrencyLimiter : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly int _maxLimit;
    private readonly Queue<Waiter> _waiters = new();
    private int _currentLimit;
    private int _inFlight;
    private bool _completed;

    public AdaptiveConcurrencyLimiter(int initialLimit, int maxLimit)
    {
        if (initialLimit < 1)
            throw new ArgumentOutOfRangeException(
                nameof(initialLimit),
                initialLimit,
                "Initial concurrency limit must be greater than or equal to one.");

        if (maxLimit < initialLimit)
            throw new ArgumentOutOfRangeException(
                nameof(maxLimit),
                maxLimit,
                "Maximum concurrency limit must be greater than or equal to the initial limit.");

        _currentLimit = initialLimit;
        _maxLimit = maxLimit;
    }

    public int CurrentLimit
    {
        get
        {
            lock (_gate)
            {
                return _currentLimit;
            }
        }
    }

    public int InFlight
    {
        get
        {
            lock (_gate)
            {
                return _inFlight;
            }
        }
    }

    public ValueTask<Lease> AcquireAsync(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return ValueTask.FromCanceled<Lease>(ct);

        Waiter waiter;
        lock (_gate)
        {
            if (_completed)
                return ValueTask.FromException<Lease>(CreateDisposedException());

            if (_inFlight < _currentLimit)
            {
                _inFlight++;
                return new ValueTask<Lease>(CreateLease());
            }

            waiter = new Waiter();
            _waiters.Enqueue(waiter);
        }

        waiter.RegisterCancellation(ct);
        return new ValueTask<Lease>(waiter.Task);
    }

    public void UpdateLimit(int newLimit)
    {
        if (newLimit < 1 || newLimit > _maxLimit)
            throw new ArgumentOutOfRangeException(
                nameof(newLimit),
                newLimit,
                "Concurrency limit must be between one and the configured maximum limit.");

        lock (_gate)
        {
            if (_completed)
                throw CreateDisposedException();

            _currentLimit = newLimit;
            DrainWaitersLocked();
        }
    }

    public void Complete()
    {
        Waiter[] waiters;
        lock (_gate)
        {
            if (_completed)
                return;

            _completed = true;
            waiters = DequeueAllWaitersLocked();
        }

        foreach (var waiter in waiters)
            waiter.TryComplete(CreateDisposedException());
    }

    public void Dispose()
    {
        Complete();
    }

    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }

    private void ReleaseLease()
    {
        lock (_gate)
        {
            if (_inFlight == 0)
                throw new InvalidOperationException("Cannot release a lease without a matching acquire.");

            _inFlight--;
            DrainWaitersLocked();
        }
    }

    private Lease CreateLease() => new(this, new LeaseState());

    private void DrainWaitersLocked()
    {
        if (_completed)
            return;

        while (_inFlight < _currentLimit && _waiters.Count > 0)
        {
            var waiter = _waiters.Dequeue();
            if (!waiter.IsPending)
            {
                waiter.DisposeRegistration();
                continue;
            }

            _inFlight++;
            if (!waiter.TryGrant(CreateLease()))
                _inFlight--;
        }
    }

    private Waiter[] DequeueAllWaitersLocked()
    {
        if (_waiters.Count == 0)
            return [];

        var waiters = new Waiter[_waiters.Count];
        var index = 0;
        while (_waiters.Count > 0)
            waiters[index++] = _waiters.Dequeue();

        return waiters;
    }

    private static ObjectDisposedException CreateDisposedException() =>
        new(nameof(AdaptiveConcurrencyLimiter));

    public readonly struct Lease : IDisposable, IAsyncDisposable
    {
        private readonly AdaptiveConcurrencyLimiter? _owner;
        private readonly LeaseState? _state;

        internal Lease(AdaptiveConcurrencyLimiter owner, LeaseState state)
        {
            _owner = owner;
            _state = state;
        }

        public void Release()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_owner is null || _state is null)
                throw new InvalidOperationException("Cannot release a lease that was not acquired.");

            if (Interlocked.Exchange(ref _state.Released, 1) != 0)
                throw new InvalidOperationException("Lease has already been released.");

            _owner.ReleaseLease();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class LeaseState
    {
        public int Released;
    }

    private sealed class Waiter
    {
        private const int Pending = 0;
        private const int Completed = 1;

        private readonly TaskCompletionSource<Lease> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private CancellationTokenRegistration _registration;
        private int _state;

        public Task<Lease> Task => _completion.Task;

        public bool IsPending => Volatile.Read(ref _state) == Pending;

        public void RegisterCancellation(CancellationToken ct)
        {
            if (!ct.CanBeCanceled)
                return;

            var registration = ct.Register(() => TryCancel(ct));
            _registration = registration;
            if (!IsPending)
                registration.Dispose();
        }

        public bool TryGrant(Lease lease)
        {
            if (Interlocked.CompareExchange(ref _state, Completed, Pending) != Pending)
                return false;

            _registration.Dispose();
            return _completion.TrySetResult(lease);
        }

        public void TryComplete(Exception exception)
        {
            if (Interlocked.CompareExchange(ref _state, Completed, Pending) == Pending)
                _completion.TrySetException(exception);

            _registration.Dispose();
        }

        public void DisposeRegistration()
        {
            _registration.Dispose();
        }

        private void TryCancel(CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _state, Completed, Pending) == Pending)
                _completion.TrySetCanceled(ct);
        }
    }
}
