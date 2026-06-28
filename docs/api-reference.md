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

- Factory-created `PipelineRun<TOutput>` handles preserve runtime controls,
  structured drain, and metrics while adding DI scope lifetime management.

Important selector and streaming contracts:

- `EfCoreSelector<T>` reads with `AsNoTracking()` by default. Use
  `.WithTracking()` to opt into EF Core change tracking for returned entities.
- `DapperSelector<T>` uses asynchronous `DbConnection` open/read operations and
  leaves externally supplied connections open by default. Use the explicit
  ownership overload with `leaveOpen: false` when the selector should dispose
  the connection.
- `ChannelMerge.Merge(first, second, options, cancellationToken)` is the
  cancellation-aware overload for bounded or backpressure-sensitive merges. The
  compatibility overload without a cancellation token remains available.

## Observability

- `SmartPipeMetrics.Export()`
- `SmartPipeMetrics.ExportJson()`
- `SmartPipeMetrics.ToDiagnosticText()`

`ToDiagnosticText()` is diagnostic text, not a Prometheus exporter. Use
OpenTelemetry metrics exporters for Prometheus integration.
