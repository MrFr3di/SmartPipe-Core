# API Reference

## Typed Abstractions

```csharp
public interface IPipelineSource<T>
{
    ValueTask InitializeAsync(CancellationToken ct = default);
    IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(CancellationToken ct = default);
    ValueTask DisposeAsync();
}

public interface IPipelineTransformer<TInput, TOutput>
{
    ValueTask InitializeAsync(CancellationToken ct = default);
    ValueTask<StageResult<TOutput>> TransformAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct = default);
    ValueTask DisposeAsync();
}

public interface IPipelineSink<T>
{
    ValueTask InitializeAsync(CancellationToken ct = default);
    ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default);
    ValueTask DisposeAsync();
}
```

## Runtime

- `PipelineBuilder`
- `PipelineRun<T>`
- `PipelineRuntimeOptions`
- `PipelineOutput<T>`
- `PipelineResult<T>`
- `ProcessingEnvelope<T>`
- `StageResult<T>`

`PipelineRuntimeOptions.OutputPolicy` defaults to
`SuppressSuccessWhenSinkAttached`. Set `EmitAll` explicitly when a sink-backed
pipeline also needs every success output to be consumed.

`PipelineBuilder.From(source).Transform(stage).To(sink)` is the single-use
instance path. Reusable definitions use `FromFactory`, `TransformFactory`, and
`ToFactory` together so every start receives fresh components. `TransformFactory`
and `ToFactory` throw on instance pipelines; use `.Transform(instance)` and
`.To(instance)` there.

## Failure Handling

- `StageFailureOptions`
- `RetryPolicy`
- `TimeoutPolicy`
- `CircuitBreakerPolicy`
- `FailureAction`
- `DeadLetterEnvelope<T>`
- `StageDeadLetterOptions<T>`

## Adapters

- `PipelineSource.FromAsyncEnumerable`
- `PipelineTransformer.FromFunc`
- `PipelineSink.FromFunc`

## Extensions

`SmartPipe.Extensions` provides typed selectors, transforms, sinks, DI
registration, hosted service integration, and health-check support.

- Factory-created `PipelineRun<TOutput>` instances preserve runtime controls, structured drain, and metrics while adding DI scope lifetime management.

Important selector and streaming contracts:

- `HttpSelector<T>` logs request URIs without userinfo, query strings, or
  fragments. Malformed absolute URIs are logged as `[unparseable-uri]`.
  Reflection JSON constructors are annotated for trimming and NativeAOT risk;
  prefer the `JsonTypeInfo<List<T>>` buffered overload or `JsonTypeInfo<T>`
  streaming overload in trimmed or NativeAOT applications.
- `EfCoreSelector<T>` reads with `AsNoTracking()` by default. Use
  `.WithTracking()` to opt into EF Core change tracking for returned entities.
- `DapperSelector<T>` uses asynchronous `DbConnection` open/read operations and
  leaves externally supplied connections open by default. Use the explicit
  ownership overload with `leaveOpen: false` when the selector should dispose
  the connection. For trimming or NativeAOT, prefer the explicit
  `Func<DbDataReader,T>` mapper overload instead of the reflection mapper.
- `DbSink<T>` preserves the legacy `IDbConnection` constructor behavior as
  `leaveOpen: false`. Prefer the explicit `DbConnection` overloads for new
  code: use `leaveOpen: true` for externally owned connections, and provide
  explicit INSERT SQL in trimming or NativeAOT-sensitive applications.
- `JsonFileSink<T>` writes newline-delimited JSON batches: each flush appends
  one UTF-8 JSON array followed by a newline. Path-backed files use append
  semantics, checkpoint seekable stream length and position before each batch,
  and roll back in-process write exceptions by truncating to that checkpoint
  while keeping the batch buffered for retry. This is not a crash-atomicity
  guarantee.
- `ChannelMerge.Merge(first, second, options, cancellationToken)` is the
  cancellation-aware overload for bounded or backpressure-sensitive merges. The
  compatibility overload without a cancellation token remains available.
- `DeadLetterSink<T>` writes JSON Lines records with append semantics for
  path-backed files. It checkpoints seekable stream length before each record,
  retries failed writes up to four total attempts with 100ms, 200ms, and 400ms
  backoff, and throws `DeadLetterWriteException` by default when attempts are
  exhausted. Set `FailureMode = DeadLetterWriteFailureMode.LogAndDrop` only for
  explicit drop-on-failure behavior. `FlushEachWrite` defaults to `true`.

## Secret Scanning

- `SecretScanner.Scan(content)` returns `SecretScanResult.Clean`,
  `SecretScanResult.SecretFound`, or `SecretScanResult.Indeterminate`.
- `SecretScanner.HasSecrets(content)` fails closed: it returns `true` for both
  `SecretFound` and `Indeterminate`.
- `SecretScanner.Redact(content)` returns `***REDACTION_INDETERMINATE***` when
  scanning or redaction cannot complete within regex, input-size, decode-budget,
  or recursion limits.

## Observability

- `SmartPipeMetrics.Export()`
- `SmartPipeMetrics.ExportJson()`
- `SmartPipeMetrics.ToDiagnosticText()`

`ToDiagnosticText()` is diagnostic text, not a Prometheus exporter. Use
OpenTelemetry metrics exporters for Prometheus integration.
