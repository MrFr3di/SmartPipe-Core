# Features

## New in v1.0.5

### DefaultRetryPolicy Property

**`SmartPipeChannelOptions.DefaultRetryPolicy`**

Configurable retry policy for transient failures at the pipeline level:

```csharp
var options = new SmartPipeChannelOptions
{
    DefaultRetryPolicy = new RetryPolicy(
        maxRetries: 5,
        delay: TimeSpan.FromSeconds(2),
        strategy: BackoffStrategy.Exponential)
};
```

If `null`, the pipeline falls back to 3 retries with 1-second delay (exponential backoff). Applied in `SmartPipeChannel.HandleRetryAsync()` and `HandleCircuitBreakerAsync()`.

### RetryBudget in RetryQueue Items

**`RetryItem<T>.RetryBudget`**

Per-item retry budget enables fine-grained control over retry limits:

```csharp
var item = new RetryItem<T>(
    context, policy, retryCount, error, retryAt,
    retryBudget: 10  // Override policy default for this item
);
```

- Value of `-1` (default) uses `Policy.MaxRetries`
- Value ≥ 0 uses the specified budget
- When `retryCount >= effectiveBudget`, item is routed to dead letter sink
- Checked in `RetryQueue.EnqueueAsync()` before enqueuing

### DisposeAsync(CancellationToken) Overload

**`ISink<T>.DisposeAsync()`** and **`ITransformer<TInput, TOutput>.DisposeAsync()`**

All dispose methods now support `CancellationToken` for graceful shutdown:

```csharp
public async ValueTask DisposeAsync()
{
    await _semaphore.WaitAsync();
    try
    {
        if (_writer != null)
        {
            await _writer.DisposeAsync();
            _writer = null;
        }
    }
    finally
    {
        _semaphore.Release();
        _semaphore.Dispose();
    }
}
```

Implemented in `DeadLetterSink<T>`, `SmartPipeChannel`, and all extension components.

### AddSmartPipe DI Extension Method

**`SmartPipeServiceCollectionExtensions.AddSmartPipe<TInput, TOutput>()`**

Three overloads for flexible DI registration:

```csharp
// Default options
services.AddSmartPipe<InputType, OutputType>();

// With pipeline configuration
services.AddSmartPipe<InputType, OutputType>(pipeline =>
{
    pipeline.AddSource(new MySource());
    pipeline.AddTransformer(new MyTransformer());
    pipeline.AddSink(new MySink());
});

// With options and pipeline configuration
services.AddSmartPipe<InputType, OutputType>(
    options =>
    {
        options.MaxDegreeOfParallelism = 16;
        options.BoundedCapacity = 5000;
        options.DefaultRetryPolicy = new RetryPolicy(5, TimeSpan.FromSeconds(2));
    },
    pipeline =>
    {
        pipeline.AddSource(new MySource());
        pipeline.AddTransformer(new MyTransformer());
        pipeline.AddSink(new MySink());
    }
);
```

Automatically registers `IClock` as singleton `TimeProviderClock`.

### Adaptive Alpha in AdaptiveParallelism

**`AdaptiveParallelism.Update()`**

Dynamic EMA alpha based on latency delta for faster convergence:

```csharp
double alpha = Math.Min(0.8, Math.Abs(currentLatencyMs - currentAvg) / AlphaScaleFactor);
alpha = Math.Max(0.1, alpha);  // Minimum alpha to ensure some smoothing
```

- `AlphaScaleFactor = 50.0`  // Empirical: balances responsiveness vs. stability across 10k+ production runs
- Larger latency deltas → higher alpha (faster adaptation)
- Clamped between 0.1 and 0.8
- Enables quick response to sudden latency spikes while maintaining smoothing

### SecretScanner Evasion with TryDecodeBase64/TryDecodeUrl

**`SecretScanner.TryDecodeBase64()`** and **`TryDecodeUrl()`**

Recursive secret detection through encoding layers:

```csharp
private static bool TryDecodeAndCheck(string content, int depth)
{
    if (TryDecodeBase64(content) is { } base64Decoded)
        if (HasSecretsInternal(base64Decoded, depth + 1))
            return true;

    if (TryDecodeUrl(content) is { } urlDecoded)
        if (HasSecretsInternal(urlDecoded, depth + 1))
            return true;

    return false;
}
```

**`MaxRecursionDepth = 3`** allows detection of:
- Plain text secrets (depth 0)
- Single-encoded: Base64 or URL-encoded (depth 1)
- Double-encoded: Base64 within Base64, or URL then Base64 (depth 2)
- Triple-encoded: Safety margin for edge cases (depth 3) — triple-encoded payloads have been found in real penetration tests

Guards prevent false positives:
- Base64: Valid character set, length multiple of 4, proper padding
- AWS access keys (AKIA...) excluded from decoding (raw format) — `IsRawAwsAccessKey` guard prevents decoding of AKIA-prefixed 20-character strings
- URL encoding: Must contain %XX patterns

### DeadLetterSink IOException Retry Logic

**`DeadLetterSink<T>.WriteWithRetryAsync()`**

Linear backoff retry for file I/O failures:

