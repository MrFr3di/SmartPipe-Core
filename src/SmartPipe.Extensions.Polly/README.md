# SmartPipe.Extensions.Polly

A Polly resilience decorator for `SmartPipe.Core` transforms. Every Polly
attempt runs the real inner `IPipelineTransformer<TInput,TOutput>`, and the
application owns the typed `ResiliencePipeline<StageResult<TOutput>>` that
decides whether to retry, time out, break, hedge, or fall back.

## Package graph

`SmartPipe.Extensions.Polly` depends only on `SmartPipe.Core` and `Polly.Core`
(8.8.0). It does not reference `Polly.Extensions`, `Polly.RateLimiting`,
`Microsoft.Extensions.Resilience`, dependency injection, Hosting, HTTP, or the
`SmartPipe.Extensions` bundle. Applications that want a pipeline registry,
telemetry, or rate limiting add those Polly packages themselves.

## What this package owns

- `PollyTransformDecorator<TInput,TOutput>` wraps an inner transform with a
  typed Polly pipeline.
- `PollyPipelineComponents.Decorate` returns a runtime-owned
  `PipelineComponent<IPipelineTransformer<TInput,TOutput>>` for Core's
  stage-keyed `Transform(stageKey, component)`. One overload takes a pipeline
  factory that runs for each activation; the other shares one pipeline.
- `PollyInnerTransformOwnership` (`Borrowed` or `Owned`) is required on the
  constructor and on both factory overloads. Ownership is never inferred from
  dependency injection or from the runtime type.
- `PollyTransformDecoratorOptions` carries an optional `OperationKey` and an
  optional `PollyTransformExceptionMapper`. Options are snapshotted when the
  decorator or component is created.

There are no Polly-specific builder extensions, no service registrations, and no
built-in strategies. Retry, timeout, circuit-breaker, hedging, and fallback
policy is always your application's choice.

## Execution

For each item the decorator:

1. rents a `ResilienceContext` from `ResilienceContextPool.Shared` with the
   operation key, the caller's cancellation token, and
   `continueOnCapturedContext: false`;
2. calls `ExecuteOutcomeAsync` with a static callback that awaits the inner
   `TransformAsync(envelope, context.CancellationToken)` once per attempt and
   reports a thrown exception as an `Outcome` (Polly requires that the
   callback never throws);
3. returns the context to the pool on every path, including cancellation, a
   faulting strategy, and a failing mapper;
4. translates the final outcome.

The envelope and payload are never placed in context properties and never
logged. Every attempt receives the same envelope instance; the decorator does
not clone it.

### Final outcome

- A final result is returned unchanged, whatever its kind: `Success`,
  `Failure`, `Filtered`, `Cancelled`, or `TimedOut`. It stays a result even if
  the caller's token is cancelled after it completed.
- A final exception is rethrown with its original identity and stack trace.
  Polly exceptions such as `TimeoutRejectedException`,
  `BrokenCircuitException`, and `IsolatedCircuitException` keep their type.
- An `OperationCanceledException` is never mapped. Caller cancellation is not
  turned into a Polly timeout, and a Polly timeout is not turned into caller
  cancellation.

### Exception mapper

`ExceptionMapper` is opt-in. It runs once, for the final exception only, after
every configured strategy has finished. It never sees a `StageResult` failure,
an `OperationCanceledException`, or its own failure.

| Mapper returns | Decorator result |
|---|---|
| `null` | The original exception is rethrown unchanged. |
| a `SmartPipeError` | `StageResult<TOutput>.Failure(error)` |
| throws | `AggregateException` with the original exception first, then the mapper failure. |

### Attempt counts

The decorator adds no attempts of its own. With a Polly retry of `Rpolly`
retries, the inner transform runs at most `1 + Rpolly` times per item, and stops
at the first result or exception that your `ShouldHandle` predicate does not
handle. Polly's default retry predicate handles exceptions only, so a
`StageResult` failure is retried only when you add a result predicate, for
example `HandleResult(r => r.Kind == StageResultKind.Failure)`.

When the Core stage also retries with `Rcore` retries, the two layers multiply:

```text
inner attempts per item ≤ (1 + Rcore) × (1 + Rpolly)
```

HTTP transports add their own attempts only when you configure a handler on the
`HttpClient`; see `SmartPipe.Extensions.Http`.

### Hedging

Hedging runs attempts concurrently against the same envelope. Use it only when
the inner transform is reentrant and either free of side effects or externally
synchronized. The decorator cannot make side effects safe.

## Ownership and lifetime

| Resource | Contract |
|---|---|
| Inner transform, `Borrowed` | Initialized once by the decorator. Never disposed by it. |
| Inner transform, `Owned` | Initialized once and disposed exactly once by the decorator. |
| `ResiliencePipeline` | Always application-owned. Never disposed. |
| Decorator from `Decorate` | Runtime-owned: Core initializes and disposes it. |
| Decorator you construct | You call `InitializeAsync` and `DisposeAsync`, including after a failed initialization. |

