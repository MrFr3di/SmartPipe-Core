# SmartPipe Architecture

## Overview

SmartPipe is a streaming pipeline engine built on `System.Threading.Channels`.
It consists of **21 integrated components** organized in a resilience pipeline order.

## Pipeline Flow
ISource<T> (or RunInBackground)
    │
    ▼
Bounded Channel (or Rendezvous Channel)
    │
    ▼
BackpressureStrategy (Dual-threshold: Pause/Resume)
    │
    ▼
DeduplicationFilter (Bloom, O(1)) + HyperLogLogEstimator
    │
    ▼
AdaptiveParallelism (Little's Law)
    │
    ▼
CircuitBreaker (Lock-free, Closed→Open→HalfOpen)
    │
    ▼
MiddlewareTransformer (Func<T,T>) + ITransformer (ValueTask)
    │
    ▼
RetryQueue (Jitter + Exponential Backoff)
    │
    ▼
Bounded Channel
    │
    ▼
ISink<T> (Logger, DeadLetter, HealthChecks)
    │
    ▼
AsChannelReader() → SignalR/gRPC

## Resilience Pipeline Order

1. **TotalRequestTimeout** — maximum time for entire pipeline
2. **CircuitBreaker** — stops processing on high failure rate
3. **RetryQueue** — delays and retries transient errors
4. **AttemptTimeout** — per-transformer timeout
5. **DeadLetterSink** — captures exhausted retries for later replay
6. **LivenessCheck** — detects stalled pipeline
7. **ReadinessCheck** — detects overloaded pipeline

## Component Overview

| Component | Type | Memory | Performance |
|-----------|------|--------|-------------|
| DeduplicationFilter | Bloom filter | O(1) | 20.86 ns |
| ObjectPool | Lock-free | O(n) | 15.67 ns |
| ExponentialHistogram | Percentiles | O(log² n) | < 100 ns |
| JumpHash | Sharding | O(1) | < 10 ns |
| CuckooFilter | Dedup + delete | O(1) | < 50 ns |
| ReservoirSampler | Sampling | O(k) | < 10 ns |
| CircuitBreaker | Lock-free (Interlocked) | O(n) | 27.76 ns |
| RetryQueue | Lock-free (Channel) | O(n) | 69.16 ns |
| HyperLogLogEstimator | Count-Distinct | O(1) | < 50 ns |
| DeadLetterSink | Error persistence | O(n) | — |
| ChannelMerge | Stream merging | O(n) | — |

## Extension Architecture

Extensions follow the **Selection Pattern** — a single package with categorized components:

- **Selectors** — data sources (Http, EF Core, Dapper)
- **Transforms** — data transformers (JSON, CSV, Mapster, Compression, Polly, Middleware)
- **Sinks** — data destinations (Logger, DeadLetter)
- **Health** — Kubernetes probes (Liveness, Readiness)
- **Streaming** — ChannelMerge, RunInBackground, AsChannelReader