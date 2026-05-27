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

### ExponentialHistogram Percentile Caching

The `ExponentialHistogram` now caches `P50`, `P95`, and `P99` values. Percentiles are recomputed only when `_totalCount` changes, avoiding redundant bucket scans.

- **Read path**: `GetPercentile()` uses `Volatile.Read` instead of `Interlocked.CompareExchange`, preventing cache line invalidations under concurrent reads.

### CircuitBreaker States

The CircuitBreaker implements four states:

- **Closed**: Normal operation, all requests pass through. Failures are tracked via hybrid EWMA + sliding window detection.
- **Open**: Circuit is tripped, requests are blocked. Transitions to HalfOpen after `breakDuration` expires.
- **HalfOpen**: Testing if the circuit can be closed. Allows up to `maxHalfOpenRequests` concurrent requests. On sufficient success, transitions to Closed; on failure, returns to Open.
- **Isolated**: Manually isolated state (via `Isolate()` method), blocks all requests indefinitely until manually reset.
- **Performance:** `GetMetrics()` now creates the metrics dictionary with exact capacity (4), reducing internal resizing allocations

The CircuitBreaker uses atomic state transitions and combines EWMA (Exponentially Weighted Moving Average) for fast reaction with a sliding window for accurate threshold decisions.

## Core Components

### AdaptiveMetrics — Stopwatch.GetTimestamp

Uses `Stopwatch.GetTimestamp()` for throughput calculation instead of `Environment.TickCount64`, which wraps around after ~49.7 days. Includes guard against abnormally large elapsed time (>10 seconds) to prevent errors after system resume.

### TransformWithTimeoutAsync — Exception Handling

Catch-all `catch (Exception)` block handles unexpected exception types (`ArgumentException`, `JsonException`) that previously crashed the consumer task.

### ObjectPool Policy

`ObjectPool` is disabled by default in the 1.1.0 runtime path. Source-created
contexts are not returned to the pool, which avoids unclear ownership and
double-return risks. Any future pooling path must have explicit ownership,
reset, thread-safety, and benchmark evidence.

### DrainAsync

Accepts a `CancellationToken`. Uses `TryComplete()` instead of `Complete()` on channels to prevent `ChannelClosedException` when called concurrently.

### SmartPipeHostedService

`StopAsync` calls `pipeline.DisposeAsync()`. `Faulted` state exceptions are logged rather than rethrown.

### RetryQueue Polling

`TryGetNextAsync` always waits for items (removed race between `Count == 0` check and `WaitToReadAsync`). Single `CancellationTokenSource` per call. `TryWrite` for re-queueing.

### PipelineDashboard

Readonly record struct. `CBState` → `CbState`. Static `PipelineDashboard.Empty` for defaults. Constructor-based creation.

### DbSink

`ExecuteAsync()` instead of `Execute()` to avoid blocking the thread pool.

### DapperSelector

`ReadAsync` wraps reader in `try/finally` for immediate disposal on early exit or exception.

### SmartPipeResilienceExtensions

Removed dead code that created `PollyResilienceTransform` without adding it to the pipeline.

### ChannelMerge

Optional `BoundedChannelOptions` parameter for bounded output channels.

## Resilience Order

The pipeline applies resilience patterns in the following order:

1. **CircuitBreaker** → Fast-fail when circuit is open, preventing resource exhaustion
2. **Retry** → Transient failure recovery with backoff and jitter
3. **Timeout** → Per-attempt timeout (`AttemptTimeout`) via `PipelineCancellation.WithTimeoutAsync()`

This order ensures:
- Open circuits fail immediately without attempting retries
- Transient failures are retried before timing out the entire request
- Timeouts bound total latency per transform attempt
- RetryQueue polling uses a single `CancellationTokenSource` per `TryGetNextAsync` call. Re-queueing uses `TryWrite` to avoid blocking.
