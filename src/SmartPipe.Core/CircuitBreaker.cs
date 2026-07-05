#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>Circuit breaker state machine states.</summary>
public enum CircuitState
{
    /// <summary>Normal operation, requests pass through.</summary>
    Closed,

    /// <summary>Circuit is open, requests are blocked.</summary>
    Open,

    /// <summary>Testing if the circuit can be closed.</summary>
    HalfOpen,

    /// <summary>Manually isolated, requests are blocked.</summary>
    Isolated,
}

internal interface ICircuitBreakerTimeSource
{
    DateTime UtcNow { get; }

    long GetTimestamp();

    TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp);
}

internal sealed class ClockCircuitBreakerTimeSource : ICircuitBreakerTimeSource
{
    private readonly IClock _clock;

    public ClockCircuitBreakerTimeSource(IClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public DateTime UtcNow => _clock.UtcNow;

    public long GetTimestamp() => _clock.UtcNow.Ticks;

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        TimeSpan.FromTicks(endingTimestamp - startingTimestamp);
}

internal sealed class TimeProviderCircuitBreakerTimeSource : ICircuitBreakerTimeSource
{
    private readonly TimeProvider _timeProvider;

    public TimeProviderCircuitBreakerTimeSource(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public long GetTimestamp() => _timeProvider.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        _timeProvider.GetElapsedTime(startingTimestamp, endingTimestamp);
}

/// <summary>
/// Thread-safe circuit breaker with hybrid failure detection:
/// EWMA for fast reaction + Sliding window for accurate threshold decisions.
/// </summary>
/// <remarks>
/// Uses atomic operations for state transitions.
/// EWMA provides early warning, sliding window makes final decisions.
/// </remarks>
public class CircuitBreaker
{
    private readonly double _failureRatio;
    private readonly TimeSpan _samplingDuration;
    private readonly int _minimumThroughput;
    private readonly TimeSpan _breakDuration;
    private readonly int _maxHalfOpenRequests;
    private readonly ICircuitBreakerTimeSource _time;

    private int _state = (int)CircuitState.Closed;
    private int _halfOpenCount;
    private int _activeHalfOpenProbes;
    private int _halfOpenSuccesses;
    private int _halfOpenGeneration;
    private long _openedAtTimestamp;
    private readonly object _halfOpenTransitionGate = new();

    // Hybrid: EWMA for early warning + Sliding window for decisions
    private double _ewmaFailureRate;
    private readonly ConcurrentQueue<(DateTime Timestamp, bool IsSuccess)> _window = new();

    /// <summary>Gets the current circuit state.</summary>
    public CircuitState State => (CircuitState)Volatile.Read(ref _state);

    /// <summary>Creates a new circuit breaker with specified thresholds.</summary>
    /// <param name="failureRatio">Failure ratio threshold (0.0-1.0).</param>
    public CircuitBreaker(double failureRatio)
        : this(
            failureRatio,
            samplingDuration: null,
            minimumThroughput: 10,
            breakDuration: null,
            maxHalfOpenRequests: 3,
            new ClockCircuitBreakerTimeSource(new TimeProviderClock()))
    {
    }

    /// <summary>Creates a new circuit breaker with specified thresholds.</summary>
    /// <param name="failureRatio">Failure ratio threshold (0.0-1.0).</param>
    /// <param name="samplingDuration">Window for sliding window evaluation.</param>
    public CircuitBreaker(double failureRatio, TimeSpan? samplingDuration)
        : this(
            failureRatio,
            samplingDuration,
            minimumThroughput: 10,
            breakDuration: null,
            maxHalfOpenRequests: 3,
            new ClockCircuitBreakerTimeSource(new TimeProviderClock()))
    {
    }

