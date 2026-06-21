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

Transformer exceptions are converted to `SmartPipeError` with category
`StageException` and routed through the same retry, dead-letter, and failure
action policy as returned `StageResult.Failure(...)`. `OperationCanceledException`
remains cancellation and is not converted into a stage failure.

## Timeout

`TimeoutPolicy.AttemptTimeout` limits one attempt. `StageTimeout` limits the
whole stage including retries and retry delays.

## Circuit Breaker

`CircuitBreakerPolicy` can use threshold-compatible or failure-ratio evaluation.
The runtime checks the breaker before a stage attempt and records success or
failure after attempts.

If the breaker is open, the item is rejected quickly and deterministically. The
runtime does not retry an item into an already-open breaker.

Half-open probes are limited by concurrent active probes. A completed probe
releases its slot. A half-open failure reopens the breaker; enough half-open
successes close it and emit `CircuitBreakerClosedEvent`.

The runtime uses `CircuitBreaker.TryAcquireHalfOpenProbe(out probe)` as the
authoritative half-open API. Callers that use `CircuitBreaker` directly should
dispose the returned `CircuitBreakerProbe` after the attempted operation
completes so the active half-open slot is released.

`CircuitBreaker.AllowRequest()` remains a compatibility/simple gate. It allows
closed breakers, rejects isolated breakers, rejects open breakers until the
break duration expires, and then counts admitted half-open requests up to
`maxHalfOpenRequests`. It does not return a lease and does not release an
active half-open slot after operation completion.

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

`StageResult.Filtered()` is normal control flow, not a failure action. Filtered
items do not retry, do not write dead-letter records, do not call sinks, and do
not increment failed metrics.

## Failure Coverage

Release hardening covers source initialization/read failures, blocked source
reads during drain, transformer default results, thrown and timed-out
transformers, cancelled retry delay, circuit-breaker open and rejection paths,
dead-letter write failures, sink initialization/write failures, inline and
buffered observer failures, absent or slow output readers, cancellation during
output writes, drain during source read, and disposal during in-flight stages.

These scenarios belong in runtime and resilience tests, not in a separate
fault-injection matrix document.
