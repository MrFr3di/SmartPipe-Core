# API Reference

## Core Interfaces

### ISource<T>

```csharp
public interface ISource<T>
{
    Task InitializeAsync(CancellationToken ct = default);
    IAsyncEnumerable<ProcessingContext<T>> ReadAsync(CancellationToken ct = default);
    Task DisposeAsync();
}
```

ITransformer<TInput, TOutput>

```csharp
public interface ITransformer<TInput, TOutput>
{
    Task InitializeAsync(CancellationToken ct = default);
    ValueTask<ProcessingResult<TOutput>> TransformAsync(ProcessingContext<TInput> ctx, CancellationToken ct = default);
    Task DisposeAsync();
}
```

ISink<T>

```csharp
public interface ISink<T>
{
    Task InitializeAsync(CancellationToken ct = default);
    Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default);
    Task DisposeAsync();
}
```

## Core Types

ProcessingContext<T>
Immutable context with auto-generated TraceId, Payload, Metadata, and timing.

ProcessingResult<T>
Result wrapper with Partial Success pattern. Never throws — errors become data.

SmartPipeError
Structured error with Type (Transient/Permanent), Category, and optional InnerException.

## Pipeline Configuration

### SmartPipeChannelOptions

| Property | Default | Description |
|----------|---------|-------------|
| `MaxDegreeOfParallelism` | `Environment.ProcessorCount` | Parallel transformers |
| `BoundedCapacity` | `1000` | Channel capacity |
| `ContinueOnError` | `true` | Continue on transformer failure |
| `TotalRequestTimeout` | `5 min` | Entire pipeline timeout |
| `AttemptTimeout` | `30 sec` | Per-transformer timeout |
| `DeduplicationFilter` | `null` | Optional Bloom filter |
| `OnMetrics` | `null` | Metrics delegate callback |
| `FeatureFlags` | see below | Toggle components |

### Feature Flags

| Flag | Default | Purpose |
|------|---------|---------|
| `RetryQueue` | `false` | Delayed retry with jitter |
| `CircuitBreaker` | `false` | Failure rate protection |
| `ObjectPool` | `true` | Context reuse (0 alloc) |
| `DebugSampling` | `false` | Reservoir sampling |
| `CuckooFilter` | `false` | TTL deduplication |
| `JumpHash` | `false` | Consumer sharding |

### New in v1.0.3

| Property | Default | Description |
|----------|---------|-------------|
| `UseRendezvous` | `false` | Strict Producer-Consumer sync (Capacity=0) |
| `FullMode` | `Wait` | Channel full behavior: Wait/DropOldest/DropNewest |

### New Components

| Component | Type | Description |
|-----------|------|-------------|
| `HyperLogLogEstimator` | Count-Distinct | Approximate unique count, O(1) memory |
| `MiddlewareTransformer<T>` | Transform | `Func<T,T>` as lightweight ITransformer |
| `DeadLetterSink<T>` | Sink | Persist failed items to JSON |
| `ChannelMerge` | Extension | Merge two ChannelReader<T> streams |
| `SmartPipeLivenessCheck` | Health | Kubernetes liveness probe |
| `SmartPipeReadinessCheck` | Health | Kubernetes readiness probe |

### Core Types

| Type | Description |
|------|-------------|
| `ProcessingContext<T>` | Mutable context with Data Lineage keys |
| `DataLineage` | Constants for provenance tracking in Metadata |