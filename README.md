# SmartPipe.Core

**Universal streaming pipeline engine for .NET 10 with zero dependencies.**

Built on `System.Threading.Channels`, SmartPipe.Core provides a production-ready pipeline engine for ETL, real-time stream processing, API aggregation, and AI agent integration — all with 0 allocations in hot path.

[![CI](https://github.com/MrFr3di/SmartPipe-Core/actions/workflows/ci.yml/badge.svg)](https://github.com/MrFr3di/SmartPipe-Core/actions)
[![NuGet Core](https://img.shields.io/nuget/v/SmartPipe.Core.svg)](https://www.nuget.org/packages/SmartPipe.Core)
[![NuGet Extensions](https://img.shields.io/nuget/v/SmartPipe.Extensions.svg)](https://www.nuget.org/packages/SmartPipe.Extensions)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Coverage](https://img.shields.io/badge/coverage-96.4%25-brightgreen.svg)](https://github.com/MrFr3di/SmartPipe-Core)

📖 **[Complete Feature Reference →](docs/features.md)**

## What is SmartPipe?

SmartPipe is not just another ETL library. It's a **universal streaming pipeline engine** that handles:

- **ETL/ELT** — extract from DB/API, transform, load to anywhere
- **Real-time stream processing** — process events as they arrive
- **API aggregation** — fan-out requests, aggregate responses
- **Data validation pipelines** — validate, enrich, route
- **AI agent tools** — integrate with Semantic Kernel, AutoGen
- **Log/sensor processing** — process IoT telemetry, application logs
- **Error recovery & dead letter** — capture failures for later replay
- **Stream merging** — combine multiple data sources into one pipeline

**All in 5 lines of code:**

```csharp
using SmartPipe.Core;
using SmartPipe.Extensions;

var pipeline = PipelineBuilder
    .From(new HttpSelector<MyDto>("https://api.example.com/data"))
    .Transform(x => x.Enrich())       // Middleware for simple ops
    .Transform(new JsonTransform<MyDto, MyEntity>())  // ITransformer for complex
    .WithOptions(o => o.MaxDegreeOfParallelism = 4);
await pipeline.To(new LoggerSink<MyEntity>(logger));
```

## Getting Started | Installation

```bash
# Core engine (zero dependencies)
dotnet add package SmartPipe.Core

# Extensions (Http, EF Core, Dapper, JSON, CSV, Mapster, Polly)
dotnet add package SmartPipe.Extensions
```

## Examples by Scenario

### Middleware Pattern (5 lines)

```csharp
var pipeline = PipelineBuilder
    .From(new HttpSelector<int>("https://api.example.com/numbers"))
    .Transform(x => x * 2)          // Middleware!
    .Transform(x => x + 1)          // Middleware!
    .WithOptions(o => o.MaxDegreeOfParallelism = 4);
await pipeline.To(new LoggerSink<int>(logger));
```

### ETL Pipeline (Database → Transform → API)

```csharp
var pipeline = PipelineBuilder
    .From(new EfCoreSelector<Order>(dbContext).WithQuery(q => q.Where(o => o.Status == "Pending")))
    .Transform(new MapsterTransform<Order, OrderDto>())
    .Transform(new PollyResilienceTransform<OrderDto>(resiliencePipeline))
    .WithOptions(o => o.MaxDegreeOfParallelism = 8);
await pipeline.To(new HttpSink<OrderDto>(httpClient, "https://api.destination.com/orders"));
```

### Real-time Stream Processing (API → Filter → Log)

```csharp
var pipeline = PipelineBuilder
    .From(new HttpSelector<SensorData>("https://iot.example.com/telemetry"))
    .Transform(new JsonTransform<SensorData, SensorData>())
    .Transform(new MapsterTransform<SensorData, Alert>())
    .WithOptions(o => { o.MaxDegreeOfParallelism = 2; o.ContinueOnError = true; });
await pipeline.To(new LoggerSink<Alert>(logger));
```

### AI Agent Tool (Semantic Kernel Integration)

```csharp
var tool = new PipelineTool<string, string>("summarize", "Summarize text using AI");
tool.AddTransformer(new JsonTransform<string, PromptDto>());
tool.AddTransformer(new HttpTransform<PromptDto, string>(openAiClient));

var result = await tool.ExecuteAsync("Long text to summarize...");
```

### API Aggregation (Fan-out → Aggregate)

```csharp
var pipeline = PipelineBuilder
    .From(new HttpSelector<User>("https://users.api.com"))
    .Transform(new MapsterTransform<User, EnrichedUser>())
    .Transform(new PollyResilienceTransform<EnrichedUser>(resiliencePipeline));
await pipeline.To(new Sink<EnrichedUser>(user => enrichedUsers.Add(user)));
```

### Error Persistence with DeadLetterSink

```csharp
var pipeline = PipelineBuilder
    .From(new HttpSelector<Order>("https://api.example.com/orders"))
    .Transform(new OrderValidator())
    .WithOptions(o => o.ContinueOnError = true);
await pipeline.To(new DeadLetterSink<Order>("failed_orders.json"));
```

## First Pipeline (5 lines)

```csharp
using SmartPipe.Core;
using SmartPipe.Extensions.Selectors;
using SmartPipe.Extensions.Transforms;
using SmartPipe.Extensions.Sinks;

var pipeline = PipelineBuilder
    .From(new HttpSelector<MyDto>("https://api.example.com/data"))
    .Transform(new JsonTransform<MyDto, MyEntity>())
    .WithOptions(o => o.MaxDegreeOfParallelism = 4);
await pipeline.To(new LoggerSink<MyEntity>(logger));
```

## ASP.NET Core BackgroundService

```csharp
public class PipelineWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var pipeline = PipelineBuilder
            .From(new EfCoreSelector<Order>(_dbContext))
            .Transform(new MapsterTransform<Order, OrderDto>())
            .WithOptions(o => o.MaxDegreeOfParallelism = 8);
        await pipeline.To(new HttpSink<OrderDto>(_httpClient, "https://api.dest.com"));
    }
}
```

# SmartPipe Architecture

## Overview

SmartPipe is a streaming pipeline engine built on `System.Threading.Channels`.
It consists of **24 integrated components** organized in a resilience pipeline order.

## Pipeline Flow

```markdown

ISource<T> (or RunInBackground)
    ▼
Bounded Channel (or Rendezvous Channel)
    ▼
BackpressureStrategy (Dual-threshold: Pause/Resume)
    ▼
DeduplicationFilter (Bloom, O(1)) + HyperLogLogEstimator
    ▼
AdaptiveParallelism (Little's Law)
    ▼
CircuitBreaker (Lock-free, Closed→Open→HalfOpen)
    │
    ▼
MiddlewareTransformer (Func<T,T>) + ITransformer (ValueTask)
    ▼
RetryQueue (Jitter + Exponential Backoff)
    ▼
Bounded Channel
    ▼
ISink<T> (Logger, DeadLetter, HealthChecks)
    ▼
AsChannelReader() → SignalR/gRPC
```

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
| DeduplicationFilter | Bloom filter | O(1) | 20.65 ns |
| ObjectPool | Lock-free | O(n) | 15.55 ns |
| CircuitBreaker | Lock-free (Interlocked) | O(n) | 28.10 ns |
| RetryQueue | Lock-free (Channel) | O(n) | 69.16 ns |
| ExponentialHistogram | Percentiles | O(log² n) | < 100 ns |
| JumpHash | Sharding | O(1) | < 10 ns |
| CuckooFilter | Dedup + delete | O(1) | < 50 ns |
| ReservoirSampler | Sampling | O(k) | < 10 ns |
| HyperLogLogEstimator | Count-Distinct | O(1) | < 50 ns |
| DeadLetterSink | Error persistence | O(n) | — |
| ChannelMerge | Stream merging | O(n) | — |
| AdaptiveMetrics (Update) | Double EMA | O(1) | 20.25 ns |
| AdaptiveMetrics (Predict) | Double EMA | O(1) | 0.16 ns |

## Extension Architecture

Extensions follow the **Selection Pattern** — a single package with categorized components:

- **Selectors** — data sources (Http, EF Core, Dapper, CSV, JSON, DeadLetter)
- **Transforms** — data transformers (JSON, CSV, Mapster, Compression, Polly, Filter, Validation, Conditional, Composite)
- **Sinks** — data destinations (Logger, DeadLetter, Http, Db, CSV, JSON)
- **Health** — Kubernetes probes (Liveness, Readiness)
- **Streaming** — ChannelMerge, RunInBackground, AsChannelReader

Instead of 12 separate NuGet packages, SmartPipe uses a single SmartPipe.Extensions package with the Selection Pattern:

```text
SmartPipe.Extensions/
├── Selectors/          ← Data sources
│   ├── HttpSelector      ← REST API client
│   ├── EfCoreSelector    ← Entity Framework streaming
│   ├── DapperSelector    ← High-performance SQL
│   ├── CsvFileSource     ← CSV file reader
│   ├── JsonFileSource    ← JSON array & NDJSON reader
│   └── DeadLetterSource  ← Replay failed items
├── Transforms/         ← Data transformers
│   ├── JsonTransform          ← JSON serialization
│   ├── CsvTransform           ← CSV parsing
│   ├── MapsterTransform       ← Object mapping
│   ├── CompressionTransform   ← Brotli/GZip
│   ├── PollyResilienceTransform ← Retry/CB/Hedging
│   ├── FilterTransform        ← Predicate filtering
│   ├── ValidationTransform    ← DataAnnotations validation
│   ├── ConditionalTransform   ← Conditional execution
│   └── CompositeTransform     ← Chain transforms
└── Sinks/              ← Data destinations
    ├── LoggerSink       ← Structured logging
    ├── DeadLetterSink   ← Failed items persistence
    ├── HttpSink         ← REST API client
    ├── DbSink           ← Database insert
    ├── CsvFileSink      ← CSV file writer
    └── JsonFileSink     ← JSON file writer
One package. All integrations. Zero boilerplate.
```

## Requirements

- .NET 10.0+
- SmartPipe.Core: **0 dependencies**
- SmartPipe.Extensions: Polly, EF Core, Dapper, Mapster, CsvHelper
- **243 tests, 96.4% code coverage**

## What's New in v1.0.4

- **22 new features** (243 tests, 96.4% coverage)
- **P-Controller Parallelism** — smooth thread scaling, no binary jumps
- **Double EMA + Prediction** — velocity tracking + one-step latency forecast
- **Hybrid CircuitBreaker** — EWMA early warning + Sliding window decisions
- **P-Controller Backpressure** — continuous throttling, no oscillation
- **PipelineState + Cancel()** — lifecycle management with events
- **Progress reporting** — `OnProgress` with ETA calculation
- **Auto DeadLetter routing** — exhausted retries → DeadLetterSink
- **12 new Extensions** — CsvFileSource/Sink, JsonFileSource/Sink, FilterTransform, ValidationTransform, DbSink, HttpSink, ConditionalTransform, DeadLetterSource, CompositeTransform
- **Metrics.Export()** — JSON + Prometheus format
- **4 new OWASP patterns** in SecretScanner
- **12% faster** ValueTask_Transform (69.12 ns)

## What's New in v1.0.3

- **13 new features** (215 tests, 96.3% coverage)
- **Middleware Transformer** — `Func<T,T>` as lightweight ITransformer
- **Rendezvous Channel** — (BoundedCapacity=0)
- **HyperLogLogEstimator** — Count-Distinct with O(1) memory
- **Dual-threshold Watermark** — Pause/Resume prevents oscillation
- **Liveness/Readiness Health Checks** — Kubernetes-native
- **DeadLetterSink** — failed items persistence
- **Data Lineage** — provenance tracking in Metadata
- **ChannelMerge** — merge two streams
- **RunInBackground()** — streaming pipeline consumption
- **Hybrid Queue** — FullMode option (Wait/DropOldest)
- **AsChannelReader()** — SignalR/gRPC integration

## What's New in v1.0.2

- **Lock-free RetryQueue**
- **Lock-free CircuitBreaker**
- **SmartPipeEventSource** — monitor via `dotnet-counters`
- **SmartPipeHostedService** — native ASP.NET Core integration
- **SmartPipeHealthCheck** — pipeline health for YARP/Kubernetes
- **Adaptive EMA** — dynamic α for spike detection
- **Dynamic Watermark** — throughput-based backpressure
- **96.3% code coverage** (up from 86.5%)
- **47 new tests**, 0 regressions in benchmarks

## Documentation

- [Complete Feature Reference](docs/features.md) — all 24 components in detail
- [Architecture Overview](docs/architecture.md) — pipeline flow and design
- [API Reference](docs/api-reference.md) — interfaces and configuration
- [Contributing Guide](CONTRIBUTING.md)
- [Security Policy](SECURITY.md)
- [Changelog](CHANGELOG.md)

## Acknowledgements

SmartPipe is built on ideas and research from:

- **Polly** — resilience patterns for .NET ([github.com/App-vNext/Polly](https://github.com/App-vNext/Polly))
- **System.Threading.Channels** — lock-free producer/consumer infrastructure by Microsoft
- **OpenTelemetry** — observability framework for cloud-native software
- **Little's Law** — queue theory applied to adaptive parallelism (ACM Queue, 2025)
- **Bloom & Cuckoo Filters** — probabilistic data structures for deduplication
- **ReTraced** — three-level retry model inspiration
- **TheCodeMan** — production Channel pipeline patterns
- **Microsoft.Extensions.Resilience** — resilience pipeline integration
- **OWASP** — security patterns for secret detection
- **BenchmarkDotNet** — performance measurement framework

License
MIT License — see LICENSE for details.