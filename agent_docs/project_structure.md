# Project Structure

## Source packages

The authoritative package inventory and publish order are in
`eng/package-graph.json`. The current 2.2 candidate contains 20 publishable
SmartPipe package IDs.

Core and compatibility:

- `src/SmartPipe.Core/` — runtime, typed contracts, definitions, lifecycle,
  resilience primitives, dead-letter contracts, diagnostics and metrics.
- `src/SmartPipe.Extensions/` — broad 2.x compatibility facade/bundle; retained
  legacy implementations and type forwarders live here.

Processing and data leaves:

- `src/SmartPipe.Extensions.Channels/`
- `src/SmartPipe.Extensions.Transforms/`
- `src/SmartPipe.Extensions.Logging/`
- `src/SmartPipe.Extensions.DataAnnotations/`
- `src/SmartPipe.Extensions.Json/`
- `src/SmartPipe.Extensions.Csv/`
- `src/SmartPipe.Extensions.Dapper/`
- `src/SmartPipe.Extensions.EntityFrameworkCore/`
- `src/SmartPipe.Extensions.Mapster/`

Transport, infrastructure, and provider leaves:

- `src/SmartPipe.Extensions.Http/`
- `src/SmartPipe.Extensions.Http.Json/`
- `src/SmartPipe.Extensions.Polly/`
- `src/SmartPipe.Extensions.DependencyInjection/`
- `src/SmartPipe.Extensions.OpenTelemetry/`
- `src/SmartPipe.Extensions.Hosting/`
- `src/SmartPipe.Extensions.HealthChecks/`
- `src/SmartPipe.Extensions.PostgreSql/`

Testing:

- `src/SmartPipe.Testing/` — framework-neutral test helpers; not a runtime
  dependency of the facade.

Shared implementation seams such as bounded JSON framing live under
`src/Shared/` and remain internal.

## Tests and consumers

`tests/` contains package/unit/integration/repository-check projects and
`tests/Consumers/Scenarios/` contains packed-package executable consumers.

Consumer coverage includes direct package use, facade source compatibility,
unchanged 2.1.2 binary consumers, package metadata, trim, NativeAOT, and
integration-specific scenarios. PostgreSQL has real-server integration coverage;
HTTP/JSON/DI/Hosting/Health/OpenTelemetry/Polly and other leaves have package-scoped
consumer coverage matching their documented claims.

Do not infer current scenario counts from old checkpoint notes. The canonical
selection is `eng/consumer-scenarios.json` plus `eng/package-graph.json`.

## Engineering metadata

`eng/` contains:

- package build props/targets;
- package graph and ownership manifests;
- immutable compatibility baselines;
- RepositoryChecks implementation;
- consumer scenario definitions/templates;
- workflow and release-validation contract tests;
- package scaffolding and release tooling.

The package graph is normative for lifecycle, dependency policy, publish order,
and AOT contract. The ownership manifest is normative for implementation ownership,
forwarding, retention, and intentional removals.

## Documentation

- Root `README.md` is the SmartPipe.Core/NuGet-facing entry point.
- `src/*/README.md` files are package/NuGet-facing documentation.
- `docs/` contains current product, architecture, reference, recipes, migration,
  release notes, and DocFX navigation.
- `docs/maintainers/` contains release plans, evidence, readiness, and governance.
- `CHANGELOG.md` is the chronological release-facing change record.
- `agent_docs/` is current agent context and must not preserve stale
  "current/paused" checkpoint state; historical execution state belongs in
  maintainer evidence or completed plans.

## CI and release layout

`.github/workflows/` contains ordinary CI, documentation, security analysis, and
release/publication orchestration. Current release validation uses GitHub-hosted
runners and one immutable producer package artifact. Windows and PostgreSQL replay
that artifact rather than repacking it.

`eng/baselines/2.1.2/` is immutable compatibility evidence and must not be
regenerated to make a candidate pass.

## Current handoff

The active release-completion area is Checkpoint G:

- SP220-17 — facade/type-forwarding/migration compatibility, already true-merged;
- SP220-18 — immutable artifact and release validation, already true-merged;
- 2.2.0 release-document finalization — the active slice for changelog/release-note/package-doc
  reconciliation and stronger documentation verification;
- after finalization merges, run a fresh exact-head release-validation workflow on the
  resulting Checkpoint G SHA before promotion.

Old Checkpoint D / SP220-08 / paused SP220-09 state is historical and must not be
used as the current repository handoff.
