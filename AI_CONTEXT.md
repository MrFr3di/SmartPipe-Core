# AI Context — SmartPipe.Core

## 1. Project Identity

- **Project**: SmartPipe.Core v1.0.5
- **Language**: C#
- **Framework**: .NET 10
- **Architecture**: Zero-dependency ETL pipeline engine (Core layer has only Microsoft.Extensions.Logging.Abstractions as external NuGet dependency)

---

## 2. All Public Interfaces

```csharp
public interface ISource<T>
{
    Task InitializeAsync(CancellationToken ct = default);
    IAsyncEnumerable<ProcessingContext<T>> ReadAsync([EnumeratorCancellation] CancellationToken ct = default);
    Task DisposeAsync();
}
```

```csharp
public interface ISink<T>
{
    Task InitializeAsync(CancellationToken ct = default);
    Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default);
    Task DisposeAsync();
}
```

```csharp
public interface ITransformer<TInput, TOutput>
{
    Task InitializeAsync(CancellationToken ct = default);
    ValueTask<ProcessingResult<TOutput>> TransformAsync(ProcessingContext<TInput> ctx, CancellationToken ct = default);
    Task DisposeAsync();
}
```

---

## 2.1 Enums

### PipelineState
```csharp
public enum PipelineState
{
    NotStarted,
    Running,
    Paused,
    Completed,
    Faulted,
    Cancelled
}
```

### ErrorType
```csharp
public enum ErrorType
{
    Transient,
    Permanent
}
```

### BackoffStrategy
```csharp
public enum BackoffStrategy
{
    Fixed,
    Linear,
    Exponential
}
```

### CircuitState
```csharp
public enum CircuitState
{
    Closed,
    Open,
    HalfOpen,
    Isolated
}
```

---

## 2.2 Core Classes

### PipelineDashboard
Dashboard data snapshot
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

### ExponentialHistogram
Latency histogram with percentiles
```csharp
public class ExponentialHistogram
{
    public double P50 { get; }
    public double P95 { get; }
    public double P99 { get; }
    
    public ExponentialHistogram(double minValue = 0.1, double maxValue = 100000, int bucketCount = 100);
    public void Record(double value);
    public double GetPercentile(double p);
}
```

### JumpHash
Consistent hashing
```csharp
public class JumpHash
{
    public static int Hash(ulong key, int numBuckets);
}
```

### PipelineCancellation
Timeout handling
```csharp
public class PipelineCancellation
{
    public static CancellationTokenSource CreateTimeout(TimeSpan timeout);
    public static ValueTask<ProcessingResult<T>> WithTimeoutAsync<T>(ValueTask<ProcessingResult<T>> task, TimeSpan timeout, ulong traceId);
}
```

### PipelineBuilder<T>
Fluent API for pipeline construction
```csharp
public class PipelineBuilder<T>
{
    public static PipelineBuilder<T> From<T>(ISource<T> source);
}
```

### HyperLogLogEstimator
Cardinality estimation
```csharp
public class HyperLogLogEstimator
{
    public HyperLogLogEstimator(int precision = 12);
    public void Add(ulong hash);
    public double Estimate();
    public static HyperLogLogEstimator Merge(params HyperLogLogEstimator[] es);
}
```

### ReservoirSampler<T>
Reservoir sampling
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

### AdaptiveMetrics
Latency and throughput tracking
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

**MiddlewareTransformer<T>** - Middleware wrapper for simple Func delegates
```csharp
public class MiddlewareTransformer<T> : ITransformer<T, T>
{
    public MiddlewareTransformer(Func<T, T> func);
    
    // From ITransformer<T, T>:
    public Task InitializeAsync(CancellationToken ct = default);
    public ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default);
    public Task DisposeAsync();
}
```

### ObjectPool<T>
Object pooling
```csharp
public class ObjectPool<T>
{
    public ObjectPool(Func<T> factory, int capacity = 256);
    public T Rent();
    public void Return(T item);
}
```

### SecretScanner
Security scanning
```csharp
public class SecretScanner
{
    public bool HasSecrets(string content);
    public string Redact(string content);
}
```

### PipelineSimulator
Test/demo utilities
```csharp
public class PipelineSimulator
{
    public static Task RunDemoAsync(CancellationToken ct = default);
}
```

---

## 2.2 Extensions Project Types (SmartPipe.Extensions)

### SmartPipeLivenessCheck<TIn, TOut>
```csharp
public class SmartPipeLivenessCheck<TIn, TOut>
{
    public SmartPipeLivenessCheck(SmartPipeChannel<TIn, TOut> p);
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext ctx, CancellationToken ct = default);
}
```

### SmartPipeReadinessCheck<TIn, TOut>
```csharp
public class SmartPipeReadinessCheck<TIn, TOut>
{
    // Similar to LivenessCheck
}
```

### SmartPipeHostedService<TInput, TOutput>
```csharp
public class SmartPipeHostedService<TInput, TOutput> : IHostedService
{
    public SmartPipeHostedService(SmartPipeChannel<TInput, TOutput> pipeline, ILogger<SmartPipeHostedService<TInput, TOutput>> logger);
    public Task StopAsync(CancellationToken ct);
}
```

