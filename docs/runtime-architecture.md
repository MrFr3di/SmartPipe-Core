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
for 1.x compatibility. It supports single-stage pipelines with the 1.x `ISource<T>` /
`ITransformer<TInput,TOutput>` / `ISink<T>` model, but does NOT implement envelope-aware
execution, typed observers, or typed dead-letter semantics. New code should prefer the
modern typed runtime via `PipelineBuilder`:

```csharp
var run = PipelineBuilder
    .From(source)
    .Transform(firstStage)
    .Transform(secondStage)
    .To(sink);

await run.Completion;
```

**Legacy lifecycle hardening (1.1.x update2):** `SmartPipeChannel` lifecycle operations have been hardened. `DrainAsync` now stops accepting new work and waits for all accepted items to complete processing through transformers and sinks before returning; it must NOT be used as an abort/discard operation. Mutation methods (`AddSource`, `AddTransformer`, `AddSink`) are rejected after the pipeline has started (when `ThrowOnMutationAfterStart` is enabled). `RunInBackground` throws on repeated calls. This update does NOT implement sink retry/timeout, `PipelineTimeout`, or circuit breaker semantics — those remain out of scope for the legacy runtime.

The legacy `RetryQueue<T>` now supports explicit overflow policies via
`SmartPipeChannelOptions.RetryQueueOverflowPolicy`. When the retry queue reaches
capacity, the policy determines whether to wait, fail immediately, dead-letter,
or drop items. The default is `Wait` (blocking backpressure). This update does
not redesign retry scheduling/execution separation — that remains future hardening.

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
`StageTimeout` is a wall-clock budget for the whole stage, including execution
attempts and retry delays. A timed-out attempt is converted into a `TimedOut`
stage result with a transient `SmartPipeError` in the `Timeout` category and
emits `StageFailedEvent`. If the timeout is not retryable, the item follows
`OnPermanentFailure`; if retry accepts it but the retry budget is exhausted, the
runtime emits `RetryExhaustedEvent` and applies `OnRetryExhausted`. If a retry
delay cannot fit into the remaining stage budget, retry is not scheduled.
`AttemptTimeout` remains per-attempt. Effective attempt timeout remains
`min(AttemptTimeout, remaining StageTimeout)`. `PipelineTimeout` and sink
timeout/retry policy remain out of scope.

`StageFailureOptions.Retry` is also enforced for typed transformer stages. A
retryable failure emits `StageFailedEvent`, `RetryScheduledEvent`, updates the
envelope attempt count, emits `RetryAttemptedEvent`, and then invokes the same
stage again. When the retry budget is exhausted, the runtime emits
`RetryExhaustedEvent` and applies `OnRetryExhausted` once.

The runtime does not dispose stage dead-letter streams. Storage lifetime,
rotation, encryption, and payload redaction remain application-owned decisions.

## Circuit Breaker (typed runtime update4)

Typed transformer stages can configure a circuit breaker policy:

```csharp
var run = PipelineBuilder
    .From(source)
    .Transform(
        transformer,
        new StageFailureOptions
        {
            CircuitBreaker = new CircuitBreakerPolicy
            {
                FailureThreshold = 5,
                BreakDuration = TimeSpan.FromSeconds(30),
            },
            OnPermanentFailure = FailureAction.Skip,
        })
    .Run();
```

Each stage with a `CircuitBreakerPolicy` owns an independent breaker instance
scoped to the run. The breaker is checked before every attempt (initial and
retry). When open, the transformer is not called — the item is rejected with a
permanent `SmartPipeError` (category `"CircuitBreaker"`) and follows
`OnPermanentFailure`.

The breaker records every failed attempt (including retries) as a failure and
every successful attempt as a success. Breaker-open rejection is terminal and
does not invoke retry.

The runtime emits `CircuitBreakerOpenedEvent` when the breaker opens and
`CircuitBreakerRejectedEvent` for each rejected item. Half-open behavior is
driven by `BreakDuration` and respects the existing `CircuitBreaker` state
machine.

Legacy `SmartPipeChannel` circuit breaker is unchanged. Sink and
`PipelineTimeout` circuit breaker remain future hardening.
