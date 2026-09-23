# Package ownership

The machine-readable authority is `eng/package-ownership.json`.

| Surface | Implementation package | Compatibility package | Strategy |
|---|---|---|---|
| Canonical observation contracts | `SmartPipe.Extensions.DependencyInjection` | none | new 2.2 API |
| Key-based liveness/readiness API | `SmartPipe.Extensions.HealthChecks` | none | new 2.2 API |
| Legacy snapshot, monitor, options, and registration | `SmartPipe.Extensions` | `SmartPipe.Extensions` | quarantined compatibility implementation |
| `ChannelMerge` | `SmartPipe.Extensions.Channels` | `SmartPipe.Extensions` | type forwarding |
| Composite, conditional, compression, and filter transforms | `SmartPipe.Extensions.Transforms` | `SmartPipe.Extensions` | type forwarding |
| `LoggerSink<T>` | `SmartPipe.Extensions.Logging` | `SmartPipe.Extensions` | type forwarding |
| `ValidationTransform<T>` and `ToFilter` | `SmartPipe.Extensions.DataAnnotations` | `SmartPipe.Extensions` | type forwarding |
| Canonical JSON pipeline definitions | `SmartPipe.Extensions.Json` | none | new 2.2 API |
| CSV file source, sink, transform, and strict definitions | `SmartPipe.Extensions.Csv` | `SmartPipe.Extensions` | legacy type forwarding plus new strict 2.2 API |
| Dapper selector and DB sink plus explicit-SQL definitions | `SmartPipe.Extensions.Dapper` | `SmartPipe.Extensions` | legacy type forwarding plus new explicit-SQL 2.2 API |
| Entity Framework Core query sources | `SmartPipe.Extensions.EntityFrameworkCore` | `SmartPipe.Extensions` | legacy type forwarding plus new provider-neutral 2.2 query sources |
| Mapster composition transform | `SmartPipe.Extensions.Mapster` | `SmartPipe.Extensions` | legacy type forwarding plus new composition-time isolation API |

The HealthChecks leaf depends only on Core, DependencyInjection, DI abstractions, Diagnostics.HealthChecks, and Options. It does not depend on Hosting, ASP.NET Core, or the broad facade.

The four SP220-07 leaves do not reference the broad facade. DataAnnotations has
the single narrow leaf edge to Transforms required by `ToFilter`; other leaves
depend only on Core and Logging additionally uses Logging.Abstractions.

`SmartPipe.Extensions.Csv` depends only on Core, CsvHelper, and
Logging.Abstractions. The broad facade keeps its direct CsvHelper dependency
through 2.2 as a compatibility quarantine; the CSV leaf has no dependency on
the facade, DI, Hosting, JSON, or HTTP packages.

`SmartPipe.Extensions.Dapper` depends only on Core, Dapper, and
Logging.Abstractions. The broad facade keeps its direct Dapper dependency while
the `DapperSelector<T>` and `DbSink<T>` forwarders exist, and the Dapper leaf has
no dependency on the facade, DI, Hosting, JSON, CSV, or HTTP packages.

`SmartPipe.Extensions.EntityFrameworkCore` depends only on Core, `Microsoft.EntityFrameworkCore`, and
Logging.Abstractions. The broad facade keeps its direct Entity Framework Core dependency while the
`EfCoreSelector<T>` forwarder exists. The leaf has no dependency on the facade, DI, Hosting, JSON,
CSV, Dapper, or HTTP packages, and no dependency on any Entity Framework Core provider.

`SmartPipe.Extensions.Mapster` depends only on Core and `Mapster`. The broad facade keeps its direct
Mapster dependency while the `MapsterTransform<TInput,TOutput>` forwarder exists, and the Mapster leaf has
no dependency on the facade, DI, Hosting, JSON, CSV, Dapper, Entity Framework Core, or HTTP packages. It
takes no logging dependency: composition and mapping are silent, and Core owns result classification.