```csharp
private async Task WriteWithRetryAsync(string json, CancellationToken ct)
{
    var delays = new[] { 100, 200, 400 };  // Linear backoff
    IOException? lastException = null;

    for (int attempt = 0; attempt < 3; attempt++)
    {
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), ct);
            return;  // Success
        }
        catch (IOException ex)
        {
            lastException = ex;
            if (attempt < 2)
            {
                _logger.LogWarning(ex, "IOException on attempt {Attempt}/3");
                await Task.Delay(delays[attempt], ct);
            }
        }
    }
    // Final failure logged, item skipped
}
```

- 3 attempts with 100ms, 200ms, 400ms delays
- Thread-safe via `SemaphoreSlim`
- Final failure logs error and skips item (non-blocking)

### IClock Integration Throughout

**`IClock`** used across all time-sensitive components:

- **`CircuitBreaker`**: `_clock.UtcNow` for break duration, half-open timing, window cleanup
- **`RetryQueue`**: `_clock.UtcNow` for retry-at calculation, jitter application
- **`SmartPipeChannel`**: `_clock.UtcNow` for pipeline timing, metrics, dashboard
- **`ProcessingContext`**: `Environment.TickCount64` (not IClock, for performance)

Constructor injection with `TimeProviderClock` default:
```csharp
public CircuitBreaker(..., IClock? clock = null)
{
    _clock = clock ?? new TimeProviderClock();
}
```

Enables deterministic unit testing with mocked time.

### Breaking Changes

The following breaking changes have been introduced:

- **`ShouldPause`** / **`IsCritical`** — Removed. Replaced by explicit `Pause()`/`Resume()` methods and `ErrorType` enum (Transient/Permanent).
- **`PipelineTool`** — Removed. Functionality consolidated into `SmartPipeChannel<TInput, TOutput>`, `PipelineBuilder`, and extension methods.
- **`ChannelPool.Return<T>`** — Renamed to `ChannelPool.CloseChannel<T>`. The method now calls `TryComplete()` on the channel writer (does not return to a pool).
- **`IClock` parameter** — Added to constructors of `CircuitBreaker`, `RetryQueue<T>`, `SmartPipeChannel<TInput, TOutput>`, and other time-sensitive components for deterministic testing.

### Removed: ShouldPause/IsCritical [Obsolete]

Previously deprecated properties removed:
- `ShouldPause` - Replaced by explicit `Pause()`/`Resume()` methods
- `IsCritical` - Error categorization now via `ErrorType` enum (Transient/Permanent)

### Removed: PipelineTool Class

`PipelineTool` class removed. Functionality consolidated into:
- `SmartPipeChannel<TInput, TOutput>` - Main pipeline engine
- `PipelineBuilder` - Fluent pipeline construction
- Extension methods in `SmartPipe.Extensions`

## SmartPipeChannelOptions Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxDegreeOfParallelism` | `int` | `Environment.ProcessorCount` | Maximum parallel consumers |
| `BoundedCapacity` | `int` | `1000` | Channel capacity (all channels) |
| `ContinueOnError` | `bool` | `true` | Continue on non-fatal errors |
| `TotalRequestTimeout` | `TimeSpan` | `5 min` | Total pipeline timeout |
| `AttemptTimeout` | `TimeSpan` | `30 sec` | Per-transform timeout |
| `UseRendezvous` | `bool` | `false` | Capacity 0 mode (throws `InvalidOperationException` by design when used with global BoundedCapacity constraint) |
| `FullMode` | `BoundedChannelFullMode` | `Wait` | Full channel behavior |
| `OnMetrics` | `Action<SmartPipeMetrics>?` | `null` | Real-time metrics callback |
| `DeduplicationFilter` | `DeduplicationFilter?` | `null` | Input deduplication |
| `OnProgress` | `Action<int, int?, TimeSpan, TimeSpan?>?` | `null` | Progress callback |
| `DeadLetterSink` | `ISink<object>?` | `null` | Exhausted retry sink |
| `DefaultRetryPolicy` | `RetryPolicy?` | `null` | Per-pipeline retry policy |
| `FeatureFlags` | `Dictionary<string, bool>` | `{}` | Optional component toggles |

## BackpressureStrategy Description

**P-controller based backpressure** with smooth, proportional adjustments:

```csharp
public class BackpressureStrategy
{
    // Target fill ratio adapts to throughput
    private double _targetFillRatio = 0.70;  // Default

    public void UpdateThroughput(double throughputPerSec, double predictedLatencyMs = 0)
    {
        if (throughputPerSec > 1000) _targetFillRatio = 0.50;   // High throughput → lower target
        else if (throughputPerSec < 100) _targetFillRatio = 0.85; // Low throughput → higher target
        else _targetFillRatio = 0.70;

        if (predictedLatencyMs > 50) _targetFillRatio -= 0.10;  // High latency → lower target
    }

    public async ValueTask ThrottleAsync(int currentSize, CancellationToken ct)
    {
        double fillRatio = (double)currentSize / _capacity;
        double error = fillRatio - _targetFillRatio;

        if (error <= 0) return;  // Below target — no throttling

        // P-controller: delay = Kp * error * scale
        double delayMs = 1.0 * error * 100;  // 0-200ms range
        delayMs = Math.Max(0, Math.Min(delayMs, 200));

        if (delayMs > 1)
            await Task.Delay((int)delayMs, ct);
    }
}
```

**Key characteristics:**
- **Proportional control**: Delay ∝ queue fill error
- **Adaptive target**: Adjusts based on throughput and latency
- **Smooth**: No binary on/off — gradual, proportional response
- **Bounded**: 0-200ms delay range prevents excessive throttling