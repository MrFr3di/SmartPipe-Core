# API Reference

## New Public Types

### IClock

```csharp
namespace SmartPipe.Core

public interface IClock
{
    /// <summary>Gets the current UTC date and time.</summary>
    DateTime UtcNow { get; }
}
```

Provides testable access to current UTC time. Enables deterministic unit tests by mocking time.

**Implementations:**
- `TimeProviderClock` - Production implementation using `System.TimeProvider`

**Usage:**
```csharp
public class MyService
{
    private readonly IClock _clock;
    public MyService(IClock clock) => _clock = clock;
    
    public void DoWork()
    {
        var now = _clock.UtcNow;  // Testable
    }
}
```

---

### TimeProviderClock

```csharp
namespace SmartPipe.Core

public sealed class TimeProviderClock : IClock
{
    /// <summary>Creates a new TimeProviderClock.</summary>
    /// <param name="timeProvider">The TimeProvider to use (defaults to TimeProvider.System).</param>
    public TimeProviderClock(TimeProvider? timeProvider = null);
    
    /// <summary>Gets the current UTC date and time using the configured TimeProvider.</summary>
    public DateTime UtcNow { get; }
}
```

Production clock implementation using .NET's `System.TimeProvider` (available in .NET 8+).

**Example:**
```csharp
IClock clock = new TimeProviderClock();
DateTime now = clock.UtcNow;
```

**Dependency Injection:**
```csharp
services.AddSingleton<IClock>(new TimeProviderClock());
```

---

## Updated Types

### PipelineState Enum

```csharp
namespace SmartPipe.Core

public enum PipelineState
{
    /// <summary>Pipeline has not started yet.</summary>
    NotStarted,
    
    /// <summary>Pipeline is currently running.</summary>
    Running,
    
    /// <summary>Pipeline is paused (producer suspended).</summary>
    Paused,  // NEW in v1.0.5
    
    /// <summary>Pipeline completed successfully.</summary>
    Completed,
    
    /// <summary>Pipeline terminated due to an error.</summary>
    Faulted,
    
    /// <summary>Pipeline was cancelled by user or timeout.</summary>
    Cancelled
}
```

**New State:** `Paused` - Pipeline is paused (producer suspended). Transitions:
- `Running` → `Paused`: via `SmartPipeChannel.Pause()`
- `Paused` → `Running`: via `SmartPipeChannel.Resume()`

**Usage:**
```csharp
var channel = new SmartPipeChannel<Input, Output>(options);
channel.Pause();   // State = PipelineState.Paused
channel.Resume();  // State = PipelineState.Running
```

---

### SmartPipeChannelOptions

```csharp
namespace SmartPipe.Core

public class SmartPipeChannelOptions
{
    // ... existing properties ...
    
    /// <summary>Default retry policy for transient failures. 
    /// If null, fallback to 3 retries with 1s delay.</summary>
    public RetryPolicy? DefaultRetryPolicy { get; set; }  // NEW in v1.0.5
    
    // ... existing properties ...
    
    /// <summary>Checks if a feature flag is enabled.</summary>
    public bool IsEnabled(string featureName);

    /// <summary>Enables a feature flag.</summary>
    public void EnableFeature(string featureName);

    /// <summary>Disables a feature flag.</summary>
    public void DisableFeature(string featureName);
}
```

**New Property:** `DefaultRetryPolicy`
- Type: `RetryPolicy?`
- Default: `null`
- Description: Per-pipeline retry policy for transient failures. If `null`, pipeline falls back to 3 retries with 1-second delay (exponential backoff).

**Example:**
```csharp
var options = new SmartPipeChannelOptions
{
    DefaultRetryPolicy = new RetryPolicy(
        maxRetries: 5,
        delay: TimeSpan.FromSeconds(2),
        strategy: BackoffStrategy.Exponential,
        maxDelay: TimeSpan.FromSeconds(60))
};
```

---

### DataLineage (Constants in ProcessingContext)

**Note:** `DataLineage` is no longer a standalone type. Constants are now defined in `ProcessingContext<T>`:

```csharp
public class ProcessingContext<T>
{
    // Data Lineage keys for Metadata dictionary
    public const string LineageSource = "lineage_source";
    public const string LineagePipeline = "lineage_pipeline";
    public const string LineageEnteredAt = "lineage_entered_at";
    public const string LineageTransform = "lineage_transform";
}
```

