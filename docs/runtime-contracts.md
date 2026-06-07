# Runtime Contracts

SmartPipe.Core is an in-process, envelope-aware typed pipeline runtime with a
legacy compatibility path. These contracts describe current 1.1.0 behavior.

## Defaults

- Output remains default-compatible and unbounded unless `OutputCapacity` is
  configured.
- Typed output mode defaults to `EmitAll`.
- Typed runtime concurrency defaults to `MaxDegreeOfParallelism = 1`.
- Observer dispatch is inline by default.
- `CircuitBreakerEvaluationMode.CompatibilityThreshold` is the default circuit
  breaker mode.
- `CircuitBreakerEvaluationMode.FailureRatio` is an opt-in sampling mode.
- Retry behavior is configured separately from circuit breaker behavior.
- Sink failure behavior is unchanged by runtime options.

## Component Ownership

Typed runtime-owned source, transformer, and sink instances are disposed once
when a run completes, faults, or is cancelled. Components are considered
runtime-owned unless they implement `IPipelineComponentDescriptor` and declare
`PipelineComponentLifetime.SingletonExternal`.

`SingletonExternal` components are treated as externally owned and are not
disposed by default. Set `ComponentOwnershipOptions.DisposeExternalComponents`
only when the pipeline runtime is explicitly responsible for disposing those
externally described components.

Factory-based registrations create fresh runtime-owned instances for each run.
Use factory APIs or components that declare `Reusable` or `SingletonExternal`
when a pipeline definition must create more than one runtime.

## Bounded Output

If `OutputCapacity` is configured and `OutputFullMode` is `Wait`, callers must
consume `PipelineRun<T>.Outputs`. The runtime writes to the output stream before
an attached sink, so unread bounded outputs can apply backpressure before sink
write unless output emission is suppressed by `PipelineOutputMode`.

`PipelineOutputMode` applies only at the typed output channel write boundary.
Sink writes, observer events, retry, and failure routing are independent of
output mode.

## Typed Runtime Concurrency

Typed `PipelineRuntimeOptions.MaxDegreeOfParallelism` defaults to `1`, which
keeps the sequential runtime path. Values greater than `1` process multiple
accepted envelopes concurrently through a bounded internal buffer.

The stage chain inside a single envelope remains sequential. Sink writes are
serialized by default because attached sinks are not assumed to be thread-safe.
`FailureAction.StopPipeline` stops new source acceptance and completes already
accepted work; it does not blindly cancel in-flight envelopes. Cross-envelope
output ordering is not guaranteed under typed concurrency.

## Channel Contracts

`ChannelPool.RentBounded<T>` remains a public compatibility API and keeps its
legacy assumptions. New runtime-internal paths use factories that match their
actual reader and writer cardinality.

Legacy `SmartPipeChannel` shared input uses a multi-reader, multi-writer bounded
channel because `MaxDegreeOfParallelism` can create multiple workers. Legacy
runtime output uses a single-reader, multi-writer bounded channel because
multiple workers can publish results while one output consumer reads them.

`BoundedChannelFullMode.Wait` provides backpressure and is the only bounded mode
where accepted items are expected to be retained under pressure. Lossy bounded
modes such as `DropWrite`, `DropOldest`, and `DropNewest` may drop items when
capacity is exhausted.

## Adaptive Parallelism Contract

Legacy `SmartPipeChannel` adaptive parallelism is opt-in and disabled by
default. Adaptive mode uses bounded input lanes plus a separate in-flight
limiter. A permit is acquired before a worker reads an item, so the configured
in-flight budget bounds concurrent item processing.

Adaptive mode changes the number of active input lanes conservatively. Decreased
lanes are not completed and their buffered items are not discarded; inactive
lane backlog remains readable until drained. Writes route only to active lanes.

Adaptive queue pressure is approximate. Runtime snapshots separate active
buffered items, inactive buffered items, and total buffered items. Total
buffered items includes inactive drain backlog. Controller decisions may lag
behind workload changes because they honor sampling and cooldown windows.

Adaptive mode requires `BoundedChannelFullMode.Wait` and rejects `JumpHash` in
1.1. It does not change retry policy, source replay, durability, ThreadPool
settings, storage, or synchronization behavior.

## Metrics Snapshot

