# SmartPipe Architecture

## Overview

SmartPipe is a streaming pipeline engine built on `System.Threading.Channels`.
It consists of **21 integrated components** organized in a resilience pipeline order.

## Pipeline Flow
ISource<T>
    │
    ▼
Bounded Channel
    │
    ▼
BackpressureStrategy (Watermark 80%/95%)
    │
    ▼
DeduplicationFilter (Bloom, O(1))
    │
    ▼
AdaptiveParallelism (Little's Law)
    │
    ▼
CircuitBreaker (Closed→Open→HalfOpen)
    │
    ▼
ITransformer (ValueTask, parallel) + AttemptTimeout
    │
    ▼
RetryQueue (Jitter + Exponential Backoff)
    │
    ▼
Bounded Channel
    │
    ▼
ISink<T>

## Resilience Pipeline Order

1. **TotalRequestTimeout** — maximum time for entire pipeline
2. **CircuitBreaker** — stops processing on high failure rate
3. **RetryQueue** — delays and retries transient errors
4. **AttemptTimeout** — per-transformer timeout

## Component Overview

| Component | Type | Memory | Performance |
|-----------|------|--------|-------------|
| DeduplicationFilter | Bloom filter | O(1) | 20.86 ns |
| ObjectPool | Lock-free | O(n) | 15.67 ns |
| ExponentialHistogram | Percentiles | O(log² n) | < 100 ns |
| JumpHash | Sharding | O(1) | < 10 ns |
| CuckooFilter | Dedup + delete | O(1) | < 50 ns |
| ReservoirSampler | Sampling | O(k) | < 10 ns |

## Extension Architecture

Extensions follow the **Selection Pattern** — a single package with categorized components:

- **Selectors** — data sources (Http, EF Core, Dapper)
- **Transforms** — data transformers (JSON, CSV, Mapster, Compression, Polly)
- **Sinks** — data destinations (Logger)