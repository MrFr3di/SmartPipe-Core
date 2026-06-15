# Resilience

SmartPipe resilience is stage-local and typed-runtime owned.

## Retry

Attach retry to a transform stage with `StageFailureOptions`:

```csharp
.Transform(
    stage,
    new StageFailureOptions
    {
        Retry = new RetryPolicy(maxRetries: 3, delay: TimeSpan.FromMilliseconds(100)),
        OnRetryExhausted = FailureAction.EmitFailureResult,
    })
```

Retries apply to transient stage failures. Retry delay observes cancellation and
stage timeout budget.

## Timeout

`TimeoutPolicy.AttemptTimeout` limits one attempt. `StageTimeout` limits the
whole stage including retries and retry delays.

## Circuit Breaker

`CircuitBreakerPolicy` can use threshold-compatible or failure-ratio evaluation.
The runtime checks the breaker before a stage attempt and records success or
failure after attempts.

If the breaker is open, the item is rejected quickly and deterministically. The
runtime does not retry an item into an already-open breaker.

## Dead Letter

Use `FailureAction.DeadLetter` with `StageDeadLetterOptions<T>`:

```csharp
.Transform(
    stage,
    new StageFailureOptions { OnPermanentFailure = FailureAction.DeadLetter },
    new StageDeadLetterOptions<Order>(stream, serializer, redactor))
```

Dead-letter persistence writes `DeadLetterEnvelope<T>`, not a result-only shape.
The envelope preserves original payload and replay context.

`DeadLetterSource<T>` reads these envelopes and yields typed
`ProcessingEnvelope<T>` values for explicit replay.

## Failure Actions

- `EmitFailureResult`
- `DeadLetter`
- `Skip`
- `StopPipeline`
- `FaultPipeline`

## Failure Coverage

Release hardening covers source initialization/read failures, blocked source
reads during drain, transformer default results, thrown and timed-out
transformers, cancelled retry delay, circuit-breaker open and rejection paths,
dead-letter write failures, sink initialization/write failures, inline and
buffered observer failures, absent or slow output readers, cancellation during
output writes, drain during source read, and disposal during in-flight stages.

These scenarios belong in runtime and resilience tests, not in a separate
fault-injection matrix document.