    /// <summary>Creates a new circuit breaker with specified thresholds.</summary>
    /// <param name="failureRatio">Failure ratio threshold (0.0-1.0).</param>
    /// <param name="samplingDuration">Window for sliding window evaluation.</param>
    /// <param name="minimumThroughput">Minimum requests before evaluating ratio.</param>
    public CircuitBreaker(double failureRatio, TimeSpan? samplingDuration, int minimumThroughput)
        : this(
            failureRatio,
            samplingDuration,
            minimumThroughput,
            breakDuration: null,
            maxHalfOpenRequests: 3,
            new ClockCircuitBreakerTimeSource(new TimeProviderClock()))
    {
    }

    /// <summary>Creates a new circuit breaker with specified thresholds.</summary>
    /// <param name="failureRatio">Failure ratio threshold (0.0-1.0).</param>
    /// <param name="samplingDuration">Window for sliding window evaluation.</param>
    /// <param name="minimumThroughput">Minimum requests before evaluating ratio.</param>
    /// <param name="breakDuration">Duration to stay open before half-open.</param>
    public CircuitBreaker(
        double failureRatio,
        TimeSpan? samplingDuration,
        int minimumThroughput,
        TimeSpan? breakDuration)
        : this(
            failureRatio,
            samplingDuration,
            minimumThroughput,
            breakDuration,
            maxHalfOpenRequests: 3,
            new ClockCircuitBreakerTimeSource(new TimeProviderClock()))
    {
    }

    /// <summary>Creates a new circuit breaker with specified thresholds.</summary>
    /// <param name="failureRatio">Failure ratio threshold (0.0-1.0).</param>
    /// <param name="samplingDuration">Window for sliding window evaluation.</param>
    /// <param name="minimumThroughput">Minimum requests before evaluating ratio.</param>
    /// <param name="breakDuration">Duration to stay open before half-open.</param>
    /// <param name="maxHalfOpenRequests">Max requests in half-open state.</param>
    public CircuitBreaker(
        double failureRatio,
        TimeSpan? samplingDuration,
        int minimumThroughput,
        TimeSpan? breakDuration,
        int maxHalfOpenRequests)
        : this(
            failureRatio,
            samplingDuration,
            minimumThroughput,
            breakDuration,
            maxHalfOpenRequests,
            new ClockCircuitBreakerTimeSource(new TimeProviderClock()))
    {
    }

    /// <summary>Creates a new circuit breaker with specified thresholds.</summary>
    /// <param name="failureRatio">Failure ratio threshold (0.0-1.0).</param>
    /// <param name="samplingDuration">Window for sliding window evaluation.</param>
    /// <param name="minimumThroughput">Minimum requests before evaluating ratio.</param>
    /// <param name="breakDuration">Duration to stay open before half-open.</param>
    /// <param name="maxHalfOpenRequests">Max requests in half-open state.</param>
    /// <param name="clock">Optional clock for testability (defaults to TimeProviderClock()).</param>
#pragma warning disable RS0027 // Existing 2.1.0 optional constructor preserved for source compatibility.
    public CircuitBreaker(
        double failureRatio = 0.5,
        TimeSpan? samplingDuration = null,
        int minimumThroughput = 10,
        TimeSpan? breakDuration = null,
        int maxHalfOpenRequests = 3,
        IClock? clock = null
    )
        : this(
            failureRatio,
            samplingDuration,
            minimumThroughput,
            breakDuration,
            maxHalfOpenRequests,
            new ClockCircuitBreakerTimeSource(clock ?? new TimeProviderClock()))
    {
    }
#pragma warning restore RS0027

    /// <summary>Creates a new circuit breaker backed by the supplied time provider.</summary>
    /// <param name="timeProvider">Time provider used for UTC and monotonic elapsed time.</param>
    public CircuitBreaker(TimeProvider timeProvider)
        : this(
            failureRatio: 0.5,
            samplingDuration: null,
            minimumThroughput: 10,
            breakDuration: null,
            maxHalfOpenRequests: 3,
            new TimeProviderCircuitBreakerTimeSource(timeProvider))
    {
    }

