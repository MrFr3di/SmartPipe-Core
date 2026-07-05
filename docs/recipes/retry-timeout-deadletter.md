# Retry, Timeout, And Dead Letter

Use stage failure options when a typed stage needs retry, timeout, or
dead-letter behavior. Retry is separate from circuit breaker behavior, and
dead-letter persistence is application-owned.

## Stage Failure Policy

```csharp
await using var deadLetterStream = File.OpenWrite("orders.deadletter.jsonl");
var serializer = new JsonLinesDeadLetterSerializer<Order>();

var run = PipelineBuilder
    .From(orderSource)
    .Transform(
        validateOrder,
        new StageFailureOptions
        {
            Retry = new RetryPolicy(
                maxRetries: 3,
                delay: TimeSpan.FromMilliseconds(200),
                strategy: BackoffStrategy.Exponential),
            Timeout = new TimeoutPolicy
            {
                AttemptTimeout = TimeSpan.FromSeconds(2),
                StageTimeout = TimeSpan.FromSeconds(10),
                RetryMode = TimeoutRetryMode.CooperativeOnly,
                CancellationGracePeriod = TimeSpan.FromSeconds(1),
                LateAttemptFinalizationTimeout = TimeSpan.FromSeconds(30),
            },
            OnRetryExhausted = FailureAction.DeadLetter,
            OnPermanentFailure = FailureAction.DeadLetter,
        },
        new StageDeadLetterOptions<Order>(deadLetterStream, serializer))
    .Transform(projectOrder)
    .Run();

await foreach (var output in run.Outputs.ReadAllAsync())
{
    if (!output.Result)
        LogFailure(output.Result.Error);
}

await run.Completion;
```

`AttemptTimeout` limits one stage attempt. `StageTimeout` limits the stage
including attempt execution, cancellation grace, retry delay, and the next
attempt budget. If retry delay plus another meaningful attempt cannot fit
inside the stage budget, the runtime exhausts retry instead of waiting for a
delay that cannot be used.

The default timeout retry mode is `CooperativeOnly`: the runtime cancels the
attempt, waits `CancellationGracePeriod`, and retries only after the timed-out
attempt has completed. This prevents overlapping attempts for the same item by
default.

Use `DetachWithoutRetry` when a timed-out attempt should be observed in the
background but must not be retried. Use `DetachAndRetryIdempotent` only when
the transformer is idempotent and safe to overlap with a retry for the same
input. Detached late attempts are observed until they finish, and owned stage
disposal waits up to `LateAttemptFinalizationTimeout`. Non-cooperative user
code cannot be forcibly stopped in-process; if it outlives the finalization
timeout, pipeline completion reports a cleanup failure.

## Dead-Letter Boundaries

`DeadLetterEnvelope<T>` preserves the original stage input payload, trace id,
metadata, stage id, stage name, attempt, error, and failure timestamp. This is
replay-safe context, not a durability guarantee.

The runtime writes through the stream and serializer you provide. It does not
own the stream, rotate files, encrypt storage, upload records, or provide
exactly-once delivery. Put those concerns in the application, an extension
package, or a storage-specific component.

Core has no connectors. In data integration workloads, connector retry and
transport-specific timeout policies should live outside SmartPipe.Core unless
they are expressed as ordinary source, transformer, sink, or observer behavior.

## Rules

- Use retry only for transient stage failures.
- Use dead-letter for records that need replay or manual inspection.
- Provide an `IDeadLetterRedactor<T>` before persisting sensitive payloads.
- Keep circuit breaker policy separate from retry policy.
- Do not describe dead-letter files or streams as durable unless the
  application-owned storage actually provides durability.
