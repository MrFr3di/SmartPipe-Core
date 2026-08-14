# SmartPipe.Core

Typed in-process streaming pipelines for .NET.

SmartPipe.Core runs explicit `source -> transform -> sink` pipelines inside your
process with bounded channels, envelope metadata, retry/timeout/circuit-breaker
stage handling, observer events, metrics snapshots, and dead-letter records
with replay context. It is not a distributed workflow engine, message broker,
durable queue, or exactly-once delivery system.

[![CI](https://github.com/MrFr3di/SmartPipe-Core/actions/workflows/ci.yml/badge.svg)](https://github.com/MrFr3di/SmartPipe-Core/actions)
[![NuGet Core](https://img.shields.io/nuget/v/SmartPipe.Core.svg)](https://www.nuget.org/packages/SmartPipe.Core)
[![NuGet Extensions](https://img.shields.io/nuget/v/SmartPipe.Extensions.svg)](https://www.nuget.org/packages/SmartPipe.Extensions)
[![NuGet JSON Extensions](https://img.shields.io/nuget/v/SmartPipe.Extensions.Json.svg)](https://www.nuget.org/packages/SmartPipe.Extensions.Json)
![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)

## Contract

### Guarantees

| Guarantee | Notes |
|---|---|
| In-process processing only | Pipelines run inside the caller's process. No cross-process hops. |
| Bounded channels | Input, output, and buffered observer channels are bounded. |
| Adaptive admission | Adaptive parallelism is opt-in; see [Configuration](docs/configuration.md#adaptive-parallelism). |
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

Dead-letter records preserve replay context, but persistence durability depends
on the configured sink and storage. Path-backed dead-letter and JSON file sinks
provide in-process exception rollback on seekable streams; they do not provide
crash-atomic file replacement.

### Lifecycle Semantics

| Operation | Semantics |
|---|---|
| `DrainAsync` / `TryDrainAsync` | Stops source reading and waits for already accepted work to complete. |
| `CancelAsync` | Cancels source reading and in-flight processing. |
| `AbortAsync` | Immediate cancellation of source and processing. |
| `DisposeAsync` | Idempotent; disposes runtime-owned components once. |

## Install

```bash
dotnet add package SmartPipe.Core --version 2.1.2
dotnet add package SmartPipe.Extensions.Json --version 2.1.2
```

For canonical 2.2 DI pipelines, `SmartPipe.Extensions.HealthChecks` adds exact-key liveness, readiness, aggregate, ASP.NET tag, trimming, and NativeAOT support. See [Health checks](docs/health-checks.md).

Install `SmartPipe.Extensions` 2.1.2 for HTTP, database, CSV, mapping,
resilience, hosting, and health-check integrations. JSON-only applications
should reference `SmartPipe.Extensions.Json` directly.

## Canonical Definitions In 2.2

The 2.2 definition API separates resource-free construction from per-run
activation. Every component enters through an explicit ownership descriptor;
`Build()` and terminal `To()` validate and snapshot the definition without
invoking factories:

```csharp
PipelineDefinition<int, string> definition = PipelineDefinitionBuilder
    .From(
        new PipelineKey("orders"),
        PipelineComponent.RuntimeOwned<IPipelineSource<int>>(
            static (_, _) => ValueTask.FromResult<IPipelineSource<int>>(new OrderSource())))
    .Transform(
        new PipelineStageKey("format"),
        PipelineComponent.RuntimeOwned<IPipelineTransformer<int, string>>(
            static (_, _) => ValueTask.FromResult<IPipelineTransformer<int, string>>(
                PipelineTransformer.FromFunc<int, string>(
                    static (value, _) => ValueTask.FromResult(value.ToString())))))
    .Build();

await using PipelineRun<string> run = await definition.StartAsync(cancellationToken);
await run.Completion;
```

Definitions containing only per-run component descriptors are reusable. A
borrowed component, observer instance, or `StageDeadLetterOptions<T>` makes the
definition single-use; Core never disposes those external resources.
`StartAsync` returns only after activation, initialization, runtime `Running`
state, and `PipelineStartedEvent` acceptance. Runtime-created handles expose the
exact `PipelineKey` and `Guid RunId` for the run.

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
Legacy instance registrations and observers are single-use even when an instance
advertises `Reusable` or `SingletonExternal`; use the all-factory form for every
repeated or concurrent run.

Typed health checks can be registered for DI pipelines:

```csharp
services
    .AddHealthChecks()
    .AddSmartPipeHealthCheck<Order, OrderDto>("orders");
```

The health check reads the typed run state and immutable metrics snapshot. It
reports high queue utilization or stale processing as degraded and faulted runs
as unhealthy. Running pipelines with no initial activity remain healthy by
default unless `RequireInitialActivity` is enabled.

Hosted-service pipeline faults are configurable through
`SmartPipeHostedServiceOptions`. The default `FailureBehavior` is
`StopApplication`, so a background pipeline fault requests host shutdown instead
of being logged and swallowed.

Lossy bounded channel modes are observable. Input, output, and buffered
observer drops record `smartpipe.items.dropped`,
`smartpipe.output.items.dropped`, and `smartpipe.observer.events.dropped`.
Queue depths in metrics snapshots are point-in-time pressure indicators, not
durable work accounting.

## AOT And Trimming

SmartPipe.Core is AOT-conscious and analyzer-gated.

Reflection-based HTTP, JSON file, and dead-letter helpers are annotated with
`RequiresUnreferencedCode` / `RequiresDynamicCode`. Use constructors that accept
source-generated `JsonTypeInfo` for NativeAOT or trimming-sensitive consumers.
`JsonFileSink<T>` writes newline-delimited JSON batches: one JSON array per
flushed batch.

Options-based constructors also support one root JSON array and conventional
NDJSON. Sources stream arrays and top-level values, reject null records by
default, and enforce configurable depth and framed-record limits.

JSON file, transform, and JSON dead-letter integrations live in
`SmartPipe.Extensions.Json`. HTTP JSON helpers remain in `SmartPipe.Extensions`.
Some non-JSON integrations may not be AOT-friendly.

## Extensions Package Surface

`SmartPipe.Extensions.Json` owns JSON file sources and sinks, JSON transforms,
and JSON dead-letter persistence without the broad Extensions dependency graph.
`SmartPipe.Extensions` 2.1.2 retains type forwarders and a transitive JSON
dependency for 2.x source and binary compatibility. New JSON applications
should reference the dedicated package directly.

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
- [Package authoring](docs/contributing/package-authoring.md)
- [Package infrastructure](docs/architecture/package-infrastructure.md)
- [Migration from removed legacy APIs](docs/migration/legacy-to-typed.md)
- [Migration to SmartPipe.Extensions.Json](docs/migration/2.1.2-json-package-split.md)

## Requirements

- .NET 10.0 or later.
- `SmartPipe.Core` depends on `Microsoft.Extensions.Logging.Abstractions`.
- `SmartPipe.Extensions.Json` adds System.Text.Json file, transform, and
  dead-letter integrations.
- `SmartPipe.Extensions` adds HTTP, EF Core, Dapper, CSV, Mapster, Polly,
  hosting, health-check, and compatibility forwarding for JSON integrations.

## License

MIT [LICENSE](LICENSE).
