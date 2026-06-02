# Configuration

This document lists implemented configuration APIs only.

## SmartPipeChannelOptions

`SmartPipeChannelOptions` configures the legacy `SmartPipeChannel` runtime.

| Option | Type | Default | Notes |
|---|---|---|---|
| `MaxDegreeOfParallelism` | `int` | `Environment.ProcessorCount` | Maximum number of parallel consumers. |
| `BoundedCapacity` | `int` | `1000` | Capacity used by bounded channels. |
| `ContinueOnError` | `bool` | `true` | Continue on non-fatal item failures. |
| `TotalRequestTimeout` | `TimeSpan` | `5 minutes` | Legacy total run timeout setting. Do not treat this as typed `PipelineTimeout` support. |
| `AttemptTimeout` | `TimeSpan` | `30 seconds` | Legacy per-transform attempt timeout. |
| `UseRendezvous` | `bool` | `false` | Capacity-zero mode. Current option validation rejects it when the bounded-capacity constraint is enforced. |
| `FullMode` | `BoundedChannelFullMode` | `Wait` | Bounded channel full behavior. |
| `OnMetrics` | `Action<SmartPipeMetrics>?` | `null` | Metrics callback. |
| `ThrowOnMutationAfterStart` | `bool` | `false` | When true, mutation after start throws. Default remains false for compatibility. |
| `DeduplicationFilter` | `DeduplicationFilter?` | `null` | Optional input deduplication. |
| `OnProgress` | `Action<int,int?,TimeSpan,TimeSpan?>?` | `null` | Progress callback after each item. |
| `DeadLetterSink` | `ISink<object>?` | `null` | Legacy sink for exhausted retries or permanent errors. |
| `DefaultRetryPolicy` | `RetryPolicy?` | `null` | Legacy default retry policy. Null falls back to 3 retries with 1 second delay. |
| `RetryQueueOverflowPolicy` | `RetryQueueOverflowPolicy` | `Wait` | Legacy bounded retry queue overflow behavior. |
| `FeatureFlags` | `Dictionary<string,bool>` | see below | Optional components and diagnostics. |

Default feature flags:

| Flag | Default |
|---|---|
| `RetryQueue` | `false` |
| `Metrics` | `true` |
| `CircuitBreaker` | `false` |
| `ObjectPool` | `false` |
| `DebugSampling` | `false` |
| `CuckooFilter` | `false` |
| `JumpHash` | `false` |
| `SecretScanner` | `false` |

Use `EnableFeature(name)`, `DisableFeature(name)`, and `IsEnabled(name)` for
feature flags.

## Backpressure

Legacy backpressure is based on bounded channels. `BoundedCapacity` controls
queue size and `FullMode` controls what writers do when capacity is reached.
The safe default is `BoundedChannelFullMode.Wait`.

## Retry

`DefaultRetryPolicy` configures the legacy runtime's transient retry behavior.
`RetryQueueOverflowPolicy` controls behavior when the legacy retry queue is full:

- `Wait`: block until capacity is available;
- `FailFast`: surface overflow as terminal failure;
- `DeadLetter`: write to `DeadLetterSink` when available, otherwise terminal
  failure;
- `DropNewest`: drop the incoming retry item;
- `DropOldest`: drop the oldest queued retry item.

Dropping policies are lossy and must be explicit.

## Drain And Cancel

`DrainAsync` stops accepting new work and waits for already accepted work to
finish. It is not an abort operation. Use `Cancel()` for immediate stop.

`RunInBackground` can be called once per `SmartPipeChannel` instance.

## Typed Stage Failure Options

Typed transformer stages can receive `StageFailureOptions`:

| Option | Type | Default |
|---|---|---|
| `Retry` | `RetryPolicy?` | `null` |
| `Timeout` | `TimeoutPolicy?` | `null` |
| `CircuitBreaker` | `CircuitBreakerPolicy?` | `null` |
| `OnPermanentFailure` | `FailureAction` | `EmitFailureResult` |
| `OnRetryExhausted` | `FailureAction` | `EmitFailureResult` |

`TimeoutPolicy` supports:

- `AttemptTimeout`: one transformer attempt;
- `StageTimeout`: the whole stage including retries and retry delays.

`CircuitBreakerPolicy` supports:

- `EvaluationMode`: default `ConsecutiveFailures`;
- `FailureThreshold`: default `5`, used by the default consecutive-failure mode;
- `BreakDuration`: default `30 seconds`;
- `FailureRatio`: default `0.1`, used only by opt-in `FailureRatio` mode;
- `SamplingDuration`: default `30 seconds`, used only by opt-in `FailureRatio` mode;
- `MinimumThroughput`: default `100`, used only by opt-in `FailureRatio` mode;
- `MaxHalfOpenRequests`: default `1`, used only by opt-in `FailureRatio` mode.

`FailureAction` values are:

- `EmitFailureResult`;
- `DeadLetter`;
- `Skip`;
- `StopPipeline`;
- `FaultPipeline`.

## Observer Options

Typed pipelines attach observers with:

```csharp
builder.WithObserver(observer, reliability, failurePolicy);
```

Implemented reliability values are `BestEffort`, `Reliable`, and `Critical`.
Implemented failure policies are `Ignore`, `Log`, `FaultPipeline`, and
`RemoveObserver`.

## Pipeline Identity

Envelope-aware typed pipelines can set an operational pipeline id:

```csharp
var run = PipelineBuilder
    .From(source)
    .WithPipelineId("orders-sync")
    .Transform(transformer)
    .Run();
```

If `WithPipelineId` is not called, current generated pipeline id behavior is
preserved. If it is called, observer events and normalized output envelopes use
the configured id.

## Runtime Options

`PipelineRuntimeOptions` configures opt-in typed runtime behavior:

| Option | Default | Notes |
|---|---|---|
| `OutputCapacity` | `null` | Null preserves current unbounded output behavior. A value creates a bounded output channel. |
| `OutputFullMode` | `Wait` | Used only when `OutputCapacity` is set. |
| `ObserverDispatch` | `ObserverDispatchOptions.Inline` | Inline dispatch preserves current event ordering and failure behavior. |
| `Clock` | `SystemPipelineClock.Instance` | Used by typed runtime event timestamps and time budget decisions. |

Bounded output with `Wait` can apply backpressure if consumers do not read
outputs. It is intentionally not the default.

## Clock / Time Provider

Typed runtime options accept `IPipelineClock`. `SystemPipelineClock` is the
default. `TimeProviderPipelineClock` adapts a .NET `TimeProvider` for tests and
custom time sources.

## Observer Dispatch

`ObserverDispatchOptions` supports:

- `Inline`: default, preserves current dispatch behavior;
- `BufferedBestEffort`: bounded background queue where dropped events are
  allowed when configured by full-mode behavior;
- `BufferedReliable`: bounded background queue intended to flush before
  completion when `FlushOnCompletion` is true.

Buffered modes are opt-in and bounded. They do not introduce unbounded
fire-and-forget observer queues.
