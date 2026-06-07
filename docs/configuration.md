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
| `AdaptiveParallelism` | `AdaptiveParallelismOptions` | disabled | Opt-in adaptive legacy runtime lane and in-flight budget control. |
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

## Dependency Injection

`SmartPipe.Extensions` keeps the existing `AddSmartPipe` and
`AddSmartPipeHostedService` compatibility APIs. P3 adds
`AddSmartPipeFactory<TInput,TOutput>()`, which registers
`ISmartPipeChannelFactory<TInput,TOutput>` as scoped.

Factory registrations create a fresh `SmartPipeChannel<TInput,TOutput>` for
each `Create()` call. Options supplied during registration are cloned so later
mutation of the original `SmartPipeChannelOptions` instance does not change
future factory-created pipelines. Pipeline configuration delegates run for each
created pipeline and receive the caller scope's `IServiceProvider`, so scoped
dependencies are resolved from the active scope rather than the root provider.

Use the factory path when a hosted service or scoped workflow needs a fresh
pipeline instance. The shipped constructor that accepts an existing
`SmartPipeChannel<TInput,TOutput>` remains supported.

## Backpressure

Legacy backpressure is based on bounded channels. `BoundedCapacity` controls
queue size and `FullMode` controls what writers do when capacity is reached.
The safe default is `BoundedChannelFullMode.Wait`, which applies backpressure
instead of dropping accepted work.

`ChannelPool.RentBounded<T>` is kept as a public compatibility API. The legacy
runtime does not use it for shared input/output channels; those paths use
internal channel factories whose reader and writer assumptions match the
runtime topology.

Lossy bounded modes can drop items under pressure:

- `DropWrite`: drops the item being written;
- `DropNewest`: drops the newest buffered item;
- `DropOldest`: drops the oldest buffered item.

Use lossy modes only when dropped work is acceptable and externally visible in
the caller's reliability model.

## Adaptive Parallelism

`AdaptiveParallelism` is disabled by default. When enabled, it controls the
legacy runtime's active input lanes and in-flight item budget. It does not
manage the .NET ThreadPool, durable queues, storage, sync, checkpoints, replay,
or retry policy.

Adaptive mode requires `FullMode = BoundedChannelFullMode.Wait`. Validation
rejects lossy bounded modes (`DropWrite`, `DropOldest`, `DropNewest`) because
the adaptive runtime is not allowed to discard accepted work while changing
lane counts.

Adaptive mode cannot be combined with `JumpHash` in 1.1. JumpHash routing is
rejected until partition routing happens before lane writes.

Key adaptive options:

| Option | Default | Notes |
|---|---|---|
| `Enabled` | `false` | Enables adaptive lane and in-flight control. |
| `MinDegreeOfParallelism` | `1` | Minimum active input lane count. |
| `MaxDegreeOfParallelism` | `Environment.ProcessorCount` | Maximum adaptive lane count. |
| `InitialDegreeOfParallelism` | `min(4, Environment.ProcessorCount)` | Initial active lane count. |
| `InitialInFlightItems` | `min(4, Environment.ProcessorCount)` | Initial in-flight item budget. Must be at least the initial lane count. |
| `MaxInFlightItems` | `Environment.ProcessorCount * 4` | Maximum in-flight item budget. |
| `SamplingInterval` | `1 second` | Controller sampling cadence. |
| `Cooldown` | `5 seconds` | Minimum interval between controller changes. |

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

Legacy `SmartPipeChannel.DrainAsync` stops accepting new work and waits for
already accepted work to finish. It is not an abort operation. Use `Cancel()`
for immediate stop.

Typed `PipelineRun<T>.DrainAsync` requests graceful source-boundary drain. It
stops requesting new source items, waits for already accepted work, and waits
for run completion or timeout. Accepted work is any source item already yielded
to the runtime and handed into the typed processing path. If a source is already
blocked inside `MoveNextAsync`, drain waits until the source cooperates or the
drain timeout/cancellation token fires; use cancel or abort for immediate
interruption.

`RunInBackground` can be called once per `SmartPipeChannel` instance.

## Metrics

`SmartPipeMetrics` keeps its public counters and export methods. Use
`CaptureSnapshot()` when exporting or reporting metrics from code that may run
concurrently with pipeline updates.

The snapshot is observational and safe to enumerate or serialize. It includes
counters plus current-state values such as queue size and pool hit rate. The
runtime `Meter` publishes counters and a latency histogram in 1.1. The snapshot
is not a transactional synchronization primitive and does not replace a
telemetry recorder.

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

- `EvaluationMode`: default `CompatibilityThreshold`;
- `FailureThreshold`: default `5`, used by the default compatibility threshold mode;
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
`RemoveObserver`. Global `ObserverFailureMode.Ignore` ignores observer failures
even for critical observers. With `UseRegistrationPolicy`, registration-level
`FaultPipeline` and `Critical` reliability take priority over `RemoveObserver`;
removal applies only to non-critical observers and affects events processed
after the observer is marked inactive.

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
| `OutputMode` | `EmitAll` | Controls which typed results are emitted to `PipelineRun<T>.Outputs`; sink writes, observers, retry, and failure routing are independent. |
| `MaxDegreeOfParallelism` | `1` | Maximum typed envelopes processed concurrently. `1` keeps the sequential path. |
| `ObserverDispatch` | `ObserverDispatchOptions.Inline` | Inline dispatch preserves current event ordering and failure behavior. |
| `Clock` | `SystemPipelineClock.Instance` | Used by typed runtime event timestamps and time budget decisions. |

`PipelineOutputMode` values:

- `EmitAll`: emit successful and failed typed outputs;
- `FailuresOnlyWhenSinkAttached`: when a sink is attached, emit only failures;
  without a sink, emit all outputs;
- `SuppressWhenSinkAttached`: when a sink is attached, suppress output channel
  emission; without a sink, emit all outputs;
- `SuppressAll`: suppress output channel emission even without a sink.

Typed `MaxDegreeOfParallelism` keeps stage order sequential inside each
envelope. With values greater than `1`, multiple envelopes may be processed at
the same time, sink writes are serialized by default, and same-trace stage event
order remains stage-local while cross-envelope output order is not guaranteed.

Bounded output with `Wait` can apply backpressure if consumers do not read
outputs. If `OutputCapacity` is configured and `OutputFullMode` is `Wait`,
callers must consume `run.Outputs` unless `OutputMode` suppresses output
emission enough for the workload. Bounded output is intentionally not the
default.

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

`BufferedReliable` requires `FlushOnCompletion = true`. Buffered observer
failure behavior uses `ObserverFailureMode.UseRegistrationPolicy`,
`ObserverFailureMode.Ignore`, or `ObserverFailureMode.FaultPipeline`.
Buffered dispatch does not emit inline-equivalent `ObserverFailedEvent`
diagnostics for observer callback failures.
Buffered overflow is configured with `ObserverDispatchOptions.FullMode` /
`BoundedChannelFullMode`. `ObserverQueueOverflowPolicy` is a shipped
domain-level enum reserved for a future observer overflow API.
