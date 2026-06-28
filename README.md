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

## Contract

### Guarantees

| Guarantee | Notes |
|---|---|
| In-process processing only | Pipelines run inside the caller's process. No cross-process hops. |
| Bounded channels | Input, output, and buffered observer channels are bounded. |
| Envelope metadata | `ProcessingEnvelope<T>` carries `PipelineId`, `RunId`, `TraceId`, `Metadata`, `Lineage`, `Attempt`, `CreatedAtUtc`. |
| Typed source/transform/sink | `IPipelineSource<T>`, `IPipelineTransformer<TInput,TOutput>`, `IPipelineSink<T>`. |
| Configured retry/timeout/circuit breaker | Per-stage `StageFailureOptions`; circuit breaker uses half-open probe leases. |
| Observer events | Lifecycle, stage, sink, retry, dead-letter, drop, and circuit-breaker transitions. |
| Metrics snapshots | `SmartPipeMetricsRecorder` and immutable `SmartPipeMetricsSnapshot`. |

### Non-Goals

| Non-goal | Notes |
|---|---|
| Distributed coordination | No cluster or leader election. |
| Durable queue | Work is in memory; crash recovery is the user's source/sink responsibility. |
| Exactly-once guarantee | At-least-once and at-most-once only. |
| Replay after process crash | Provided only if the user source/sink implements it. |
| Broker semantics | Not a message broker or workflow engine. |

### Output Semantics

| Situation | Behavior |
|---|---|
| No sink attached | Success output is emitted after transform success. |
| Sink attached | Success output is emitted only after sink write succeeds. |
| Default `OutputPolicy` | `SuppressSuccessWhenSinkAttached` — safe default for sink-backed runs. |
| `PipelineOutputPolicy.EmitAll` | Requires an active consumer of `PipelineRun<T>.Outputs`; otherwise the run can backpressure. |

Default `OutputPolicy` is `SuppressSuccessWhenSinkAttached`.

This is the safe default for sink-backed pipelines because successful outputs are not written to `PipelineRun<T>.Outputs` unless the caller explicitly opts into `EmitAll`.

Use `EmitAll` only when the caller actively consumes `PipelineRun<T>.Outputs`.
For the `OutputMode` compatibility deprecation policy and migration map, see
[Configuration](docs/configuration.md#output-filtering-api-deprecation).

### Failure Semantics

| Event | Behavior |
|---|---|
| Transformer exception | Routed through the stage's `FailureAction` policy. |
| `StageResult.Filtered()` | Non-failure terminal state. No sink call, no dead-letter, no failed-metric increment. |
| Stage timeout | Treated as transient failure; subject to retry policy. |
| Circuit breaker rejection | Terminal for the current item; not retried into the open breaker. |
| Dead-letter action | Requires `StageDeadLetterOptions<T>`; the action fails the run if misconfigured. |

### Lifecycle Semantics

| Operation | Semantics |
|---|---|
| `DrainAsync` / `TryDrainAsync` | Stops source reading and waits for already accepted work to complete. |
| `CancelAsync` | Cancels source reading and in-flight processing. |
| `AbortAsync` | Immediate cancellation of source and processing. |
| `DisposeAsync` | Idempotent; disposes runtime-owned components once. |

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
The output channel is single-reader by contract. Callers that need fan-out
must do it explicitly in user code (for example by reading outputs and
re-publishing through their own dispatcher).
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

Factory-created runs preserve the underlying runtime controls: `CancelAsync`,
`DrainAsync`, `TryDrainAsync`, `AbortAsync`, `Metrics`, `Outputs`, and `State`.
The DI wrapper only replaces the completion/disposal lifetime so the run scope is
disposed exactly once when the run completes or is disposed manually.

### Factory Vs Instance Builders

Instance pipelines use concrete components and are single-use:

```csharp
PipelineBuilder
    .From(source)
    .Transform(stage)
    .To(sink);
```

Reusable factory pipelines must use factories from source through sink:

```csharp
PipelineBuilder
    .FromFactory(_ => new Source())
    .TransformFactory(_ => new Stage())
    .ToFactory(_ => new Sink());
```

Do not mix instance components with `TransformFactory` or `ToFactory`. Use
`.Transform(instance)` and `.To(instance)` for instance pipelines, or start with
`.FromFactory(...)` when every run needs fresh runtime-owned components.

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
- [Migration from removed legacy APIs](docs/migration/legacy-to-typed.md)

## Requirements

- .NET 10.0 or later.
- `SmartPipe.Core` depends on `Microsoft.Extensions.Logging.Abstractions`.
- `SmartPipe.Extensions` adds HTTP, EF Core, Dapper, JSON, CSV, Mapster, Polly,
  hosting, health-check, and file integration components.

## License

MIT [LICENSE](LICENSE).
