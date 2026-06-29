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

    /// <summary>
    /// Initializes a new concurrency limiter with the specified limits.
    /// </summary>
    /// <param name="initialLimit">The initial concurrency limit.</param>
    /// <param name="maxLimit">The maximum concurrency limit.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="initialLimit" /> is less than 1 or when <paramref name="maxLimit" /> is less than <paramref name="initialLimit" />.
    /// </exception>
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

    /// <summary>
    /// Acquires a concurrency lease.
    /// </summary>
    /// <param name="ct">A token that cancels the acquisition request.</param>
    /// <returns>A lease for an available slot, or a canceled or faulted value task if the request is canceled or the limiter has been completed.</returns>
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

    /// <summary>
    /// Updates the current concurrency limit.
    /// </summary>
    /// <param name="newLimit">The new limit to apply.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="newLimit"/> is less than one or greater than the configured maximum limit.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the limiter has been completed or disposed.</exception>
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

    /// <summary>
    /// Marks the limiter as completed and releases any queued acquisitions.
    /// </summary>
    /// <remarks>
    /// Pending acquisition requests are completed with an <see cref="ObjectDisposedException"/>.
    /// </remarks>
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

    /// <summary>
    /// Completes the limiter and releases pending waiters.
    /// </summary>
    public void Dispose()
    {
        Complete();
    }

    /// <summary>
    /// Completes the limiter and releases pending waiters.
    /// </summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Releases one acquired concurrency slot.
    /// </summary>
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

    /// <summary>
/// Creates a new lease linked to this limiter.
/// </summary>
/// <returns>A lease that releases one granted concurrency slot.</returns>
private Lease CreateLease() => new(this, new LeaseState());

    /// <summary>
    /// Grants queued waiters while capacity is available.
    /// </summary>
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

    /// <summary>
    /// Dequeues all pending waiters from the queue.
    /// </summary>
    /// <returns>An array containing the queued waiters in FIFO order.</returns>
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

    /// <summary>
        /// Creates an exception that indicates the limiter has been completed.
        /// </summary>
        /// <returns>An <see cref="ObjectDisposedException"/> for <see cref="AdaptiveConcurrencyLimiter"/>.</returns>
        private static ObjectDisposedException CreateDisposedException() =>
        new(nameof(AdaptiveConcurrencyLimiter));

    public readonly struct Lease : IDisposable, IAsyncDisposable
    {
        private readonly AdaptiveConcurrencyLimiter? _owner;
        private readonly LeaseState? _state;

        /// <summary>
        /// Creates a lease associated with the specified limiter.
        /// </summary>
        /// <param name="owner">The limiter that owns the lease.</param>
        /// <param name="state">The lease state used to track release.</param>
        internal Lease(AdaptiveConcurrencyLimiter owner, LeaseState state)
        {
            _owner = owner;
            _state = state;
        }

        /// <summary>
        /// Releases the leased concurrency slot.
        /// </summary>
        public void Release()
        {
            Dispose();
        }

        /// <summary>
        /// Releases the lease and returns its concurrency slot to the limiter.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the lease was not acquired or has already been released.</exception>
        public void Dispose()
        {
            if (_owner is null || _state is null)
                throw new InvalidOperationException("Cannot release a lease that was not acquired.");

            if (Interlocked.Exchange(ref _state.Released, 1) != 0)
                throw new InvalidOperationException("Lease has already been released.");

            _owner.ReleaseLease();
        }

        /// <summary>
        /// Completes the limiter and releases its resources.
        /// </summary>
        /// <returns>A task that has completed.</returns>
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

        /// <summary>
        /// Registers cancellation for a pending acquisition request.
        /// </summary>
        /// <param name="ct">The cancellation token that cancels the request.</param>
        public void RegisterCancellation(CancellationToken ct)
        {
            if (!ct.CanBeCanceled)
                return;

            var registration = ct.Register(() => TryCancel(ct));
            _registration = registration;
            if (!IsPending)
                registration.Dispose();
        }

        /// <summary>
                /// Completes the waiter with a lease.
                /// </summary>
                /// <param name="lease">The lease to deliver.</param>
                /// <returns><c>true</c> if the lease was delivered, <c>false</c> otherwise.</returns>
        public bool TryGrant(Lease lease)
        {
            if (Interlocked.CompareExchange(ref _state, Completed, Pending) != Pending)
                return false;

            _registration.Dispose();
            return _completion.TrySetResult(lease);
        }

        /// <summary>
        /// Completes the waiting acquisition with an exception.
        /// </summary>
        /// <param name="exception">The exception to set on the waiting acquisition.</param>
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

        /// <summary>
        /// Cancels the waiting acquisition if it is still pending.
        /// </summary>
        /// <param name="ct">The cancellation token associated with the acquisition request.</param>
        private void TryCancel(CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _state, Completed, Pending) == Pending)
                _completion.TrySetCanceled(ct);
        }
    }
}
