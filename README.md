# SmartPipe.Core

Streaming pipeline engine for .NET.

SmartPipe.Core is an in-process library for source -> transform -> sink
pipelines built on `System.Threading.Channels`. It supports the established
1.x legacy API and the 1.1.0 envelope-aware typed API for runs that need
metadata, observer events, and replay-safe dead-letter context.

[![CI](https://github.com/MrFr3di/SmartPipe-Core/actions/workflows/ci.yml/badge.svg)](https://github.com/MrFr3di/SmartPipe-Core/actions)
[![NuGet Core](https://img.shields.io/nuget/v/SmartPipe.Core.svg)](https://www.nuget.org/packages/SmartPipe.Core)
[![NuGet Extensions](https://img.shields.io/nuget/v/SmartPipe.Extensions.svg)](https://www.nuget.org/packages/SmartPipe.Extensions)
![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)

## Install

```bash
dotnet add package SmartPipe.Core
dotnet add package SmartPipe.Extensions
```

## Minimal Legacy Pipeline

Use the legacy API when you already have `ISource<T>`,
`ITransformer<TInput,TOutput>`, or `ISink<T>` components.

```csharp
using SmartPipe.Core;
using SmartPipe.Extensions.Selectors;
using SmartPipe.Extensions.Sinks;

var httpClient = new HttpClient();

var pipeline = PipelineBuilder
    .From(new HttpSelector<int>(httpClient, "https://api.example.com/numbers"))
    .Transform(new MiddlewareTransformer<int>(x => x * 2));

await pipeline.To(new LoggerSink<int>(logger));
```

## Minimal Typed Pipeline

Use the typed API for new code that needs envelope metadata, lineage, observers,
stage failure policies, or replay-safe dead-letter records.

```csharp
IPipelineSource<Order> source = new OrderSource();
IPipelineTransformer<Order, OrderDto> transformer = new OrderDtoStage();
IPipelineSink<OrderDto> sink = new OrderSink();

var run = PipelineBuilder
    .From(source)
    .Transform(transformer)
    .To(sink);

await run.Completion;
```

`PipelineBuilder.To(IPipelineSink<T>)` returns `PipelineRun<T>`. Consume
`run.Outputs` when the caller needs result and envelope data.

## Docs

- [Getting started](docs/getting-started.md)
- [Configuration](docs/configuration.md)
- [Resilience and failure semantics](docs/resilience.md)
- [Runtime architecture](docs/architecture.md)
- [API reference](docs/api-reference.md)
- [AOT and trimming compatibility](docs/aot-compatibility.md)
- [1.0 to 1.1 migration guide](docs/migration/1.0-to-1.1.md)
- [Changelog](CHANGELOG.md)

## Requirements

- .NET 10.0 or later.
- `SmartPipe.Core` depends on `Microsoft.Extensions.Logging.Abstractions`.
- `SmartPipe.Extensions` adds integrations for HTTP, EF Core, Dapper, JSON, CSV,
  Mapster, Polly, hosting, health checks, and file sinks/sources.

## API Direction

- New pipelines should prefer `PipelineBuilder` with `IPipelineSource<T>`,
  `IPipelineTransformer<TInput,TOutput>`, and `IPipelineSink<T>`.
- Existing 1.x consumers can keep using `SmartPipeChannel<TInput,TOutput>` and
  the legacy `ISource<T>`, `ITransformer<TInput,TOutput>`, and `ISink<T>` APIs.
- Legacy components can be bridged into typed pipelines with
  `LegacySourceAdapter<T>`, `LegacyTransformerAdapter<TInput,TOutput>`, and
  `LegacySinkAdapter<T>`.

## Claims Policy

Release-facing documentation does not claim exactly-once delivery,
package-wide AOT readiness, zero dependencies, zero allocations, exact coverage,
or exact current test counts unless committed CI, package, benchmark, or
consumer validation proves the claim. Legacy Extensions dead-letter records
are diagnostic; replay-safe dead-letter uses `DeadLetterEnvelope<T>` and
`JsonLinesDeadLetterSerializer<T>`.

## License

MIT License.
