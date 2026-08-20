# Health Checks

`SmartPipe.Extensions.HealthChecks` adds exact-key liveness and readiness checks to canonical pipelines registered with `SmartPipe.Extensions.DependencyInjection`.

```csharp
var registration = services.AddSmartPipe().AddPipeline(orderDefinition);
registration.AddLiveness();
registration.AddReadiness(options =>
{
    options.RunRequirement = SmartPipeReadinessRunRequirement.ActiveRunRequired;
    options.StaleAfter = TimeSpan.FromMinutes(1);
    options.QueueUtilizationDegradedThreshold = 0.90;
});
```

Liveness answers whether restart may be justified. Idle, active, completed, cancelled, and aborted pipelines are healthy. A latest runtime fault fails by default; activation failure is opt-in because it usually indicates dependency/configuration readiness. An active replacement suppresses an older terminal failure.

Readiness answers whether the pipeline can serve work. `ActiveRunRequired` is the default. Use `RegistrationOnly` for on-demand pipelines and `ActiveOrSuccessfulCompletion` for finite jobs. `Draining` is not ready. Initial-activity, stale-activity, and per-run queue policies apply only to running runs. `Degraded` normally maps to HTTP 200 in ASP.NET Core, so missing-run readiness uses the registration's hard failure status by default.

Default names are `smartpipe:liveness:{exact-key}` and `smartpipe:readiness:{exact-key}`. Default tags are `smartpipe`, the check-kind tag, and `smartpipe-pipeline:{exact-key}`. Aggregate defaults are `smartpipe:liveness` and `smartpipe:readiness`, with `smartpipe-aggregate`.

```csharp
services.AddHealthChecks()
    .AddSmartPipeAggregateLiveness()
    .AddSmartPipeAggregateReadiness();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(SmartPipeHealthCheckTags.Liveness),
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(SmartPipeHealthCheckTags.Readiness),
});
```

Options are isolated by health registration name. Invalid options fail Generic Host startup through `ValidateOnStart`, or first named materialization without a host. Active runs come from the canonical registry; DI retains at most one immutable latest terminal value per key. Multi-run and aggregate evaluation use explicit worst-status ranking and deterministic registration order.

Result data is bounded to primitive counts, exact strings, `Guid` strings, UTC ISO-8601 timestamps, and queue/capacity totals. The number of data entries and reported problem keys/runs is bounded; exact identity values such as `PipelineKey` are preserved without length truncation, hashing, normalization, or case change. Aggregate descriptions remain bounded by reporting counts instead of identities. Exceptions, payloads, metadata, providers, scopes, and delegates are not retained or emitted.

## Legacy 2.1 behavior

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

Runtime clock settings create run, activity, and snapshot timestamps. The
health-check `TimeProvider` defines the instant used for stale and initial
activity policy evaluation. Production hosts should normally use system UTC for
both clocks. Custom providers are intended for deterministic tests and
controlled hosts.

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
