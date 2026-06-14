# Architecture

SmartPipe.Core is organized around one typed runtime model.

## Core Flow

```text
PipelineBuilder
  -> PipelineDefinition / PipelineRuntime
  -> TypedPipelineExecutor
  -> PipelineProducer
  -> PipelineWorker(s)
  -> StageExecutor
  -> PipelineOutputEmitter
  -> SinkExecutor
  -> PipelineRun<TOutput>
```

Sources produce `ProcessingEnvelope<T>`. Transformers return
`StageResult<T>`. Sinks consume `ProcessingEnvelope<T>`.

## Runtime Ownership

`PipelineRun<T>` owns one running execution. Component instances are disposed by
the runtime unless they declare external ownership through
`IPipelineComponentDescriptor`.

Factory-based DI registration creates a new scope and new runtime per run.

## Channels

Input, output, and observer queues are bounded. Runtime channel factories state
reader/writer cardinality explicitly and disable synchronous continuations.

## Failure And Lifecycle

`StageExecutor` owns retry, timeout, circuit breaker, dead-letter routing, and
terminal failure action decisions. `PipelineLifecycleController` owns run state
transitions for drain, cancel, abort, completion, and fault.

## Observability

`SmartPipeActivitySource` emits `Pipeline.Run` and `Transform` activities.
`SmartPipeMetricsRecorder` records counters and immutable snapshots.
`SmartPipeMeter` publishes runtime instruments.
