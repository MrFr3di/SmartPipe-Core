# Resilience

SmartPipe is an in-process pipeline library. It does not claim exactly-once
delivery. Delivery behavior depends on source, sink, retry, dead-letter, and
application idempotency choices.

## Delivery Guarantees

- Legacy and typed runtimes process items in memory.
- Retries can create at-least-once effects against non-idempotent sinks.
- Dropping retry overflow policies create at-most-once behavior for dropped
  retry items.
- Replay safety requires a dead-letter record that preserves the original
  payload and enough metadata to replay intentionally.

## Drain And Cancel

Legacy `SmartPipeChannel.DrainAsync` is a graceful compatibility-runtime
operation. It stops accepting new work and waits for accepted work to complete
through transformers and sinks.

Typed `PipelineRun<T>.DrainAsync` in 1.1.0 is a completion-wait helper. It
waits for run/output completion or timeout. It does not independently stop
source enumeration unless the source cooperates through cancellation or natural
completion.

`Cancel()` is the immediate stop operation.

## Stage Failure Actions

Typed stages use `StageFailureOptions` to choose terminal behavior:

- `EmitFailureResult`: emit one failed output and continue reading later items;
- `DeadLetter`: write a replay-safe envelope, emit one failed output, and
  continue;
- `Skip`: emit no output for the failed item and continue;
- `StopPipeline`: emit one failed output and stop reading new source items;
- `FaultPipeline`: fault `PipelineRun<T>.Completion` with
  `PipelineFailureActionException`.

Cancellation is not treated as transient, is not retried, and is not
dead-lettered by default.

## Timeouts

The typed runtime supports transformer-stage timeout policy:

- `AttemptTimeout`: one transformer attempt;
- `StageTimeout`: the whole stage including retries and retry delays.

Effective attempt timeout is the smaller of `AttemptTimeout` and remaining
`StageTimeout`. If a retry delay does not fit within the remaining stage budget,
the retry is not scheduled and `OnRetryExhausted` is applied.

Do not claim typed `PipelineTimeout` or sink timeout/retry support for 1.1.0.

## Retry

Typed retry applies to envelope-aware transformer stages when:

- `StageFailureOptions.Retry` is configured;
- the stage returns a retryable `SmartPipeError`;
- `RetryPolicy.ShouldRetry(error)` accepts the error;
- the item has not exhausted its retry budget.

The runtime emits retry events, updates `ProcessingEnvelope<T>.Attempt`, and
applies `OnRetryExhausted` exactly once when the retry budget ends.

Typed retry does not hide replay or materialization. The runtime retries the
current in-memory envelope attempt; it does not materialize the source or
provide hidden restart replay.

Legacy retry uses `SmartPipeChannelOptions.DefaultRetryPolicy` and the legacy
retry queue when the corresponding feature is enabled.

## Retry Queue Overflow

`RetryQueueOverflowPolicy` controls legacy bounded retry queue overflow:

- `Wait`: safe backpressure default;
- `FailFast`: terminal failure without enqueueing;
- `DeadLetter`: write to `DeadLetterSink` when configured;
- `DropNewest`: lossy drop of the incoming item;
- `DropOldest`: lossy drop of the oldest queued item.

## Circuit Breaker

Typed transformer stages can configure `CircuitBreakerPolicy`. Each configured
stage gets an independent breaker for each run.

The default evaluation mode is `CompatibilityThreshold`, preserving the
existing threshold and break-duration behavior without promising a strict
consecutive-failure counter.

The opt-in `FailureRatio` mode evaluates:

- `FailureRatio`;
- `SamplingDuration`;
- `MinimumThroughput`;
- `BreakDuration`;
- `MaxHalfOpenRequests`.

Ratio mode does not open before the sampling window has at least
`MinimumThroughput` samples. When the ratio threshold is met, the breaker opens,
rejects requests during `BreakDuration`, and then allows half-open probes using
the underlying circuit breaker implementation.

The breaker is checked before every transformer attempt, including retries. When
open, the transformer is not called. The item is rejected with a permanent
`SmartPipeError` in the `CircuitBreaker` category and follows
`OnPermanentFailure`. Breaker-open rejection is terminal and does not retry.

Circuit breaker policy does not manage retry. Retry remains configured through
`RetryPolicy` on the stage failure options.

Legacy `SmartPipeChannel` circuit breaker behavior is unchanged by typed runtime
stage policies.

## Dead Letter

Legacy `DeadLetterSink<T>` persists diagnostic `ProcessingResult<T>` records.
That legacy format is not replay-safe because failed results do not preserve the
original payload in a modern envelope.

Replay-safe dead-letter uses:

- `DeadLetterEnvelope<T>`;
- `IDeadLetterSerializer<T>`;
- `IDeadLetterRedactor<T>`;
- `JsonLinesDeadLetterSerializer<T>`.

For trim and NativeAOT scenarios, prefer the
`JsonLinesDeadLetterSerializer<T>` constructor that accepts
`JsonTypeInfo<DeadLetterEnvelope<T>>`.

## Observer Events

Typed pipelines can emit structured lifecycle, stage, sink, retry, circuit
breaker, dead-letter, and observer-failure events through `IPipelineObserver`.

Inline observer dispatch is the default. Best-effort observer failures are
reported through `ObserverFailedEvent`. Critical observers or registrations
configured with `FaultPipeline` can fault the run.

`PipelineRuntimeOptions.ObserverDispatch` can opt into bounded buffered
dispatch:

- `BufferedBestEffort`: reduces hot-path blocking risk and may drop events when
  configured with dropping full modes;
- `BufferedReliable`: uses a bounded queue and can apply backpressure when the
  queue is full.

Buffered observer failure behavior is controlled by `ObserverFailureMode`.
`UseRegistrationPolicy` follows each observer registration's failure policy.
Buffered dispatch is not a full inline-equivalent observer-failure propagation
model; recursive buffered `ObserverFailedEvent` propagation is not claimed for
1.1.0.
