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
transformer stages. `StageTimeout` is also used as an upper bound for each stage
attempt, so a stage can time out even when an attempt-specific timeout is not
configured. A timed-out attempt produces `StageResultKind.TimedOut`, a transient
`SmartPipeError` with category `Timeout`, and a `StageFailedEvent`. The
resulting terminal item then follows the same `OnPermanentFailure` action as
other terminal stage failures. `PipelineTimeout`, sink attempt timeout
enforcement, and full retry-delay budget enforcement remain planned hardening
work and must not be claimed as complete.

## Retry

Retry scheduling and retry execution must remain separate. Retry count increments
once per failed attempt. Retry exhaustion produces one terminal action.

The safe default retry queue overflow policy is `Wait`; dropping retry items is
only acceptable when explicitly configured.

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
