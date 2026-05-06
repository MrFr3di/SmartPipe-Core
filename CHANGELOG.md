# Changelog

## [1.0.5] — 2026-05-06

### New Features
- **DefaultRetryPolicy** in SmartPipeChannelOptions — per-pipeline retry policy for transient failures
- **RetryBudget per RetryItem** — per-item retry budget with DeadLetterSink routing when exhausted
- **DisposeAsync(CancellationToken)** — graceful shutdown with timeout support
- **AddSmartPipe DI** — three overloads for flexible pipeline registration in IServiceCollection
- **IClock integration** — TimeProviderClock for testable time across CircuitBreaker, RetryQueue, SmartPipeChannel

### Core Improvements
- **CircuitBreaker thread-safety** — _ewmaFailureRate updated via AtomicHelper.CompareExchangeLoop (lock-free)
- **AdaptiveParallelism adaptive alpha** — dynamic EMA alpha based on latency delta for faster convergence
- **ObjectPool ABA protection** — version stamps prevent ABA race conditions under high concurrency
- **CleanupWindow race fix** — TryPeek+TryDequeue replaced with TryDequeue+check pattern
- **P-controller recovery** — currentLatencyMs used directly for error calculation, faster spike response
- **AdaptiveMetrics thread-safety** — _avgLatencyMs updated via Volatile.Read/Write for thread-safe access

### Pipeline Management
- **PipelineState.Paused** — Pause()/Resume() now fire OnStateChanged events correctly
- **BoundedCapacity guard** — UseRendezvous=true throws InvalidOperationException (by design)
- **Magic numbers → named constants** — AlphaScaleFactor, MaxDelayMs, throughput/latency thresholds in BackpressureStrategy

### SecretScanner Improvements
- **Evasion detection** — TryDecodeBase64/TryDecodeUrl for Base64 and URL-encoded secrets
- **MaxRecursionDepth=3** — handles triple-encoded payloads with safety margin (found in real penetration tests)
- **Padding fix** — TryDecodeBase64 handles missing Base64 padding correctly
- **AWS key guard** — IsRawAwsAccessKey prevents false Base64 decode on raw AWS keys
- **169 SecretScanner tests** — +26 from v1.0.4

### Code Quality Sweep
- **Broad catch(Exception) → specific types** — 8 catch blocks with Polly, Mapster, CsvHelper, IO exceptions
- **AtomicHelper utility** — 3 duplicate CompareExchange loops extracted into reusable class (internal)
- **Dead code removal** — PipelineTool, ShouldPause/IsCritical
- **Method extraction** — ProcessRetriesAsync, RecordFailure, Merge refactored to ≤3 levels nesting
- **XML documentation** — 0 CS1591 warnings in production code (50+ files documented)
- **ILogger logging** — added to all catch blocks, debug-level logging for cancellation events
- **#nullable enable** — verified in all source files

### Testing & Quality
- **598 tests** (+355 from v1.0.4)
- **Stress tests** — 50-thread CircuitBreaker, 20-thread ObjectPool, 10-producer/10-consumer RetryQueue
- **Property-based tests** — RetryPolicy invariants (monotonicity, boundedness, overflow protection)

### Breaking Changes
- **Removed ShouldPause()/IsCritical()** — replaced by P-controller based throttling. Code using these must migrate to Pause()/Resume() and check ErrorType directly.
- **Removed PipelineTool class** — functionality consolidated into SmartPipeChannel and PipelineBuilder. Use ProcessSingleAsync() for AI agent integration.
- **ChannelPool.Return() → CloseChannel()** — method now calls TryComplete() on writer, does NOT return channel to pool. Update callers.
- **IClock parameter added** to CircuitBreaker, RetryQueue, SmartPipeChannel constructors — optional with TimeProviderClock default, no changes required for existing code.

## [1.0.4] — 2026-04-28

