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
