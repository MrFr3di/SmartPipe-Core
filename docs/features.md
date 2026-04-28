# SmartPipe.Core — Complete Feature Reference

## Core Engine

### SmartPipeChannel<TInput, TOutput>
Main pipeline engine based on System.Threading.Channels.
Producer → Bounded Channel → Parallel Consumers → Output Channel → Sink.

**Execution modes:**
- `RunAsync()` — blocking execution until all sources are exhausted. Handles initialization, processing, graceful shutdown, and disposal of all components.
- `RunInBackground()` — non-blocking execution. Returns `ChannelReader<ProcessingResult<TOutput>>` for streaming consumption by SignalR hubs, gRPC streams, or other consumers.
- `ProcessSingleAsync()` — single item processing for AI agent tools (Semantic Kernel, AutoGen). Applies all configured transformers to one item.

**Flow control:**
- `Pause()` — stops reading from sources. In-flight items complete normally.
- `Resume()` — resumes reading from sources.
- `DrainAsync(timeout)` — graceful shutdown: pauses input, drains pending items, completes output channel. Returns when all items are processed or timeout expires.
- `AsChannelReader()` — exposes output channel as `ChannelReader<ProcessingResult<TOutput>>` for direct integration.

**Lifecycle management (new in v1.0.4):**
- `PipelineState` — enum: NotStarted, Running, Completed, Faulted, Cancelled. Tracked via volatile field.
- `OnStateChanged` — event fired on state transitions: `Action<PipelineState, PipelineState>`.
- `Cancel()` — graceful cancellation via internal CancellationTokenSource. Transitions state to Cancelled.
- `CreateDashboard()` — returns `PipelineDashboard` aggregating State, Progress, Metrics, CB info.

**Pipeline Dashboard:**
- `State` — current pipeline state
- `Current` — items processed so far
- `Elapsed` — time since pipeline started
- `P99LatencyMs` — 99th percentile latency
- `CBState` — CircuitBreaker state (or "N/A")
- `Metrics` — dictionary of all exported metrics

**Internal pipeline structure:**
1. Producer reads from all `ISource<TInput>` instances via `IAsyncEnumerable`
2. Items pass through `BackpressureStrategy` throttling
3. `DeduplicationFilter` removes duplicates (optional)
4. Items enter bounded input channel
5. Multiple parallel consumers (up to `MaxDegreeOfParallelism`) read from input channel
6. Each consumer checks `CircuitBreaker` before processing
7. `ITransformer.TransformAsync()` is called with `AttemptTimeout`
8. On transient failure, item goes to `RetryQueue` for later retry
9. On success or permanent failure, result goes to output channel
10. Output consumer writes results to all registered `ISink<TOutput>` instances
11. On completion, all components are disposed, channels returned to pool

### SmartPipeChannelOptions
Configuration for pipeline behavior.

| Property | Default | Description |
|----------|---------|-------------|
| `MaxDegreeOfParallelism` | CPU cores | Number of parallel consumer tasks |
| `BoundedCapacity` | 1000 | Input/output channel buffer size |
| `ContinueOnError` | true | Don't cancel pipeline on transformer failure |
| `TotalRequestTimeout` | 5 min | Maximum time for entire pipeline run |
| `AttemptTimeout` | 30 sec | Timeout per transformer invocation |
| `UseRendezvous` | false | Strict sync: BoundedCapacity=0, producer waits for consumer |
| `FullMode` | Wait | Channel behavior when full: Wait, DropOldest, DropNewest |
| `DeduplicationFilter` | null | Optional Bloom filter for deduplication |
| `OnMetrics` | null | Callback invoked after each processed item |
| `OnProgress` | null | Progress delegate: `(int current, int? total, TimeSpan elapsed, TimeSpan? eta)` |
| `DeadLetterSink` | null | Auto-routing for exhausted retries |

### SmartPipeMetrics
OpenTelemetry-compatible metrics collected during pipeline execution.
Uses `Meter` (static singleton) with Counter, Histogram, and ObservableGauge instruments.

**Counters (monotonic):**
- `ItemsProcessed` — total items successfully read from sources
- `ItemsFailed` — total items that failed transformation
- `DuplicatesFiltered` — items skipped by deduplication filter
- `Retries` — total retry attempts across all items

**Gauges (point-in-time):**
- `QueueSize` — current number of items in input channel
- `PoolHitRate` — percentage of ObjectPool hits vs misses