### ChannelMerge
```csharp
public class ChannelMerge
{
    public static ChannelReader<T> Merge<T>(ChannelReader<T> first, ChannelReader<T> second);
}
```

### SmartPipeResilienceExtensions
```csharp
public static class SmartPipeResilienceExtensions
{
    public static IServiceCollection AddSmartPipe<TInput, TOutput>(
        this IServiceCollection services,
        Action<SmartPipeChannel<TInput, TOutput>> configurePipeline,
        Action<ResiliencePipelineBuilder>? configureResilience = null);
        
    public static IServiceCollection AddSmartPipeHostedService<TInput, TOutput>(
        this IServiceCollection services,
        Action<SmartPipeChannel<TInput, TOutput>> configurePipeline,
        Action<ResiliencePipelineBuilder>? configureResilience = null);
}
```

### Additional Extensions Types:
- **CsvTransform** - CSV transformation
- **JsonTransform** - JSON transformation  
- **CompressionTransform** - Compression/decompression
- **FilterTransform** - Filtering transformation
- **CompositeTransform** - Composite transformation
- **ConditionalTransform** - Conditional transformation
- **ValidationTransform** - Validation transformation
- **MapsterTransform** - Mapster-based transformation
- **PollyResilienceTransform** - Polly resilience wrapper
- **JsonFileSink** - JSON file output
- **CsvFileSink** - CSV file output
- **DbSink** - Database output
- **HttpSink** - HTTP output
- **LoggerSink** - ILogger output
- **DeadLetterSink** - Dead letter sink
- **JsonFileSource** - JSON file source
- **CsvFileSource** - CSV file source
- **DapperSelector** - Dapper-based selector
- **EfCoreSelector** - EF Core selector
- **HttpSelector** - HTTP selector
- **DeadLetterSource** - Dead letter source
- **SmartPipeServiceCollectionExtensions** - DI registration

---

## 3. SmartPipeChannel Public API

```csharp
public class SmartPipeChannel<TInput, TOutput> : IAsyncDisposable
{
    // Core properties
    public PipelineState State { get; }
    public ExponentialHistogram LatencyHistogram { get; }
    
    // Events
    public event Action<PipelineState, PipelineState>? OnStateChanged;
    
    // Methods
    public ChannelReader<ProcessingResult<TOutput>>? AsChannelReader();
    public ChannelReader<ProcessingResult<TOutput>> RunInBackground(CancellationToken ct = default);
    public PipelineDashboard CreateDashboard();
    public ValueTask<ProcessingResult<TOutput>> ProcessSingleAsync(ProcessingContext<TInput> ctx, CancellationToken ct = default);
    
    // ... existing members ...
}
```

---

## 4. Module Map - ALL 55+ FILES

### Core Project (src/SmartPipe.Core/):
1. **ISource.cs** - Defines data source contract for pipeline inputs
2. **ISink.cs** - Defines data sink contract for pipeline outputs
3. **ITransformer.cs** - Defines transformation contract between pipeline stages
4. **SmartPipeChannel.cs** - Core pipeline engine orchestrating sources, transformers, sinks (contains PipelineState, PipelineDashboard)
5. **SmartPipeChannelOptions.cs** - Pipeline configuration and feature flags
6. **ProcessingContext.cs** - Mutable context carrying payload through pipeline
7. **ProcessingResult.cs** - Result wrapper with success/failure semantics (readonly record struct)
8. **SmartPipeError.cs** - Structured error with classification (readonly record struct)
9. **ErrorType.cs** - Error classification enum (Transient/Permanent)
10. **SmartPipeMetrics.cs** - OpenTelemetry-compatible metrics collection
11. **RetryPolicy.cs** - Configurable retry behavior with backoff strategies (contains BackoffStrategy enum)
12. **DeduplicationFilter.cs** - Bloom filter for probabilistic deduplication
13. **CuckooFilter.cs** - Advanced filter with deletion support
14. **RetryQueue.cs** - Lock-free retry queue with jitter

**RetryItem<T>** - Retry queue item (readonly record struct)
```csharp
public readonly record struct RetryItem<T>
{
    public ProcessingContext<T> Context { get; }
    public RetryPolicy Policy { get; }
    public int RetryCount { get; }
    public SmartPipeError Error { get; }
    public DateTime RetryAt { get; }
    public int RetryBudget { get; }
    public int EffectiveRetryBudget { get; }
    
    public RetryItem(ProcessingContext<T> Context, RetryPolicy Policy, int RetryCount, SmartPipeError Error, DateTime RetryAt, int RetryBudget = -1);
}
```

