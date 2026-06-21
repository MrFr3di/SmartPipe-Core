# Runtime Contracts

SmartPipe.Core is a typed, in-process, envelope-aware pipeline runtime.

It does not provide durable queues, exactly-once delivery, local-first storage,
distributed orchestration, hidden source replay, or cross-process coordination.

## Execution Model

Runtime flow:

```text
IPipelineSource<TInput>
  -> bounded input channel of ProcessingEnvelope<TInput>
  -> one or more typed workers
  -> StageExecutor
  -> optional IPipelineSink<TOutput>
  -> PipelineOutputEmitter
  -> PipelineRun<TOutput>.Outputs
```

The stage chain inside one envelope is sequential. `MaxConcurrency > 1` permits
multiple envelopes to be processed at the same time. Cross-envelope output order
is not guaranteed.

## Factory And Instance Lifetimes

Instance pipelines use concrete components and are single-use:

```csharp
PipelineBuilder
    .From(source)
    .Transform(stage)
    .To(sink);
```

Factory pipelines are reusable definitions. `FromFactory`, `TransformFactory`,
and `ToFactory` create fresh runtime-owned components per start:

```csharp
PipelineBuilder
    .FromFactory(_ => new Source())
    .TransformFactory(_ => new Stage())
    .ToFactory(_ => new Sink());
```

Factory APIs are strict. `TransformFactory` and `ToFactory` require a pipeline
created with `.FromFactory(...)`; use `.Transform(instance)` and `.To(instance)`
for instance pipelines.

## Channels

Runtime input, output, and buffered observer channels are bounded and created by
typed runtime factories with `AllowSynchronousContinuations = false`.

`PipelineRun<T>.Outputs` is intended for one consumer. It is configured as a
single-reader channel. Users who need fan-out must do it explicitly.

`InputFullMode = Wait` and `OutputFullMode = Wait` apply backpressure. Lossy
bounded modes may drop work and should be used only when that is acceptable to
the caller. Input and output drop callbacks record `smartpipe.items.dropped`
and `smartpipe.output.items.dropped` and emit best-effort `InputDroppedEvent`
or `OutputDroppedEvent`.

Default `OutputPolicy` is `SuppressSuccessWhenSinkAttached`.

This is the safe default for sink-backed pipelines because successful outputs are not written to `PipelineRun<T>.Outputs` unless the caller explicitly opts into `EmitAll`.

Use `EmitAll` only when the caller actively consumes `PipelineRun<T>.Outputs`.

## Lifecycle

`PipelineRunState` transitions:

```text
Created -> Running
Running -> Draining -> Completed
Running -> Cancelled
Running -> Aborted
Running -> Faulted
Completed/Cancelled/Aborted/Faulted -> Disposed
```

`DrainAsync` stops source reading and waits for already accepted work to
complete. It cancels the source-read token so a cooperative source blocked
inside `MoveNextAsync` can stop promptly, but it does not cancel the processing
token for work that was already accepted. A drain timeout throws
`TimeoutException` and does not mark the run as cancelled.

`TryDrainAsync` is the structured non-throwing drain API. It returns
`PipelineDrainResult` with `Completed`, `TimedOutStillRunning`,
`CancelledByCaller`, `Faulted`, or `AlreadyCompleted`.

`CancelAsync` cancels source and in-flight processing. It requests cooperative
cancellation and completes outputs as cancelled.

`AbortAsync` performs immediate cancellation and marks the run aborted. It is
the immediate stop path, distinct from graceful drain.

`DisposeAsync` is idempotent and disposes runtime-owned components once.

## Failure Handling

`StageExecutor` owns retry, timeout, circuit-breaker checks, dead-letter writes,
and terminal failure action decisions.

Circuit-breaker rejection is a terminal failure for the current item. It is not
retried into the open breaker.

For sink-backed pipelines, a success output means both transform processing and
the sink write completed successfully. The runtime writes the sink first and
emits `PipelineResult.Success` only after the sink write returns successfully.

`StageResult.Filtered()` is a non-failure terminal state: it does not call the
sink, does not increment failed metrics, and does not write dead-letter records.
It emits `ItemFilteredEvent`, records `smartpipe.items.filtered`, and can appear
as `PipelineResultKind.Filtered` when output policy emits all terminal states.

Dead-letter records use `DeadLetterEnvelope<T>` and preserve original payload,
pipeline id, run id, trace id, stage id/name, metadata, error, attempt, and
failure timestamp.

## Observability

Runtime activities use the `SmartPipe.Core` `ActivitySource`. The runtime emits
`Pipeline.Run` and `Transform` activities with tags such as
`smartpipe.pipeline_id`, `smartpipe.run_id`, `smartpipe.parallelism`,
`smartpipe.stage_id`, and `smartpipe.trace_id`. `run_id` and `trace_id` are
debugging/tracing identifiers and must not be used as metric dimensions by
default.

Runtime instruments use the `SmartPipe.Core` `Meter`. Current instruments
are `smartpipe.items.processed`, `smartpipe.items.failed`,
`smartpipe.items.filtered`, `smartpipe.items.dropped`,
`smartpipe.output.items.dropped`, `smartpipe.observer.events.dropped`,
`smartpipe.items.retried`, `smartpipe.items.deadlettered`,
`smartpipe.items.duplicates_filtered`, `smartpipe.stage.duration`, and
`smartpipe.sink.duration`.

Concurrency and lifecycle tests must be deterministic. They should use explicit
coordination primitives such as `TaskCompletionSource`, bounded channels,
barriers, fake clocks, or blocked source/sink components. Timeouts are guards,
not synchronization.

Failure coverage should exercise source initialization/read failures,
transformer default/throw/timeout paths, retry cancellation, circuit-breaker
open and rejection paths, dead-letter write failures, sink initialization/write
failures, observer failures, absent or slow output readers, cancellation during
output writes, drain during source reads, and disposal during in-flight work.

## Scope Boundary

Core contains the runtime and typed abstractions. Integration components belong
in `SmartPipe.Extensions`.