**Histograms (distribution):**
- Latency in milliseconds (per item processing time)

**Computed metrics:**
- `AvgLatencyMs` — running average of processing latency
- `SmoothLatencyMs` — exponentially smoothed latency (EMA)
- `SmoothThroughput` — exponentially smoothed throughput (items/sec)

**Export methods (new in v1.0.4):**
- `Export()` — returns `Dictionary<string, object>` with all metrics
- `ExportJson()` — returns JSON string
- `ExportPrometheus()` — returns Prometheus text format

## Resilience

### CircuitBreaker
Lock-free implementation preventing cascading failures when downstream systems are unhealthy.

**State machine:**
- **Closed** — normal operation, all requests pass through
- **Open** — failure threshold exceeded, all requests rejected immediately
- **HalfOpen** — limited requests allowed to test recovery
- **Isolated** — manually forced open, requires manual Reset()

**Failure detection:**
- Sliding window tracks success/failure timestamps
- `FailureRatio` — fraction of failures that triggers opening (default: 0.5)
- `MinimumThroughput` — minimum requests before circuit can open (default: 10)
- `SamplingDuration` — time window for counting failures (default: 30 sec)
- `BreakDuration` — time to stay open before attempting HalfOpen (default: 30 sec)
- `MaxHalfOpenRequests` — maximum requests in HalfOpen state (default: 3)

**Thread safety:**
- All state transitions use `Interlocked.CompareExchange`
- Timestamp tracking uses `ConcurrentQueue`
- No lock contention in hot path

**Performance:**
- `AllowRequest()` — 27.76 ns, 0 allocations

**Hybrid failure detection (new in v1.0.4):**
- EWMA (exponential weighted moving average) for fast reaction
- Sliding window for accurate threshold decisions
- Adaptive α: 0.5 when failure rate > 10%, 0.2 otherwise
- Early warning: if EWMA > 1.5× threshold → pre-emptively adds to window

**Metrics export (new in v1.0.4):**
- `GetMetrics()` — returns dictionary with cb_state, cb_failure_ratio, cb_ewma_failure_rate, cb_half_open_attempts

### RetryQueue
Lock-free delayed retry mechanism for transient failures.

**Retry strategies:**
- **Fixed** — constant delay between attempts
- **Linear** — delay increases linearly (delay × attempt)
- **Exponential** — delay doubles each attempt (delay × 2^attempt)

**Jitter:**
- Random 75-100% of calculated delay
- Prevents thundering herd when multiple items fail simultaneously

**Security (new in v1.0.4):**
- `RandomNumberGenerator` replaces `System.Random` for cryptographically secure jitter

**Configuration:**
- `MaxRetries` — maximum retry attempts (default: 3)
- `Delay` — base delay (default: 1 sec)
- `MaxDelay` — cap for exponential backoff (default: 30 sec)

**Flow:**
1. `EnqueueAsync()` — stores item with jittered retry timestamp
2. Items wait in bounded channel until their retry time arrives
3. `TryGetNextAsync()` — returns ready items, uses `WaitToReadAsync` for efficiency
4. After all retries exhausted, item is dropped (or routed to DeadLetterSink)

**Performance:**
- `EnqueueAsync()` — 69.16 ns, 0 allocations

### RetryPolicy
Defines when and how to retry failed operations.

- `MaxRetries` — maximum number of retry attempts
- `Delay` — base delay between retries
- `MaxDelay` — upper bound for exponential backoff
- `Strategy` — Fixed, Linear, or Exponential
- `RetryOn` — predicate determining which errors are retryable (default: all Transient)
- `OnRetry` — optional callback invoked on each retry attempt

### BackpressureStrategy
P-controller based continuous throttling (new in v1.0.4). Replaces binary Pause/Resume.

**How it works:**
- `ThrottleAsync(int currentSize)` — applies delay proportional to queue fill error
- Error = currentFillRatio - targetFillRatio
- Delay = Kp × error × 100ms, clamped to 0-200ms
- If below target → no delay (returns immediately)

**Adaptive target:**
- `UpdateThroughput(throughputPerSec, predictedLatencyMs)` — adjusts target fill ratio
- High throughput (>1000/s): target 50%
- Low throughput (<100/s): target 85%
- Medium: target 70%
- If latency predicted to spike (>50ms): lowers target by 10%

**Obsolete methods (kept for backward compatibility):**
- `ShouldPause(int)` — always returns false
- `IsCritical(int)` — returns true if fill ≥ 95%

