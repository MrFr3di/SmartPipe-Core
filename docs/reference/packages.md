# Package reference

This page is a human-readable projection of `eng/package-graph.json`. The
`verify-docs` repository check rejects drift in package ID, lifecycle, publish
order, AOT contract, or direct SmartPipe dependency data.

For package purpose, usage, ownership, and examples, follow the package README.

| Package | Lifecycle | Publish order | AOT contract | Direct SmartPipe dependencies |
| --- | --- | ---: | --- | --- |
| `SmartPipe.Core` | active | 1 | `full` | — |
| `SmartPipe.Extensions.Channels` | active | 2 | `full` | `SmartPipe.Core` |
| `SmartPipe.Extensions.Transforms` | active | 3 | `full` | `SmartPipe.Core` |
| `SmartPipe.Extensions.Logging` | active | 4 | `full` | `SmartPipe.Core` |
| `SmartPipe.Extensions.Json` | active | 5 | `full-json-type-info` | `SmartPipe.Core` |
| `SmartPipe.Extensions.Csv` | active | 6 | `verified-no-blanket` | `SmartPipe.Core` |
| `SmartPipe.Extensions.Dapper` | active | 7 | `explicit-sql` | `SmartPipe.Core` |
| `SmartPipe.Extensions.EntityFrameworkCore` | active | 8 | `no-blanket` | `SmartPipe.Core` |
| `SmartPipe.Extensions.Mapster` | active | 9 | `unsupported-blanket` | `SmartPipe.Core` |
| `SmartPipe.Extensions.Polly` | active | 10 | `verified` | `SmartPipe.Core` |
| `SmartPipe.Extensions.Http` | active | 11 | `transport-full` | `SmartPipe.Core` |
| `SmartPipe.Testing` | active | 12 | `not-runtime` | `SmartPipe.Core` |
| `SmartPipe.Extensions.Http.Json` | active | 13 | `full-json-type-info` | `SmartPipe.Extensions.Http`, `SmartPipe.Extensions.Json` |
| `SmartPipe.Extensions.DependencyInjection` | active | 14 | `full` | `SmartPipe.Core` |
| `SmartPipe.Extensions.OpenTelemetry` | active | 15 | `verified` | `SmartPipe.Core` |
| `SmartPipe.Extensions.Hosting` | active | 16 | `full` | `SmartPipe.Core`, `SmartPipe.Extensions.DependencyInjection` |
| `SmartPipe.Extensions.HealthChecks` | active | 17 | `full` | `SmartPipe.Core`, `SmartPipe.Extensions.DependencyInjection` |
| `SmartPipe.Extensions.DataAnnotations` | active | 18 | `annotated-reflection` | `SmartPipe.Core`, `SmartPipe.Extensions.Transforms` |
| `SmartPipe.Extensions` | compatibility-facade | 19 | `no-blanket` | `SmartPipe.Core`, `SmartPipe.Extensions.Channels`, `SmartPipe.Extensions.Transforms`, `SmartPipe.Extensions.Logging`, `SmartPipe.Extensions.Json`, `SmartPipe.Extensions.Csv`, `SmartPipe.Extensions.Dapper`, `SmartPipe.Extensions.EntityFrameworkCore`, `SmartPipe.Extensions.Mapster`, `SmartPipe.Extensions.Polly`, `SmartPipe.Extensions.Http`, `SmartPipe.Extensions.Http.Json`, `SmartPipe.Extensions.DependencyInjection`, `SmartPipe.Extensions.OpenTelemetry`, `SmartPipe.Extensions.Hosting`, `SmartPipe.Extensions.HealthChecks`, `SmartPipe.Extensions.DataAnnotations` |
| `SmartPipe.Extensions.PostgreSql` | active | 20 | `verified` | `SmartPipe.Core` |

## Interpretation

- `active` packages are normal release packages.
- `compatibility-facade` is the broad 2.x compatibility bundle.
- AOT values are package-scoped contracts, not a repository-wide blanket claim.
- Dependencies are the current direct SmartPipe edges from the canonical graph;
  transitive closure is intentionally not duplicated here.

The exact dependency policy, including external-package allowances and release
mode, remains normative in `eng/package-graph.json`.
