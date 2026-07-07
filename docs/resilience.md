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
stage timeout budget. `RetryPolicy.OnRetry` runs after the retry delay and
before the next attempt starts. If the callback throws, the runtime faults the
run instead of silently continuing with another attempt.

Transformer exceptions are converted to `SmartPipeError` with category
`StageException` and `ErrorType.Permanent` by default, then routed through the
same retry, dead-letter, and failure action policy as returned
`StageResult.Failure(...)`. Runtime-generated timeout results remain transient.
Thrown `TimeoutException` and `HttpRequestException` are permanent by default.
Use `StageFailureOptions.ExceptionClassifier` to classify thrown exceptions as
transient when a stage has domain-specific retry rules.

`OperationCanceledException` caused by pipeline cancellation remains
cancellation and bypasses the classifier. A user-thrown
`OperationCanceledException` when the pipeline token is not cancelled follows
the normal exception-classifier path.

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

`DeadLetterSink<T>` writes newline-delimited JSON records. Path-backed sinks open
the file with append semantics and use a seekable `FileStream`. Before each
record write, the sink checkpoints the file length, writes UTF-8 bytes directly,
and flushes by default through `FlushEachWrite = true`. If an in-process write
throws, a seekable stream is truncated back to the checkpoint before retry.
Injected non-seekable streams can still be used, but they do not provide this
in-process rollback guarantee. This is not crash-atomic persistence; durability
after process or machine failure depends on the configured stream, file system,
and storage.

Dead-letter writes make at most four attempts. The retry backoff before attempts
2, 3, and 4 is 100ms, 200ms, and 400ms. After attempts are exhausted, the
default `DeadLetterWriteFailureMode.Throw` raises `DeadLetterWriteException`.
Use `DeadLetterWriteFailureMode.LogAndDrop` only when dropping the failed
dead-letter record is an explicit policy choice.

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
