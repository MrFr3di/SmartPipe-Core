# Failure Semantics

This document captures the intended 1.1.0 failure model.

## Delivery

SmartPipe.Core is an in-process pipeline library. It does not claim exactly-once
delivery. At-most-once or at-least-once behavior depends on source, sink, retry,
dead-letter, and caller idempotency choices.

## Stage Failure

By default, a stage failure prevents downstream stages from running for the same
item. A stage policy can route permanent failures to one of these actions:

- `EmitFailureResult`: emit one failed output item and continue reading later source items.
- `DeadLetter`: write a replay-safe dead-letter envelope, emit one failed output item, and continue.
- `Skip`: emit no output for the failed item and continue reading later source items.
- `StopPipeline`: emit one failed output item and stop reading additional source items.
- `FaultPipeline`: fault `PipelineRun<T>.Completion` with `PipelineFailureActionException`.

Cancellation is not treated as transient, is not retried, and is not dead-lettered
by default.

## Timeout Semantics

The timeout vocabulary is:

- `AttemptTimeout`: one transformer or sink attempt.
- `StageTimeout`: a whole stage including retries.
- `PipelineTimeout`: the whole run.
- `DrainTimeout`: graceful wait for accepted work.
- `ShutdownTimeout`: graceful shutdown before abort.

In the 1.1.0 typed runtime, `AttemptTimeout` is enforced for envelope-aware
transformer stages. `StageTimeout` is a wall-clock budget for the whole stage,
including execution attempts and retry delays. If a retry delay cannot fit into
the remaining stage budget, retry is not scheduled. Budget exhaustion for
retryable errors uses `OnRetryExhausted`. `AttemptTimeout` remains per-attempt.
Effective attempt timeout remains `min(AttemptTimeout, remaining StageTimeout)`.
A timed-out attempt produces `StageResultKind.TimedOut`, a transient
`SmartPipeError` with category `Timeout`, and a `StageFailedEvent`. If no retry
policy is configured, or the timeout error is not retryable, the terminal item
uses `OnPermanentFailure`. If retry accepts the timeout error but the stage
budget is exhausted before another retry can run, the runtime emits
`RetryExhaustedEvent` and applies `OnRetryExhausted`. `PipelineTimeout` and sink
attempt timeout enforcement remain planned hardening work and must not be
claimed as complete.

## Retry

Retry scheduling and retry execution must remain separate. Retry count increments
once per failed attempt. Retry exhaustion produces one terminal action.

The safe default retry queue overflow policy is `Wait`; dropping retry items is
only acceptable when explicitly configured.

### Legacy RetryQueue overflow policy

`SmartPipeChannelOptions.RetryQueueOverflowPolicy` controls behavior when the
bounded retry queue is full (only when feature flag `"RetryQueue"` is enabled):

- `Wait` — block until capacity is available. Respects cancellation token.
- `FailFast` — do not enqueue; the overflowed item is treated as terminal failure.
- `DeadLetter` — do not enqueue; write to `DeadLetterSink` if configured. Falls
  back to terminal failure when no `DeadLetterSink` is configured.
- `DropNewest` — drop the incoming item. Lossy; opt-in.
- `DropOldest` — drop the oldest queued item. Lossy; opt-in.

The default is `Wait`. Lossy policies (`DropNewest`, `DropOldest`) are opt-in
and documented as at-most-once for dropped retry items.

In the 1.1.0 typed runtime, `StageFailureOptions.Retry` is enforced for
envelope-aware transformer stages that return a retryable `SmartPipeError`.
Retries are attempted only when `RetryPolicy.ShouldRetry(error)` returns true
and the item attempt is below `MaxRetries`. Each retry updates
`ProcessingEnvelope<T>.Attempt`, emits `RetryScheduledEvent`, then emits
`RetryAttemptedEvent` immediately before invoking the next attempt. When the
retry budget is exhausted, the runtime emits `RetryExhaustedEvent` and applies
`OnRetryExhausted` exactly once.

## Dead Letter

Replay-safe dead-letter records require the original payload, metadata, trace
identity, stage identity, error, attempt count, and schema version. The default
format direction is JSON Lines, one envelope per line, with documented corrupt
line behavior.

In the 1.1.0 typed runtime, a stage can route permanent `StageResult<T>` failures
to dead-letter persistence:

```csharp
await using var stream = File.Open("deadletter.ndjson", FileMode.Append);

var run = PipelineBuilder
    .From(source)
    .Transform(
        stage,
        new StageFailureOptions { OnPermanentFailure = FailureAction.DeadLetter },
        new StageDeadLetterOptions<Order>(stream))
    .Run();
```

The stage writes `DeadLetterEnvelope<TStageInput>` through the configured
serializer/redactor and emits `DeadLetterWrittenEvent`. The runtime leaves the
target stream open; applications own stream lifetime, rotation, encryption,
redaction policy, and access control.

## Circuit Breaker (typed runtime, 1.1.x update4)

The typed runtime supports stage-level circuit breaker policy for envelope-aware
transformer stages. Each stage with a configured `CircuitBreakerPolicy` gets an
independent circuit breaker instance scoped to the pipeline run.

The circuit breaker check happens before every transformer attempt (including
retries). When the breaker is open, the transformer is not called, and the item
immediately follows the configured `OnPermanentFailure` action.

Failure counting is per-attempt: every failed transformer attempt (including
failed retries) is recorded. Successes are recorded when an attempt succeeds.

A breaker-open rejection produces a `SmartPipeError` with `ErrorType.Permanent`
and category `"CircuitBreaker"`. The rejection is terminal — it does not invoke
the retry policy. The runtime emits `CircuitBreakerOpenedEvent` when the breaker
transitions to open and `CircuitBreakerRejectedEvent` for each item rejected
while the breaker is open.

Legacy `SmartPipeChannel` circuit breaker behavior is unchanged by this update.
Sink circuit breaker and `PipelineTimeout` remain out of scope.
