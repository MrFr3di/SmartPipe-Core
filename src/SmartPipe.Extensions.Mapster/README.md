# SmartPipe.Extensions.Mapster

Mapster object-mapping transforms for `SmartPipe.Core` with composition-time configuration isolation.

## What this package owns

- `MapsterPipelineComponents.Transform<TInput,TOutput>(Action<TypeAdapterConfig>? configure = null)` — a
  runtime-owned `PipelineComponent<IPipelineTransformer<TInput,TOutput>>` over one compiled root mapping.
- `MapsterPipelineDefinitionBuilderExtensions.MapWithMapster<TInput,TOutput>` — attaches that stage to an
  initial `PipelineDefinitionBuilder<TInput>`.
- `MapsterPipelineDefinitionBuilderExtensions.MapWithMapster<TPipelineInput,TCurrent,TOutput>` — attaches
  the same stage after an existing stage while preserving the original pipeline input type.

The package depends on `Mapster` only. It never references `SmartPipe.Extensions`, dependency injection,
Entity Framework Core, Dapper, HTTP, Polly, `Mapster.DependencyInjection`, `Mapster.EFCore`,
`Mapster.Async`, `Mapster.Tool`, or `FastExpressionCompiler`. The composition entry points above take a
configuration callback: there is no overload accepting a caller-owned mutable `TypeAdapterConfig` and no
options type. The forwarded legacy `MapsterTransform<TInput,TOutput>` keeps its own shipped
`TypeAdapterConfig?` constructor.

## Composition and configuration isolation

Composition follows one fixed sequence for the requested root pair:

```text
new working TypeAdapterConfig
  -> invoke configure once (when supplied)
  -> clone once into a private configuration container
  -> eagerly compile/cache the exact TInput -> TOutput pair once
  -> capture the resulting Func<TInput,TOutput>
```

| Concern | Contract |
|---|---|
| Callback | `configure` runs exactly once during composition against the working configuration. An exception thrown there is a composition failure; it is never converted into an item failure. |
| Global settings | `TypeAdapterConfig.GlobalSettings` is never read. Only the private clone is used. |
| Per run | The captured delegate is reused by a fresh Core-owned transformer per run. No run clones the configuration or recompiles the root pair. |
| Nested pairs | A nested or `MapToTarget` pair may compile lazily on its first mapping against the private clone. That bounded lazy compilation is part of the contract. Configuration added after composition does not recompile or change the captured root delegate. |
| Isolation ceiling | `Clone()` isolates the configuration containers and recursively clones supported `IApplyable` values. Ordinary objects, delegates, closures, and custom mutable values stay shared references. Callers must not mutate them while composition or transform execution is active. |
| Failures | Mapping exceptions propagate to Core unchanged. This package does not translate them into permanent stage failures, does not catch `InvalidOperationException` heuristically, and does not hide cleanup errors. Core owns result and error classification. |
| Cancellation | The synchronous Mapster mapper has no mid-call cancellation point. Cancellation is checked immediately before invoking it and again after it returns, so a cancelled run never reports a successful mapping. |
| Logging | The package logs nothing: no payloads, exception messages, configuration, or identifiers. It takes no logging dependency. |

## Trimming and NativeAOT

The package makes no blanket trimming or NativeAOT claim. Every runtime mapping entry point carries
`RequiresUnreferencedCode` and `RequiresDynamicCode`, because Mapster builds expression trees and
compiles them at runtime. Two measured facts define the boundary: a trimmed publish reports the aggregate
`IL2104` for the `Mapster` and `Mapster.Core` assemblies, and executing composition under
`TrimMode=link` fails inside `Mapster.TypeAdapterConfig.GetMapFunction`. Consumers that need trimming or
NativeAOT therefore use a hand-written or source-generated mapper through Core's
`PipelineTransformer.FromFunc` instead; the `mapster-trim-diagnostic` consumer publishes and runs that
route under `TrimMode=link`.

## Usage

```csharp
var definition = PipelineDefinitionBuilder
    .From(new PipelineKey("orders"), sourceComponent)
    .MapWithMapster<Order, OrderDto>(
        new PipelineStageKey("map"),
        static config => config
            .NewConfig<Order, OrderDto>()
            .Map(destination => destination.Label, source => source.Name.ToUpperInvariant()))
    .Build();

await using var run = await definition.StartAsync();
await run.Completion;
```

## Migrating from 2.1.2

`SmartPipe.Extensions.Transforms.MapsterTransform<TInput,TOutput>` moved into this package and stays
reachable from `SmartPipe.Extensions` through type forwarding with its namespace, constructors, and
behaviour unchanged. It keeps its shipped semantics, including its optional `TypeAdapterConfig`
constructor and its `Permanent` "Mapster mapping error" stage failure. New code uses the composition
API above, where the caller supplies a configuration callback instead of a mutable configuration
instance and Core owns result classification.
