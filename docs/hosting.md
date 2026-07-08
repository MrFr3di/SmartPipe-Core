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

During `StopAsync`, the hosted service attempts graceful drain, base hosted
service stop, and pipeline disposal independently. If the host shutdown token is
already cancelled when `StopAsync` begins, graceful drain is skipped, but base
stop and disposal are still attempted. If the host token is active, the drain
request links that token with `DrainTimeout`. When multiple shutdown steps
fail, the reported `AggregateException` preserves drain, base stop, then
disposal ordering.

Health checks use the typed run monitor registered by `AddSmartPipe` or
`AddSmartPipeHostedService`. They inspect `PipelineRunState` and immutable
metrics snapshots, including started time, last activity time, last processed
time, queue depths, failed items, and dead-lettered items. Running hosted
pipelines with no initial activity remain healthy by default; set
`SmartPipeHealthCheckOptions.RequireInitialActivity` when a hosted pipeline must
produce activity within `InitialActivityGracePeriod`.
