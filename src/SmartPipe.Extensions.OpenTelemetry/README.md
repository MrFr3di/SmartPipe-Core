# SmartPipe.Extensions.OpenTelemetry

Exporter-neutral OpenTelemetry registration for SmartPipe pipeline diagnostics.

`SmartPipe.Core` owns its telemetry emission through the .NET diagnostics APIs:

- a `Meter` named `SmartPipe.Core` (see `SmartPipeDiagnostics.MeterName`);
- an `ActivitySource` named `SmartPipe.Core` (see `SmartPipeDiagnostics.ActivitySourceName`).

This package does **not** emit telemetry itself. It registers those existing
sources into the standard OpenTelemetry builder so the application decides how
signals are collected and exported.

## Usage

```csharp
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using SmartPipe.Extensions.OpenTelemetry;

var services = new ServiceCollection();

services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddOtlpExporter())
    .WithTracing(tracing => tracing.AddOtlpExporter())
    .AddSmartPipeInstrumentation();
```

`AddSmartPipeInstrumentation` is the only public API. It:

- null-checks the builder and returns the exact same builder instance;
- registers `AddMeter(SmartPipeDiagnostics.MeterName)` for metrics;
- registers `AddSource(SmartPipeDiagnostics.ActivitySourceName)` for tracing;
- does not build providers, does not start background work, does not create
  additional `Meter`/`ActivitySource` instances;
- does not select an exporter or configure resources, samplers, views,
  processors, or readers.

Repeated successful calls on the same `IServiceCollection` produce one logical
SmartPipe registration for metrics and one for tracing. Different service
collections remain independent. Retrying on the same collection after an
OpenTelemetry configuration callback threw is not supported.

## Emission vs registration vs export

| Concern | Owner |
|---|---|
| Emitting metrics and activities | `SmartPipe.Core` |
| Registering SmartPipe sources with OpenTelemetry | `SmartPipe.Extensions.OpenTelemetry` |
| Selecting and configuring exporters (OTLP, Console, Prometheus, vendor) | Application |

The production dependency surface is `SmartPipe.Core` and
`OpenTelemetry.Api.ProviderBuilderExtensions` only. No exporter, SDK hosting
integration, or instrumentation package is referenced by this package; an
application can change its exporter without rebuilding SmartPipe packages.

## Exporters

Any application-owned composition is valid: no exporter, OTLP, Console,
Prometheus, or a vendor exporter. OTLP is the recommended neutral production
example. OpenTelemetry .NET automatic instrumentation environment variables may
interoperate with the same sources, but they remain an optional, experimental
application concern; they are not part of this package's API or its
compatibility promise, and no profiler or startup hook dependency is added.
