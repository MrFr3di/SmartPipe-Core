# Configuration

This document lists the typed runtime configuration surface.

## Runtime Options

`PipelineRuntimeOptions` controls runtime behavior:

| Option | Default | Notes |
|---|---|---|
| `MaxConcurrency` | `1` | Preferred typed-only concurrency setting. |
| `MaxDegreeOfParallelism` | `1` | Compatibility alias; conflicting non-default values are rejected. |
| `InputCapacity` | `1024` | Bounded input queue capacity. |
| `InputFullMode` | `Wait` | Input queue full-mode behavior. |
| `OutputCapacity` | `null` | Automatic bounded default when null. |
| `OutputFullMode` | `Wait` | Output queue full-mode behavior. |
| `OutputPolicy` | `EmitAll` | Preferred typed output policy. |
| `OutputMode` | `EmitAll` | Compatibility output filter. |
| `OrderingMode` | `Unordered` | `PreserveInputOrder` with parallelism is rejected. |
| `ObserverDispatch` | `Inline` | Inline or bounded buffered observer dispatch. |
| `Clock` | `SystemPipelineClock.Instance` | Timestamps and timeout budgets. |

All runtime channels are bounded. `BoundedChannelFullMode.Wait` is the safe
default because it applies backpressure instead of dropping accepted work.

## Output Policy

`PipelineOutputPolicy` values:

- `EmitAll`
- `EmitFailuresOnly`
- `SuppressSuccessWhenSinkAttached`
- `SuppressAllWhenSinkAttached`

Output policy only controls `PipelineRun<T>.Outputs`. Sink writes, observers,
retry, circuit breaker, and dead-letter behavior are independent.

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

## Observers

`ObserverDispatchOptions.Inline` is the default. Buffered modes use bounded
channels and can fault, ignore, or remove observers according to
`ObserverFailureMode` and registration policy.

## Metrics

`SmartPipeMetricsRecorder` owns mutable metric state and
`SmartPipeMetricsSnapshot` is immutable. Use `CaptureSnapshot()` for reporting.
The runtime also publishes `Meter` instruments under `SmartPipe.Core`.

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