    /// <summary>Creates a new circuit breaker backed by the supplied time provider.</summary>
    /// <param name="timeProvider">Time provider used for UTC and monotonic elapsed time.</param>
    /// <param name="failureRatio">Failure ratio threshold (0.0-1.0).</param>
    public CircuitBreaker(TimeProvider timeProvider, double failureRatio)
        : this(
            failureRatio,
            samplingDuration: null,
            minimumThroughput: 10,
            breakDuration: null,
            maxHalfOpenRequests: 3,
            new TimeProviderCircuitBreakerTimeSource(timeProvider))
    {
    }

    /// <summary>Creates a new circuit breaker backed by the supplied time provider.</summary>
    /// <param name="timeProvider">Time provider used for UTC and monotonic elapsed time.</param>
    /// <param name="failureRatio">Failure ratio threshold (0.0-1.0).</param>
    /// <param name="samplingDuration">Window for sliding window evaluation.</param>
    public CircuitBreaker(
        TimeProvider timeProvider,
        double failureRatio,
        TimeSpan? samplingDuration)
        : this(
            failureRatio,
            samplingDuration,
            minimumThroughput: 10,
            breakDuration: null,
            maxHalfOpenRequests: 3,
            new TimeProviderCircuitBreakerTimeSource(timeProvider))
    {
    }

    /// <summary>Creates a new circuit breaker backed by the supplied time provider.</summary>
    /// <param name="timeProvider">Time provider used for UTC and monotonic elapsed time.</param>
    /// <param name="failureRatio">Failure ratio threshold (0.0-1.0).</param>
    /// <param name="samplingDuration">Window for sliding window evaluation.</param>
    /// <param name="minimumThroughput">Minimum requests before evaluating ratio.</param>
    public CircuitBreaker(
        TimeProvider timeProvider,
        double failureRatio,
        TimeSpan? samplingDuration,
        int minimumThroughput)
        : this(
            failureRatio,
            samplingDuration,
            minimumThroughput,
            breakDuration: null,
            maxHalfOpenRequests: 3,
            new TimeProviderCircuitBreakerTimeSource(timeProvider))
    {
    }

    /// <summary>Creates a new circuit breaker backed by the supplied time provider.</summary>
    /// <param name="timeProvider">Time provider used for UTC and monotonic elapsed time.</param>
    /// <param name="failureRatio">Failure ratio threshold (0.0-1.0).</param>
    /// <param name="samplingDuration">Window for sliding window evaluation.</param>
    /// <param name="minimumThroughput">Minimum requests before evaluating ratio.</param>
    /// <param name="breakDuration">Duration to stay open before half-open.</param>
    public CircuitBreaker(
        TimeProvider timeProvider,
        double failureRatio,
        TimeSpan? samplingDuration,
        int minimumThroughput,
        TimeSpan? breakDuration)
        : this(
            failureRatio,
            samplingDuration,
            minimumThroughput,
            breakDuration,
            maxHalfOpenRequests: 3,
            new TimeProviderCircuitBreakerTimeSource(timeProvider))
    {
    }

    /// <summary>Creates a new circuit breaker backed by the supplied time provider.</summary>
    /// <param name="timeProvider">Time provider used for UTC and monotonic elapsed time.</param>
    /// <param name="failureRatio">Failure ratio threshold (0.0-1.0).</param>
    /// <param name="samplingDuration">Window for sliding window evaluation.</param>
    /// <param name="minimumThroughput">Minimum requests before evaluating ratio.</param>
    /// <param name="breakDuration">Duration to stay open before half-open.</param>
    /// <param name="maxHalfOpenRequests">Max requests in half-open state.</param>
    public CircuitBreaker(
        TimeProvider timeProvider,
        double failureRatio,
        TimeSpan? samplingDuration,
        int minimumThroughput,
        TimeSpan? breakDuration,
        int maxHalfOpenRequests)
        : this(
            failureRatio,
            samplingDuration,
            minimumThroughput,
            breakDuration,
            maxHalfOpenRequests,
            new TimeProviderCircuitBreakerTimeSource(timeProvider))
    {
    }

