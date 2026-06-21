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

## Observability

- `SmartPipeMetrics.Export()`
- `SmartPipeMetrics.ExportJson()`
- `SmartPipeMetrics.ToDiagnosticText()`

`ToDiagnosticText()` is diagnostic text, not a Prometheus exporter. Use
OpenTelemetry metrics exporters for Prometheus integration.
