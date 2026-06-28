# Dependency Injection

`SmartPipe.Extensions` registers typed pipeline definitions and factories.

```csharp
services.AddSmartPipe<Order, OrderDto>(
    "orders",
    builder => builder
        .UseSource<OrderSource>()
        .UseStage<OrderStage>()
        .UseSink<OrderSink>());
```

The registration adds:

- immutable `ISmartPipeDefinition<TInput,TOutput>`;
- stateless `ISmartPipeFactory<TInput,TOutput>`;
- source, stage, and sink component registrations chosen by the application.

`ISmartPipeFactory<TInput,TOutput>.Start()` creates one DI scope per run. The
scope owns scoped source/stage/sink services. The returned run and the
completion path share one idempotent disposal path. Scoped dependencies are
resolved inside that run scope and are disposed with it.

The DI registration model is factory-based: each factory start creates fresh
source, stage, and sink components in the run scope. Do not mix singleton
component instances into a DI factory definition unless the component is
explicitly registered and intended to be reused by the container.

Use `ValidateScopes = true` in tests and hosted applications to catch accidental
root-scope captures.

## Factory Run Lifetime

`ISmartPipeFactory<TInput,TOutput>.Start()` and `StartAsync()` return a
`PipelineRun<TOutput>` that preserves the underlying runtime controls:
`CancelAsync`, `DrainAsync`, `TryDrainAsync`, `AbortAsync`, `Metrics`, `Outputs`,
and `State`.

The factory wrapper replaces only completion/disposal lifetime so the DI scope
that owns scoped source/stage/sink services is disposed exactly once when the run
completes or when the caller disposes the run manually.

The built-in DI factory overrides `StartAsync()`. The default interface
implementation throws instead of bridging through `Start()` so custom factories
must explicitly implement asynchronous startup when they support it.

## Hosted Service

```csharp
services.AddSmartPipeHostedService<Order, OrderDto>(
    "orders",
    builder => builder
        .UseSource<OrderSource>()
        .UseStage<OrderStage>()
        .UseSink<OrderSink>());
```

The hosted service starts a typed run on `ExecuteAsync` and drains/disposes the
run during stop.
