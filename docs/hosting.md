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

Hosted services receive the same factory-created run contract as direct factory
callers. Health checks and hosted shutdown observe the underlying run state and
metrics while the factory wrapper owns DI scope disposal.

Hosted pipeline faults are not log-only by default. Configure
`SmartPipeHostedServiceOptions`:

```csharp
services.Configure<SmartPipeHostedServiceOptions>(options =>
{
    options.FailureBehavior = SmartPipeHostedFailureBehavior.StopApplication;
    options.DrainTimeout = TimeSpan.FromSeconds(30);
});
```

`StopApplication` is the default and requests host shutdown through
`IHostApplicationLifetime`. `Rethrow` faults the hosted execution task.
`MarkUnhealthyAndKeepHostAlive` keeps the host alive so health checks can report
the tracked faulted run. `Ignore` is an explicit log-and-continue choice.

Hosted services should use cooperative sources and sinks so `DrainAsync` and
host shutdown can complete promptly.

Health checks use the typed run monitor registered by `AddSmartPipe` or
`AddSmartPipeHostedService`. They inspect `PipelineRunState` and immutable
metrics snapshots, including last processed time, queue depths, failed items,
and dead-lettered items.
