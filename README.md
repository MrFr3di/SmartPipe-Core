# SmartPipe.Core

**Universal streaming pipeline engine for .NET 10 with zero dependencies.**

Built on `System.Threading.Channels`, SmartPipe.Core provides a production-ready pipeline engine for ETL, real-time stream processing, API aggregation, and AI agent integration — all with 0 allocations in hot path.

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

## What is SmartPipe?

SmartPipe is not just another ETL library. It's a **universal streaming pipeline engine** that handles:

- **ETL/ELT** — extract from DB/API, transform, load to anywhere
- **Real-time stream processing** — process events as they arrive
- **API aggregation** — fan-out requests, aggregate responses
- **Data validation pipelines** — validate, enrich, route
- **AI agent tools** — integrate with Semantic Kernel, AutoGen
- **Log/sensor processing** — process IoT telemetry, application logs

**All in 5 lines of code:**

```csharp
using SmartPipe.Core;
using SmartPipe.Extensions;

var pipeline = PipelineBuilder
    .From(new HttpSelector<MyDto>("https://api.example.com/data"))
    .Transform(new JsonTransform<MyDto, MyEntity>())
    .Transform(new PollyResilienceTransform<MyEntity>(resiliencePipeline))
    .WithOptions(o => o.MaxDegreeOfParallelism = 4);
await pipeline.To(new LoggerSink<MyEntity>(logger));
```

## Examples by Scenario

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

```markdown
## Getting Started 
### Installation
```

```bash
# Core engine (zero dependencies)
dotnet add package SmartPipe.Core

# Extensions (Http, EF Core, Dapper, JSON, CSV, Mapster, Polly)
dotnet add package SmartPipe.Extensions
```

## First Pipeline (5 lines)

```csharp
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;
using SmartPipe.Extensions.Sinks;

var pipeline = PipelineBuilder
    .From(new FileSource("data.csv"))
    .Transform(new CsvTransform<CsvRow, MyEntity>())
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
It consists of **21 integrated components** organized in a resilience pipeline order.

```markdown
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
```

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

Instead of 12 separate NuGet packages, SmartPipe uses a single SmartPipe.Extensions package with the Selection Pattern:

```text
SmartPipe.Extensions/
├── Selectors/          ← Data sources
│   ├── HttpSelector
│   ├── EfCoreSelector
│   └── DapperSelector
├── Transforms/         ← Data transformers
│   ├── JsonTransform
│   ├── CsvTransform
│   ├── MapsterTransform
│   ├── CompressionTransform
│   └── PollyResilienceTransform
└── Sinks/              ← Data destinations
    └── LoggerSink
One package. All integrations. Zero boilerplate.
```

Requirements
.NET 10.0+

No external dependencies for SmartPipe.Core

Optional: Polly, EF Core, Dapper, Mapster, CsvHelper for Extensions

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