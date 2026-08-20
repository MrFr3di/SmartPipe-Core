# Dependency Injection

`SmartPipe.Extensions.DependencyInjection` is the canonical DI leaf for typed
pipeline definitions. Its production dependency closure is limited to
`SmartPipe.Core` and `Microsoft.Extensions.DependencyInjection.Abstractions`.

```csharp
PipelineDefinition<Order, OrderDto> definition = PipelineDefinitionBuilder
    .From(
        new PipelineKey("orders"),
        PipelineComponent.ScopeOwned<IPipelineSource<Order>>(
            static (context, _) => ValueTask.FromResult(
                context.Services!.GetRequiredService<OrderSource>())))
    .Transform(
        new PipelineStageKey("map"),
        PipelineComponent.ScopeOwned<IPipelineTransformer<Order, OrderDto>>(
            static (context, _) => ValueTask.FromResult(
                context.Services!.GetRequiredService<OrderStage>())))
    .Build();

ISmartPipeRegistrationBuilder<Order, OrderDto> registration = services
    .AddSmartPipe()
    .AddPipeline(definition);
```

`AddPipeline` registers the immutable definition and
`ISmartPipeRunFactory<TInput,TOutput>` as keyed singletons under the exact,
case-sensitive `PipelineKey.Value`. It does not register unkeyed typed aliases.
Keys are globally unique across generic type pairs. Registration is atomic: a
collection failure removes only descriptors inserted by that attempt and rolls
back the matching key reservation.
The reservation belongs to the synchronous `AddPipeline` call and is completed
by commit or rollback before that call returns or throws; ownership is never
transferred asynchronously.

`ISmartPipeRegistry` exposes defensive registration snapshots in successful
zero-based registration order. `ISmartPipeFactoryProvider` resolves only the
exact registered key and type pair; it does not resolve pipeline components.

## Run Scope And Lifetime

`ISmartPipeRunFactory<TInput,TOutput>.StartAsync()` is the only canonical start
operation. Pre-cancellation creates no scope. Every accepted start creates one
`AsyncServiceScope`, passes that scope provider and the registered
`TimeProvider` to `PipelineActivationContext`, and awaits Core readiness before
returning a run. `ScopeOwned` source, stage, and sink factories therefore share
one scoped dependency within a run and receive different scoped instances in
different runs.

The returned `PipelineRun<TOutput>` shares one cleanup task across natural
completion and explicit or concurrent disposal. Cleanup reaches a terminal Core
run, removes its active-run lease, and then disposes the DI scope. Core never
disposes `ScopeOwned` components.

`ISmartPipeRunRegistry` returns point-in-time active snapshots ordered by UTC
start time and run identifier. A snapshot contains the exact key/run identity,
input and output types, state, metrics, and the effective positive input/output
channel capacities reported by Core. Terminal history is not retained.

Use `ValidateScopes = true` in tests and applications to catch accidental root
scope captures. Registration never calls `BuildServiceProvider()`.

## Legacy Facade

The existing `SmartPipe.Extensions` registration and factory APIs remain the
2.1 compatibility facade. Their synchronous `Start` member is obsolete and
keeps its immediate-return behavior; it is not bridged to the canonical async
factory. The coupled legacy DI, Hosting, and Health identities remain physically
owned by the facade until the next major release. They still enter the same Core
compiler, activator, start operation, and executor, but new leaf packages never
reference or resolve them.

New code should use the keyed `ISmartPipeRunFactory<TInput,TOutput>.StartAsync`
path above. It awaits readiness and owns the single canonical DI scope lifetime.
