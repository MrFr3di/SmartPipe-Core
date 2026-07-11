# Getting Started

SmartPipe.Core has one runtime model: typed envelopes.

## Choose the integration package

```bash
dotnet add package SmartPipe.Core --version 2.1.2
dotnet add package SmartPipe.Extensions.Json --version 2.1.2
dotnet add package SmartPipe.Extensions --version 2.1.2
```

Use `SmartPipe.Extensions.Json` for JSON files, JSON transforms, and JSON
dead-letter persistence. Use `SmartPipe.Extensions` for HTTP, database, CSV,
mapping, resilience, hosting, and health-check integrations.

`SmartPipe.Extensions` 2.1.2 forwards the JSON types for 2.x compatibility.
Direct JSON package references are recommended for new applications.

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

Instance pipelines are single-use. If the same definition must start multiple
runs, build it with factories from source through sink:

```csharp
var builder = PipelineBuilder
    .FromFactory(_ => new OrdersSource())
    .TransformFactory(_ => new ValidateOrderStage())
    .TransformFactory(_ => new OrderDtoStage());

var first = builder.ToFactory(_ => new OrderSink());
var second = builder.ToFactory(_ => new OrderSink());
```

Do not call `TransformFactory` or `ToFactory` on a pipeline that started with
`.From(source)`. Use `.Transform(instance)` and `.To(instance)` there.

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

`SmartPipe.Extensions.Json` provides the JSON components below.
`SmartPipe.Extensions` provides the remaining selectors, transforms, sinks, DI,
hosting, and health-check integrations. Common components include:

- selectors: `HttpSelector<T>`, `JsonFileSource<T>`, `CsvFileSource<T>`,
  `EfCoreSelector<T>`, `DapperSelector<T>`, `DeadLetterSource<T>`;
- transforms: `JsonTransform<TInput,TOutput>`, `CsvTransform<TInput,TOutput>`,
  `MapsterTransform<TInput,TOutput>`, `FilterTransform<T>`,
  `ValidationTransform<T>`, `PollyResilienceTransform<T>`;
- sinks: `LoggerSink<T>`, `HttpSink<T>`, `JsonFileSink<T>`, `CsvFileSink<T>`,
  `DbSink<T>`, `DeadLetterSink<T>`.

`MapsterTransform<TInput,TOutput>` uses Mapster runtime mapping and is not
trim- or NativeAOT-safe. Use a hand-written mapper, a source-generated mapper,
or `PipelineTransformer.FromFunc` for trimmed or NativeAOT applications.

Next links:

- [Configuration](configuration.md)
- [Runtime contracts](runtime-contracts.md)
- [Resilience](resilience.md)
- [API reference](api-reference.md)
- [Migration guide](migration/legacy-to-typed.md)