    internal CircuitBreaker(
        double failureRatio,
        TimeSpan? samplingDuration,
        int minimumThroughput,
        TimeSpan? breakDuration,
        int maxHalfOpenRequests,
        ICircuitBreakerTimeSource timeSource)
    {
        if (double.IsNaN(failureRatio) || failureRatio <= 0 || failureRatio > 1)
            throw new ArgumentOutOfRangeException(
                nameof(failureRatio),
                failureRatio,
                "Failure ratio must be greater than zero and less than or equal to one.");

        var resolvedSamplingDuration = samplingDuration ?? TimeSpan.FromSeconds(30);
        if (resolvedSamplingDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(samplingDuration),
                resolvedSamplingDuration,
                "Sampling duration must be greater than zero.");

        if (minimumThroughput <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(minimumThroughput),
                minimumThroughput,
                "Minimum throughput must be greater than zero.");

        var resolvedBreakDuration = breakDuration ?? TimeSpan.FromSeconds(30);
        if (resolvedBreakDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(breakDuration),
                resolvedBreakDuration,
                "Break duration must be greater than zero.");

        if (maxHalfOpenRequests <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxHalfOpenRequests),
                maxHalfOpenRequests,
                "Max half-open requests must be greater than zero.");

        _failureRatio = failureRatio;
        _samplingDuration = resolvedSamplingDuration;
        _minimumThroughput = minimumThroughput;
        _breakDuration = resolvedBreakDuration;
        _maxHalfOpenRequests = maxHalfOpenRequests;
        _time = timeSource ?? throw new ArgumentNullException(nameof(timeSource));
    }

    /// <summary>Checks if a request is allowed through the circuit.</summary>
    /// <returns>True if request is allowed; false if circuit is open or isolated.</returns>
    /// <remarks>
    /// This is a compatibility/simple gate. Closed breakers allow requests.
    /// Open breakers deny requests until the break duration expires, then transition to
    /// half-open and allow up to the configured total half-open request count.
    /// It does not return a lease and does not release half-open slots after completion.
    /// Runtime half-open execution uses <see cref="AcquirePermit" />.
    /// </remarks>
    public bool AllowRequest() => AcquirePermit().IsAllowed;

    /// <summary>Acquires a correlated circuit breaker permit for one request attempt.</summary>
    /// <returns>A permit that reports whether the request is allowed.</returns>
    /// <remarks>
    /// Runtime code should record success or failure through the returned permit.
    /// Completion from stale half-open generations is ignored.
    /// </remarks>
    public CircuitBreakerPermit AcquirePermit()
    {
        CleanupWindow();
        var currentState = Volatile.Read(ref _state);

        if (currentState == (int)CircuitState.Closed)
            return CircuitBreakerPermit.Allowed(this, isHalfOpen: false, generation: 0);

        if (currentState == (int)CircuitState.Open)
        {
            var nowTimestamp = _time.GetTimestamp();
            if (!TryTransitionOpenToHalfOpen(nowTimestamp))
                return default;

            currentState = Volatile.Read(ref _state);
        }

        if (currentState == (int)CircuitState.HalfOpen)
        {
            var generation = Volatile.Read(ref _halfOpenGeneration);
            if (Interlocked.Increment(ref _activeHalfOpenProbes) > _maxHalfOpenRequests)
            {
                Interlocked.Decrement(ref _activeHalfOpenProbes);
                return default;
            }

            Interlocked.Increment(ref _halfOpenCount);
            return CircuitBreakerPermit.Allowed(this, isHalfOpen: true, generation);
        }

        return default;
    }

