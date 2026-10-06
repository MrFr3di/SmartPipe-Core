# Getting Started

SmartPipe.Core has one runtime model: typed envelopes.

## Choose the integration package

Install `SmartPipe.Core` for the runtime:

```bash
dotnet package add SmartPipe.Core --version 2.2.0
```

Use `SmartPipe.Extensions.Json` for JSON files, JSON transforms, and JSON
dead-letter persistence:

```bash
dotnet package add SmartPipe.Extensions.Json --version 2.2.0
```

For integrations, prefer the narrow package that owns the capability. This table is
kept aligned with the release package graph so a newly activated leaf cannot disappear
from the primary package-selection path:

| Capability | Package |
|---|---|
| Channel merge primitives | `SmartPipe.Extensions.Channels` |
| Composable transforms | `SmartPipe.Extensions.Transforms` |
| Logging sink | `SmartPipe.Extensions.Logging` |
| DataAnnotations validation | `SmartPipe.Extensions.DataAnnotations` |
| JSON files/transforms/dead-letter | `SmartPipe.Extensions.Json` |
| CSV files | `SmartPipe.Extensions.Csv` |
| Explicit SQL / Dapper | `SmartPipe.Extensions.Dapper` |
| Entity Framework Core queries | `SmartPipe.Extensions.EntityFrameworkCore` |
| Mapster | `SmartPipe.Extensions.Mapster` |
| HTTP transport | `SmartPipe.Extensions.Http` |
| HTTP JSON codecs | `SmartPipe.Extensions.Http.Json` |
| Polly resilience | `SmartPipe.Extensions.Polly` |
| Dependency injection | `SmartPipe.Extensions.DependencyInjection` |
| Generic Host | `SmartPipe.Extensions.Hosting` |
| Health checks | `SmartPipe.Extensions.HealthChecks` |
| OpenTelemetry registration | `SmartPipe.Extensions.OpenTelemetry` |
| PostgreSQL binary COPY / LISTEN | `SmartPipe.Extensions.PostgreSql` |
| Test helpers | `SmartPipe.Testing` |

Use the broad compatibility bundle only when its complete integration dependency
set is intentional:

```bash
dotnet package add SmartPipe.Extensions --version 2.2.0
```

`SmartPipe.Extensions` preserves retained 2.x facade identities and forwards
moved types where compatibility is supported. It does not include the optional
PostgreSQL package or the test-only Testing package. New applications should
prefer leaf references. See the [2.2.0 release notes](releases/2.2.0.md) and
[2.1.2 → 2.2.0 migration guide](migration/2.2.0-integration-packages.md).

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

### SmartPipe.Extensions.Json

- `JsonFileSource<T>`
- `DeadLetterSource<T>`
- `JsonTransform<TInput,TOutput>`
- `JsonFileSink<T>`
- `DeadLetterSink<T>`

### SmartPipe.Extensions.PostgreSql

- sources: `PostgreSqlPipelineDefinitionBuilder.FromBinaryCopy<T>`,
  `FromNotifications`;
- sink: `ToPostgreSqlBinaryCopy`.

Binary `COPY` and `LISTEN`/`NOTIFY` over an application-owned `NpgsqlDataSource`. See the
[PostgreSQL subsystem reference](postgresql.md).

### SmartPipe.Extensions

- selectors: `CsvFileSource<T>`, `EfCoreSelector<T>`,
  `DapperSelector<T>`;
- transforms: `CsvTransform<TInput,TOutput>`, `MapsterTransform<TInput,TOutput>`,
  `FilterTransform<T>`, `ValidationTransform<T>`;
- sinks: `LoggerSink<T>`, `CsvFileSink<T>`, `DbSink<T>`.

The 2.1.2 `HttpSelector<T>`, `HttpClientFactorySelector<T>`, `HttpSink<T>`, and
`HttpClientFactorySink<T>` were removed in 2.2.0. Use `SmartPipe.Extensions.Http`
(`HttpPipelineComponents`, `FromHttp`/`ToHttp`) for the transport and
`SmartPipe.Extensions.Http.Json` (`HttpJsonResponseReaders`,
`HttpJsonRequestContent`, `FromHttpNdjson`/`FromHttpJsonArray`/`ToHttpJson`) for
source-generated JSON bodies.

The 2.1.2 `PollyResilienceTransform<T>` was also removed: it never ran an inner
transform. Use `SmartPipe.Extensions.Polly` (`PollyPipelineComponents.Decorate`
with Core's `Transform(stageKey, component)`, or `PollyTransformDecorator<TInput,TOutput>`)
with an application-owned `ResiliencePipeline<StageResult<TOutput>>`.

`EfCoreSelector<T>` is forwarded from `SmartPipe.Extensions.EntityFrameworkCore`; new code uses
`EfCorePipelineComponents.QuerySource`/`CompiledQuerySource` or the typed `FromQuery`/`FromCompiledQuery`
builders from that leaf.

`MapsterTransform<TInput,TOutput>` is forwarded from `SmartPipe.Extensions.Mapster`; new code uses
`MapsterPipelineComponents.Transform<TInput,TOutput>` or the `MapWithMapster` builder extensions from
that leaf, which isolate the caller callback, clone the working configuration once, and compile the
requested root pair once at composition.

`MapsterTransform<TInput,TOutput>` and the Mapster composition API both use Mapster runtime mapping and
are not trim- or NativeAOT-safe. Use a hand-written mapper, a source-generated mapper, or
`PipelineTransformer.FromFunc` for trimmed or NativeAOT applications.

Next links:

- [Configuration](configuration.md)
- [Runtime contracts](runtime-contracts.md)
- [Resilience](resilience.md)
- [PostgreSQL](postgresql.md)
- [API reference](reference/api-overview.md)
- [Migration guide](migration/legacy-to-typed.md)
