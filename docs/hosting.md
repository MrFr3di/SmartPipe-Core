# Hosting

`SmartPipeHostedService<TInput,TOutput>` hosts a typed pipeline run through
`ISmartPipeFactory<TInput,TOutput>`.

```csharp
services.AddSmartPipeHostedService<Order, OrderDto>(
    "orders",
    builder => builder
        .UseSource<OrderSource>()
        .UseStage<OrderStage>()
        .UseSink<OrderSink>());

services
    .AddHealthChecks()
    .AddSmartPipeHealthCheck<Order, OrderDto>("orders");
```

The hosted service:

- creates a fresh typed runtime through the factory;
- starts the run when the service executes;
- observes run completion and faults;
- drains the run during stop;
- disposes runtime-owned components.

Hosted services should use cooperative sources and sinks so `DrainAsync` and
host shutdown can complete promptly.

Health checks use the typed run monitor registered by `AddSmartPipe` or
`AddSmartPipeHostedService`. They inspect `PipelineRunState` and immutable
metrics snapshots, including last processed time, queue depths, failed items,
and dead-lettered items.
