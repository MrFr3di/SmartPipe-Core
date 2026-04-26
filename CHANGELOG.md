# Changelog

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