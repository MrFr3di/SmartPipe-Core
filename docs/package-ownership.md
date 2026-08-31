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

The HealthChecks leaf depends only on Core, DependencyInjection, DI abstractions, Diagnostics.HealthChecks, and Options. It does not depend on Hosting, ASP.NET Core, or the broad facade.

The four SP220-07 leaves do not reference the broad facade. DataAnnotations has
the single narrow leaf edge to Transforms required by `ToFilter`; other leaves
depend only on Core and Logging additionally uses Logging.Abstractions.
