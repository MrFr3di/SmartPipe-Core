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

## Package Dependencies

```text
SmartPipe.Core
    ↑
    ├── SmartPipe.Extensions.Json
    └── SmartPipe.Extensions
            └── SmartPipe.Extensions.Json (2.x compatibility bridge)
```

`SmartPipe.Extensions.Json` owns JSON file, transform, and JSON dead-letter
implementations. It must never reference `SmartPipe.Extensions`.
`SmartPipe.Extensions` 2.1.2 references the JSON package only to preserve the
2.x type-forwarding contract.

Core permanently owns `DeadLetterEnvelope<T>`, `IDeadLetterSerializer<T>`, and
the standard `JsonLinesDeadLetterSerializer<T>` codec reused by the JSON source
and sink. This is the final ownership boundary, not a deferred package move.

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

## AOT And Trimming

SmartPipe.Core is AOT-conscious and analyzer-gated. Reflection-sensitive JSON
and dead-letter helpers expose source-generated serializer paths where relevant.

`SmartPipe.Extensions.Json` exposes source-generated metadata paths for its
reflection-sensitive APIs. Some integrations remaining in
`SmartPipe.Extensions` may not be AOT-friendly.