    /// <summary>Attempts to acquire a half-open probe slot.</summary>
    /// <param name="probe">Lease that releases the half-open probe slot on disposal.</param>
    /// <returns>
    /// True when the open breaker has reached its break duration and a probe slot was
    /// acquired, or when the breaker is already half-open and a slot is available.
    /// False for closed, isolated, still-open, or saturated half-open breakers.
    /// </returns>
    /// <remarks>
    /// This is the authoritative half-open probe API for runtime execution.
    /// Dispose the returned probe when the attempted operation completes.
    /// </remarks>
    public bool TryAcquireHalfOpenProbe(out CircuitBreakerProbe probe)
    {
        probe = default;

        var currentState = Volatile.Read(ref _state);
        if (currentState is not ((int)CircuitState.Open or (int)CircuitState.HalfOpen))
            return false;

        var permit = AcquirePermit();
        if (!permit.IsAllowed || !permit.IsHalfOpen)
            return false;

        probe = new CircuitBreakerProbe(permit);
        return true;
    }

    private bool TryTransitionOpenToHalfOpen(long nowTimestamp)
    {
        var openedAtTimestamp = Interlocked.Read(ref _openedAtTimestamp);
        if (_time.GetElapsedTime(openedAtTimestamp, nowTimestamp) < _breakDuration)
            return false;

        lock (_halfOpenTransitionGate)
        {
            var currentState = Volatile.Read(ref _state);
            if (currentState == (int)CircuitState.HalfOpen)
                return true;

            if (currentState != (int)CircuitState.Open)
                return false;

            openedAtTimestamp = Interlocked.Read(ref _openedAtTimestamp);
            if (_time.GetElapsedTime(openedAtTimestamp, nowTimestamp) < _breakDuration)
                return false;

            Interlocked.Exchange(ref _halfOpenCount, 0);
            Interlocked.Exchange(ref _activeHalfOpenProbes, 0);
            Interlocked.Exchange(ref _halfOpenSuccesses, 0);
            Interlocked.Increment(ref _halfOpenGeneration);
            Volatile.Write(ref _state, (int)CircuitState.HalfOpen);
            return true;
        }
    }

    /// <summary>Records a successful request and updates state.</summary>
    /// <remarks>
    /// Compatibility API for uncorrelated callers. Runtime code should use
    /// <see cref="AcquirePermit" /> and record completion through the returned permit.
    /// </remarks>
    public void RecordSuccess()
    {
        var currentState = Volatile.Read(ref _state);
        var generation = Volatile.Read(ref _halfOpenGeneration);
        RecordSuccess(currentState == (int)CircuitState.HalfOpen, generation);
    }

    internal void RecordPermitSuccess(int generation, bool isHalfOpenPermit)
    {
        RecordSuccess(isHalfOpenPermit, generation);
    }

    private void RecordSuccess(bool isHalfOpenPermit, int generation)
    {
        if (isHalfOpenPermit && !IsCurrentHalfOpenGeneration(generation))
            return;

        _window.Enqueue((_time.UtcNow, true));
        CleanupWindow();

        double alpha = _ewmaFailureRate > 0.1 ? 0.5 : 0.2;
        AtomicHelper.CompareExchangeLoop(ref _ewmaFailureRate, current => (1.0 - alpha) * current);

        if ((isHalfOpenPermit || Volatile.Read(ref _state) == (int)CircuitState.HalfOpen)
            && IsCurrentHalfOpenGeneration(generation))
        {
            int successes = Interlocked.Increment(ref _halfOpenSuccesses);
            if (successes >= _maxHalfOpenRequests / 2 + 1)
                TryCloseHalfOpen(generation);
        }
    }

    /// <summary>Records a failed request and updates state.</summary>
    /// <remarks>
    /// Compatibility API for uncorrelated callers. Runtime code should use
    /// <see cref="AcquirePermit" /> and record completion through the returned permit.
    /// </remarks>
    public void RecordFailure()
    {
        var currentState = Volatile.Read(ref _state);
        var generation = Volatile.Read(ref _halfOpenGeneration);
        RecordFailure(currentState == (int)CircuitState.HalfOpen, generation);
    }

    internal void RecordPermitFailure(int generation, bool isHalfOpenPermit)
    {
        RecordFailure(isHalfOpenPermit, generation);
    }

