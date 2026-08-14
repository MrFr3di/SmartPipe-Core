# Package ownership

The machine-readable authority is `eng/package-ownership.json`.

| Surface | Implementation package | Compatibility package | Strategy |
|---|---|---|---|
| Canonical observation contracts | `SmartPipe.Extensions.DependencyInjection` | none | new 2.2 API |
| Key-based liveness/readiness API | `SmartPipe.Extensions.HealthChecks` | none | new 2.2 API |
| Legacy snapshot, monitor, options, and registration | `SmartPipe.Extensions` | `SmartPipe.Extensions` | quarantined compatibility implementation |

The HealthChecks leaf depends only on Core, DependencyInjection, DI abstractions, Diagnostics.HealthChecks, and Options. It does not depend on Hosting, ASP.NET Core, or the broad facade.
