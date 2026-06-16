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

`BufferedReliable` flushes during completion and preserves the original
observer exception when a registration-level or critical observer failure
faults the run. `BufferedBestEffort` is for diagnostics that must not block the
pipeline.

Buffered best-effort pressure is observable. Dropped observer events increment
`smartpipe.observer.events.dropped`. When `EmitDroppedObserverEvents` is true,
the dispatcher also tries to publish `ObserverEventDroppedEvent`; the metric is
the reliable signal when the observer queue is already full.

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
