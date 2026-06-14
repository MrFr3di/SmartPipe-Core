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
  -> PipelineOutputEmitter
  -> optional IPipelineSink<TOutput>
  -> PipelineRun<TOutput>.Outputs
```

The stage chain inside one envelope is sequential. `MaxConcurrency > 1` permits
multiple envelopes to be processed at the same time. Cross-envelope output order
is not guaranteed.

## Channels

Runtime input, output, and buffered observer channels are bounded and created by
typed runtime factories with `AllowSynchronousContinuations = false`.

`InputFullMode = Wait` and `OutputFullMode = Wait` apply backpressure. Lossy
bounded modes may drop work and should be used only when that is acceptable to
the caller.

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

`DrainAsync` stops accepting new source items at source boundaries and completes
already accepted work. A drain timeout throws `TimeoutException` and does not
mark the run as cancelled.

`CancelAsync` requests cooperative cancellation and completes outputs as
cancelled.

`AbortAsync` is the immediate stop path. It is distinct from graceful drain.

`DisposeAsync` is idempotent and disposes runtime-owned components once.

## Failure Handling

`StageExecutor` owns retry, timeout, circuit-breaker checks, dead-letter writes,
and terminal failure action decisions.

Circuit-breaker rejection is a terminal failure for the current item. It is not
retried into the open breaker.

Dead-letter records use `DeadLetterEnvelope<T>` and preserve original payload,
pipeline id, run id, trace id, stage id/name, metadata, error, attempt, and
failure timestamp.

## Observability

Runtime activities use the `SmartPipe.Core` `ActivitySource`. The runtime emits
`Pipeline.Run` and `Transform` activities with low-cardinality tags such as
`smartpipe.pipeline_id`, `smartpipe.run_id`, `smartpipe.parallelism`,
`smartpipe.stage_id`, and `smartpipe.trace_id`.

Runtime instruments use the `SmartPipe.Core` `Meter`. Current instruments
include processed, failed, retried, dead-lettered, duplicate-filtered counters
and a stage latency histogram.

## Scope Boundary

Core contains the runtime and typed abstractions. Integration components belong
in `SmartPipe.Extensions`.