**Usage:**
```csharp
var context = new ProcessingContext<MyData>(payload);
context.Metadata[ProcessingContext<MyData>.LineageSource] = "my-source";
context.Metadata[ProcessingContext<MyData>.LineagePipeline] = "my-pipeline";
```

---

### AtomicHelper (Internal Only)

```csharp
namespace SmartPipe.Core

internal static class AtomicHelper
{
    /// <summary>
    /// Atomically updates a double value by applying a transformation function.
    /// </summary>
    /// <param name="location">Reference to the value to update.</param>
    /// <param name="update">Function that computes the new value from the current value.</param>
    /// <remarks>
    /// Uses compare-exchange loop for lock-free thread-safe updates.
    /// </remarks>
    public static void CompareExchangeLoop(ref double location, Func<double, double> update);
}
```

**⚠️ Internal Use Only:** This class is marked `internal` and is not part of the public API. It is used by `CircuitBreaker` for lock-free EWMA failure rate updates.

**Not intended for direct consumption.** Subject to change without notice.

---

## Core Types Summary

### SmartPipeChannelOptions

| Property | Type | Description |
|----------|------|-------------|
| `MaxDegreeOfParallelism` | `int` | Maximum parallel consumers |
| `BoundedCapacity` | `int` | Channel capacity (all channels) |
| `ContinueOnError` | `bool` | Continue on non-fatal errors |
| `TotalRequestTimeout` | `TimeSpan` | Total pipeline timeout |
| `AttemptTimeout` | `TimeSpan` | Per-transform timeout |
| `UseRendezvous` | `bool` | Capacity 0 mode |
| `FullMode` | `BoundedChannelFullMode` | Full channel behavior |
| `OnMetrics` | `Action<SmartPipeMetrics>?` | Metrics callback |
| `DeduplicationFilter` | `DeduplicationFilter?` | Input deduplication |
| `OnProgress` | `Action<int, int?, TimeSpan, TimeSpan?>?` | Progress callback |
| `DeadLetterSink` | `ISink<object>?` | Exhausted retry sink |
| `DefaultRetryPolicy` | `RetryPolicy?` | **NEW** Pipeline retry policy |
| `FeatureFlags` | `Dictionary<string, bool>` | Component toggles |

### CircuitBreaker

```csharp
public class CircuitBreaker
{
    public CircuitState State { get; }
    
    public CircuitBreaker(
        double failureRatio = 0.5,
        TimeSpan? samplingDuration = null,
        int minimumThroughput = 10,
        TimeSpan? breakDuration = null,
        int maxHalfOpenRequests = 3,
        IClock? clock = null);
    
    public bool AllowRequest();
    public void RecordSuccess();
    public void RecordFailure();
    public void Isolate();
    public void Reset();
    public double GetCurrentFailureRatio();
    public Dictionary<string, object> GetMetrics();
}
```

**States:** `Closed`, `Open`, `HalfOpen`, `Isolated`

### RetryQueue<T>

```csharp
public class RetryQueue<T>
{
    public int Count { get; }
    
    public RetryQueue(
        int capacity = 10000,
        ILogger<RetryQueue<T>>? logger = null,
        ISink<object>? deadLetterSink = null,
        IClock? clock = null);
    
    public ValueTask<bool> EnqueueAsync(
        ProcessingContext<T> ctx,
        RetryPolicy policy,
        int retryCount,
        SmartPipeError error,
        CancellationToken ct = default,
        int? retryBudget = null);
    
    public ValueTask<RetryItem<T>?> TryGetNextAsync(CancellationToken ct = default);
}
```

**RetryItem<T>:**
```csharp
public readonly record struct RetryItem<T>(
    ProcessingContext<T> Context,
    RetryPolicy Policy,
    int RetryCount,
    SmartPipeError Error,
    DateTime RetryAt,
    int RetryBudget = -1  // NEW: Per-item budget
)
```

### BackpressureStrategy

```csharp
public class BackpressureStrategy
{
    public BackpressureStrategy(int capacity);
    
    public void UpdateThroughput(double throughputPerSec, double predictedLatencyMs = 0);
    public double GetFillRatio(int currentSize);
    public ValueTask ThrottleAsync(int currentSize, CancellationToken ct);
}
```

**P-controller based:** Smooth, proportional adjustments (not binary thresholds).

