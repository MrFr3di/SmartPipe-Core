# SmartPipe.Extensions.HealthChecks

Key-based liveness and readiness checks for canonical SmartPipe DI registrations.

```bash
dotnet add package SmartPipe.Extensions.HealthChecks
```

```csharp
var orders = services.AddSmartPipe().AddPipeline(orderDefinition);
orders.AddLiveness();
orders.AddReadiness();

services.AddHealthChecks()
    .AddSmartPipeAggregateLiveness()
    .AddSmartPipeAggregateReadiness();
```

Defaults preserve the exact `PipelineKey`. Observation storage is bounded to active snapshots plus one latest immutable terminal value per key. The package uses explicit DI registrations and no assembly scanning or runtime reflection, supporting trimming and NativeAOT.

The 2.1 generic-pair health API remains physically implemented in `SmartPipe.Extensions`.
