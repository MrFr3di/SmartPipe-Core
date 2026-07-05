# Health Checks

SmartPipe.Extensions provides typed health checks for DI-registered pipelines.
Health checks read a typed run monitor; they do not require a singleton runtime
instance.

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

`AddSmartPipe<TInput,TOutput>()` registers
`ISmartPipeRunHealthMonitor<TInput,TOutput>`. The factory updates that monitor
when a run starts. The monitor exposes immutable snapshots containing:

- `PipelineRunState`;
- `StartedAtUtc`;
- `LastActivityAtUtc`;
- `LastProcessedAtUtc`;
- `InputQueueDepth` and `OutputQueueDepth`;
- `ItemsFailed`;
- `ItemsDropped`, `OutputItemsDropped`, and `ObserverEventsDropped`;
- `ItemsDeadLettered`;
- input and output capacities.

## Status Rules

- `Faulted` reports `Unhealthy`.
- `NotStarted` reports `Degraded` when
  `TreatNotStartedAsDegraded` is `true`.
- Queue depth reports `Degraded` when input or output utilization is greater
  than or equal to `QueueUtilizationDegradedThreshold`.
- Running or draining pipelines report `Degraded` when
  `LastActivityAtUtc` is older than `StaleAfter`.
- Running or draining pipelines with no activity report `Degraded` after
  `InitialActivityGracePeriod` only when `RequireInitialActivity` is `true`.
- Otherwise the pipeline reports `Healthy`.

Queue checks are capacity-aware. The default degraded threshold is `0.80`,
`StaleAfter` defaults to 30 seconds, and not-started pipelines default to
degraded. `RequireInitialActivity` defaults to `false`, so idle event-driven
pipelines remain healthy until they fault, report high queue utilization, or
report stale activity after activity has occurred.

Queue depths are observational point-in-time samples from runtime channels.
Health checks should treat them as current pressure indicators, not as durable
work accounting or synchronization guarantees.

For hosted services, `SmartPipeHostedFailureBehavior.MarkUnhealthyAndKeepHostAlive`
keeps the host process alive after a pipeline fault; the tracked run state then
causes the health check to report `Unhealthy`.

```csharp
services.Configure<SmartPipeHealthCheckOptions>(options =>
{
    options.QueueUtilizationDegradedThreshold = 0.90;
    options.StaleAfter = TimeSpan.FromMinutes(1);
    options.TreatNotStartedAsDegraded = false;
    options.RequireInitialActivity = true;
    options.InitialActivityGracePeriod = TimeSpan.FromMinutes(2);
});
```

The health-check data payload includes the pipeline id, run state, queue
depths, capacities, failed count, dead-lettered count, started timestamp, last
activity timestamp, and last processed timestamp.
