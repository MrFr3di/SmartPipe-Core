# SmartPipe.Extensions

**One package. All integrations. Zero boilerplate.**
**Ready-to-use components for SmartPipe.Core — build ETL pipelines in minutes.**

Extensions for SmartPipe.Core providing ready-to-use selectors, transforms, sinks, and health checks for building production ETL pipelines in minutes.

## Selectors (Data Sources)

| Selector | Library | Description |
|----------|---------|-------------|
| `HttpSelector<T>` | HttpClient + Polly | Fetch data from REST APIs |
| `EfCoreSelector<T>` | Entity Framework Core | Stream entities from database |
| `DapperSelector<T>` | Dapper | High-performance SQL queries |
| `CsvFileSource<T>` | CsvHelper | Read CSV files |
| `JsonFileSource<T>` | System.Text.Json | Read JSON arrays and NDJSON |
| `DeadLetterSource<T>` | System.Text.Json | Replay failed items |

## Transforms

| Transform | Library | Description |
|-----------|---------|-------------|
| `JsonTransform<TIn,TOut>` | System.Text.Json | JSON serialization |
| `CsvTransform<TIn,TOut>` | CsvHelper | CSV parsing |
| `MapsterTransform<TIn,TOut>` | Mapster | Object mapping |
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
| `DeadLetterSink<T>` | System.Text.Json | Persist failed items to JSON |
| `HttpSink<T>` | HttpClient + Polly | Send data to REST APIs |
| `DbSink<T>` | Dapper | Insert into any database |
| `CsvFileSink<T>` | CsvHelper | Write CSV files |
| `JsonFileSink<T>` | System.Text.Json | Write JSON files |

## Health Checks

| Component | Description |
|-----------|-------------|
| `SmartPipeLivenessCheck` | Is pipeline alive? (Kubernetes liveness probe) |
| `SmartPipeReadinessCheck` | Can pipeline accept data? (Kubernetes readiness probe) |

## Hosting

| Component | Description |
|-----------|-------------|
| `SmartPipeHostedService` | ASP.NET Core BackgroundService |
| `AddSmartPipe<TIn,TOut>()` | DI registration for SmartPipeChannel |
| `AddSmartPipeResilience()` | DI registration for Polly pipelines |

## Streaming

| Component | Description |
|-----------|-------------|
| `ChannelMerge` | Merge two ChannelReader streams |

## Installation

```bash
dotnet add package SmartPipe.Extensions
```

## Requirements

- .NET 10.0+
- SmartPipe.Core 1.0.5 (included as dependency)
- **Zero additional dependencies required for basic usage**
- Individual features pull their own dependencies:
  - `HttpSelector` / `HttpSink` → Polly (via Microsoft.Extensions.Resilience)
  - `EfCoreSelector` → Entity Framework Core
  - `DapperSelector` / `DbSink` → Dapper
  - `MapsterTransform` → Mapster
  - `CsvFileSource` / `CsvFileSink` / `CsvTransform` → CsvHelper
  - `PollyResilienceTransform` → Polly.Core
  - `SmartPipeHostedService` / `SmartPipeHealthCheck` → Microsoft.Extensions.Hosting / HealthChecks
  - All other components (FilterTransform, ValidationTransform, ConditionalTransform, CompositeTransform, LoggerSink, DeadLetterSink, JsonFileSource, JsonFileSink) — **zero additional dependencies**

## What's New in v1.0.5

- **AddSmartPipe<TIn,TOut>()** — register SmartPipeChannel in ASP.NET Core DI
- **DeadLetterSink retry** — automatic IOException recovery with exponential backoff (100ms/200ms/400ms)
- **FilterValidationExtensions** — convert ValidationTransform to FilterTransform with `.ToFilter()`
- **SmartPipeServiceCollectionExtensions** — fluent DI registration for all pipeline configurations

## License

MIT License — see [LICENSE](https://github.com/MrFr3di/SmartPipe-Core/blob/main/LICENSE) for details.
