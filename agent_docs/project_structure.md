# Project Structure

## Directory Layout

- `src/SmartPipe.Core/` — core typed pipeline runtime; `Runtime/` is split into
  `Execution`, `Definitions`, `Channels`, and `Activation`.
- `src/SmartPipe.Extensions/` — broad integrations, grouped into `Selectors`,
  `Transforms`, and `Sinks`.
- `src/SmartPipe.Extensions.Json/` — JSON selectors/transforms/sinks,
  dead-letter persistence, reusable definition adapters, and private metadata
  snapshot support.
- `src/SmartPipe.Extensions.Csv/` — strict bounded CSV source/sink definition
  adapters plus the physically moved legacy CSV source, sink, and transform.
- `src/SmartPipe.Extensions.DependencyInjection/` — factory-owned run
  observation contracts, active registry composition, and bounded terminal
  retention used by HealthChecks.
- `src/SmartPipe.Extensions.HealthChecks/` — canonical liveness/readiness
  registrations, options, evaluators, aggregate policies, and standard
  HealthCheckService integration.
- `src/SmartPipe.Extensions.Channels/` — narrow N-reader `ChannelMerge` leaf.
- `src/SmartPipe.Extensions.Transforms/` — composite, conditional,
  compression, filtering, and framework-free rule-validation leaves.
- `src/SmartPipe.Extensions.Logging/` — compatible `LoggerSink<T>` and safe
  logging options leaf.
- `src/SmartPipe.Extensions.DataAnnotations/` — BCL validation transform and
  filter bridge leaf.
- `tests/` — core, extension, JSON, repository-check projects, process
  fixtures, consumer scenarios, and shared testing assets. JSON definition
  contracts and four current JSON consumer paths live here.
- `src/Shared/JsonFraming/` — internal linked BCL-only UTF-8 line framing used
  by the JSON package.
- `benchmarks/` — BenchmarkDotNet projects, including 21 method-scoped JSON
  definition cases.
- `eng/` — package build targets, repository-check implementation, package
  graph/ownership data, baselines, templates, consumer scenarios, and workflow
  contracts. Retired runner tooling is historical.
- `docs/` — tracked architecture, configuration, runtime, resilience,
  migration, recipe, and contribution documentation.
- `.agent/exec-plans/active/` — active execution plans; these are operational
  state, not product source.

## Modules and Responsibilities

`SmartPipe.Core` contains pipeline builders/definitions, envelope and result
types, stage execution, sink execution, lifecycle control, bounded channel
factories, retries/timeouts/circuit breakers, dead-letter contracts, metrics,
activities, and event sources. `SmartPipe.Extensions` adapts external HTTP,
database, CSV, mapping, hosting, and legacy health-check functionality.
DependencyInjection owns run observations used by the canonical HealthChecks
leaf. The four SP220-07 leaves own moved implementations and depend only on
their narrow edges: Channels → Core, Transforms → Core, Logging → Core plus
logging abstractions, and DataAnnotations → Core plus Transforms. The JSON leaf
contains JSON file/dead-letter/transform implementations and reusable
definition adapters; it depends on Core plus logging abstractions and links the
internal framing helper.

## Main Interfaces and Integration Boundaries

The primary typed boundaries are `IPipelineSource<T>`,
`IPipelineTransformer<TInput,TOutput>`, `IPipelineSink<T>`,
`PipelineRun<TOutput>`, `PipelineRuntimeOptions`, `StageFailureOptions`, and
the definition/component ownership APIs. SP220-07 adds `ChannelMerge`, the
token-aware Filter path, frozen framework-free rule validation, and additive
safe Logger options. SP220-08 adds `JsonPipelineComponents`, typed JSON
definition builders/extensions, resolver-backed metadata snapshots, and the
internal `Utf8LineRecordReader` seam. `SmartPipe.Extensions` forwards moved
types for 2.x source, binary, and reflection compatibility; leaves do not
reference the broad facade.

## Tests and Supporting Assets

The solution file includes the three source packages, repository checks, the
Core/Extensions/Extensions.Json test projects, and a repository process
fixture. Consumer projects under `tests/Consumers/Scenarios/` cover direct,
trimmed, NativeAOT, JSON, extension metadata, and legacy compatibility paths.
HealthChecks direct, ASP.NET, trim, and NativeAOT scenarios live under
`tests/Consumers/Scenarios/health-checks-*`; feature tests are under
`tests/SmartPipe.Extensions.HealthChecks.Tests/` and the DI observation tests
are under `tests/SmartPipe.Extensions.DependencyInjection.Tests/`. SP220-07
adds leaf test projects for Channels, Transforms, Logging, and DataAnnotations,
five current consumer scenarios, and seven benchmark paths.
Package lock files sit beside projects; release baselines are under
`eng/baselines/`.

## Agent Workflow Boundaries

- The existing `eng/SmartPipe.RepositoryChecks` project is the home for
  deterministic context, verification, and evidence commands; no separate
  tool project was introduced.
- The completed `.agent/exec-plans/completed/2026-08-24-sp220-08-json-integration.md`
  records the finished SP220-08 deployment contract. The tracked
  `eng/verification-profiles.json` contains only reusable executable gate
  recipes for CI and fresh clones, avoiding a duplicate task manifest.
- The reusable release workflow invokes the `sp220-05` profile for duplicate
  source gates while retaining specialized package, baseline, audit, and
  consumer gates. Consumer logs remain local; only bounded result artifacts
  are uploaded.
- Same-repository pull requests use standard GitHub-hosted CI, CodeQL, and
  Dependency Review; non-PR hosted routes remain unchanged. The workflow
  contract tests own trigger, restore-source, cache, and mutation checks.
  Retired self-hosted runner assets are historical and are not part of the
  current runtime or release contract.

## Current handoff

Heavy deployment `sp220-08-20260831-0439163` is complete. PR #65 and the
servicing PR #66 established the accepted post-servicing Checkpoint-D base
`3ae9a52398e9101cde34d09f4a04769eeab3c4dc`; PR #70 then true-merged the JSON
contribution head `1b5210a` into Checkpoint D as
`741892d4f41345ad7585ce1ecc58c200d8e6d1b0`. Promotion PR #71 true-merged
release `0439163f43391f219a70a9008e7dafa591d4ae35`.

The deployment delivered hosted-CI restoration and servicing, reusable JSON
definition adapters, the internal framing seam, four JSON consumer scenarios,
35 manifest scenarios, 21 JSON benchmark cases, package metadata, and tracked
JSON documentation. Checkpoint-D run `33407952886` and exact release-head CI
`33411352721` plus CodeQL `33411352437` passed. Package publication and
SP220-09 remain separate future scope.


## SP220-09 CSV Addition

The CSV leaf owns `CsvOptions`, map-registration snapshots, component factories,
typed definition builders, the decoded-character `CsvLogicalRecordFramer`,
bounded reader/writer helpers, strict source/sink implementations, and the
legacy CSV implementations. `SmartPipe.Extensions/CsvTypeForwarders.cs`
contains only facade forwarding attributes. Repository manifests under
`eng/` own package graph, physical type ownership, and consumer selection;
CSV package locks sit beside the leaf and its tests.

The strict source creates one typed logger at activation when logging is
required, retains one CsvHelper mapping context per run, and composes
activation/enumeration cancellation for the iterator lifetime. The strict sink
uses a bounded record staging boundary, Create/Append header and BOM preflight,
per-record checkpoint/rollback, and single-flight disposal. Internal stream
factories are test seams only; public APIs remain file-oriented.