### AdaptiveParallelism

```csharp
public class AdaptiveParallelism
{
    public int Current { get; }
    public int Min { get; }
    public int Max { get; }
    
    public AdaptiveParallelism(int min = 2, int max = 32);
    
    public void Update(double currentLatencyMs, int queueSize);
}
```

**Features:** P-controller with dead zone (5ms) and anti-windup. Adaptive alpha for fast convergence.

### SecretScanner

```csharp
public static class SecretScanner
{
    public static bool HasSecrets(string content);
    public static string Redact(string content);
}
```

**MaxRecursionDepth = 3:** Detects plain text, Base64-encoded, URL-encoded, and nested encodings.

### RetryPolicy

```csharp
public class RetryPolicy
{
    public int MaxRetries { get; }
    public TimeSpan Delay { get; }
    public TimeSpan MaxDelay { get; }
    public BackoffStrategy Strategy { get; }
    public Predicate<SmartPipeError> RetryOn { get; }
    public Action<ProcessingContext<object>, SmartPipeError, int>? OnRetry { get; }
    
    public RetryPolicy(
        int maxRetries = 3,
        TimeSpan? delay = null,
        TimeSpan? maxDelay = null,
        BackoffStrategy strategy = BackoffStrategy.Exponential,
        Predicate<SmartPipeError>? retryOn = null,
        Action<ProcessingContext<object>, SmartPipeError, int>? onRetry = null);
    
    public bool ShouldRetry(SmartPipeError error);
    public TimeSpan GetDelay(int retryCount);
}
```

**BackoffStrategy:** `Fixed`, `Linear`, `Exponential`

### SmartPipeChannel<TInput, TOutput>

```csharp
public class SmartPipeChannel<TInput, TOutput> : IAsyncDisposable
{
    public SmartPipeChannelOptions Options { get; }
    public SmartPipeMetrics Metrics { get; }
    public ExponentialHistogram LatencyHistogram { get; }
    public bool IsPaused { get; }
    public PipelineState State { get; }
    
    public event Action<PipelineState, PipelineState>? OnStateChanged;
    
    public SmartPipeChannel(
        SmartPipeChannelOptions options,
        IClock? clock = null,
        ILogger<SmartPipeChannel<TInput, TOutput>>? logger = null);
    
    public void AddSource(ISource<TInput> source);
    public void AddTransformer(ITransformer<TInput, TOutput> t);
    public void AddSink(ISink<TOutput> sink);
    
    public void Pause();      // NEW: Sets PipelineState.Paused
    public void Resume();     // NEW: Sets PipelineState.Running
    public void Cancel();
    
    public Task DrainAsync(TimeSpan timeout);
    public ValueTask DisposeAsync();
    
    public ValueTask<ProcessingResult<TOutput>> ProcessSingleAsync(
        ProcessingContext<TInput> ctx, CancellationToken ct = default);

    public ChannelReader<ProcessingResult<TOutput>>? AsChannelReader();
    public ChannelReader<ProcessingResult<TOutput>> RunInBackground(CancellationToken ct = default);
    public Task RunAsync(CancellationToken ct = default);
    
    public PipelineDashboard CreateDashboard();
}
```

**Resilience Order:** CircuitBreaker → Retry → Timeout

### ISink<T>

```csharp
public interface ISink<T>
{
    Task InitializeAsync(CancellationToken ct = default);
    Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default);
    Task DisposeAsync();
}
```

### ITransformer<TInput, TOutput>

```csharp
public interface ITransformer<TInput, TOutput>
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<ProcessingResult<TOutput>> TransformAsync(
        ProcessingContext<TInput> ctx,
        CancellationToken ct);
    Task DisposeAsync();
}
```

### ISource<T>

```csharp
public interface ISource<T>
{
    Task InitializeAsync(CancellationToken ct = default);
    IAsyncEnumerable<ProcessingContext<T>> ReadAsync(CancellationToken ct);
    ValueTask DisposeAsync();
}
```

## Error Types

```csharp
public enum ErrorType
{
    Transient,   // Retryable
    Permanent    // Non-retryable
}

public readonly record struct SmartPipeError(
    string Message,
    ErrorType Type,
    string Category,
    Exception? InnerException = null
);
```

## ProcessingResult<T>