### DeadLetterSink<T>
Captures items that failed after all retry attempts for later analysis.

- Saves only failed `ProcessingResult<T>` items
- JSON format for easy inspection and replay
- Thread-safe collection during pipeline execution
- File written on disposal

## Mathematics & Data Structures

### AdaptiveParallelism
Discrete P-controller for smooth thread scaling (new in v1.0.4). Replaces binary thresholds.

**P-controller parameters:**
- Dead zone: ±5ms error ignored
- Proportional band: 20ms error = 1 thread adjustment
- Anti-windup: stops accumulating error at min/max limits

**How it works:**
1. Calculates error = targetLatency - currentLatency
2. If error in dead zone → no change
3. If at limit and error pushes further → anti-windup blocks
4. Otherwise: threads += error / proportionalBand
5. Result clamped to [min, max]

### AdaptiveMetrics
Double Exponential Moving Average with velocity tracking and one-step prediction (new in v1.0.4).

**Double EMA:**
- Level EMA (α=0.2/0.8) — smoothed latency
- Velocity EMA (β=0.1) — rate of change of latency

**Dynamic alpha:**
- α=0.2 during stable operation
- α=0.8 when spike detected (current > 3× EMA)

**Tracked metrics:**
- `SmoothLatencyMs` — smoothed processing latency
- `SmoothThroughputPerSec` — smoothed items per second
- `LatencyVelocity` — rate of change (ms per update)

**Prediction (new in v1.0.4):**
- `PredictNextLatency()` — returns max(0, level + velocity)
- Used by Backpressure and Parallelism controllers for proactive adjustment
- Performance: 0.16 ns

### DeduplicationFilter
Bloom filter probabilistic data structure for O(1) duplicate detection.

- **False positive rate:** ~0.1% (configurable)
- **False negatives:** impossible
- **Memory:** O(1), independent of items processed
- Uses double hashing for bit array indexing
- `ContainsAndAdd(ulong)` — returns true if already seen, adds otherwise
- **Performance:** 20.04 ns, 0 allocations

### CuckooFilter
Alternative to Bloom filter with deletion support.

- **Operations:** Add, Contains, Remove
- **Collision resolution:** Cuckoo hashing with bucket eviction
- 4 slots per bucket, 500 max kicks
- Fingerprint-based storage
- **Performance:** < 50 ns per operation

### HyperLogLogEstimator
Cardinality estimation — counts "how many unique items" without storing them.

- **Memory:** O(1), ~4KB with default precision 12
- **Accuracy:** ~3% with default precision
- **Operations:** Add(ulong), Estimate(), Merge()
- Based on Flajolet et al. HyperLogLog algorithm
- Uses `BitOperations.LeadingZeroCount` for register calculation
- MurmurHash3 finalizer for bit mixing

### ExponentialHistogram
Logarithmic bucket histogram for computing percentiles.

- **p50, p95, p99** — latency percentiles
- **Memory:** O(log² n) with configurable bucket count
- Double-precision atomic updates via CompareExchange loop
- Auto-clamping for values outside min/max range

### ReservoirSampler
Algorithm R for representative sampling from streaming data.

- Maintains fixed-size sample from infinite stream
- Each item has equal probability of being in sample
- **Memory:** O(k) where k is sample size
- `Add(item)`, `Reset()`, `Sample` property

### JumpHash
Deterministic consistent hashing for sharding.

- Maps any 64-bit key to bucket index [0, N)
- O(1) computation, O(1) memory
- Minimal redistribution when N changes
- **Performance:** < 10 ns per hash

## Infrastructure

### ObjectPool<T>
Lock-free object pool for reusing objects without allocations.

- Factory-based creation: `new ObjectPool<T>(() => new T(), capacity)`
- `Rent()` — get object from pool, or create new if exhausted
- `Return()` — return object to pool
- Thread-safe via `Interlocked` operations
- **Performance:** 15.63 ns RentReturn, 0 allocations

### ChannelPool
Reuses `Channel<T>` instances between pipeline runs.

- `RentUnbounded<T>()` — get unbounded channel
- `RentBounded<T>(capacity, mode)` — get bounded channel
- `Return<T>(channel)` — complete writer, return to pool
- Reduces GC pressure on repeated pipeline executions

### PipelineBuilder
Fluent API for type-safe pipeline construction.