15. **BackpressureStrategy.cs** - P-controller based backpressure management
16. **ChannelPool.cs** - Channel reuse pool for allocation reduction
17. **SmartPipeEventSource.cs** - EventSource with EventCounters for runtime telemetry
18. **CircuitBreaker.cs** - Lock-free circuit breaker with hybrid failure detection (contains CircuitState enum)
19. **AdaptiveParallelism.cs** - P-controller for dynamic parallelism adjustment
20. **ExponentialHistogram.cs** - Latency histogram with P50/P95/P99
21. **JumpHash.cs** - Consistent hashing implementation
22. **PipelineCancellation.cs** - Timeout handling utilities
23. **PipelineSimulator.cs** - Test/demo pipeline utilities
24. **PipelineBuilder.cs** - Fluent API for pipeline construction
26. **HyperLogLogEstimator.cs** - Cardinality estimation algorithm
27. **ReservoirSampler.cs** - Reservoir sampling algorithm
28. **AdaptiveMetrics.cs** - Latency and throughput tracking
29. **MiddlewareTransformer.cs** - Middleware wrapper for ITransformer
30. **ObjectPool.cs** - Generic object pooling
31. **SecretScanner.cs** - Security scanning for secrets

### Extensions Project (src/SmartPipe.Extensions/):
32. **SmartPipeHealthCheck.cs** - Health checks (SmartPipeLivenessCheck, SmartPipeReadinessCheck)
33. **SmartPipeHostedService.cs** - IHostedService implementation
34. **SmartPipeResilienceExtensions.cs** - Resilience pipeline configuration
35. **ChannelMerge.cs** - Channel merging utilities
36. **SmartPipeServiceCollectionExtensions.cs** - DI registration extensions
37. **Transforms/CsvTransform.cs** - CSV transformation
38. **Transforms/JsonTransform.cs** - JSON transformation
39. **Transforms/CompressionTransform.cs** - Compression/decompression
40. **Transforms/FilterTransform.cs** - Filtering transformation
41. **Transforms/CompositeTransform.cs** - Composite transformation
42. **Transforms/ConditionalTransform.cs** - Conditional transformation
43. **Transforms/ValidationTransform.cs** - Validation transformation
44. **Transforms/MapsterTransform.cs** - Mapster-based transformation
45. **Transforms/PollyResilienceTransform.cs** - Polly resilience wrapper
46. **Transforms/FilterValidationExtensions.cs** - Filter validation extensions
47. **Sinks/JsonFileSink.cs** - JSON file output sink
48. **Sinks/CsvFileSink.cs** - CSV file output sink
49. **Sinks/DbSink.cs** - Database output sink
50. **Sinks/HttpSink.cs** - HTTP output sink
51. **Sinks/LoggerSink.cs** - ILogger output sink
52. **Sinks/DeadLetterSink.cs** - Dead letter sink
53. **Selectors/JsonFileSource.cs** - JSON file source
54. **Selectors/CsvFileSource.cs** - CSV file source
55. **Selectors/DapperSelector.cs** - Dapper-based selector
56. **Selectors/EfCoreSelector.cs** - EF Core selector
57. **Selectors/HttpSelector.cs** - HTTP selector
58. **Selectors/DeadLetterSource.cs** - Dead letter source

---

## 5. Key Conventions

- **ValueTask** — Used for hot-path transformations (`ITransformer.TransformAsync` returns `ValueTask<ProcessingResult<TOutput>>`).
- **BoundedCapacity** — Mandatory for all channels (default: 1000). Do not use `SemaphoreSlim` for backpressure.
- **ILogger<T>** — Logging via dependency injection. Never use `Console.WriteLine`.
- **Nullable** — Enabled project-wide (`#nullable enable`).
- **readonly record struct** — Used for `ProcessingResult<T>` and `SmartPipeError` for zero-allocation results.
- **ObjectPool** — `ProcessingContext` reused via `ObjectPool` to reduce allocations.
- **ChannelPool** — Channels reused to reduce allocations.
- **ETW Events** — `SmartPipeEventSource` for high-performance telemetry.
- **No sync-over-async** — Never use `.Result`, `.Wait()`, or blocking calls in async methods.
- **`IClock.UtcNow` (via TimeProvider)** — Do not use `DateTime.UtcNow` or `DateTime.Now` directly.

---

## 6. Anti-Patterns (Do Not Use)

- ❌ **No `.Result` or `.Wait()`** in async methods — causes deadlocks and blocks threads.
- ❌ **No catch blocks without logging** — swallowed exceptions must be logged via `ILogger<T>`.
- ❌ **No `DateTime.UtcNow` or `DateTime.Now`** — use `IClock.UtcNow` (via `TimeProvider`) for time measurements.
- ❌ **No hardcoded version strings** — versions must be sourced from build/packaging configuration.
- ❌ **No `SemaphoreSlim` for backpressure** — use `BoundedCapacity` on channels instead.
- ❌ **No sync-over-async patterns** — avoid `.GetAwaiter().GetResult()` and blocking wrappers.
- ❌ **No `Console.WriteLine`** — use `ILogger<T>` for all logging.
- ❌ **No direct SQL in Core layer** — Core must remain persistence-agnostic; SQL belongs in Infrastructure.

---