`SmartPipeMetrics.CaptureSnapshot()` returns an observational sample of the
current counters and current-state values such as queue size and pool hit rate.
The snapshot is safe for export and reporting, and `Export()`, `ExportJson()`,
and `ExportPrometheus()` preserve their existing output shape by exporting a
sampled view.

The snapshot is not transactional. It does not synchronize concurrent pipeline
updates and should not be used as a coordination primitive or as a replacement
for an external telemetry recorder.

## Diagnostics Contracts

Runtime activities use the `SmartPipe.Core` `ActivitySource`. The legacy
channel emits `Pipeline.Run` for a run and item processing activities such as
`Transform`; activities may carry correlation tags such as
`smartpipe.trace_id` and low-cardinality runtime tags such as
`smartpipe.parallelism`.

Runtime instruments use the `SmartPipe.Core` `Meter`. Current instrument names
are `smartpipe.items.processed` (`items`), `smartpipe.items.failed` (`items`),
`smartpipe.duplicates.filtered` (`items`), `smartpipe.retries` (`retries`), and
`smartpipe.latency` (`ms`). The `Meter` surface currently publishes counters
and a latency histogram; it does not publish observable gauges in 1.1. Meter
measurements must not include high-cardinality dimensions such as trace id, run
id, item id, payload values, exception messages, or user data. The runtime does
not claim an external OpenTelemetry exporter integration in 1.1.

## Observer Dispatch

Inline observer dispatch preserves current event ordering and observer failure
notification. Non-critical inline observer failures emit `ObserverFailedEvent`
to the remaining observers.

Buffered observer modes are opt-in and bounded. `BufferedReliable` requires
`FlushOnCompletion = true`. Buffered observer failures are controlled by
`ObserverFailureMode.UseRegistrationPolicy`, `Ignore`, or `FaultPipeline`.
Global `FaultPipeline` faults the run for any observer failure. Global `Ignore`
ignores observer failures, including critical and registration-level
`FaultPipeline` failures. `UseRegistrationPolicy` follows each observer
registration: registration-level `FaultPipeline` and `Critical` reliability
fault the run, while `RemoveObserver` removes only non-critical observers after
a failure. Removal does not cancel an observer callback already in progress.
Buffered dispatch does not emit or recursively propagate inline-equivalent
`ObserverFailedEvent` diagnostics for observer callback failures.

`ObserverQueueOverflowPolicy` is a shipped domain-level enum reserved for a
future observer overflow API. In 1.1.0 buffered observer overflow is configured
with `ObserverDispatchOptions.FullMode` / `BoundedChannelFullMode`.

## Circuit Breaker And Retry

`CompatibilityThreshold` preserves the current threshold-compatible default
behavior without promising a strict consecutive-failure counter. `FailureRatio`
uses `FailureRatio`, `SamplingDuration`, `MinimumThroughput`, `BreakDuration`,
and `MaxHalfOpenRequests`.

Circuit breaker rejection is a transient stage failure with category
`CircuitBreaker`. Retry remains a separate `RetryPolicy` on
`StageFailureOptions`; configure retry delays with the breaker recovery duration
in mind.

## DrainAsync

Legacy `SmartPipeChannel.DrainAsync` is a graceful compatibility-runtime
operation that stops accepting new work and waits for accepted work.

Typed `PipelineRun<T>.DrainAsync` requests source-boundary drain. It stops
requesting new source items, completes already accepted work, and waits for the
run task until completion, timeout, or external cancellation.

Accepted work means a source item has been yielded to the runtime and handed
into the typed processing path. Drain is graceful, not an abort operation. If a
source is already blocked inside `MoveNextAsync`, drain cannot interrupt that
await by itself and may wait until the source cooperates. Use cancel or abort
for immediate interruption.

## Replay And Durability

SmartPipe.Core has no exactly-once claim and no durability claim. It does not
provide LocalFirst behavior, SQLite persistence, outbox/inbox processing, sync,
checkpoints, or hidden source replay/materialization.

Typed retry retries the current in-memory envelope attempt. It does not
materialize the source for restart replay. Replay must be explicit through
application-owned sources, sinks, and dead-letter handling.

## Scope Boundary

Core does not contain connectors. Connector integrations belong in
SmartPipe.Extensions or future extension packages. SmartPipe.Core's current
advantage is envelope-aware typed runtime behavior by default; it does not
claim a separate lineage system beyond the envelope and lineage data carried by
the runtime.
