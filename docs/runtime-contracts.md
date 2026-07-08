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

## Adaptive Admission

Adaptive parallelism is opt-in. It is an admission-control layer for parallel
envelope processing: it changes the active concurrent envelope admission limit,
not the sequential stage chain inside one envelope.

`MaxConcurrency` remains the hard cap. The adaptive limit can move within the
configured min/max bounds based on completion latency and interval failure
ratio. Completion samples are accumulated until `EvaluationInterval` elapses,
then processed and failed counts are snapshotted and reset together. Failure
pressure can reduce concurrency only when the interval processed count reaches
`MinimumFailureSamples` and `failed / processed` is at least
`FailurePressureThreshold`. `AdjustmentCooldown` can block a limit change, but
the evaluated window is still reset and not carried into the next interval.
`EvaluationInterval` values much smaller than `AdjustmentCooldown` can drop
several windows of latency or failure signal between concurrency changes. The
current model is completion-based and does not run a background sampling loop.
Retry attempts remain observable through metrics and events, but they do not
affect adaptive admission decisions.

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

`CancelAsync` cancels source and in-flight processing. It records cancellation
intent and requests cooperative cancellation. The terminal state is not final
until runtime finalization publishes one outcome.

`AbortAsync` performs immediate cancellation and records abort intent. It is
the immediate stop path, distinct from graceful drain. Abort intent has
precedence over cancellation intent when no processing or mandatory cleanup
fault exists.

After a run starts, `RunAsync` owns source, stage, sink, and observer cleanup.
Runtime finalization processes work first, attempts cleanup for every owned
component, determines the terminal outcome, publishes state, completes the
output channel, sends one terminal pipeline event, and then completes/disposes
observer dispatch. `PipelineRun<T>.Completion` is the same execution task used
by the runtime, so state, output completion, terminal event, and completion
derive from one finalization pass.

Terminal precedence is:

```text
processing or mandatory cleanup fault > abort request > cancellation request > completion
```

After state and output completion are published, terminal observer delivery and
observer teardown failures are diagnostics only. They do not change the
published state, output-channel completion, or `PipelineRun<T>.Completion`
outcome.

For buffered observer dispatch, `FlushOnCompletion = false` affects completion
only. Disposal still stops and awaits the buffered worker before returning.
Observer callbacks should observe cancellation tokens so buffered shutdown is
bounded.

Cleanup attempts are best-effort but complete: one cleanup failure does not
skip later owned resources. If processing and cleanup both fail, the processing
exception remains primary and cleanup errors are reported after it. If cleanup
is the only failure, the run faults during finalization.

Late timed-out stage attempts are part of runtime cleanup. The runtime tracks
detached attempts and waits up to `TimeoutPolicy.LateAttemptFinalizationTimeout`
before disposing the owning stage. If a non-cooperative transformer continues
past that timeout, the runtime reports a cleanup failure instead of forcibly
stopping user code in-process. A stage with a still-running late attempt is not
disposed during that failed finalization pass.

`DisposeAsync` is idempotent. Concurrent callers await one shared disposal
task. For a started run, external disposal requests cancellation, waits for the
run task to finish its owned cleanup, and then disposes executor-level
primitives. If the runtime was never started, disposal performs component
cleanup itself.

## Failure Handling

`StageExecutor` owns retry, timeout, circuit-breaker checks, dead-letter writes,
and terminal failure action decisions.

Thrown transformer exceptions are stage failures. By default they become
permanent `StageException` errors. `StageFailureOptions.ExceptionClassifier`
can map thrown exceptions to a custom `SmartPipeError`, including transient
errors for retry. Runtime-generated timeout results remain transient through
the timeout result path; thrown `TimeoutException` and `HttpRequestException`
remain permanent unless a classifier says otherwise. Pipeline cancellation
`OperationCanceledException` bypasses the classifier and remains cancellation.

`RetryPolicy.OnRetry` is invoked after the retry delay completes and before the
next retry attempt starts. It is not invoked when the retry delay is cancelled.
If the callback throws, the run faults with that exception.

`TimeoutPolicy.AttemptTimeout` limits one attempt. `StageTimeout` is measured
with the runtime monotonic clock and includes attempt execution, cancellation
grace, retry delay, and the next attempt budget. When `Clock` is a
`TimeProviderPipelineClock`, runtime retry delays and timeout waits use the
underlying `TimeProvider`; custom `IPipelineClock` implementations keep the
compatibility fallback. `RetryMode` controls overlap after an attempt timeout:

- `CooperativeOnly` is the default. The runtime cancels the attempt, waits
  `CancellationGracePeriod`, and retries only if the timed-out attempt has
  completed.
- `DetachWithoutRetry` returns the timeout result, observes the late task, and
  does not retry.
- `DetachAndRetryIdempotent` detaches the late task and permits retry overlap;
  use it only for idempotent transformers that are safe to run concurrently for
  the same item.

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
