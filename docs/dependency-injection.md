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

`ISmartPipeFactory<TInput,TOutput>.Start()` creates a fresh service scope and a
fresh runtime. Scoped dependencies are resolved inside that run scope and are
disposed with it.

Use `ValidateScopes = true` in tests and hosted applications to catch accidental
root-scope captures.

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
