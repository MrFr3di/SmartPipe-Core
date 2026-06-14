# Legacy Surface Inventory

Status: active
Date: 2026-06-11

## Purpose

This inventory prevents accidental deletion of useful functionality during the
typed-only refactor. Legacy runtime implementation may be removed only after the
useful behavior below has a typed runtime replacement, adapter, or explicit
deletion decision.

## Classification Legend

- `Delete`: remove after replacement coverage exists.
- `Move to typed`: preserve behavior by implementing it in typed runtime.
- `Replace with adapter`: preserve simple usage through typed adapters.
- `Keep internal`: implementation detail may remain only if no public legacy
  runtime dependency remains.

## Core Legacy Runtime Surface

| Surface | Current location | Classification | Typed target |
|---|---|---|---|
| `SmartPipeChannel<TInput,TOutput>` | `src/SmartPipe.Core/SmartPipeChannel.cs` | Delete | `PipelineBuilder`, `PipelineRuntime`, `PipelineRun<TOutput>` |
| `SmartPipeChannelOptions` | `src/SmartPipe.Core/SmartPipeChannelOptions.cs` | Delete | `PipelineRuntimeOptions`, stage failure options, typed observer options |
| `ISource<T>` | `src/SmartPipe.Core/ISource.cs` | Replace with adapter | `IPipelineSource<T>` plus convenience source adapters |
| `ITransformer<TInput,TOutput>` | `src/SmartPipe.Core/ITransformer.cs` | Replace with adapter | `IPipelineTransformer<TInput,TOutput>` plus function adapters |
| `ISink<T>` | `src/SmartPipe.Core/ISink.cs` | Replace with adapter | `IPipelineSink<T>` plus function adapters |
| `ProcessingContext<T>` | `src/SmartPipe.Core/ProcessingContext.cs` | Move to typed / then delete | `ProcessingEnvelope<T>` metadata, lineage, trace id |
| `ProcessingResult<T>` | `src/SmartPipe.Core/ProcessingResult.cs` | Move to typed / then delete | `StageResult<T>` and `PipelineOutput<T>` failure/success results |
| `RunLegacyAsPipelineRun` | `src/SmartPipe.Core/PipelineBuilder.cs` | Delete | typed builder paths only |

## Useful Legacy Behavior To Preserve

| Capability | Legacy source | Classification | Typed replacement requirement |
|---|---|---|---|
| Source -> transform -> sink | `SmartPipeChannel`, `PipelineBuilder` legacy branch | Move to typed | typed builder + adapters must cover simple usage |
| Bounded input backpressure | `BoundedCapacity`, `FullMode`, input channels | Move to typed | `InputCapacity`, `InputFullMode`, channel factory |
| Multiple workers | `MaxDegreeOfParallelism` | Move to typed | `MaxConcurrency` real typed workers |
| Output reader/backpressure | output channel and `RunInBackground` | Move to typed | bounded output + `PipelineRun.Outputs` + output policy |
| Retry queue | `RetryQueue<T>`, `DefaultRetryPolicy` | Move to typed | `StageExecutor` retry loop |
| Attempt timeout | `AttemptTimeout` | Move to typed | stage timeout policy |
| Total timeout | `TotalRequestTimeout` | Move to typed or delete as legacy-specific | run/stage timeout decision in typed lifecycle |
| Circuit breaker | `CircuitBreaker` feature flag | Move to typed | stage-level `CircuitBreakerPolicy` |
| Dead-letter | `DeadLetterSink`, `DeadLetterEnvelope<T>` | Move to typed | typed dead-letter sink/envelope path |
| Metrics callback/snapshot | `SmartPipeMetrics`, `OnMetrics` | Move to typed | metrics recorder + immutable snapshot + `Meter` |
| Activity tracing | `ActivitySource` in legacy run | Move to typed | typed runtime activities and low-cardinality tags |
| Drain/cancel/dispose | `DrainAsync`, `Cancel`, `DisposeAsync` | Move to typed | true `PipelineRun.DrainAsync`, `CancelAsync`, `AbortAsync`, idempotent dispose |
| Deduplication | `DeduplicationFilter` option | Move to typed or adapter | typed source/stage dedup decision required |
| Convenience mutation APIs | `AddSource`, `AddTransformer`, `AddSink` | Replace with adapter | typed builder/function adapters |
| Adaptive parallelism | adaptive lane stack | Delete unless proven useful | keep `MaxConcurrency`; do not preserve misleading adaptive mode |
| `ChannelPool` public API | `ChannelPool` | Delete or keep internal | typed channel factory; avoid public pooled channels |

## Extensions Legacy Surface

| Surface | Current location | Classification | Typed target |
|---|---|---|---|
| `AddSmartPipe` overloads returning `SmartPipeChannel` | `src/SmartPipe.Extensions` | Delete | typed definition/factory registration |
| `ISmartPipeChannelFactory` | `src/SmartPipe.Extensions` | Delete | typed factory per run |
| `SmartPipeHostedService` over `SmartPipeChannel` | `src/SmartPipe.Extensions` | Move to typed | hosted service creates typed run per start |
| Health checks over `SmartPipeChannel` | `src/SmartPipe.Extensions/SmartPipeHealthCheck.cs` | Move to typed | health checks over `PipelineRunState` + metrics snapshot |
| Extension sources/transforms/sinks using `ProcessingContext`/`ProcessingResult` | `src/SmartPipe.Extensions` | Replace with adapter | typed extension components or compatibility adapters |

## Public API Impact

Most legacy surface is already in `PublicAPI.Shipped.txt`, so removal is a
breaking API change. Public API files must not be updated to remove shipped
legacy entries until the final legacy deletion step and version strategy are
explicitly accepted.

## Do Not Delete Yet

Do not delete these until typed equivalents are implemented and tested:

- `SmartPipeChannel<TInput,TOutput>`;
- `SmartPipeChannelOptions`;
- `ProcessingContext<T>`;
- `ProcessingResult<T>`;
- legacy `ISource`, `ITransformer`, `ISink`;
- extension package components based on the legacy contracts;
- hosted service and health checks.
