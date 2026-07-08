# Observers

Observers receive typed runtime events for pipeline, stage, retry,
circuit-breaker, dead-letter, sink, and observer-dispatch activity.

Register observers through the typed builder:

```csharp
var run = PipelineBuilder
    .From(source)
    .Transform(stage)
    .WithObserver(
        observer,
        ObserverReliability.Critical,
        ObserverFailurePolicy.FaultPipeline)
    .To(sink);
```

## Dispatch Modes

`ObserverDispatchOptions.Inline` is the default. Inline observers execute on
the runtime path and critical failures can fault the run immediately.

Buffered dispatch uses bounded observer channels:

```csharp
var options = new PipelineRuntimeOptions
{
    ObserverDispatch = new ObserverDispatchOptions
    {
        Mode = ObserverDispatchMode.BufferedReliable,
        Capacity = 1024,
        FullMode = BoundedChannelFullMode.Wait,
        FailureMode = ObserverFailureMode.UseRegistrationPolicy,
        FlushOnCompletion = true,
        BestEffortWriteTimeout = TimeSpan.FromMilliseconds(100),
        EmitDroppedObserverEvents = true,
    },
};
```

`BufferedReliable` requires `FullMode = BoundedChannelFullMode.Wait` and
`FlushOnCompletion = true`. It flushes already queued observer events before
the runtime publishes the final run outcome. Registration-level or critical
observer failures observed during that flush fault the run and preserve the
original observer exception, except user-thrown `OperationCanceledException` is
wrapped as an observer dispatch failure so it is not mistaken for pipeline
shutdown. Expected shutdown cancellation from the dispatcher token remains
cancellation, not an observer failure.

`BufferedBestEffort` is for diagnostics that must not block the pipeline. Lossy
drop full modes require `FlushOnCompletion = false`; a lossy queue cannot
guarantee completion flush delivery. Observer flush uses an internal control
message and is never delivered to observers as a `PipelineEvent`.
`FlushOnCompletion = false` affects `CompleteAsync` only. `DisposeAsync` stops
and awaits the buffered worker before returning. Observer callbacks should
observe cancellation tokens so shutdown remains bounded.

The runtime sends at most one terminal pipeline event for a run:
`PipelineCompletedEvent`, `PipelineCancelledEvent`, or `PipelineFaultedEvent`.
Terminal-event delivery happens after the runtime has published the terminal
state and completed the output channel. Observer delivery or teardown failures
at that point are cleanup diagnostics only: they do not fault
`PipelineRun<T>.Completion` or rewrite the already published terminal state or
output-channel outcome.

Buffered best-effort pressure is observable. Dropped observer events increment
`smartpipe.observer.events.dropped`. When `EmitDroppedObserverEvents` is true,
the dispatcher also tries to publish `ObserverEventDroppedEvent`; the metric is
the reliable signal when the observer queue is already full.

When an observer failure is ignored or removes the failed observer, the
dispatcher emits `ObserverFailedEvent` to the remaining observers on a
best-effort basis. Failure notifications are not sent back to the failed
observer and do not recurse indefinitely.

## Rules

- Observers are diagnostic hooks, not lifecycle synchronization primitives.
- Use bounded buffered dispatch when observer work may block.
- Critical observer failures should be reserved for telemetry required to trust
  the run.
- Observer handlers should avoid throwing for ordinary logging/export failures
  unless the configured policy intentionally faults the pipeline.

## Circuit Breaker And Filter Events

The circuit breaker event contract includes:

- `CircuitBreakerOpenedEvent`
- `CircuitBreakerClosedEvent`
- `CircuitBreakerRejectedEvent`

Filtered items emit `ItemFilteredEvent`. Filtering is non-failure terminal
control flow, so a filtered item does not emit `StageFailedEvent`.

Bounded input and output drop policies emit `InputDroppedEvent` and
`OutputDroppedEvent` when the runtime channel callback reports a dropped item.