```csharp
public readonly record struct ProcessingResult<T>(
    bool IsSuccess,
    T? Value,
    SmartPipeError? Error,
    ulong TraceId
)
{
    public static ProcessingResult<T> Success(T value, ulong traceId);
    public static ProcessingResult<T> Failure(SmartPipeError error, ulong traceId);
}
```

## ProcessingContext<T>

```csharp
public class ProcessingContext<T>
{
    public ulong TraceId { get; set; }
    public T Payload { get; set; } = default!;
    public Dictionary<string, string> Metadata { get; set; } = new();
    public long EnterPipelineTicks { get; set; }
    
    // Data Lineage constants
    public const string LineageSource = "lineage_source";
    public const string LineagePipeline = "lineage_pipeline";
    public const string LineageEnteredAt = "lineage_entered_at";
    public const string LineageTransform = "lineage_transform";
    
    /// <summary>Resets the context for reuse (clears payload, keeps metadata).</summary>
    public void Reset();
}
```

## DeduplicationFilter

```csharp
public class DeduplicationFilter
{
    public long ItemsSeen { get; }
    public bool ContainsAndAdd(ulong traceId);
}
```

## SmartPipeMetrics

```csharp
public class SmartPipeMetrics
{
    public int ItemsProcessed { get; }
    public int ItemsFailed { get; }
    public int DuplicatesFiltered { get; }
    public int Retries { get; }
    public double AvgLatencyMs { get; }
    public double PoolHitRate { get; }
    public int QueueSize { get; set; }
    public double SmoothLatencyMs { get; set; }
    public double SmoothThroughput { get; set; }
    
    public void RecordProcessed(long elapsedMs);
    public void RecordFailed();
    public void RecordRetry();
    public void RecordDuplicate();
    
    public Dictionary<string, object> Export();
    public string ExportJson();      // NEW
    public string ExportPrometheus(); // NEW
}
```

## PipelineDashboard

```csharp
public class PipelineDashboard
{
    public PipelineState State { get; set; }
    public int Current { get; set; }
    public int? Total { get; set; }
    public TimeSpan Elapsed { get; set; }
    public double P99LatencyMs { get; set; }
    public string CBState { get; set; } = "N/A";
    public Dictionary<string, object> Metrics { get; set; } = new();
}
```

---

### ChannelPool

```csharp
public static class ChannelPool
{
    /// <summary>Rent an unbounded channel with optimized options.</summary>
    /// <typeparam name="T">Channel element type.</typeparam>
    /// <returns>Configured unbounded channel.</returns>
    public static Channel<T> RentUnbounded<T>();
    
    /// <summary>Rent a bounded channel with capacity and full mode.</summary>
    /// <typeparam name="T">Channel element type.</typeparam>
    /// <param name="capacity">Maximum capacity.</param>
    /// <param name="mode">Behavior when channel is full.</param>
    /// <returns>Configured bounded channel.</returns>
    public static Channel<T> RentBounded<T>(int capacity, BoundedChannelFullMode mode);
    
    /// <summary>Close the channel writer (calls TryComplete, does not return to pool).</summary>
    /// <typeparam name="T">Channel element type.</typeparam>
    /// <param name="channel">Channel to close.</param>
    public static void CloseChannel<T>(Channel<T> channel);
}
```

Static utility class for creating and managing channels. Reduces allocations on repeated executions. Channels are not pooled for reuse — `CloseChannel` completes the writer (calls `TryComplete`) rather than returning to a pool.

---

### PipelineBuilder

```csharp
public static class PipelineBuilder
{
    /// <summary>Start building from a source.</summary>
    public static PipelineBuilder<T> From<T>(ISource<T> source);
}
```

```csharp
public class PipelineBuilder<TInput>
{
    /// <summary>Add a transformer (ITransformer).</summary>
    public PipelineBuilder<TInput, TOutput> Transform<TOutput>(ITransformer<TInput, TOutput> transformer);
}
```

```csharp
public class PipelineBuilder<TInput, TOutput>
{
    /// <summary>Add another transformer (same types).</summary>
    public PipelineBuilder<TInput, TOutput> Pipe(ITransformer<TInput, TOutput> transformer);
    
    /// <summary>Configure channel options.</summary>
    public PipelineBuilder<TInput, TOutput> WithOptions(Action<SmartPipeChannelOptions> configure);
    
    /// <summary>Add a sink and run the pipeline.</summary>
    public async Task To(ISink<TOutput> sink, CancellationToken ct = default);
}
```