    private void RecordFailure(bool isHalfOpenPermit, int generation)
    {
        if (isHalfOpenPermit && !IsCurrentHalfOpenGeneration(generation))
            return;

        _window.Enqueue((_time.UtcNow, false));
        CleanupWindow();
        UpdateEwmaFailureRate();
        AddEarlyWarningToWindow();

        if ((isHalfOpenPermit || Volatile.Read(ref _state) == (int)CircuitState.HalfOpen)
            && IsCurrentHalfOpenGeneration(generation))
        {
            TransitionToOpenIfNeeded((int)CircuitState.HalfOpen, generation);
            return;
        }

        EvaluateSlidingWindow();
    }

    private void UpdateEwmaFailureRate()
    {
        double alpha = _ewmaFailureRate > 0.1 ? 0.5 : 0.2;
        AtomicHelper.CompareExchangeLoop(
            ref _ewmaFailureRate,
            current => alpha * 1.0 + (1.0 - alpha) * current
        );
    }

    private void AddEarlyWarningToWindow()
    {
        // Early warning: EWMA spike → pre-emptively add to window
        if (_ewmaFailureRate > _failureRatio * 1.5)
            _window.Enqueue((_time.UtcNow, false));
    }

    private void EvaluateSlidingWindow()
    {
        int total = _window.Count;
        if (total < _minimumThroughput)
            return;

        int failures = 0;
        foreach (var (_, ok) in _window)
            if (!ok)
                failures++;

        int currentState = Volatile.Read(ref _state);
        if ((double)failures / total >= _failureRatio)
            TransitionToOpenIfNeeded(currentState, expectedGeneration: null);
    }

    private bool TransitionToOpenIfNeeded(int expectedState, int? expectedGeneration)
    {
        if (expectedState is not ((int)CircuitState.Closed or (int)CircuitState.HalfOpen))
            return false;

        while (true)
        {
            var currentState = Volatile.Read(ref _state);
            if (currentState != expectedState)
                return false;

            if (expectedGeneration is int generation
                && Volatile.Read(ref _halfOpenGeneration) != generation)
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)CircuitState.Open,
                    expectedState) != expectedState)
            {
                continue;
            }

            Interlocked.Exchange(ref _openedAtTimestamp, _time.GetTimestamp());
            ResetHalfOpenCounters();
            return true;
        }
    }

    /// <summary>Manually isolates the circuit (blocks all requests).</summary>
    public void Isolate() => Interlocked.Exchange(ref _state, (int)CircuitState.Isolated);

    /// <summary>Resets the circuit to Closed state and clears history.</summary>
    public void Reset()
    {
        while (_window.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _state, (int)CircuitState.Closed);
        Interlocked.Increment(ref _halfOpenGeneration);
        ResetHalfOpenCounters();
        Interlocked.Exchange(ref _ewmaFailureRate, 0.0);
    }

    /// <summary>Calculates the current failure ratio from the sliding window.</summary>
    /// <returns>Failure ratio in range [0.0, 1.0].</returns>
    public double GetCurrentFailureRatio()
    {
        CleanupWindow();
        int total = _window.Count;
        if (total == 0)
            return 0;
        int failures = 0;
        foreach (var (_, ok) in _window)
            if (!ok)
                failures++;
        return (double)failures / total;
    }

    /// <summary>Export metrics for dashboard integration.</summary>
    /// <returns>Dictionary of circuit breaker metrics.</returns>
    private static readonly string[] _metricKeys =
    [
        "cb_state",
        "cb_failure_ratio",
        "cb_ewma_failure_rate",
        "cb_half_open_attempts",
    ];

    /// <summary>Export metrics for dashboard integration. Returns a dictionary with circuit breaker state, failure ratio, EWMA rate, and half-open attempts.</summary>
    /// <returns>Dictionary of circuit breaker metrics keyed by metric name.</returns>
    public Dictionary<string, object> GetMetrics()
    {
        var dict = new Dictionary<string, object>(4); // Exact capacity
        dict[_metricKeys[0]] = State.ToString();
        dict[_metricKeys[1]] = GetCurrentFailureRatio();
        dict[_metricKeys[2]] = _ewmaFailureRate;
        dict[_metricKeys[3]] = _halfOpenCount;
        return dict;
    }

    internal void ReleaseHalfOpenPermit(int generation)
    {
        if (Volatile.Read(ref _halfOpenGeneration) == generation
            && Volatile.Read(ref _activeHalfOpenProbes) > 0)
        {
            Interlocked.Decrement(ref _activeHalfOpenProbes);
        }
    }

    private void CleanupWindow()
    {
        var cutoff = _time.UtcNow - _samplingDuration;

        while (_window.TryPeek(out var item) && item.Timestamp < cutoff)
        {
            _window.TryDequeue(out _);
        }
    }

    private bool IsCurrentHalfOpenGeneration(int generation) =>
        Volatile.Read(ref _state) == (int)CircuitState.HalfOpen
        && Volatile.Read(ref _halfOpenGeneration) == generation;

    private void TryCloseHalfOpen(int generation)
    {
        if (!IsCurrentHalfOpenGeneration(generation))
            return;

        if (Interlocked.CompareExchange(
                ref _state,
                (int)CircuitState.Closed,
                (int)CircuitState.HalfOpen) != (int)CircuitState.HalfOpen)
        {
            return;
        }

        if (Volatile.Read(ref _halfOpenGeneration) != generation)
            return;

        Interlocked.Increment(ref _halfOpenGeneration);
        ResetHalfOpenCounters();
        Interlocked.Exchange(ref _ewmaFailureRate, 0.0);
    }

    private void ResetHalfOpenCounters()
    {
        Interlocked.Exchange(ref _halfOpenCount, 0);
        Interlocked.Exchange(ref _activeHalfOpenProbes, 0);
        Interlocked.Exchange(ref _halfOpenSuccesses, 0);
    }
}

