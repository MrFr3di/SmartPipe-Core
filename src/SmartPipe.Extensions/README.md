
# SmartPipe.Extensions

Ready-to-use integrations for SmartPipe.Core: file, HTTP, database, mapping, validation, resilience, hosting, and health check components.

## Selectors (Data Sources)

| Selector | Library | Description |
|----------|---------|-------------|
| `HttpSelector<T>` | HttpClient + Polly | Fetch data from REST APIs |
| `EfCoreSelector<T>` | Entity Framework Core | Stream entities from database |
| `DapperSelector<T>` | Dapper | High-performance SQL queries |
| `CsvFileSource<T>` | CsvHelper | Read CSV files |
| `JsonFileSource<T>` | System.Text.Json | Read JSON arrays and NDJSON |
| `DeadLetterSource<T>` | System.Text.Json | Read persisted failed-item records |

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
| `AddSmartPipe<TIn,TOut>()` | Typed definition/factory DI registration |
| `AddSmartPipeHostedService<TIn,TOut>()` | Typed hosted-service registration |

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
- SmartPipe.Core 1.1.0 (included as dependency)
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


## License

MIT License — see [LICENSE](https://github.com/MrFr3di/SmartPipe-Core/blob/main/LICENSE) for details.
