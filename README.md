# SmartPipe.Core

**Streaming pipeline engine for .NET**

Built on `System.Threading.Channels`, SmartPipe.Core provides source, transform, sink, resilience, diagnostics, and compatibility APIs for in-process streaming pipelines. Version `1.1.0` focuses on a compatibility-preserving runtime model: legacy `ISource`/`ITransformer`/`ISink` APIs remain supported, while new `IPipeline*` APIs introduce envelope-aware execution for advanced scenarios.

[![CI](https://github.com/MrFr3di/SmartPipe-Core/actions/workflows/ci.yml/badge.svg)](https://github.com/MrFr3di/SmartPipe-Core/actions)
[![NuGet Core](https://img.shields.io/nuget/v/SmartPipe.Core.svg)](https://www.nuget.org/packages/SmartPipe.Core)
[![NuGet Extensions](https://img.shields.io/nuget/v/SmartPipe.Extensions.svg)](https://www.nuget.org/packages/SmartPipe.Extensions)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

📖 **[Complete Feature Reference →](docs/features.md)**

Release-grade 1.1.0 docs:

- [Runtime architecture](docs/runtime-architecture.md)
- [Failure semantics](docs/failure-semantics.md)
- [Claims policy](docs/claims.md)
- [AOT and trimming posture](docs/aot-compatibility.md)
- [Package readiness](docs/package-readiness.md)
- [1.0 to 1.1 migration guide](docs/migration/1.0-to-1.1.md)

## What is SmartPipe?

SmartPipe is an in-process streaming pipeline library for:

- **ETL/ELT** — extract from DB/API, transform, load to anywhere
- **Real-time stream processing** — process events as they arrive
- **API aggregation** — fan-out requests, aggregate responses
- **Data validation pipelines** — validate, enrich, route
- **AI agent tools** — integrate with Semantic Kernel, AutoGen
- **Log/sensor processing** — process IoT telemetry, application logs
- **Error recovery & dead letter** — capture failures for diagnostics and controlled retry flows
- **Stream merging** — combine multiple data sources into one pipeline

**All in 5 lines of code:**

```csharp
using SmartPipe.Core;
using SmartPipe.Extensions;

var pipeline = PipelineBuilder
    .From(new HttpSelector<MyDto>("https://api.example.com/data"))
    .Transform(new JsonTransform<MyDto, MyEntity>())
    .WithOptions(o => o.MaxDegreeOfParallelism = 4);
await pipeline.To(new LoggerSink<MyEntity>(logger));
```

## Getting Started | Installation

```bash
# Core engine 
dotnet add package SmartPipe.Core

# Extensions (Http, EF Core, Dapper, JSON, CSV, Mapster, Polly)
dotnet add package SmartPipe.Extensions
```

## Examples by Scenario

### Middleware Pattern (5 lines)

```csharp
var pipeline = PipelineBuilder
    .From(new HttpSelector<int>("https://api.example.com/numbers"))
    .Transform(x => x * 2)
    .Pipe(new MiddlewareTransformer<int>(x => x + 1))
    .WithOptions(o => o.MaxDegreeOfParallelism = 4);
await pipeline.To(new LoggerSink<int>(logger));
```

### ETL Pipeline (Database → Transform → API)

```csharp
var run = PipelineBuilder
    .From(new LegacySourceAdapter<Order>(
        new EfCoreSelector<Order>(dbContext).WithQuery(q => q.Where(o => o.Status == "Pending"))))
    .Transform(new LegacyTransformerAdapter<Order, OrderDto>(
        new MapsterTransform<Order, OrderDto>()))
    .Transform(new LegacyTransformerAdapter<OrderDto, OrderDto>(
        new PollyResilienceTransform<OrderDto>(resiliencePipeline)))
    .To(new LegacySinkAdapter<OrderDto>(
        new HttpSink<OrderDto>(httpClient, "https://api.destination.com/orders")));

await run.Completion;
```

### Real-time Stream Processing (API → Filter → Log)

```csharp
var pipeline = PipelineBuilder
    .From(new HttpSelector<SensorData>("https://iot.example.com/telemetry"))
    .Transform(new MapsterTransform<SensorData, Alert>())
    .WithOptions(o => { o.MaxDegreeOfParallelism = 2; o.ContinueOnError = true; });
await pipeline.To(new LoggerSink<Alert>(logger));
```

### Single Item Processing

```csharp
var pipeline = new SmartPipeChannel<string, string>();
pipeline.AddTransformer(new MiddlewareTransformer<string>(text => text.Trim()));
var result = await pipeline.ProcessSingleAsync(new ProcessingContext<string>("Long text to summarize..."));
```

`SmartPipeChannel` is a compatibility runtime for legacy 1.x consumers. For new projects, prefer `PipelineBuilder` with envelope-aware `IPipeline*` interfaces. Mutation methods (`AddSource`, `AddTransformer`, `AddSink`) must be called before `RunAsync` or `RunInBackground`. `DrainAsync` waits for accepted items to finish processing; use `Cancel()` for immediate stop.

### API Aggregation (Fan-out → Aggregate)

```csharp
var pipeline = PipelineBuilder
    .From(new HttpSelector<User>("https://users.api.com"))
    .Transform(new MapsterTransform<User, EnrichedUser>());
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
The current release line keeps the established `ISource<T>` → `ITransformer<TInput,TOutput>` → `ISink<T>` model and adds envelope-aware runtime contracts for future-safe execution semantics.

## Pipeline Flow

```markdown

ISource<T> (or RunInBackground)
    ▼
Bounded Channel (or Rendezvous Channel)
    ▼
BackpressureStrategy (P-controller: continuous throttling)
    ▼
DeduplicationFilter (Bloom, O(1)) + HyperLogLogEstimator
    ▼
AdaptiveParallelism (P-controller with dead zone + anti-windup)
    ▼
CircuitBreaker (atomic state transitions, Closed→Open→HalfOpen + Isolated)
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
8. **DefaultRetryPolicy** — per-pipeline default retry configuration
9. **RetryBudget** — per-item retry budget control

## Component Overview

| Component | Role | Notes |
|-----------|------|-------|
| DeduplicationFilter | Bloom-style deduplication | TTL mode has different false-negative semantics than a non-expiring Bloom filter. |
| ObjectPool | Internal allocation-control primitive | Not used as a public performance claim until benchmark gates prove it. |
| CircuitBreaker | Resilience primitive | Uses atomic state transitions and concurrent data structures. |
| RetryQueue | Delayed retry scheduling | Built on bounded channels. |
| ExponentialHistogram | Approximate latency percentiles | Intended for diagnostics, not exact quantiles. |
| JumpHash | Stable sharding | Deterministic bucket assignment. |
| CuckooFilter | Deduplication with removal | Lock-based thread safety. |
| ReservoirSampler | Bounded sampling | Keeps a bounded sample of observed items. |
| HyperLogLogEstimator | Approximate count-distinct | Supports merge semantics. |
| DeadLetterSink | Error persistence | Existing sink is diagnostic-oriented; replay-safe envelope APIs are introduced for 1.1.0 work. |
| ChannelMerge | Stream merging | Combines channel readers. |
| AdaptiveMetrics | EMA metrics | Feeds diagnostics and adaptive policies. |
| IClock | Time abstraction | Improves deterministic tests. |
| AtomicHelper | Atomic helper operations | Used by resilience and metrics primitives. |

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
│   ├── CompositeTransform     ← Chain transforms
│   └── FilterValidationExtensions ← ToFilter() conversion
├── Sinks/              ← Data destinations
│   ├── LoggerSink       ← Structured logging
│   ├── DeadLetterSink   ← Failed items persistence
│   ├── HttpSink         ← REST API client
│   ├── DbSink           ← Database insert
│   ├── CsvFileSink      ← CSV file writer
│   └── JsonFileSink     ← JSON file writer
├── Hosting/            ← ASP.NET Core integration
│   ├── SmartPipeHostedService       ← BackgroundService
│   ├── SmartPipeServiceCollectionExtensions ← AddSmartPipe DI
│   └── SmartPipeResilienceExtensions ← Polly registration
├── Health/             ← Kubernetes probes
│   ├── SmartPipeLivenessCheck
│   └── SmartPipeReadinessCheck
└── Streaming/          ← Stream utilities
    └── ChannelMerge    ← Merge two channels
One package. All integrations. 
```

## Requirements

- .NET 10.0+
- SmartPipe.Core currently depends on `Microsoft.Extensions.Logging.Abstractions`.
- SmartPipe.Extensions: Polly, EF Core, Dapper, Mapster, CsvHelper
- Test and coverage claims are published only when backed by CI artifacts.

## API Direction

For new code, prefer `PipelineBuilder` and the envelope-aware `IPipelineSource<T>`, `IPipelineTransformer<TInput,TOutput>`, and `IPipelineSink<T>` APIs. Legacy `ISource<T>`, `ITransformer<TInput,TOutput>`, and `ISink<T>` remain supported for 1.x compatibility and are bridged through adapters.

`SmartPipeChannel<TInput,TOutput>` remains a public compatibility and advanced single-stage facade. Multi-stage typed pipelines should use `PipelineBuilder`.

Use `FromFactory`, `TransformFactory`, and `ToFactory` when a pipeline definition
must create more than one run. Concrete source, transformer, and sink instances
are single-use by default unless they explicitly declare a reusable or external
singleton lifetime.

Typed pipelines can attach structured observers with `WithObserver`. Best-effort
observers are suitable for routine logging and metrics; critical observers may
fault the run and should be reserved for policy decisions.

## Claims Policy

Claims such as `production-ready`, `0 allocations`, `0 dependencies`, `lock-free`, exact coverage, exact test counts, AOT-ready, and dead-letter replay are not used as release claims unless there is reproducible evidence from CI, package validation, benchmarks, or consumer harnesses.

## What's New in v1.0.6


- **Thread safety** — CuckooFilter, DeduplicationFilter, ReservoirSampler now fully thread-safe
- **ObjectPool max capacity** — prevents unbounded pool growth under sustained load
- **DeduplicationFilter TTL** — automatic entry expiration for long-running pipelines
- **JsonFileSink periodic flushing** — NDJSON batch writes, prevents OOM on large datasets
- **RetryQueue polling optimization** — single CancellationTokenSource per call, reduced allocations
- **DrainAsync + WithTimeoutAsync** — CancellationToken support
- **PipelineDashboard** — readonly record struct, `PipelineDashboard.Empty`
- **TransformWithTimeoutAsync** — catch-all exception handling prevents consumer crashes
- **SecretScanner** — disabled by default, explicit opt-in via `EnableFeature("SecretScanner")`
- **ExponentialHistogram** — Volatile.Read for percentile reads, P50/P95/P99 caching
- **AdaptiveMetrics** — Stopwatch.GetTimestamp() instead of TickCount64
- **DbSink** — async ExecuteAsync, no thread pool blocking
- **DapperSelector** — try/finally reader disposal
- **ChannelMerge** — optional BoundedChannelOptions


## What's New in v1.0.5

- Test and coverage reporting was expanded in this release line; exact coverage gates are now expected to come from CI artifacts.
- **DefaultRetryPolicy** — per-pipeline retry configuration in SmartPipeChannelOptions
- **RetryBudget** — per-item retry budget in RetryQueue, auto-routes exhausted items to DeadLetterSink
- **DisposeAsync(CancellationToken)** — graceful cancellation during pipeline disposal
- **AddSmartPipe DI** — service collection extensions for ASP.NET Core integration
- **IClock integration** — time abstraction for testability, replaces DateTime.UtcNow
- **AtomicHelper** — CompareExchange loop utility
- **SecretScanner evasion detection** — Base64/URL decoding, MaxRecursionDepth=3, 164 tests
- **DeadLetterSink retry** — IOException recovery with exponential backoff
- **AdaptiveParallelism adaptive alpha** — faster response to latency changes
- **CircuitBreaker CleanupWindow** — thread-safe via TryDequeue+check
- **ObjectPool ABA protection** — version stamps prevent race conditions
- **CuckooFilter Merge** — combine multiple filters


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

- [Complete Feature Reference](https://github.com/MrFr3di/SmartPipe-Core/blob/main/docs/features.md) — all 24 components in detail
- [Architecture Overview](https://github.com/MrFr3di/SmartPipe-Core/blob/main/docs/architecture.md) — pipeline flow and design
- [API Reference](https://github.com/MrFr3di/SmartPipe-Core/blob/main/docs/api-reference.md) — interfaces and configuration
- [Contributing Guide](https://github.com/MrFr3di/SmartPipe-Core/blob/main/CONTRIBUTING.md)
- [Security Policy](https://github.com/MrFr3di/SmartPipe-Core/blob/main/SECURITY.md)
- [Changelog](https://github.com/MrFr3di/SmartPipe-Core/blob/main/CHANGELOG.md)

## Acknowledgements

SmartPipe is built on ideas and research from:

- **Polly** — resilience patterns for .NET ([github.com/App-vNext/Polly](https://github.com/App-vNext/Polly))
- **System.Threading.Channels** — producer/consumer infrastructure by Microsoft
- **OpenTelemetry** — observability framework for cloud-native software
- **Little's Law** — queue theory applied to adaptive parallelism (ACM Queue, 2025)
- **Bloom & Cuckoo Filters** — probabilistic data structures for deduplication
- **ReTraced** — three-level retry model inspiration
- **TheCodeMan** — production Channel pipeline patterns
- **Microsoft.Extensions.Resilience** — resilience pipeline integration
- **OWASP** — security patterns for secret detection
- **BenchmarkDotNet** — performance measurement framework
- **Control Theory (P-controllers)** — applied to AdaptiveParallelism and BackpressureStrategy
- **HyperLogLog (Flajolet et al.)** — cardinality estimation algorithm

License
MIT License — see LICENSE for details.