/// <summary>Correlated permit for one circuit breaker request attempt.</summary>
public readonly struct CircuitBreakerPermit : IDisposable
{
    private readonly CircuitBreaker? _owner;
    private readonly int _generation;
    private readonly bool _isHalfOpen;
    private readonly LeaseState? _lease;

    private CircuitBreakerPermit(
        CircuitBreaker owner,
        bool isHalfOpen,
        int generation)
    {
        _owner = owner;
        _isHalfOpen = isHalfOpen;
        _generation = generation;
        _lease = isHalfOpen ? new LeaseState(owner, generation) : null;
        IsAllowed = true;
    }

    /// <summary>Gets whether the request is allowed through the circuit.</summary>
    public bool IsAllowed { get; }

    internal bool IsHalfOpen => _isHalfOpen;

    internal static CircuitBreakerPermit Allowed(
        CircuitBreaker owner,
        bool isHalfOpen,
        int generation) =>
        new(owner, isHalfOpen, generation);

    /// <summary>Records successful completion for this permit.</summary>
    public void RecordSuccess()
    {
        if (!IsAllowed || _owner is null)
            return;

        _owner.RecordPermitSuccess(_generation, _isHalfOpen);
    }

    /// <summary>Records failed completion for this permit.</summary>
    public void RecordFailure()
    {
        if (!IsAllowed || _owner is null)
            return;

        _owner.RecordPermitFailure(_generation, _isHalfOpen);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lease?.Release();
    }

    private sealed class LeaseState
    {
        private readonly CircuitBreaker _owner;
        private readonly int _generation;
        private int _released;

        internal LeaseState(CircuitBreaker owner, int generation)
        {
            _owner = owner;
            _generation = generation;
        }

        internal void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _owner.ReleaseHalfOpenPermit(_generation);
        }
    }
}

/// <summary>Lease for a circuit breaker half-open probe slot.</summary>
public readonly struct CircuitBreakerProbe : IDisposable
{
    private readonly CircuitBreakerPermit _permit;

    internal CircuitBreakerProbe(CircuitBreakerPermit permit)
    {
        _permit = permit;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _permit.Dispose();
    }
}
