# Migration From Removed Legacy APIs

SmartPipe.Core is typed-only. Replace removed legacy concepts with typed
runtime APIs:

| Removed concept | Typed replacement |
|---|---|
| channel runtime | `PipelineBuilder` + `PipelineRun<T>` |
| context object | `ProcessingEnvelope<T>` |
| result object | `StageResult<T>` for stages, `PipelineResult<T>` for run outputs |
| source interface | `IPipelineSource<T>` |
| transformer interface | `IPipelineTransformer<TInput,TOutput>` |
| sink interface | `IPipelineSink<T>` |
| per-pipeline options | `PipelineRuntimeOptions` |
| retry queue behavior | `StageFailureOptions.Retry` inside `StageExecutor` |
| channel factory/DI runtime singleton | `ISmartPipeDefinition<TInput,TOutput>` + `ISmartPipeFactory<TInput,TOutput>` |

## Removed Legacy APIs

The typed-only release removes the legacy channel runtime model, including
`SmartPipeChannel`, `SmartPipeChannelOptions`, `ProcessingContext`,
`ProcessingResult`, legacy `ISource<T>`, legacy `ITransformer<TInput,TOutput>`,
legacy `ISink<T>`, legacy adapters, middleware transformer APIs, retry queue
types, channel pool types, and legacy pipeline cancellation helpers.

## Transitional 2.0 Compatibility Aliases

The primary 2.0 runtime settings are `MaxConcurrency` and `OutputPolicy`.
Existing typed consumers may still see these obsolete compatibility names in
the 2.0 public API:

- `PipelineRuntimeOptions.MaxDegreeOfParallelism`
- `PipelineRuntimeOptions.OutputMode`
- `PipelineOutputMode`

Use `MaxConcurrency` and `OutputPolicy` in new code. `MaxDegreeOfParallelism`
is honored only when `MaxConcurrency` keeps its default value. Conflicting
non-default `MaxConcurrency` and `MaxDegreeOfParallelism` values fail
validation.

`OutputMode` is honored only when explicitly set without `OutputPolicy`.
Incompatible explicit `OutputMode` and `OutputPolicy` combinations fail
validation.

`PipelineOrderingMode.PreserveInputOrder` remains public only as an obsolete
compatibility value. Parallel order preservation is not implemented; combining
`PreserveInputOrder` with `MaxConcurrency > 1` fails validation. Keep
`OrderingMode` at `Unordered` unless a future release documents order
preservation support.

## Simple Delegate Pipelines

```csharp
var run = PipelineBuilder
    .From(PipelineSource.FromAsyncEnumerable(items))
    .Transform(PipelineTransformer.FromFunc<int, string>(
        static (value, ct) => ValueTask.FromResult(value.ToString())))
    .To(PipelineSink.FromFunc<string>(
        static (value, ct) => ValueTask.CompletedTask));
```

## Component Pipelines

```csharp
await using var run = PipelineBuilder
    .From(source)
    .Transform(stage)
    .To(sink);

await run.Completion;
```

## DI

Register typed definitions and factories:

```csharp
services.AddSmartPipe<TInput, TOutput>(
    "pipeline-id",
    builder => builder
        .UseSource<TSource>()
        .UseStage<TStage>()
        .UseSink<TSink>());
```

Factories create a fresh runtime per run and preserve scoped dependency
ownership.

## Narrow extension packages

Existing `SmartPipe.Extensions` source and binaries keep the same namespaces and
type identities through forwarding. New applications should install
`SmartPipe.Extensions.Channels`, `.Transforms`, `.Logging`, or `.DataAnnotations`
directly. The legacy `LoggerSink<T>(ILogger<LoggerSink<T>>)` constructor is not
obsolete in 2.2.0; choose the options constructor to disable raw payload logging.