### New Features (22)
- **CsvFileSource\<T\>** + **CsvFileSink\<T\>** — streaming CSV read/write
- **JsonFileSource\<T\>** + **JsonFileSink\<T\>** — JSON array and NDJSON read/write
- **FilterTransform\<T\>** — predicate-based filtering with And/Or/Not combinators
- **ValidationTransform\<T\>** — DataAnnotations validation with custom `.Require()` rules
- **DbSink\<T\>** — database insert via Dapper with auto-generated SQL from attributes
- **HttpSink\<T\>** — HTTP POST sink with optional Polly resilience pipeline
- **ConditionalTransform\<T\>** — apply transform only when condition met
- **DeadLetterSource\<T\>** — replay failed items from DeadLetterSink JSON
- **CompositeTransform\<T\>** — chain multiple transforms into one
- **Filter-to-Validation extension** — `.ToFilter()` method

### Core Improvements (10)
- **P-Controller Parallelism** — discrete P-controller with dead zone and anti-windup (replaces binary thresholds)
- **Double EMA + Prediction** — velocity tracking + `PredictNextLatency()` for proactive control
- **Hybrid CircuitBreaker** — EWMA for fast reaction + Sliding window for accurate decisions + adaptive α
- **P-Controller Backpressure** — continuous throttling replaces binary Pause/Resume (prevents oscillation)
- **Adaptive Pipeline** — controllers linked via `PredictNextLatency()` for coordinated response

### Pipeline Management
- **PipelineState** — `NotStarted → Running → Completed/Faulted/Cancelled` with `OnStateChanged` event
- **Cancel()** — graceful pipeline cancellation
- **CreateDashboard()** — aggregated State + Progress + Metrics + CB info
- **Progress reporting** — `OnProgress(int current, int? total, TimeSpan elapsed, TimeSpan? eta)` delegate

### Observability
- **Metrics.Export()** — Dictionary export + JSON + Prometheus text format
- **CircuitBreaker.GetMetrics()** — CB state, failure ratio, EWMA rate export

### Resilience
- **Auto DeadLetter routing** — exhausted retries → DeadLetterSink automatically (via Options.DeadLetterSink)
- **Filtered category handling** — `Category=="Filtered"` not counted as error
- **Cryptographically secure Jitter** — `Random` → `RandomNumberGenerator` in RetryQueue

### Security
- **4 new OWASP patterns** in SecretScanner — JWT, AWS Key, GitHub Token, OAuth Token

### Testing & Quality
- **243 tests** (+28 from v1.0.3)
- **96.4% line coverage**
- **Algorithm benchmarks** — P-controller, Double EMA, Hybrid CB, Backpressure
- **Performance**: ValueTask_Transform 12% faster (69.12 ns vs 78.81 ns), 0 regressions


## [1.0.3] — 2026-04-27

### New Features (13)
- **Middleware Transformer** — `Func<T,T>` as lightweight `ITransformer`, zero boilerplate
- **Rendezvous Channel** — `UseRendezvous=true` enables strict Producer-Consumer sync (BoundedCapacity=0)
- **HyperLogLogEstimator** — Count-Distinct with O(1) memory, ~3% accuracy
- **Dual-threshold Watermark** — Pause/Resume thresholds prevent oscillation (System.IO.Pipelines pattern)
- **Liveness/Readiness Health Checks** — Kubernetes-native probes (`SmartPipeLivenessCheck`, `SmartPipeReadinessCheck`)
- **DeadLetterSink** — persists failed items to JSON for later analysis
- **Data Lineage** — provenance tracking via `ProcessingContext.Metadata` keys
- **ChannelMerge** — merge two `ChannelReader<T>` streams into one
- **RunInBackground()** — non-blocking pipeline execution returning `ChannelReader`
- **Hybrid Queue** — `FullMode` option in `SmartPipeChannelOptions` (Wait/DropOldest/DropNewest)
- **AsChannelReader()** — exposes pipeline output for SignalR/gRPC integration
- **Hybrid Queue** — `FullMode` option (Wait/DropOldest/DropNewest)
- **Lambda sources/sinks** — `AddSource(Func)` and `AddSink(Action)` for rapid prototyping

