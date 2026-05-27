# Runtime Architecture

SmartPipe.Core 1.1.0 introduces a compatibility-preserving runtime direction
without removing the 1.x public APIs.

## Layers

### PipelineDefinition

`PipelineDefinition` is an immutable declarative topology. It records pipeline
identity, component registrations, stage definitions, ownership options, lineage
mode, and resilience/observer metadata.

Instance-based definitions are single-use unless all components declare a
reusable or externally-owned lifetime. Factory-based definitions create fresh
components for each run and can create multiple runtimes safely.

### PipelineExecutionPlan

`PipelineExecutionPlan` is the compiled and validated form of a definition. The
current implementation validates adjacent stage type flow and reusability rules.
Future validators should include bounded capacity, observer overflow policy,
timeout/retry/circuit combinations, and package diagnostics.

### PipelineRuntime

`PipelineRuntime` is a single-use execution owner. It owns run identity and marks
the definition as used. The typed 1.1.0 path now runs envelope-aware source,
transformer, and sink components through this boundary and returns a
`PipelineRun<TOutput>` handle. The intended runtime responsibility set remains
channels, workers, retry scheduler, cancellation, output stream, observer
dispatcher, initialized components, and runtime-owned resource disposal.

The legacy `SmartPipeChannel<TInput,TOutput>` execution engine remains available
for 1.x compatibility. New typed pipelines should prefer:

```csharp
var run = PipelineBuilder
    .From(source)
    .Transform(firstStage)
    .Transform(secondStage)
    .To(sink);

await run.Completion;
```

## Component Lifetimes

Component lifetime is explicit:

- `SingleUse`: default for concrete source, transformer, and sink instances.
- `Reusable`: component can participate in more than one run.
- `SingletonExternal`: component is owned by an external container and is not
  disposed by the runtime unless explicitly configured.

Factory-created components are runtime-owned by default.

Factory APIs make the reusable boundary explicit:

```csharp
var builder = PipelineBuilder
    .FromFactory(_ => new Source())
    .TransformFactory(_ => new ParseStage())
    .TransformFactory(_ => new ValidateStage());

var firstRun = builder.Run();
var secondRun = builder.Run();
```

The same concrete source, stage, or sink instance is single-use by default. A
second run throws an actionable exception unless the component implements
`IPipelineComponentDescriptor` and declares `Reusable` or `SingletonExternal`.
Externally-owned singleton components are not disposed unless ownership options
explicitly allow it.

## Output Model

The primary output model is `PipelineOutput<T>`, which carries both the final
`ProcessingResult<T>` and the optional final `ProcessingEnvelope<T>`. Legacy
result-only consumption is exposed as a projection so the runtime does not need
two independent output channels.

`PipelineRun<T>.Outputs` is the single primary runtime output stream.
`PipelineRun<T>.ReadResultsAsync()` projects the same stream into legacy
`ProcessingResult<T>` values for consumers that do not need envelopes.

## Observer Model

Typed pipelines can attach observers with `WithObserver`. Events currently
include run start/completion/fault/cancellation, stage start/success/failure,
sink write start/failure, and observer failure. The event contract carries
`PipelineId`, `RunId`, `TraceId`, `StageId`, attempt number, and UTC timestamp
so diagnostics, audit, and future lineage integrations can correlate events
without scraping logs.

Observer registrations define reliability and failure policy:

- `BestEffort`: routine diagnostics such as logging and metrics.
- `Reliable`: audit-oriented observers such as dead-letter or lineage capture.
- `Critical`: policy observers that may fault the pipeline.

In 1.1.0 the typed runtime dispatches observer events inline. Non-critical
observer failures are converted into `ObserverFailedEvent` notifications for the
remaining observers. `Critical` observers and registrations with
`FaultPipeline` failure policy fault the run. A bounded asynchronous dispatcher
with overflow policies is part of the planned hardening path, so lossy observer
queues must not be used yet for Memory lineage or other reliability-critical
integrations.

Terminal `PipelineFaultedEvent` and `PipelineCancelledEvent` delivery is
best-effort: observer failures during terminal notification are swallowed so
they do not hide the original exception or cancellation result exposed through
`PipelineRun<T>.Completion`.

## Stage Failure And Dead Letter

Envelope-aware `Transform` overloads can attach `StageFailureOptions` and
`StageDeadLetterOptions<TStageInput>`. When a stage returns a permanent failure
the runtime applies `OnPermanentFailure`:

- `EmitFailureResult`: writes a failed output and continues later source items.
- `DeadLetter`: writes `DeadLetterEnvelope<TStageInput>`, emits
  `DeadLetterWrittenEvent`, writes a failed output, and continues.
- `Skip`: writes no output for that item and continues.
- `StopPipeline`: writes a failed output and stops reading more source items.
- `FaultPipeline`: faults `PipelineRun<T>.Completion` with
  `PipelineFailureActionException`.

`TimeoutPolicy.AttemptTimeout` is enforced for typed transformer stages.
`StageTimeout` is used as the total stage budget available to an attempt. A
timed-out attempt is converted into a terminal `TimedOut` result with a
transient `SmartPipeError` in the `Timeout` category, emits `StageFailedEvent`,
and then follows the configured terminal failure action. This keeps timeout,
dead-letter, skip, stop, and fault behavior on one runtime path. Full retry-delay
budget enforcement is still a hardening item and is not described as complete.

`StageFailureOptions.Retry` is also enforced for typed transformer stages. A
retryable failure emits `StageFailedEvent`, `RetryScheduledEvent`, updates the
envelope attempt count, emits `RetryAttemptedEvent`, and then invokes the same
stage again. When the retry budget is exhausted, the runtime emits
`RetryExhaustedEvent` and applies `OnRetryExhausted` once.

The runtime does not dispose stage dead-letter streams. Storage lifetime,
rotation, encryption, and payload redaction remain application-owned decisions.
