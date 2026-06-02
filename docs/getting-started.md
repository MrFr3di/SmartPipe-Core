# Getting Started

SmartPipe pipelines move data from a source, through one or more transforms, to
a sink:

```text
source -> transform -> sink
```

Install the packages you need:

```bash
dotnet add package SmartPipe.Core
dotnet add package SmartPipe.Extensions
```

## Legacy Quick Start

The legacy API is the compatibility path for 1.x components.

```csharp
using SmartPipe.Core;
using SmartPipe.Extensions.Selectors;
using SmartPipe.Extensions.Sinks;

var httpClient = new HttpClient();

var pipeline = PipelineBuilder
    .From(new HttpSelector<int>(httpClient, "https://api.example.com/numbers"))
    .Transform(new MiddlewareTransformer<int>(x => x * 2));

await pipeline.To(new LoggerSink<int>(logger));
```

`PipelineBuilder.From(ISource<T>)` creates a legacy channel pipeline. The final
`To(ISink<T>)` call starts the run and returns a `Task`.

## Typed Quick Start

The typed API is the recommended path for new components.

```csharp
IPipelineSource<Order> source = new OrderSource();
IPipelineTransformer<Order, OrderDto> transformer = new OrderDtoStage();
IPipelineSink<OrderDto> sink = new OrderSink();

var run = PipelineBuilder
    .From(source)
    .Transform(transformer)
    .To(sink);

await foreach (var output in run.Outputs.ReadAllAsync())
{
    var result = output.Result;
    var envelope = output.Envelope;
}

await run.Completion;
```

`PipelineRun<T>.Outputs` carries the final `ProcessingResult<T>` and, when
available, the final `ProcessingEnvelope<T>`.

## RunInBackground Quick Start

Use `RunInBackground` when a legacy pipeline should expose its output as a
`ChannelReader<ProcessingResult<T>>`.

```csharp
var pipeline = new SmartPipeChannel<int, int>();
pipeline.AddSource(new NumbersSource([1, 2, 3]));
pipeline.AddTransformer(new MiddlewareTransformer<int>(x => x * 2));

var reader = pipeline.RunInBackground();

await foreach (var result in reader.ReadAllAsync())
{
    // Consume result.
}

sealed class NumbersSource(IEnumerable<int> values) : ISource<int>
{
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async IAsyncEnumerable<ProcessingContext<int>> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var value in values)
        {
            ct.ThrowIfCancellationRequested();
            yield return new ProcessingContext<int>(value);
            await Task.Yield();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
```

Each `SmartPipeChannel` instance supports one background run. Create a new
pipeline instance for another run.

## Extensions Quick Start

`SmartPipe.Extensions` provides common legacy components:

- selectors: `HttpSelector<T>`, `JsonFileSource<T>`, `CsvFileSource<T>`,
  `EfCoreSelector<T>`, `DapperSelector<T>`, `DeadLetterSource<T>`;
- transforms: `JsonTransform<TInput,TOutput>`, `CsvTransform<TInput,TOutput>`,
  `MapsterTransform<TInput,TOutput>`, `FilterTransform<T>`,
  `ValidationTransform<T>`, `PollyResilienceTransform<T>`;
- sinks: `LoggerSink<T>`, `HttpSink<T>`, `JsonFileSink<T>`, `CsvFileSink<T>`,
  `DbSink<T>`, `DeadLetterSink<T>`.

Legacy Extensions components can be used in typed pipelines through the legacy
adapters when envelope-aware execution is needed.

## Legacy Or Typed

Use legacy APIs when:

- you already have 1.x `ISource`, `ITransformer`, or `ISink` components;
- a simple compatibility pipeline is enough;
- `RunInBackground` result streaming is sufficient.

Use typed APIs when:

- the pipeline needs metadata or lineage;
- stage-specific retry, timeout, circuit breaker, or dead-letter policies are
  needed;
- observers need structured lifecycle, stage, sink, retry, or dead-letter
  events;
- replay-safe dead-letter records must preserve the original payload.

Next links:

- [Configuration](configuration.md)
- [Resilience](resilience.md)
- [API reference](api-reference.md)
- [Migration guide](migration/1.0-to-1.1.md)
