# Architecture

SmartPipe.Core has two public runtime paths:

- the legacy 1.x compatibility runtime built around
  `SmartPipeChannel<TInput,TOutput>`;
- the 1.1.0 envelope-aware typed runtime built around `PipelineBuilder`,
  `PipelineDefinition`, `PipelineExecutionPlan`, and `PipelineRuntime`.

## Runtime Layers

### PipelineDefinition

`PipelineDefinition` is an immutable declarative topology. It records pipeline
identity, component registrations, stage definitions, component ownership,
lineage mode, and stage failure metadata.

Concrete component instances are single-use unless the component explicitly
declares `Reusable` or `SingletonExternal` through
`IPipelineComponentDescriptor`. Factory-based definitions create fresh
runtime-owned components for each run.

### PipelineExecutionPlan

`PipelineExecutionPlan` is the compiled form of a definition. The current
implementation validates adjacent stage type flow and component reusability
rules before a runtime is created.

### PipelineRuntime

`PipelineRuntime` is a single-use execution owner. It owns run identity and
marks the definition as used. The typed path runs envelope-aware source,
transformer, and sink components through this boundary and returns a
`PipelineRun<TOutput>` handle.

```csharp
var run = PipelineBuilder
    .From(source)
    .Transform(firstStage)
    .Transform(secondStage)
    .To(sink);

await run.Completion;
```

Typed runtime options are additive and opt-in. Without `PipelineRuntimeOptions`,
the runtime keeps the existing output channel behavior, inline observer
dispatch, system clock usage, retry behavior, sink behavior, and compatibility
threshold circuit breaker mode.

## Legacy Runtime

`SmartPipeChannel<TInput,TOutput>` remains available for 1.x compatibility. It
supports `ISource<T>`, `ITransformer<TInput,TOutput>`, and `ISink<T>`.

Legacy runtime boundaries:

- `DrainAsync` waits for accepted work to finish and must not be used as an
  abort operation.
- `Cancel()` is the immediate stop operation.
- `RunInBackground` can be called once per pipeline instance.
- `ThrowOnMutationAfterStart` can reject `AddSource`, `AddTransformer`, and
  `AddSink` after start.
- Legacy circuit breaker behavior is separate from typed stage-level circuit
  breaker policies.

## Component Lifetimes

`PipelineComponentLifetime` values:

- `SingleUse`: default for concrete source, transformer, and sink instances;
- `Reusable`: component can participate in more than one run;
- `SingletonExternal`: component is owned outside the runtime.

Factory APIs make reusable definitions explicit:

```csharp
var builder = PipelineBuilder
    .FromFactory(_ => new Source())
    .TransformFactory(_ => new ParseStage())
    .TransformFactory(_ => new ValidateStage());

var firstRun = builder.Run();
var secondRun = builder.Run();
```

## Output Model

`PipelineRun<T>.Outputs` is the primary typed output stream. Each
`PipelineOutput<T>` contains:

- `Result`: the compatibility `ProcessingResult<T>`;
- `Envelope`: the final `ProcessingEnvelope<T>` when available.

`PipelineRun<T>.ReadResultsAsync()` projects the same stream into legacy-style
results for consumers that do not need envelope data.

If typed `OutputCapacity` is configured with `OutputFullMode.Wait`, callers
must consume `PipelineRun<T>.Outputs`. In sink-only worker scenarios, leave
`OutputCapacity` unset so output remains unbounded and default-compatible.

## Observer Model

Typed pipelines attach observers with `WithObserver`. Events carry pipeline id,
run id, trace id, optional stage id, attempt, and UTC timestamp.

Observer reliability values:

- `BestEffort`: routine logging and metrics;
- `Reliable`: audit-oriented observers;
- `Critical`: policy observers that may fault a run.

Observer dispatch is inline in 1.1.0. Non-critical observer failures are
reported through `ObserverFailedEvent`; critical observer failures or
`FaultPipeline` observer policies fault the run.

Buffered observer dispatch is available through `PipelineRuntimeOptions` as an
explicit bounded mode. The default remains inline. `BufferedReliable` requires
flush-on-completion, and buffered observer failure handling follows the
configured `ObserverFailureMode`; it is not a full inline-equivalent recursive
observer-failure propagation model.

## Stage Failure Model

Typed transformer stages can attach `StageFailureOptions` and
`StageDeadLetterOptions<TStageInput>`.

Supported terminal actions:

- `EmitFailureResult`;
- `DeadLetter`;
- `Skip`;
- `StopPipeline`;
- `FaultPipeline`.

Typed stages support retry, attempt timeout, stage timeout, per-stage circuit
breaker policy, and replay-safe dead-letter records. Sink retry/timeout and
typed `PipelineTimeout` are not claimed for 1.1.0.

Typed `PipelineRun<T>.DrainAsync` requests source-boundary drain, stops
requesting new source items, and completes already accepted work. Typed
`MaxDegreeOfParallelism` defaults to `1`; higher values process multiple
envelopes concurrently while keeping each envelope's stage chain sequential and
sink writes serialized.

## Diagnostics And Adaptive Components

The library includes adaptive and diagnostic primitives such as
`AdaptiveMetrics`, `AdaptiveParallelism`, `BackpressureStrategy`,
`CircuitBreaker`, `RetryQueue<T>`, `ExponentialHistogram`,
`DeduplicationFilter`, `CuckooFilter`, `HyperLogLogEstimator`, and
`ReservoirSampler`.

Do not turn those primitives into release claims such as zero allocations,
lock-free behavior, or exact performance improvements unless benchmark and CI
evidence exists for the current release.

SmartPipe.Core remains an in-process pipeline runtime. Durable local-first
storage, SQLite checkpoints, outbox/inbox, sync, and conflict resolution belong
to future packages such as `SmartPipe.LocalFirst.*`.
