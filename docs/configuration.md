# Configuration

This document lists the typed runtime configuration surface.

Default `OutputPolicy` is `SuppressSuccessWhenSinkAttached`.

This is the safe default for sink-backed pipelines because successful outputs are not written to `PipelineRun<T>.Outputs` unless the caller explicitly opts into `EmitAll`.

Use `EmitAll` only when the caller actively consumes `PipelineRun<T>.Outputs`.

## Runtime Options

`PipelineRuntimeOptions` controls runtime behavior:

| Option | Default | Notes |
|---|---|---|
| `MaxConcurrency` | `1` | Preferred typed-only concurrency setting. |
| `MaxDegreeOfParallelism` | `1` | Obsolete compatibility alias; use `MaxConcurrency`. Conflicting non-default values are rejected. |
| `InputCapacity` | `1024` | Bounded input queue capacity. |
| `InputFullMode` | `Wait` | Input queue full-mode behavior. |
| `OutputCapacity` | `null` | Automatic bounded default when null. |
| `OutputFullMode` | `Wait` | Output queue full-mode behavior. |
| `OutputPolicy` | `SuppressSuccessWhenSinkAttached` | Preferred typed output policy. |
| `OutputMode` | `EmitAll` | Obsolete compatibility output filter used only when explicitly set without `OutputPolicy`; use `OutputPolicy`. |
| `OrderingMode` | `Unordered` | `PreserveInputOrder` with parallelism is rejected. |
| `ObserverDispatch` | `Inline` | Inline or bounded buffered observer dispatch. |
| `Clock` | `SystemPipelineClock.Instance` | Timestamps and timeout budgets. |

All runtime channels are bounded. `BoundedChannelFullMode.Wait` is the safe
default because it applies backpressure instead of dropping accepted work.
When lossy input, output, or observer full modes are configured, the runtime
records drop metrics and emits best-effort drop events.

## Output Policy

`PipelineOutputPolicy` values:

- `EmitAll`
- `EmitFailuresOnly`
- `SuppressSuccessWhenSinkAttached`
- `SuppressAllWhenSinkAttached`

The default `SuppressSuccessWhenSinkAttached` prevents sink-backed runs from
blocking on unconsumed success outputs. Set `EmitAll` explicitly when a
sink-backed run also needs a consumer for every successful output.

Output policy only controls `PipelineRun<T>.Outputs`. Sink writes, observers,
retry, circuit breaker, and dead-letter behavior are independent.

For sink-backed pipelines, success output is emitted only after the sink write
succeeds. If the sink throws, no success output is published for that item.

`OutputMode` is retained only as a compatibility alias. If `OutputMode` is
explicitly set and `OutputPolicy` is not, the runtime applies the legacy
`OutputMode` behavior. If both are explicitly set to incompatible behaviors,
validation fails.

## Stage Failure Options

`StageFailureOptions` is configured per transform stage:

| Option | Default |
|---|---|
| `Retry` | `null` |
| `Timeout` | `null` |
| `CircuitBreaker` | `null` |
| `OnPermanentFailure` | `EmitFailureResult` |
| `OnRetryExhausted` | `EmitFailureResult` |

`RetryPolicy` retries transient stage failures according to `MaxRetries` and
backoff settings. `TimeoutPolicy` can set per-attempt and whole-stage budgets.
`CircuitBreakerPolicy` supports threshold-compatible and failure-ratio modes.

Open-breaker rejection is terminal for the current item and is not retried back
into the open breaker.

`MaxHalfOpenRequests` is a concurrent half-open probe limit. Runtime half-open
probe slots are released when the probe attempt completes.

## Observers

`ObserverDispatchOptions.Inline` is the default. Buffered modes use bounded
channels and can fault, ignore, or remove observers according to
`ObserverFailureMode` and registration policy.

| Observer option | Default | Notes |
|---|---|---|
| `BestEffortWriteTimeout` | `100 ms` | Maximum wait before `BufferedBestEffort` counts a `Wait`-mode observer event as dropped. |
| `EmitDroppedObserverEvents` | `true` | Tries to publish `ObserverEventDroppedEvent`; `smartpipe.observer.events.dropped` is the reliable pressure signal. |

## Metrics

`SmartPipeMetricsRecorder` owns mutable metric state and
`SmartPipeMetricsSnapshot` is immutable. Use `CaptureSnapshot()` for reporting.
The runtime also publishes `Meter` instruments under `SmartPipe.Core`.

Drop observability uses `smartpipe.items.dropped`,
`smartpipe.output.items.dropped`, and `smartpipe.observer.events.dropped`.

## Hosted Service

`SmartPipeHostedServiceOptions` controls hosted lifecycle behavior:

| Option | Default | Notes |
|---|---|---|
| `FailureBehavior` | `StopApplication` | Requests host shutdown on pipeline fault. `Rethrow`, `MarkUnhealthyAndKeepHostAlive`, and `Ignore` are explicit alternatives. |
| `DrainTimeout` | `30 seconds` | Timeout passed to `PipelineRun<T>.DrainAsync` during `StopAsync`. |

If `StopApplication` is selected but no `IHostApplicationLifetime` is
available, the hosted service rethrows instead of silently completing.

## DI

`SmartPipe.Extensions` registers immutable definitions and per-run factories:

```csharp
services.AddSmartPipe<TInput, TOutput>(
    "pipeline-id",
    builder => builder
        .UseSource<TSource>()
        .UseStage<TStage>()
        .UseSink<TSink>());
```

`ISmartPipeFactory<TInput,TOutput>.Start()` creates a fresh DI scope and a
fresh runtime.
