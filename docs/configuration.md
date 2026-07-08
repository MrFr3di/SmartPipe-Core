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
| `InputCapacity` | `1024` | Bounded input queue capacity. |
| `InputFullMode` | `Wait` | Input queue full-mode behavior. |
| `OutputCapacity` | `null` | Automatic bounded default when null. |
| `OutputFullMode` | `Wait` | Output queue full-mode behavior. |
| `OutputPolicy` | `SuppressSuccessWhenSinkAttached` | Typed output policy. |
| `OrderingMode` | `Unordered` | Cross-item output ordering is not guaranteed. |
| `ObserverDispatch` | `Inline` | Inline or bounded buffered observer dispatch. |
| `AdaptiveParallelism` | `disabled` | Opt-in adaptive admission control for parallel envelope processing; requires `InputFullMode = Wait`. |
| `Clock` | `SystemPipelineClock.Instance` | Timestamps, monotonic durations, and timeout budgets. `TimeProviderPipelineClock` also drives runtime retry delay and timeout waits through its provider. |

All runtime channels are bounded. `BoundedChannelFullMode.Wait` is the safe
default because it applies backpressure instead of dropping accepted work.
When lossy input, output, or observer full modes are configured, the runtime
records drop metrics and emits best-effort drop events.

## Adaptive Parallelism

Adaptive parallelism is disabled by default. Enable it with
`PipelineRuntimeOptions.AdaptiveParallelism.Enabled = true`.

Adaptive parallelism applies only when the effective `MaxConcurrency` is greater
than `1`, and it requires `InputFullMode = BoundedChannelFullMode.Wait` so input
backpressure remains lossless. Runtime `MaxConcurrency` remains the hard cap.
`AdaptiveParallelism.MaxConcurrency` is an additional adaptive cap, so the
effective adaptive maximum is:

```text
min(runtime effective MaxConcurrency, AdaptiveParallelism.MaxConcurrency)
```

Adaptive admission changes how many envelopes are admitted to processing at the
same time. The stage chain inside one envelope remains sequential. With parallel
processing, cross-envelope output order is still not guaranteed.

The controller reacts to per-envelope completion latency, interval failure
ratio, target latency, dead zone, adjustment cooldown, and configured min/max
concurrency bounds. `EvaluationInterval` controls how often completion samples
are evaluated. `AdjustmentCooldown` is the minimum elapsed time between
adaptive limit changes; `Cooldown` remains as a compatibility alias. The current
model is completion-based: the runtime records each envelope completion and
does not run a background sampling loop or periodic timer.
Retry attempts remain observable through retry metrics and events, but retry
counts are not adaptive admission signals.

| Option | Default | Notes |
|---|---:|---|
| `Enabled` | `false` | Enables adaptive admission. |
| `MinConcurrency` | `1` | Lower bound for adaptive admission limit. |
| `MaxConcurrency` | `Environment.ProcessorCount` | Adaptive upper bound, still capped by runtime `MaxConcurrency`. |
| `InitialConcurrency` | `1` | Initial adaptive admission limit. |
| `TargetLatency` | `100 ms` | Desired per-envelope processing latency. |
| `DeadZone` | `5 ms` | Latency band around target where no limit change is made. |
| `EvaluationInterval` | `1 second` | Completion-sample interval used for adaptive decisions. |
| `AdjustmentCooldown` | `1 second` | Minimum elapsed time between adaptive limit changes. |
| `Cooldown` | `1 second` | Obsolete compatibility alias for `AdjustmentCooldown`. |
| `MaxAdjustmentStep` | `1` | Maximum limit change per controller decision. |
| `FailurePressureThreshold` | `0.10` | Interval failure ratio threshold that prevents growth and reduces concurrency. |
| `MinimumFailureSamples` | `10` | Minimum processed samples before interval failure ratio can reduce concurrency. |
| `MinSmoothingFactor` | `0.2` | Lower bound for latency smoothing factor. |

```csharp
var options = new PipelineRuntimeOptions
{
    MaxConcurrency = 8,
    InputFullMode = BoundedChannelFullMode.Wait,
    AdaptiveParallelism = new AdaptiveParallelismOptions
    {
        Enabled = true,
        MinConcurrency = 1,
        MaxConcurrency = 8,
        InitialConcurrency = 2,
        TargetLatency = TimeSpan.FromMilliseconds(100),
        DeadZone = TimeSpan.FromMilliseconds(10),
        EvaluationInterval = TimeSpan.FromSeconds(1),
        AdjustmentCooldown = TimeSpan.FromSeconds(1),
        MaxAdjustmentStep = 1,
        FailurePressureThreshold = 0.10,
        MinimumFailureSamples = 10,
        MinSmoothingFactor = 0.2,
    },
};
```

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

### Output Filtering API Deprecation

`PipelineOutputPolicy` is the canonical output filtering API for new code.

`PipelineOutputMode` and `PipelineRuntimeOptions.OutputMode` are compatibility
APIs retained for existing callers. They remain supported in the current major
version, but new code should use `PipelineRuntimeOptions.OutputPolicy`.

#### Migration Map

| Old `PipelineOutputMode` | New `PipelineOutputPolicy` | Notes |
|---|---|---|
| `EmitAll` | `EmitAll` | Emits all processing results to the output channel. |
| `FailuresOnlyWhenSinkAttached` | `EmitFailuresOnly` when failures-only behavior is desired | The old value had sink-aware fallback semantics. Verify behavior before migrating. |
| `SuppressWhenSinkAttached` | `SuppressAllWhenSinkAttached` | Suppresses output channel results when a sink is attached. |
| `SuppressAll` | No exact `OutputPolicy` equivalent | Keep compatibility mode until a canonical replacement is introduced. |

#### Conflict Rule

If both `OutputMode` and `OutputPolicy` are configured, they must describe
equivalent output behavior. Non-equivalent combinations are rejected by runtime
option validation. Today, only `EmitAll`/`EmitAll` and
`SuppressWhenSinkAttached`/`SuppressAllWhenSinkAttached` are treated as exact
equivalents.

#### Deprecation Timeline

- Current minor/stabilization releases: `OutputMode` remains available as an
  obsolete compatibility API.
- Next major release candidate: the project may consider promoting obsolete
  usage to an error after a documented migration window.
- Future major release only: the project may remove `OutputMode` and the
  compatibility runtime branch.

No removal is planned in a patch or minor stabilization release.

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
backoff settings. `OnRetry` runs after the retry delay and before the next
attempt starts; callback failures fault the run. `TimeoutPolicy` can set
per-attempt and whole-stage budgets. `CircuitBreakerPolicy` supports
threshold-compatible and failure-ratio modes.

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
| `Mode` | `Inline` | `BufferedReliable` requires `FullMode = Wait` and `FlushOnCompletion = true`. |
| `FullMode` | `Wait` | Lossy drop modes are allowed only for `BufferedBestEffort` with `FlushOnCompletion = false`. |
| `FlushOnCompletion` | `true` | Completion flush is guaranteed only for non-lossy observer queues. |
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