Fluent API for declarative pipeline construction. Start with `PipelineBuilder.From(source)`, add transformers with `Transform()`, and finalize with `To(sink)` to execute. Supports middleware via `Func<T, T>` delegates and optional channel configuration via `WithOptions()`.

---

### MiddlewareTransformer<T>

```csharp
public class MiddlewareTransformer<T> : ITransformer<T, T>
{
    public MiddlewareTransformer(Func<T, T> func);
}
```

Simple middleware wrapper for Func delegates.

---

### CuckooFilter

```csharp
public class CuckooFilter
{
    /// <summary>Create filter for expected items and false positive rate.</summary>
    /// <param name="expectedItems">Expected number of items (default: 1,000,000).</param>
    /// <param name="falsePositiveRate">Desired false positive rate (default: 0.001).</param>
    public CuckooFilter(long expectedItems = 1_000_000, double falsePositiveRate = 0.001);
    
    /// <summary>Check if item fingerprint exists.</summary>
    public bool Contains(ulong hash);
    
    /// <summary>Insert item fingerprint into filter.</summary>
    public bool Add(ulong hash);
    
    /// <summary>Remove item fingerprint from filter.</summary>
    public bool Remove(ulong hash);
}
```

Advanced probabilistic filter with deletion support. Based on "Cuckoo Filter: Better Than Bloom" (NSDI, 2025). Supports merging filters and cuckoo kicking for high insertion rates.

---

### HyperLogLogEstimator

```csharp
public class HyperLogLogEstimator
{
    public HyperLogLogEstimator(int precision = 12);
    public void Add(ulong hash);
    public double Estimate();
    public static HyperLogLogEstimator Merge(params HyperLogLogEstimator[] es);
}
```

Cardinality estimation algorithm.

---

### ReservoirSampler<T>

```csharp
public class ReservoirSampler<T>
{
    public int Capacity { get; }
    public long Count { get; }
    public T[] Sample { get; }
    
    public ReservoirSampler(int capacity = 1000);
    public void Add(T item);
    public void Reset();
}
```

Reservoir sampling for statistical analysis.

---

### AdaptiveMetrics

```csharp
public class AdaptiveMetrics
{
    public double SmoothLatencyMs { get; }
    public double SmoothThroughputPerSec { get; }
    public double LatencyVelocity { get; }
    
    public AdaptiveMetrics();
    public void Update(double latencyMs);
    public double PredictNextLatency();
}
```

Latency and throughput tracking with EWMA.

---

### ObjectPool<T>

```csharp
public class ObjectPool<T>
{
    public ObjectPool(Func<T> factory, int capacity = 256);
    public T Rent();
    public void Return(T item);
}
```

Generic object pooling to reduce allocations.

---

### SmartPipeEventSource

```csharp
public class SmartPipeEventSource : EventSource
{
    public static SmartPipeEventSource Log = new();
    
    [Event(1)] public void PipelineStarted();
    [Event(2)] public void PipelineCompleted(long itemsProcessed, long elapsedMs);
    [Event(3)] public void ItemProcessed(long elapsedMs);
    [Event(4)] public void ItemFailed(string error);
}
```

EventSource with EventCounters for telemetry. Provides real-time performance counters for items processed, queue size, pool hit rate, backpressure activations, and circuit breaker state. Counters are initialized when ETW tracing is enabled.

---

## SmartPipe.Extensions Namespace

### SmartPipeServiceCollectionExtensions
Location: `src/SmartPipe.Extensions/SmartPipeServiceCollectionExtensions.cs`

```csharp
public static class SmartPipeServiceCollectionExtensions
{
    // Register with default options
    public static IServiceCollection AddSmartPipe<TInput, TOutput>(
        this IServiceCollection services);
    
    // Register with pipeline configuration
    public static IServiceCollection AddSmartPipe<TInput, TOutput>(
        this IServiceCollection services,
        Action<SmartPipeChannel<TInput, TOutput>> configure);
    
    // Register with options and pipeline configuration
    public static IServiceCollection AddSmartPipe<TInput, TOutput>(
        this IServiceCollection services,
        Action<SmartPipeChannelOptions> configureOptions,
        Action<SmartPipeChannel<TInput, TOutput>>? configurePipeline = null);
}
```
DI registration for SmartPipe pipelines. Automatically registers `IClock` as `TimeProviderClock`.