The decorator lifecycle is `Created → Initializing → Initialized → Disposing →
Disposed`:

- Concurrent and repeated `InitializeAsync` calls share one initialization,
  including its failure.
- `TransformAsync` throws `InvalidOperationException` before initialization
  succeeds and `ObjectDisposedException` once disposal has started.
- Each transform holds an operation lease for the whole Polly execution,
  including retry delays and every attempt.
- `DisposeAsync` rejects new work, waits for an in-flight initialization and for
  every active lease, then disposes an owned inner transform once. Concurrent
  disposers share the same completion, including a cleanup failure.

### Activation through Core

`Decorate` never initializes the inner transform itself. For each activation it
runs the pipeline factory first, then the inner factory, then creates the
decorator. Core stores the decorator's lease before initializing it, so if the
inner transform fails to initialize, Core's rollback disposes the decorator,
which disposes an owned inner once. If activation fails after an owned inner was
acquired (for example, start-up is cancelled), the component disposes that inner
once; if that disposal also fails, you get an `AggregateException` with the
activation failure first. A borrowed inner is never disposed on any of these
paths. A factory that returns `null` fails activation with
`InvalidOperationException`.

## Options

`OperationKey` is `null` or 1–128 characters with no whitespace or control
characters. Use a stable, low-cardinality operation name. Never use payload
values, run or trace identifiers, or URIs. Invalid values throw
`ArgumentException` when the decorator or component is created.

## Trimming and NativeAOT

The package declares the `verified` contract for the decorator over `Polly.Core`.
The `polly-direct`, `polly-trim`, and `polly-nativeaot` consumers build, publish
trimmed, and publish with NativeAOT, then run a Core pipeline through the
decorator with an owned inner transform and a Polly retry, plus a direct
decorator with a borrowed inner and an exception mapper. Your own strategies,
callbacks, Polly registry packages, and other dynamic application code are
outside that claim.

## Usage

```csharp
ResiliencePipeline<StageResult<Order>> retry = new ResiliencePipelineBuilder<StageResult<Order>>()
    .AddRetry(new RetryStrategyOptions<StageResult<Order>>
    {
        MaxRetryAttempts = 2,
        ShouldHandle = new PredicateBuilder<StageResult<Order>>()
            .Handle<TimeoutException>()
            .HandleResult(result => result.Kind == StageResultKind.Failure),
    })
    .Build();

var component = PollyPipelineComponents.Decorate<OrderRequest, Order>(
    static (_, _) => ValueTask.FromResult<IPipelineTransformer<OrderRequest, Order>>(new EnrichOrder()),
    retry,
    PollyInnerTransformOwnership.Owned,
    new PollyTransformDecoratorOptions { OperationKey = "enrich-order" });

var definition = PipelineDefinitionBuilder
    .From(new PipelineKey("orders"), source)
    .Transform(new PipelineStageKey("enrich"), component)
    .To(sink);
```

The same component works on a typed builder after earlier transforms:
`typedCurrent.Transform(stageKey, component)`.

To resolve a named pipeline from `Polly.Extensions`, use the pipeline-factory
overload and read the provider from `PipelineActivationContext.Services`:

```csharp
var component = PollyPipelineComponents.Decorate<OrderRequest, Order>(
    innerFactory,
    context => context.Services!
        .GetRequiredService<ResiliencePipelineProvider<string>>()
        .GetPipeline<StageResult<Order>>("orders"),
    PollyInnerTransformOwnership.Borrowed);
```

## Migrating from 2.1.2

`SmartPipe.Extensions.Transforms.PollyResilienceTransform<T>` was removed from
`SmartPipe.Extensions` by
[ADR-0004](../../docs/adr/0004-smartpipe-2.2-breaking-migration.md). Its
callback returned `StageResult<T>.Success(envelope.Payload)` without running any
inner transform, so it never protected real work. There is no wrapper or
forwarder; update the code and recompile.

| 2.1.2 | 2.2.0 |
|---|---|
| `new PollyResilienceTransform<T>(ResiliencePipeline pipeline, logger)` | `PollyPipelineComponents.Decorate` or `new PollyTransformDecorator<TInput,TOutput>(inner, pipeline, ownership)` with a typed `ResiliencePipeline<StageResult<TOutput>>` |
| Timeout and broken-circuit exceptions became `Transient` failures in category `"Resilience"` | Exceptions are rethrown unchanged. To keep failure results, supply an `ExceptionMapper` |
| `InvalidOperationException` became a `Permanent` failure | Same: map it explicitly with `ExceptionMapper` |
| Optional `ILogger` | Removed. The decorator logs nothing |
