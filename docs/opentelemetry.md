# OpenTelemetry

`SmartPipe.Extensions.OpenTelemetry` registers the diagnostics sources that
`SmartPipe.Core` already emits into the standard OpenTelemetry builder. Core
remains the single owner of telemetry emission; this package never creates a
second telemetry pipeline, never selects an exporter, and never rewrites metric
or activity names.

## Registration

The sample assumes the application installs its own OpenTelemetry packages:

```text
dotnet add package OpenTelemetry.Extensions.Hosting --version 1.17.0
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol --version 1.17.0
```

```csharp
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SmartPipe.Extensions.OpenTelemetry;

var services = new ServiceCollection();

services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddOtlpExporter())   // application-owned exporter
    .WithTracing(tracing => tracing.AddOtlpExporter())
    .AddSmartPipeInstrumentation();
```

`AddSmartPipeInstrumentation(this IOpenTelemetryBuilder)`:

- null-checks the builder and returns the same instance;
- registers `SmartPipeDiagnostics.MeterName` with the meter provider builder
  and `SmartPipeDiagnostics.ActivitySourceName` with the tracer provider
  builder;
- does not build a provider, start background work, create `Meter` or
  `ActivitySource` instances, select exporters, or configure resource, sampler,
  view, processor, or reader settings.

The package depends only on `SmartPipe.Core` and
`OpenTelemetry.Api.ProviderBuilderExtensions`. Exporters (OTLP, Console,
Prometheus, vendor), the OpenTelemetry SDK, and its hosting integration remain
application-owned. The Prometheus exporter currently ships as a prerelease in
OpenTelemetry .NET; it stays an optional, application-owned interoperability
path outside this package's compatibility promise. SmartPipe does not promise
support for any specific backend or vendor.

## Automatic instrumentation

Manual SDK registration, as shown above, is the supported path. OpenTelemetry
.NET auto-instrumentation environment variables may observe the same
`SmartPipe.Core` sources, but they remain an optional, experimental
application concern. They are not part of this package's API, its
compatibility promise, or its NativeAOT contract, and no profiler or startup
hook dependency is added.

## Idempotency and failure contract

Repeated successful calls on the same `IServiceCollection` produce one logical
SmartPipe registration for metrics and one for tracing. Different service
collections are independent. There is no static global registration state;
idempotency is detected from a marker descriptor stored in the current
`IServiceCollection`.

OpenTelemetry builder configuration is not a transaction SmartPipe can roll
back. If an OpenTelemetry or third-party configuration callback throws,
application composition fails. Retrying the same partially-mutated
`IServiceCollection` after such a failure is not supported, and no fake
rollback is performed.

## Collected signals

The registered meter exposes the frozen 2.2.0 instrument set:

- `smartpipe.items.processed`, `.failed`, `.filtered`, `.dropped` (unit `items`)
- `smartpipe.output.items.dropped` (unit `items`)
- `smartpipe.observer.events.dropped` (unit `events`)
- `smartpipe.items.retried`, `.deadlettered`, `.duplicates_filtered`
  (unit `items`)
- `smartpipe.stage.duration`, `smartpipe.sink.duration` (unit `ms`)

The registered activity source emits `Pipeline.Run` and `Transform` activities
with the existing operation names, status mapping, and tag set. All current
instruments emit measurements with no tags (zero-tag); the allowed
low-cardinality dimension ceiling and the high-cardinality prohibitions are
defined in [Observability](observability.md). Run/trace identifiers remain
activity-only data and never become metric dimensions.

## Deployability

The package is trim- and NativeAOT-compatible by contract and adds no
reflection-based runtime behavior. NativeAOT support is bounded by what the
application's own OpenTelemetry package closure supports.