**Methods:**
- `PipelineBuilder.From(ISource<T>)` — start building from source
- `.Transform(ITransformer<T,U>)` — add typed transformer
- `.Transform(Func<T,T>)` — add middleware (lightweight)
- `.Pipe(ITransformer<T,T>)` — chain another transformer
- `.WithOptions(Action<Options>)` — configure pipeline
- `.To(ISink<T>)` — add sink and run

**Type safety:**
- Input/output types checked at compile time
- Middleware only works with same-type transforms

### MiddlewareTransformer<T>
Lightweight wrapper turning `Func<T,T>` into `ITransformer<T,T>`.

- Zero-boilerplate: no need to create a class
- Full resilience pipeline: CB, Retry, Timeout still apply
- Exception handling: wraps exceptions in `ProcessingResult.Failure`
- Works in both `Transform()` and `Pipe()` positions

### PipelineCancellation
Timeout utilities for async operations.

- `CreateTimeout(TimeSpan)` — create CancellationTokenSource with timeout
- `WithTimeoutAsync(ValueTask, TimeSpan, traceId)` — wrap operation with timeout
- On timeout: returns `ProcessingResult.Failure` with "Timed out" error

### PipelineSimulator
Deterministic simulator for testing multi-threaded scenarios.

- `SimulateDelayAsync(minMs, maxMs)` — random delay
- `SimulateFailure(probability)` — random failure with configurable probability
- `GenerateAsync(factory, count)` — generate test items
- `Reset()` — reset simulation state
- Deterministic when seeded

### PipelineTool<TInput, TOutput>
Wrapper for using pipeline as AI agent tool.

- Compatible with Semantic Kernel and AutoGen
- `ExecuteAsync(input)` — process single item
- `Name` and `Description` for agent registration
- Respects CircuitBreaker state

## Observability

### OpenTelemetry Tracing
Built-in distributed tracing via `ActivitySource`.

**Spans created:**
- `Pipeline.Run` — entire pipeline execution
- `ProcessSingle` — single item processing
- `Transform` — each transformation
- `Source.Error` — source failure

**Tags:**
- `smartpipe.trace_id`, `smartpipe.latency_ms`
- `smartpipe.error.type`, `smartpipe.error.category`
- `smartpipe.secret_found`, `smartpipe.parallelism`

### OpenTelemetry Metrics
Built-in metrics via `Meter` (static singleton, OTel compliant).

**Counters:**
- `smartpipe.items.processed` — successful items
- `smartpipe.items.failed` — failed items
- `smartpipe.duplicates.filtered` — duplicates removed
- `smartpipe.retries` — retry attempts

**Histograms:**
- `smartpipe.latency` — processing latency in ms

**Observable Gauges:**
- `smartpipe.queue.size` — current queue depth
- `smartpipe.throughput` — smoothed items/sec

### SmartPipeEventSource
EventCounters for monitoring without OTLP collector.

**Usage:** `dotnet-counters monitor --process-id PID SmartPipe.EventSource`

**Counters:**
- `items-processed` — incrementing counter
- `queue-size` — polling counter
- `pool-hit-rate` — polling counter (%)
- `backpressure/sec` — incrementing counter
- `cb-state` — polling counter (0=Closed, 1=Open, 2=HalfOpen)

## Extensions (SmartPipe.Extensions)

### Selectors (Data Sources)
- **HttpSelector<T>** — fetches data from HTTP API endpoints. Integrates with Polly resilience pipeline. Supports `ResiliencePipeline` for retry/circuit-breaker on HTTP calls.
- **EfCoreSelector<T>** — streams entities from Entity Framework Core via `IAsyncEnumerable`. Supports query customization with `WithQuery()`.
- **DapperSelector<T>** — high-performance SQL queries via Dapper. Uses `ExecuteReaderAsync` for streaming.

### Transforms
- **JsonTransform<TIn, TOut>** — serialization via System.Text.Json with source generator support.
- **CsvTransform<TIn, TOut>** — CSV parsing/writing via CsvHelper with configurable delimiter and culture.
- **MapsterTransform<TIn, TOut>** — object-to-object mapping via Mapster.
- **CompressionTransform** — Brotli/GZip compression/decompression.
- **PollyResilienceTransform<T>** — wraps any transform in Polly v8 ResiliencePipeline.
- **MiddlewareTransformer<T>** — lightweight `Func<T,T>` wrapper (in Core).

### Sinks
- **LoggerSink<T>** — writes results to `ILogger`. Supports structured logging.
- **DeadLetterSink<T>** — persists failed items to JSON file for later analysis.

