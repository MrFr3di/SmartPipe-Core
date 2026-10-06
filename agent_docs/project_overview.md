# Project Overview

## Purpose

SmartPipe.Core provides typed, bounded, in-process streaming pipelines for .NET 10.
A pipeline connects a source, zero or more transforms, and an optional sink through
typed processing envelopes. SmartPipe is not a distributed workflow engine, broker,
durable queue, crash-replay system, or exactly-once delivery system.

## Current release shape

The repository release version is `2.2.0`. The canonical release inventory is
`eng/package-graph.json`: 20 publishable/non-planned SmartPipe package IDs are in the
2.2 candidate; the broad facade uses the dedicated `compatibility-facade`
lifecycle while the remaining release packages are active.

The package model is intentionally modular:

- `SmartPipe.Core` owns runtime, definitions, lifecycle, resilience primitives,
  dead-letter contracts, diagnostics, metrics, and tracing sources.
- Narrow processing/data leaves own Channels, Transforms, Logging,
  DataAnnotations, JSON, CSV, Dapper, EF Core, and Mapster integrations.
- Narrow infrastructure/transport leaves own DependencyInjection, Hosting,
  HealthChecks, OpenTelemetry, HTTP, HTTP.Json, and Polly integration.
- `SmartPipe.Extensions.PostgreSql` is an optional provider-specific runtime leaf.
- `SmartPipe.Testing` is a separately published test-helper package.
- `SmartPipe.Extensions` is the broad 2.x compatibility facade/bundle. It has
  17 direct SmartPipe dependencies and an 18-ID SmartPipe closure including the
  facade itself; PostgreSql and Testing are intentionally outside that bundle.

New applications should prefer narrow leaves. The broad facade exists for
compatibility and for applications that deliberately want the bundled integration
surface.

## Compatibility model

The immutable 2.1.2 facade baseline contains 42 relevant public identities.
For 2.2.0:

- 23 identities are preserved through metadata type forwarding;
- 13 identities remain physically implemented in `SmartPipe.Extensions`;
- 6 HTTP/Polly identities are intentionally removed and require migration and
  recompilation.

The machine authorities are `eng/package-graph.json`,
`eng/package-ownership.json`, public API baselines, and the immutable
`eng/baselines/2.1.2/` assets. Human-readable migration and compatibility
material must remain projections of those contracts.

## Runtime architecture

The execution path is definition/builder -> activation -> runtime executor ->
source/workers/stages -> optional sink -> output/completion. `PipelineRun<T>`
owns one execution. Runtime-owned components are initialized and disposed by the
runtime; borrowed/application-owned components remain caller-owned.

Input, output, observer, and integration-specific bridges are bounded where the
contract requires bounded buffering. Per-envelope stage order is sequential;
multiple envelopes may execute concurrently, so cross-envelope output order is
not implicitly guaranteed.

Instance component graphs are single-use. Factory-backed definitions are the
supported route for sequential or concurrent reuse.

## Integration ownership

Core emits diagnostics but does not own exporters. OpenTelemetry integration only
registers Core sources with the standard builder.

DependencyInjection owns canonical keyed registrations, run factories, active-run
state, and bounded latest-terminal observations. Hosting orchestrates those
registrations. HealthChecks evaluates immutable observations. Those leaves remain
separate package boundaries.

HTTP owns transport only; HTTP.Json owns source-generated JSON codecs. Polly owns
the external resilience decorator and does not replace Core lifecycle semantics.

PostgreSql owns PostgreSQL-native binary COPY and LISTEN/NOTIFY integration over an
application-owned `NpgsqlDataSource`; ordinary SQL remains Dapper territory and
ORM query behavior remains EF Core territory.

## Build and release model

The repository uses the pinned SDK from `global.json`, central package management,
locked restore, package graph/ownership validation, package-specific trim/AOT
consumers, source/binary compatibility consumers, and hosted GitHub Actions.

Release validation produces one immutable package artifact and replays that exact
artifact through Windows and PostgreSQL validation. Publication must not repack the
candidate.

Documentation is part of the release contract: package READMEs are NuGet-facing,
`docs/` contains current product/reference/migration guidance, and
`docs/maintainers/` contains plans, exact-head evidence, readiness, and release
governance.

## Current 2.2 work

Checkpoint G is the release-completion checkpoint. SP220-17 and SP220-18 are already
true-merged into the checkpoint; their accepted task heads retain the facade/migration
and immutable-artifact release-validation evidence. The active implementation slice is
release-document finalization, which reconciles changelog, release notes, package
references, migration guidance, current handoff documentation, and documentation checks.
After that merge, Checkpoint G still requires a fresh exact-head release-validation run
before promotion.

Do not use old SP220-08/SP220-09 checkpoint SHAs or paused-candidate notes as current
state. Historical evidence belongs in completed plans/evidence, not in this current
project overview.
