# Architecture

## Pipeline Flow

The SmartPipe.Core pipeline uses a **P-controller (Proportional controller)** based approach for flow control and adaptive parallelism, replacing threshold-based binary decisions with smooth, proportional adjustments.

### P-Controller with Dead Zone and Anti-Windup

The pipeline employs P-controllers in two key areas:

1. **Adaptive Parallelism** (`AdaptiveParallelism`): Adjusts thread count based on latency error
   - **Dead Zone**: Ignores latency errors smaller than 5ms to prevent thrashing on minor fluctuations
   - **Anti-Windup**: Prevents error accumulation when at min/max limits — if `_current >= _max` and error > 0, or `_current <= _min` and error < 0, no adjustment is made
   - **Proportional Band**: An error of 20ms results in a 1-thread adjustment (raw adjustment = |error| / 20)
   - **CAP**: Maximum adjustment capped at 3 threads per iteration to prevent aggressive changes

2. **Backpressure Strategy** (`BackpressureStrategy`): Smoothly adjusts delay proportional to queue fill error
   - Calculates error as `fillRatio - targetFillRatio`
   - Applies P-controller gain (Kp = 1.0) to compute delay: `delayMs = KpGain * error * DelayScaleFactor`
   - Delay clamped between 0ms and 200ms
   - Target fill ratio adapts based on throughput (high throughput → lower target, low throughput → higher target)

### CircuitBreaker States

The CircuitBreaker implements four states:

- **Closed**: Normal operation, all requests pass through. Failures are tracked via hybrid EWMA + sliding window detection.
- **Open**: Circuit is tripped, requests are blocked. Transitions to HalfOpen after `breakDuration` expires.
- **HalfOpen**: Testing if the circuit can be closed. Allows up to `maxHalfOpenRequests` concurrent requests. On sufficient success, transitions to Closed; on failure, returns to Open.
- **Isolated**: Manually isolated state (via `Isolate()` method), blocks all requests indefinitely until manually reset.

The CircuitBreaker uses lock-free atomic operations for state transitions and combines EWMA (Exponentially Weighted Moving Average) for fast reaction with a sliding window for accurate threshold decisions.

## New v1.0.5 Components

### IClock Interface and TimeProviderClock Implementation

**`IClock`** (`src/SmartPipe.Core/IClock.cs`):
```csharp
public interface IClock
{
    DateTime UtcNow { get; }
}
```
Provides testable access to current UTC time, enabling deterministic unit tests by mocking time.

**`TimeProviderClock`** (`src/SmartPipe.Core/IClock.cs`):
```csharp
public sealed class TimeProviderClock : IClock
```
Production implementation using `System.TimeProvider` (available in .NET 8+). Defaults to `TimeProvider.System`.

Used throughout the codebase:
- `CircuitBreaker`: For tracking open/half-open timing and window cleanup
- `RetryQueue`: For calculating retry-at timestamps and jitter
- `SmartPipeChannel`: For pipeline timing and metrics

### AtomicHelper (Internal Utility)

**`AtomicHelper`** (`src/SmartPipe.Core/AtomicHelper.cs`):
```csharp
internal static class AtomicHelper
```
Internal utility providing lock-free atomic operations for `double` values using compare-exchange loops.

Key method:
- `CompareExchangeLoop(ref double location, Func<double, double> update)`: Atomically updates a double by repeatedly reading current value, computing new value, and CAS-ing until successful.

Used by `CircuitBreaker` for thread-safe EWMA failure rate updates without locks.

### DefaultRetryPolicy in SmartPipeChannelOptions

**`SmartPipeChannelOptions.DefaultRetryPolicy`** (`src/SmartPipe.Core/SmartPipeChannelOptions.cs`):
```csharp
public RetryPolicy? DefaultRetryPolicy { get; set; }
```
Optional retry policy for transient failures. If `null`, pipeline falls back to 3 retries with 1-second delay (exponential backoff).

Applied in:
- `SmartPipeChannel.HandleRetryAsync()`: Uses `_options.DefaultRetryPolicy ?? new RetryPolicy(3, TimeSpan.FromSeconds(1))`
- `SmartPipeChannel.HandleCircuitBreakerAsync()`: Same fallback when circuit is open and retry queue is enabled

### RetryBudget per Item in RetryQueue

**`RetryItem<T>.RetryBudget`** (`src/SmartPipe.Core/RetryQueue.cs`):
```csharp
public readonly record struct RetryItem<T>(
    ...,
    int RetryBudget = -1  // -1 = use policy default
)
```

Per-item retry budget allows individual items to have different retry limits than the global policy:
- `EffectiveRetryBudget` property returns `RetryBudget` if set (≥0), otherwise falls back to `Policy.MaxRetries`
- Checked in `EnqueueAsync()`: if `retryCount >= effectiveBudget`, item is routed to dead letter sink
- Enables fine-grained control: critical items can have higher budgets, non-critical items lower

## Resilience Order

The pipeline applies resilience patterns in the following order:

1. **CircuitBreaker** → Fast-fail when circuit is open, preventing resource exhaustion
2. **Retry** → Transient failure recovery with backoff and jitter
3. **Timeout** → Per-attempt timeout (`AttemptTimeout`) via `PipelineCancellation.WithTimeoutAsync()`

This order ensures:
- Open circuits fail immediately without attempting retries
- Transient failures are retried before timing out the entire request
- Timeouts bound total latency per transform attempt