# Runtime Contracts

SmartPipe.Core is an in-process, envelope-aware typed pipeline runtime with a
legacy compatibility path. These contracts describe current 1.1.0 behavior.

## Defaults

- Output remains default-compatible and unbounded unless `OutputCapacity` is
  configured.
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
write. For sink-only worker scenarios in 1.1.0, keep `OutputCapacity = null`.

## Observer Dispatch

Inline observer dispatch preserves current event ordering and observer failure
notification. Non-critical inline observer failures emit `ObserverFailedEvent`
to the remaining observers.

Buffered observer modes are opt-in and bounded. `BufferedReliable` requires
`FlushOnCompletion = true`. Buffered observer failures are controlled by
`ObserverFailureMode.UseRegistrationPolicy`, `Ignore`, or `FaultPipeline`.
Buffered dispatch does not claim full inline-equivalent recursive
`ObserverFailedEvent` propagation.

## Circuit Breaker And Retry

`CompatibilityThreshold` preserves the current threshold-compatible default
behavior without promising a strict consecutive-failure counter. `FailureRatio`
uses `FailureRatio`, `SamplingDuration`, `MinimumThroughput`, `BreakDuration`,
and `MaxHalfOpenRequests`.

Circuit breaker policy does not manage retry. Retry remains a separate
`RetryPolicy` on `StageFailureOptions`.

## DrainAsync

Legacy `SmartPipeChannel.DrainAsync` is a graceful compatibility-runtime
operation that stops accepting new work and waits for accepted work.

Typed `PipelineRun<T>.DrainAsync` in 1.1.0 is a completion-wait helper. It waits
for run/output completion or timeout. It does not independently stop source
enumeration unless the source cooperates through cancellation or natural
completion.

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
