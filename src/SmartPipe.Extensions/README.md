
# SmartPipe.Extensions

Ready-to-use integrations for SmartPipe.Core: file, HTTP, database, mapping, validation, resilience, hosting, and health check components.

## Selectors (Data Sources)

| Selector | Library | Description |
|----------|---------|-------------|
| `HttpSelector<T>` | HttpClient + Polly | Fetch data from REST APIs |
| `EfCoreSelector<T>` | Entity Framework Core | Stream entities from database |
| `DapperSelector<T>` | Dapper | High-performance SQL queries |
| `CsvFileSource<T>` | CsvHelper | Read CSV files |
| `JsonFileSource<T>` | SmartPipe.Extensions.Json / System.Text.Json | Read JSON arrays and NDJSON |
| `DeadLetterSource<T>` | SmartPipe.Extensions.Json / System.Text.Json | Read persisted failed-item records |

`EfCoreSelector<T>` uses no-tracking queries by default for read-only pipeline
source scenarios. Call `.WithTracking()` when returned entities must remain
tracked by the supplied `DbContext`.

`DapperSelector<T>` uses asynchronous open and read operations when the supplied
connection is a `DbConnection`. Non-`DbConnection` `IDbConnection`
implementations remain a synchronous compatibility fallback. Externally
supplied connections are left open by default; use the explicit `DbConnection`
ownership overload with `leaveOpen: false` when the selector should dispose the
connection.

## Transforms

| Transform | Library | Description |
|-----------|---------|-------------|
| `JsonTransform<TIn,TOut>` | SmartPipe.Extensions.Json / System.Text.Json | JSON serialization |
| `CsvTransform<TIn,TOut>` | CsvHelper | CSV parsing |
| `MapsterTransform<TIn,TOut>` | Mapster | Runtime object mapping |
| `CompressionTransform` | System.IO.Compression | Brotli/GZip compression |
| `PollyResilienceTransform<T>` | Polly v8 | Retry/CircuitBreaker/Hedging |
| `FilterTransform<T>` | — | Predicate-based filtering with And/Or/Not |
| `ValidationTransform<T>` | DataAnnotations | Data validation with custom rules |
| `ConditionalTransform<T>` | — | Apply transform only when condition met |
| `CompositeTransform<T>` | — | Chain multiple transforms into one |

## Sinks (Data Destinations)

| Sink | Library | Description |
|------|---------|-------------|
| `LoggerSink<T>` | ILogger | Structured logging |
| `DeadLetterSink<T>` | SmartPipe.Extensions.Json / System.Text.Json | Persist failed items to JSON |
| `HttpSink<T>` | HttpClient + Polly | Send data to REST APIs |
| `DbSink<T>` | Dapper | Insert into any database |
| `CsvFileSink<T>` | CsvHelper | Write CSV files |
| `JsonFileSink<T>` | SmartPipe.Extensions.Json / System.Text.Json | Write JSON files |

## HTTP Integrations

Use `HttpClientFactorySelector<T>` and `HttpClientFactorySink<T>` in DI-based
applications so clients come from `IHttpClientFactory` named or default client
configuration. Low-level `HttpSelector<T>` and `HttpSink<T>` remain available
for callers that already own an `HttpClient`.

HTTP JSON components accept source-generated `JsonTypeInfo<T>` /
`JsonTypeInfo<List<T>>` overloads for NativeAOT and trimming-sensitive apps.
`HttpSelector<T>` can read either buffered JSON arrays or streaming responses
using `HttpSelectorStreamingMode.JsonArray` and
`HttpSelectorStreamingMode.Ndjson`; the factory-backed selector exposes the
same streaming modes.
`HttpSink<T>` can send the envelope `TraceId` as an `Idempotency-Key` header for
idempotent endpoints.

Avoid configuring retry in both SmartPipe stage policies and HTTP/Polly client
pipelines for the same operation unless that layered retry budget is
intentional.

## Health Checks

| Component | Description |
|-----------|-------------|
| `SmartPipeLivenessCheck` | Is pipeline alive? (Kubernetes liveness probe) |
| `SmartPipeReadinessCheck` | Can pipeline accept data? (Kubernetes readiness probe) |

## Hosting

| Component | Description |
|-----------|-------------|
| `SmartPipeHostedService` | ASP.NET Core BackgroundService |
| `AddSmartPipe<TIn,TOut>()` | Typed definition/factory DI registration |
| `AddSmartPipeHostedService<TIn,TOut>()` | Typed hosted-service registration |

`SmartPipeHostedServiceOptions` controls hosted fault behavior and drain
timeout. The default fault behavior is `StopApplication`; use `Rethrow`,
`MarkUnhealthyAndKeepHostAlive`, or `Ignore` only when that lifecycle policy is
intentional for the host.

## Streaming

| Component | Description |
|-----------|-------------|
| `ChannelMerge` | Merge two ChannelReader streams |

Use `ChannelMerge.Merge(first, second, options, cancellationToken)` for bounded
or backpressure-sensitive merges that need cancellation to flow into source
reads and output writes. The compatibility overload without a cancellation
token remains available.

## Installation

```bash
dotnet add package SmartPipe.Extensions --version 2.1.2
```

For narrow SP220-07 integrations, install `SmartPipe.Extensions.Channels`,
`SmartPipe.Extensions.Transforms`, `SmartPipe.Extensions.Logging`, or
`SmartPipe.Extensions.DataAnnotations` directly. The broad package forwards the
existing public types and pulls these leaves only as a compatibility facade.

For JSON-only integrations, prefer:

```bash
dotnet add package SmartPipe.Extensions.Json --version 2.1.2
```

## JSON Package Migration

JSON file, transform, and JSON dead-letter implementations moved to
`SmartPipe.Extensions.Json` in version 2.1.2. Public namespaces did not change.

`SmartPipe.Extensions` 2.1.2 retains type forwarders and a transitive package
dependency, so existing 2.x source and binary consumers remain compatible. New
applications should reference `SmartPipe.Extensions.Json` directly. The bridge
is planned for removal in SmartPipe 3.0.

## Requirements

- .NET 10.0+
- SmartPipe.Core 2.1.2 (included as dependency)
- This package intentionally includes integration dependencies for the features below.
- Individual features pull their own dependencies:
  - `HttpSelector` / `HttpSink` → Polly (via Microsoft.Extensions.Resilience)
  - `EfCoreSelector` → Entity Framework Core
  - `DapperSelector` / `DbSink` → Dapper
  - `MapsterTransform` → Mapster
  - `CsvFileSource` / `CsvFileSink` / `CsvTransform` → CsvHelper
  - `PollyResilienceTransform` → Polly.Core
  - `SmartPipeHostedService` / `SmartPipeHealthCheck` → Microsoft.Extensions.Hosting / HealthChecks
  - Other components use platform APIs or dependencies already carried by this package.

### Trimming and NativeAOT

`MapsterTransform<TIn,TOut>` uses Mapster runtime mapping metadata and runtime
expression compilation. It is supported for normal runtime consumers, but is not
trim- or NativeAOT-safe. For trimmed or NativeAOT applications, prefer a
hand-written mapper, a source-generated mapper, or `PipelineTransformer.FromFunc`.


## License

MIT License — see [LICENSE](https://github.com/MrFr3di/SmartPipe-Core/blob/main/LICENSE) for details.
