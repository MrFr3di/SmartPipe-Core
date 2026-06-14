# Getting Started

SmartPipe.Core has one runtime model: typed envelopes.

```text
IPipelineSource<TInput>
  -> ProcessingEnvelope<TInput>
  -> IPipelineTransformer<TInput,TOutput>
  -> IPipelineSink<TOutput>
```

## Delegate Pipeline

```csharp
var run = PipelineBuilder
    .From(PipelineSource.FromAsyncEnumerable(Enumerable.Range(1, 10).ToAsyncEnumerable()))
    .Transform(PipelineTransformer.FromFunc<int, string>(
        static (value, ct) => ValueTask.FromResult(value.ToString())))
    .To(PipelineSink.FromFunc<string>(
        static (value, ct) => ValueTask.CompletedTask));

await run.Completion;
```

## Component Pipeline

```csharp
await using var run = PipelineBuilder
    .From(new OrdersSource())
    .WithPipelineId("orders")
    .Transform(new ValidateOrderStage())
    .Transform(new OrderDtoStage())
    .To(new OrderSink());

await foreach (var output in run.Outputs.ReadAllAsync())
{
    if (!output.Result.IsSuccess)
        Console.WriteLine(output.Result.Error?.Message);
}

await run.Completion;
```

## Runtime Options

```csharp
var options = new PipelineRuntimeOptions
{
    MaxConcurrency = 4,
    InputCapacity = 1024,
    OutputCapacity = 1024,
    OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
    ObserverDispatch = ObserverDispatchOptions.Inline,
};
```

Use `MaxConcurrency` for concurrent envelope processing. Per-envelope stages
remain sequential; cross-envelope output order is not guaranteed.

## Extensions

`SmartPipe.Extensions` provides typed selectors, transforms, sinks, DI, hosting,
and health-check integrations. Common components include:

- selectors: `HttpSelector<T>`, `JsonFileSource<T>`, `CsvFileSource<T>`,
  `EfCoreSelector<T>`, `DapperSelector<T>`, `DeadLetterSource<T>`;
- transforms: `JsonTransform<TInput,TOutput>`, `CsvTransform<TInput,TOutput>`,
  `MapsterTransform<TInput,TOutput>`, `FilterTransform<T>`,
  `ValidationTransform<T>`, `PollyResilienceTransform<T>`;
- sinks: `LoggerSink<T>`, `HttpSink<T>`, `JsonFileSink<T>`, `CsvFileSink<T>`,
  `DbSink<T>`, `DeadLetterSink<T>`.

Next links:

- [Configuration](configuration.md)
- [Runtime contracts](runtime-contracts.md)
- [Resilience](resilience.md)
- [API reference](api-reference.md)
- [Migration guide](migration/legacy-to-typed.md)
