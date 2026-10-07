# SmartPipe documentation

SmartPipe documentation is organized by task and audience. Start with the
smallest layer that answers the question; implementation plans and release
evidence are maintainer material, not product guidance.

## New to SmartPipe

- [Getting started](getting-started.md) — install Core, build a pipeline, and
  choose integration packages.
- [Runtime contracts](runtime-contracts.md) — lifecycle, ownership,
  backpressure, failure precedence, observers, and output semantics.
- [Architecture](architecture.md) — stable runtime and package architecture.
- [Package reference](reference/packages.md) — graph-backed catalog of all
  active packages, lifecycle, publish order, AOT contract, and direct SmartPipe
  dependencies.

## How-to guides

- [Bounded output](recipes/bounded-output.md)
- [Graceful shutdown](recipes/graceful-shutdown.md)
- [Retry, timeout, and dead-letter handling](recipes/retry-timeout-deadletter.md)
- [Testing pipelines](recipes/testing-pipelines.md)

## Integrations

- [Dependency injection](dependency-injection.md)
- [Hosting](hosting.md)
- [Health checks](health-checks.md)
- [OpenTelemetry](opentelemetry.md)
- [PostgreSQL](postgresql.md)
- [Channels](channels.md)
- [Transforms](transforms.md)
- [Data annotations](data-annotations.md)
- [Logging](logging.md)

Package-specific READMEs under `src/` remain the NuGet-facing entry point for
each package.

## Reference

- [Configuration](configuration.md)
- [API overview](reference/api-overview.md)
- [AOT and trimming](aot-compatibility.md)
- [Observability](observability.md)
- [Observers](observers.md)
- [Resilience](resilience.md)
- [Package ownership](package-ownership.md)
- [Package reference](reference/packages.md)

## Upgrading

- [SmartPipe 2.2.1 servicing notes](releases/2.2.1.md)
- [2.2.0 → 2.2.1 servicing migration](migration/2.2.1-integration-packages.md)
- [2.2.0 → 2.2.1 compatibility](reference/compatibility/2.2.0-to-2.2.1.md)
- [SmartPipe 2.2.0 release notes](releases/2.2.0.md)
- [2.1.2 → 2.2.0 integration migration](migration/2.2.0-integration-packages.md)
- [2.1.2 → 2.2.0 compatibility matrix](reference/compatibility/2.1.2-to-2.2.0.md)
- [Core definition model migration](migration/2.2.0-core-definition-model.md)

## Contributing

- [Contribution policy](../CONTRIBUTING.md)
- [Technical validation reference](contributing.md)
- [Package authoring](contributing/package-authoring.md)
- [Security reporting](../SECURITY.md)
- [Support policy](../SUPPORT.md)
- [Versioning and compatibility](../VERSIONING.md)

## Maintainer and release material

- [Maintainer documentation](https://github.com/MrFr3di/SmartPipe-Core/blob/main/docs/maintainers/README.md)
- [SmartPipe 2.2 maintainer index](https://github.com/MrFr3di/SmartPipe-Core/blob/main/docs/maintainers/2.2.0/README.md)
- [Architecture decision records](adr/README.md)

Plans, exact-head evidence, readiness records, and release governance live under
`maintainers/` so they remain available without competing with product/reference
documentation.