### File Sources (new in v1.0.4)
- **CsvFileSource<T>** — streams CSV files using CsvHelper.GetRecordsAsync<T>(). Supports custom delimiter and culture.
- **JsonFileSource<T>** — reads JSON arrays (System.Text.Json.DeserializeAsyncEnumerable) or NDJSON (line-by-line). Auto-detects format.
- **DeadLetterSource<T>** — replays failed items from DeadLetterSink JSON file for reprocessing.

### File Sinks (new in v1.0.4)
- **CsvFileSink<T>** — writes items to CSV file with header row. Thread-safe via lock.
- **JsonFileSink<T>** — buffers items and writes JSON array on disposal.

### Advanced Transforms (new in v1.0.4)
- **FilterTransform<T>** — predicate-based filtering. Returns Success for matching items, Failure with Category="Filtered" for non-matching. Supports `And()`, `Or()`, `Not()` combinators and `&`, `|`, `!` operators.
- **ValidationTransform<T>** — validates items using DataAnnotations (`[Required]`, `[Range]`) and custom `.Require()` rules. Returns Failure with semicolon-separated error messages.
- **ConditionalTransform<T>** — applies inner transform only when condition is met. Otherwise passes item through unchanged.
- **CompositeTransform<T>** — chains multiple transforms into one. Stops on first failure.

### Database Sink (new in v1.0.4)
- **DbSink<T>** — inserts items into any database via Dapper. Supports custom SQL or auto-generated INSERT from `[Table]`/`[Column]` attributes.

### HTTP Sink (new in v1.0.4)
- **HttpSink<T>** — POSTs items to REST API via HttpClient.PostAsJsonAsync. Supports optional Polly ResiliencePipeline.

### Auto DeadLetter Routing (new in v1.0.4)
- **HandleFailureAsync** — Permanent errors → DeadLetterSink (if configured)
- **ProcessRetriesAsync** — exhausted retries → DeadLetterSink (if configured), then Failure to output channel
- Configured via `SmartPipeChannelOptions.DeadLetterSink`

### Progress Reporting (new in v1.0.4)
- **OnProgress delegate** — `(int current, int? total, TimeSpan elapsed, TimeSpan? eta)`. Called from ProduceAsync after each item.
- ETA calculated from elapsed time and progress ratio.

### Health Checks
- **SmartPipeLivenessCheck** — reports Healthy if pipeline is not paused.
- **SmartPipeReadinessCheck** — reports Degraded if queue > 1000 or failures detected.

### Streaming
- **ChannelMerge** — merges two `ChannelReader<T>` streams into one.
- **AsChannelReader()** — exposes pipeline output for external consumers.
- **RunInBackground()** — non-blocking pipeline execution returning `ChannelReader`.

### Hosting
- **SmartPipeHostedService** — `BackgroundService` for ASP.NET Core. Handles start, graceful shutdown (Drain), and error logging.
- **AddSmartPipeResilience()** — DI extension for registering pipeline with Polly resilience.

## Security

### SecretScanner
Detects secrets in pipeline data based on OWASP patterns.

**Detected patterns:**
- API keys (`api_key: '...'`, `sk-...`)
- Passwords (`password: '...'`)
- Private keys (`-----BEGIN RSA PRIVATE KEY-----`)
- JWT tokens (`eyJ...`)
- AWS Access Keys (`AKIA...`)
- GitHub tokens (`ghp_...`)
- OAuth tokens (`ya29...`)

**Methods:**
- `HasSecrets(string)` — returns true if any pattern matches
- `Redact(string)` — replaces secrets with `***REDACTED***`
- **Performance:** 46.58 ns, 0 allocations

## Data Lineage

### ProcessingContext Metadata Keys
Constants for tracking data provenance through the pipeline.

- `ProcessingContext<T>.LineageSource` — `"lineage_source"` — where data originated
- `ProcessingContext<T>.LineagePipeline` — `"lineage_pipeline"` — pipeline identifier
- `ProcessingContext<T>.LineageEnteredAt` — `"lineage_entered_at"` — ISO 8601 timestamp
- `ProcessingContext<T>.LineageTransform` — `"lineage_transform"` — name of last transformer

**Usage:**

```csharp
ctx.Metadata[ProcessingContext<Order>.LineageSource] = "orders_db";
ctx.Metadata[ProcessingContext<Order>.LineagePipeline] = "etl_main";
```