### Testing & Quality
- **215 tests** (up from 186, +29 tests)
- **96.4% line coverage**
- 0 regressions in all benchmarks

## [1.0.2] — 2026-04-27

### Performance
- **Lock-free CircuitBreaker** — `lock()` → `Interlocked.CompareExchange` + `ConcurrentQueue`
  - `AllowRequest()`: 49.30ns → 27.76ns 
- **Lock-free RetryQueue** — `Task.Delay(50)` polling → `WaitToReadAsync` + timeout
  - `EnqueueAsync()`: 86.58ns → 69.16ns 
- **Adaptive EMA** — dynamic α (0.2 stable, 0.8 spike)
- **Dynamic Watermark** — throughput-based backpressure thresholds
- **TryRead in DrainAsync** — instant drain without 10ms delays

### Core Changes
- **ProcessingContext** — `record class` → mutable `class` with `Reset()` for ObjectPool reuse
- **Meter instruments** — static readonly (OTel singleton compliance)
- **ObjectPool** — factory-based, compatible with ProcessingContext

### Observability
- **SmartPipeEventSource** — EventSource with EventCounters for `dotnet-counters monitor`
  - `items-processed`, `queue-size`, `pool-hit-rate`, `backpressure/sec`, `cb-state`

### Extensions
- **SmartPipeHostedService** — `BackgroundService` for ASP.NET Core with graceful Drain
- **AddSmartPipeResilience()** — DI extension for `IServiceCollection`
- **SmartPipeHealthCheck** — `IHealthCheck` reporting CB state, queue size, failure rate

### Testing & Quality
- **186 tests** (up from 137, +47 tests)
- **96.3% line coverage** (up from 86.5%)
- **81.2% branch coverage** (up from 69.5%)
- **Crap Score reduced** — `ConsumeAsync` refactored into 6 smaller methods
- 0 regressions in all benchmarks

## [1.0.0] — 2026-04-26

### Core Engine
- SmartPipeChannel with System.Threading.Channels
- ValueTask signatures for zero allocations
- AdaptiveParallelism (Little's Law)
- AdaptiveMetrics (EMA smoothing)
- BackpressureStrategy (watermark-based throttling)
- DeduplicationFilter (Bloom filter, O(1) memory)
- CuckooFilter (deduplication with deletion)
- ReservoirSampler (debug sampling from stream)
- ExponentialHistogram (p50/p95/p99 percentiles)
- ObjectPool (lock-free, factory-based)
- JumpHash (deterministic sharding, O(1) memory)
- PipelineCancellation (timeout wrapping)
- ChannelPool (channel reuse between runs)
- PipelineBuilder (fluent API with type safety)
- PipelineSimulator (deterministic testing)
- SecretScanner (OWASP-based secret detection)
- FeatureFlags (runtime feature toggling)
- PipelineTool (AI agent integration)
- Graceful Shutdown (DrainAsync, Pause/Resume)

### Observability
- OpenTelemetry Tracing (ActivitySource)
- OpenTelemetry Metrics (Meter with Counters, Histograms, Gauges)

### Resilience
- RetryQueue with jitter (thundering herd protection)
- RetryPolicy (Fixed, Linear, Exponential backoff)
- CircuitBreaker (sliding window, HalfOpen limits, manual Isolate/Reset)
- TotalRequestTimeout + AttemptTimeout

### Extensions (SmartPipe.Extensions)
- HttpSelector (Polly-integrated HTTP source)
- EfCoreSelector (Entity Framework source)
- DapperSelector (high-performance SQL source)
- JsonTransform (System.Text.Json with source generation)
- CsvTransform (CsvHelper integration)
- MapsterTransform (object-to-object mapping)
- CompressionTransform (Brotli/GZip)
- PollyResilienceTransform (Polly v8 pipeline wrapper)
- LoggerSink (ILogger-based sink)

### Testing
- 137 unit tests
- 8 property-based tests (FsCheck)
- 7 chaos engineering tests
- 5 BenchmarkDotNet benchmarks (0 allocations in hot path)