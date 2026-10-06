# Project Core Technologies

## Runtime and language

- C# targeting `net10.0`.
- Repository SDK is pinned to `10.0.303` with `rollForward: disable` in
  `global.json`.
- Nullable reference types, implicit usings, deterministic builds, current
  analyzer level, locked restore, and warnings-as-errors for Release are enabled.
- Repository package version is `2.2.0`.

The SDK and dependency versions listed here are repository pins for the current
candidate; they are not claims that no newer servicing or prerelease version exists.

## Dependency management

Central package management is enabled in `Directory.Packages.props`, with
transitive pinning and per-project version overrides disabled.

Important current pins include:

- Microsoft runtime/integration cohort: `10.0.11`;
- CsvHelper `33.1.0`;
- Dapper `2.1.89`;
- Mapster `10.0.13`;
- Polly.Core `8.8.0`;
- Npgsql `10.0.3`;
- OpenTelemetry packages `1.17.0`.

Narrow packages declare only the dependencies required by their supported contract.
For example, HTTP does not acquire Polly implicitly, PostgreSql does not depend on
Dapper/EF/DI/OTel, and the facade is the only intentional broad compatibility
bundle.

## Build, tests, and repository checks

`SmartPipe.Core.slnx` groups the current source, test, repository-check, and
benchmark projects.

Tests use xUnit v3 on Microsoft Testing Platform. Repository checks validate:

- central packages and lock files;
- package graph and package projects;
- package ownership and type-forwarding destinations;
- current/release package metadata and versioning;
- immutable 2.1.2 baseline integrity;
- source, binary, trim, NativeAOT, and integration consumer scenarios;
- release artifact provenance and inventory;
- release/documentation contracts.

BenchmarkDotNet is used for measured performance work. Performance claims must be
backed by repeatable benchmark evidence rather than prose alone.

## Package and AOT boundaries

The canonical package-level AOT contract is stored in `eng/package-graph.json`.

Positive trim/AOT paths are validated with executable consumers. Reflection or
runtime-code-generation-heavy integrations keep narrower claims:

- JSON and HTTP.Json expose source-generated `JsonTypeInfo<T>` paths.
- CSV has a verified but non-blanket claim because CsvHelper mapping is dynamic.
- Dapper explicit-SQL entry points are annotated for reflection/dynamic-code risk.
- EF Core has no blanket claim; provider/query shape remains a consumer concern.
- Mapster runtime mapping is explicitly not a blanket trim/NativeAOT contract.
- DataAnnotations marks the reflection boundary; rule validation provides the
  reflection-free path.
- PostgreSql's positive claim is limited to the documented slim/static primitive
  path.

## CI and release infrastructure

Same-repository PR validation uses GitHub-hosted runners only. The retired
self-hosted runner is not part of the current contract.

Ordinary CI validates current mode. Explicit release validation produces one Linux
package artifact, verifies its source SHA/mode/version/inventory/hashes, replays the
same artifact on Windows, and uses the same artifact for PostgreSQL package
consumers. Publisher credentials are isolated to publication after prerequisite
validation gates.

Workflow mutation tests protect trigger shape, validation mode, immutable-artifact
reuse, publication dependencies, ancestry checks, recovery preflight, symbol
handling, and post-publication payload verification.

## Core technical constraints

- Runtime processing is in-process and non-durable.
- Backpressure and bounded memory are explicit contracts.
- Ownership/disposal semantics are part of public behavior.
- Core does not perform hidden service location, plugin discovery, or exporter
  configuration.
- Compatibility is multi-dimensional: source, binary, API, behavior, package
  graph, AOT/trimming, and release provenance are validated separately.
- Historical checkpoint counts or planned-package states must not be copied into
  current documentation; use the machine-readable graph and current evidence.