### SmartPipeLivenessCheck<TIn, TOut>
Location: `src/SmartPipe.Extensions/SmartPipeHealthCheck.cs`

```csharp
public class SmartPipeLivenessCheck<TIn, TOut> : IHealthCheck
{
    public SmartPipeLivenessCheck(SmartPipeChannel<TIn, TOut> p);
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext ctx, CancellationToken ct = default);
}
```
Liveness check - reports healthy when pipeline is not paused.

### SmartPipeReadinessCheck<TIn, TOut>
Location: `src/SmartPipe.Extensions/SmartPipeHealthCheck.cs`

```csharp
public class SmartPipeReadinessCheck<TIn, TOut> : IHealthCheck
{
    public SmartPipeReadinessCheck(SmartPipeChannel<TIn, TOut> p);
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext ctx, CancellationToken ct = default);
}
```
Readiness check - reports ready based on queue size and failure metrics.

### SmartPipeHostedService<TInput, TOutput>
Location: `src/SmartPipe.Extensions/SmartPipeHostedService.cs`

```csharp
public class SmartPipeHostedService<TInput, TOutput> : BackgroundService
{
    public SmartPipeHostedService(
        SmartPipeChannel<TInput, TOutput> pipeline,
        ILogger<SmartPipeHostedService<TInput, TOutput>> logger);
    
    protected override Task ExecuteAsync(CancellationToken ct);
    public override Task StopAsync(CancellationToken ct);
}
```
Hosted service for running pipelines in ASP.NET Core. Manages lifecycle with graceful draining.

### ChannelMerge
Location: `src/SmartPipe.Extensions/ChannelMerge.cs`

```csharp
public static class ChannelMerge
{
    public static ChannelReader<T> Merge<T>(params ChannelReader<T>[] readers);
}
```
Merges multiple channels into one.

### CompressionTransform

```csharp
public enum CompressionAlgorithm { Brotli, GZip }

/// <summary>
/// Transformer that compresses byte array payloads using Brotli or GZip algorithms.
/// Implements ITransformer{TInput, TOutput} for pipeline integration with byte[] input and output.
/// </summary>
public class CompressionTransform : ITransformer<byte[], byte[]>
{
    /// <summary>
    /// Initializes a new instance of CompressionTransform.
    /// </summary>
    /// <param name="algorithm">The compression algorithm to use. Defaults to Brotli.</param>
    /// <param name="level">The compression level. Defaults to Optimal.</param>
    public CompressionTransform(
        CompressionAlgorithm algorithm = CompressionAlgorithm.Brotli,
        CompressionLevel level = CompressionLevel.Optimal);
}
```

Compresses byte arrays using Brotli or GZip algorithms. Implements `ITransformer<byte[], byte[]>` for pipeline integration.

### CsvTransform<TInput, TOutput>
Location: `src/SmartPipe.Extensions/Transforms/CsvTransform.cs`

```csharp
public class CsvTransform<TInput, TOutput> : ITransformer<TInput, TOutput>
{
    public CsvTransform(...);
}
```
CSV transformation for pipeline integration.

### JsonTransform<TInput, TOutput>
Location: `src/SmartPipe.Extensions/Transforms/JsonTransform.cs`

```csharp
public class JsonTransform<TInput, TOutput> : ITransformer<TInput, TOutput>
{
    public JsonTransform(...);
}
```
JSON transformation using System.Text.Json.

### MapsterTransform<TInput, TOutput>
Location: `src/SmartPipe.Extensions/Transforms/MapsterTransform.cs`

```csharp
public class MapsterTransform<TInput, TOutput> : ITransformer<TInput, TOutput>
{
    public MapsterTransform(...);
}
```
Mapster-based object mapping transform.

### PollyResilienceTransform<T>
Location: `src/SmartPipe.Extensions/Transforms/PollyResilienceTransform.cs`

```csharp
public class PollyResilienceTransform<T> : ITransformer<T, T>
{
    public PollyResilienceTransform(...);
}
```
Wraps transforms with Polly resilience policies.

### CompositeTransform<T>
Location: `src/SmartPipe.Extensions/Transforms/CompositeTransform.cs`

