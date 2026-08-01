# Hosting

Install `SmartPipe.Extensions.Hosting` to run canonical DI registrations under the
.NET Generic Host. Every call to `RunAsHostedService` contributes immutable
metadata to one shared `SmartPipeHostedOrchestrator`; the number of pipelines
does not change the number of SmartPipe `IHostedService` registrations.

```csharp
using Microsoft.Extensions.Hosting;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;
using SmartPipe.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var smartPipe = builder.Services.AddSmartPipe();

smartPipe.AddPipeline(ordersDefinition)
    .RunAsHostedService(options =>
    {
        options.Order = 0;
        options.DrainTimeout = TimeSpan.FromSeconds(30);
        options.FailureBehavior =
            SmartPipeHostedPipelineFailureBehavior.StopApplication;
    });

smartPipe.AddPipeline(replayDefinition)
    .RunAsHostedService(options => options.Order = 10);

await builder.Build().RunAsync();
```

`PipelineKey` is the hosted identity. A key can have one hosted registration and
one active hosted run, regardless of its input/output types. Duplicate keys fail
during service registration. Registration performs no I/O, starts no pipeline,
and never builds an intermediate service provider.

## Lifecycle guarantees

The orchestrator materializes registrations in `(Order, registration order,
PipelineKey.Value ordinal)` order. It starts them sequentially and waits for each
DI factory to return a ready run before starting the next. It stops exactly that
materialized list in reverse order. `HostOptions.ServicesStartConcurrently` and
`ServicesStopConcurrently` can affect peer hosted services, but never parallelize
pipelines inside the SmartPipe orchestrator.

If startup of pipeline N fails or is cancelled, every previously started run is
aborted and disposed in reverse order. Cleanup uses
`CancellationToken.None`, so cancellation of the host startup token cannot skip
safety cleanup. The primary startup exception remains first; rollback failures
follow in actual reverse cleanup order.

Normal shutdown first attempts graceful drain. A timeout, caller cancellation,
or drain fault always causes `AbortAsync(CancellationToken.None)` before
`DisposeAsync`. An already completed run is disposed without a redundant drain or
abort. Cleanup continues after individual failures and reports errors in reverse
run order, then operation order. Host shutdown cancellation skips graceful drain
but does not skip abort or disposal.

Start, stop, and disposal are idempotent under races. Natural completion is not
an automatic restart signal: a hosted registration has at most one run for the
lifetime of that orchestrator.

## Completion and failure behavior

`SmartPipeHostedCompletionBehavior` controls successful finite completion:

- `KeepHostAlive` (default) leaves the application running.
- `StopApplication` requests application shutdown once.

`SmartPipeHostedPipelineFailureBehavior` controls an unexpected run fault:

- `StopApplication` (default) requests application shutdown once.
- `Rethrow` faults the orchestrator's `BackgroundService` task. The host then
  applies `HostOptions.BackgroundServiceExceptionBehavior`: `StopHost` stops the
  application, while `Ignore` leaves it running.
- `MarkUnhealthyAndKeepHostAlive` logs the fault and leaves the host alive for an
  external health integration to observe the canonical run state.
- `Ignore` logs the fault and leaves the host alive intentionally.

The Hosting leaf does not depend on HealthChecks. Its package closure is
`SmartPipe.Core`, `SmartPipe.Extensions.DependencyInjection`, and the Microsoft
Hosting, DI, and Logging abstractions. The DI factory remains the sole owner of
each run's async service scope; Hosting neither creates scopes nor resolves
pipeline components directly.

## Migrating from the 2.1 façade

See [Migrating Hosting to 2.2](migration/2.2.0-hosting.md) for a complete
package and API checklist.

The 2.1 API remains physically implemented in `SmartPipe.Extensions` for binary
and source compatibility:

```csharp
services.AddSmartPipeHostedService<Order, OrderDto>(
    "orders",
    pipeline => pipeline
        .UseSource<OrderSource>()
        .UseStage<OrderStage>()
        .UseSink<OrderSink>());
```

New code should register a canonical definition through the DI package and opt it
into the shared orchestrator:

```csharp
var smartPipe = services.AddSmartPipe();
smartPipe.AddPipeline(ordersDefinition)
    .RunAsHostedService(options =>
    {
        options.Order = 0;
        options.FailureBehavior =
            SmartPipeHostedPipelineFailureBehavior.StopApplication;
    });
```

This is an explicit migration, not a compatibility redirect. Legacy
`SmartPipeHostedService<TInput,TOutput>`, `SmartPipeHostedServiceOptions`,
`SmartPipeHostedFailureBehavior`, and `AddSmartPipeHostedService` keep their 2.1
behavior in the broad façade for the 2.x line. They are not forwarded into the
canonical Hosting package.
