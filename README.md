# SmartPipe.Core

Typed in-process streaming pipelines for .NET.

SmartPipe.Core runs explicit `source -> transform -> sink` pipelines inside your
process with bounded channels, envelope metadata, retry/timeout/circuit-breaker
stage handling, observer events, metrics snapshots, and replay-safe dead-letter
records. It is not a distributed workflow engine, message broker, durable queue,
or exactly-once delivery system.

[![CI](https://github.com/MrFr3di/SmartPipe-Core/actions/workflows/ci.yml/badge.svg)](https://github.com/MrFr3di/SmartPipe-Core/actions)
[![NuGet Core](https://img.shields.io/nuget/v/SmartPipe.Core.svg)](https://www.nuget.org/packages/SmartPipe.Core)
[![NuGet Extensions](https://img.shields.io/nuget/v/SmartPipe.Extensions.svg)](https://www.nuget.org/packages/SmartPipe.Extensions)
![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)

## Install

```bash
dotnet add package SmartPipe.Core
dotnet add package SmartPipe.Extensions
```

## Quick Start

```csharp
var run = PipelineBuilder
    .From(PipelineSource.FromAsyncEnumerable(items))
    .Transform(PipelineTransformer.FromFunc<int, string>(
        static (value, ct) => ValueTask.FromResult(value.ToString())))
    .To(PipelineSink.FromFunc<string>(
        static (value, ct) => ValueTask.CompletedTask));

await run.Completion;
```

For component-based pipelines:

```csharp
IPipelineSource<Order> source = new OrderSource();
IPipelineTransformer<Order, OrderDto> stage = new OrderStage();
IPipelineSink<OrderDto> sink = new OrderSink();

await using var run = PipelineBuilder
    .From(source)
    .WithPipelineId("orders")
    .Transform(stage)
    .WithRuntimeOptions(new PipelineRuntimeOptions
    {
        MaxConcurrency = 4,
        InputCapacity = 1024,
        OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
    })
    .To(sink);

await run.Completion;
```

`PipelineRun<T>.Outputs` exposes `PipelineOutput<T>` records with the final
`ProcessingEnvelope<T>` when available and a classified `PipelineResult<T>`.
Multiple output readers distribute records; they do not each receive a
broadcast copy.
For sink-backed pipelines, success output means transform processing and sink
write both completed successfully. `StageResult.Filtered()` is non-failure
terminal control flow: it does not call the sink, does not dead-letter, and does
not increment failed metrics.

## Lifecycle

- `DrainAsync` stops accepting new source items at source boundaries, cancels
  cooperative source reads, and waits for already accepted work.
- `TryDrainAsync` returns a structured `PipelineDrainResult` instead of
  throwing for timeout or run fault status.
- `CancelAsync` requests cooperative cancellation.
- `AbortAsync` is the immediate stop path.
- `DisposeAsync` is idempotent and disposes runtime-owned components once.

## DI And Hosting

`SmartPipe.Extensions` registers immutable definitions and per-run factories:

```csharp
services.AddSmartPipe<Order, OrderDto>(
    "orders",
    builder => builder
        .UseSource<OrderSource>()
        .UseStage<OrderStage>()
        .UseSink<OrderSink>());
```

Resolve `ISmartPipeFactory<Order, OrderDto>` and call `Start()`, or use
`AddSmartPipeHostedService<TInput,TOutput>()` for background hosting.

Typed health checks can be registered for DI pipelines:

```csharp
services
    .AddHealthChecks()
    .AddSmartPipeHealthCheck<Order, OrderDto>("orders");
```

The health check reads the typed run state and immutable metrics snapshot. It
reports high queue utilization or stale processing as degraded and faulted runs
as unhealthy.

Hosted-service pipeline faults are configurable through
`SmartPipeHostedServiceOptions`. The default `FailureBehavior` is
`StopApplication`, so a background pipeline fault requests host shutdown instead
of being logged and swallowed.

Lossy bounded channel modes are observable. Input, output, and buffered
observer drops record `smartpipe.items.dropped`,
`smartpipe.output.items.dropped`, and `smartpipe.observer.events.dropped`.

## AOT And Trimming

SmartPipe.Core is AOT-conscious and analyzer-gated.

Reflection-based JSON file and dead-letter helpers are annotated with
`RequiresUnreferencedCode` / `RequiresDynamicCode`. Use constructors that accept
source-generated `JsonTypeInfo` for NativeAOT or trimming-sensitive consumers.

Some SmartPipe.Extensions integrations may require source-generated serializers
or may not be AOT-friendly.

## Extensions Package Surface

SmartPipe.Extensions is currently a broad integration package. This release
keeps it monolithic to avoid expanding the typed-only hardening scope. Future
releases may split integrations into focused packages such as Hosting,
HealthChecks, Json, Csv, EFCore, Dapper, Mapster, and Resilience.

README examples are intentionally minimal. CI consumer smoke is the executable
check for the public quick-start scenarios.

## Docs

- [Getting started](docs/getting-started.md)
- [Configuration](docs/configuration.md)
- [Runtime contracts](docs/runtime-contracts.md)
- [Resilience](docs/resilience.md)
- [Architecture](docs/architecture.md)
- [Observability](docs/observability.md)
- [Observers](docs/observers.md)
- [Dependency injection](docs/dependency-injection.md)
- [Hosting](docs/hosting.md)
- [Health checks](docs/health-checks.md)
- [API reference](docs/api-reference.md)
- [Contributing](docs/contributing.md)
- [Release validation](docs/release.md)
- [Migration from removed legacy APIs](docs/migration/legacy-to-typed.md)

## Requirements

- .NET 10.0 or later.
- `SmartPipe.Core` depends on `Microsoft.Extensions.Logging.Abstractions`.
- `SmartPipe.Extensions` adds HTTP, EF Core, Dapper, JSON, CSV, Mapster, Polly,
  hosting, health-check, and file integration components.

## License

MIT [LICENSE](LICENSE.md).