```csharp
public class CompositeTransform<T> : ITransformer<T, T>
{
    public CompositeTransform(params ITransformer<T, T>[] transforms);
}
```
Chains multiple transforms sequentially.

### ConditionalTransform<T>
Location: `src/SmartPipe.Extensions/Transforms/ConditionalTransform.cs`

```csharp
public class ConditionalTransform<T> : ITransformer<T, T>
{
    public ConditionalTransform(Func<T, bool> predicate, ITransformer<T, T> transform);
}
```
Applies transform only when predicate is true.

### FilterTransform<T>
Location: `src/SmartPipe.Extensions/Transforms/FilterTransform.cs`

```csharp
public class FilterTransform<T> : ITransformer<T, T>
{
    public FilterTransform(Func<T, bool> predicate);
}
```
Filters items based on predicate.

### ValidationTransform<T>
Location: `src/SmartPipe.Extensions/Transforms/ValidationTransform.cs`

```csharp
public class ValidationTransform<T> : ITransformer<T, T>
{
    public ValidationTransform(...);
}
```
Validates items and marks failures.

### JsonFileSource<T>
Location: `src/SmartPipe.Extensions/Selectors/JsonFileSource.cs`

```csharp
public class JsonFileSource<T> : ISource<T>
{
    public JsonFileSource(string path);
}
```
Streams JSON files (array or NDJSON) as pipeline source.

### CsvFileSource<T>
Location: `src/SmartPipe.Extensions/Selectors/CsvFileSource.cs`

```csharp
public class CsvFileSource<T> : ISource<T>
{
    public CsvFileSource(string path);
}
```
Streams CSV files as pipeline source.

### DeadLetterSource<T>
Location: `src/SmartPipe.Extensions/Selectors/DeadLetterSource.cs`

```csharp
public class DeadLetterSource<T> : ISource<T>
{
    public DeadLetterSource(string path);
}
```
Reads from dead letter JSON file.

### DapperSelector<T>
Location: `src/SmartPipe.Extensions/Selectors/DapperSelector.cs`

```csharp
public class DapperSelector<T> : ISource<T>
{
    public DapperSelector(string connectionString, string sql);
}
```
Selects data using Dapper.

### EfCoreSelector<T>
Location: `src/SmartPipe.Extensions/Selectors/EfCoreSelector.cs`

```csharp
public class EfCoreSelector<T> : ISource<T>
{
    public EfCoreSelector(...);
}
```
Selects data using EF Core.

### HttpSelector<T>
Location: `src/SmartPipe.Extensions/Selectors/HttpSelector.cs`

```csharp
public class HttpSelector<T> : ISource<T>
{
    public HttpSelector(HttpClient client, string url);
}
```
Selects data via HTTP requests.

### JsonFileSink<T>
Location: `src/SmartPipe.Extensions/Sinks/JsonFileSink.cs`

```csharp
public class JsonFileSink<T> : ISink<T>
{
    public JsonFileSink(string path);
}
```
Writes results to JSON file (array format).

### CsvFileSink<T>
Location: `src/SmartPipe.Extensions/Sinks/CsvFileSink.cs`

```csharp
public class CsvFileSink<T> : ISink<T>
{
    public CsvFileSink(string path);
}
```
Writes results to CSV file.

### DbSink<T>
Location: `src/SmartPipe.Extensions/Sinks/DbSink.cs`

```csharp
public class DbSink<T> : ISink<T>
{
    public DbSink(...);
}
```
Writes results to database.

### HttpSink<T>
Location: `src/SmartPipe.Extensions/Sinks/HttpSink.cs`

```csharp
public class HttpSink<T> : ISink<T>
{
    public HttpSink(HttpClient client, string url);
}
```
Sends results via HTTP POST.

### LoggerSink<T>
Location: `src/SmartPipe.Extensions/Sinks/LoggerSink.cs`

```csharp
public class LoggerSink<T> : ISink<T>
{
    public LoggerSink(ILogger<LoggerSink<T>> logger);
}
```
Logs results via ILogger.

### DeadLetterSink<T>
Location: `src/SmartPipe.Extensions/Sinks/DeadLetterSink.cs`

```csharp
public class DeadLetterSink<T> : ISink<T>
{
    public DeadLetterSink(string path = "dead_letter.json", ILogger<DeadLetterSink<T>>? logger = null, Stream? stream = null);
}
```
Captures failed items to file with IOException retry logic.