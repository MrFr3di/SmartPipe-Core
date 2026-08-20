# ADR-0001: SmartPipe 2.2 package boundaries and integration model

- Status: Accepted for implementation; compatibility ownership partially superseded by [ADR-0002](0002-smartpipe-2.2-legacy-compatibility-quarantine.md)
- Date: 2026-07-15
- Decision owners: SmartPipe maintainers
- Target release: 2.2.0
- Baseline commit: `8e79902d22de714f493582946f7c260462b0895e`

## Context

SmartPipe 2.1.x combines the stable execution runtime with a monolithic integration package. Release 2.2.0 must establish independently reviewable integration packages without duplicating runtime semantics or breaking existing consumers. The complete implementation requirements are maintained in the [2.2.0 extension architecture plan](../plans/2.2.0-extension-architecture.md); branch and review controls are defined by the [2.2.0 governance policy](../governance/2.2.0-branch-and-review-policy.md), and baseline work is decomposed in the [SP220-00 detailed plan](../plans/2.2.0/SP220-00-governance-and-baseline.md).

## Decision

`SmartPipe.Core` remains the sole owner of runtime contracts, immutable pipeline definitions, execution plans, run lifecycle, cancellation, diagnostics, and metrics. External technologies are isolated in dedicated `SmartPipe.Extensions.*` leaf packages. `SmartPipe.Extensions` becomes a compatibility facade and convenience bundle, not a second implementation surface.

All official packages ship in lockstep at version `2.2.0`. Internal package dependencies specify a minimum of `2.2.0` without an exact range or upper bound unless a separately reviewed compatibility constraint proves one necessary.

## Package map

| Package | Responsibility | Direct SmartPipe dependencies |
|---|---|---|
| `SmartPipe.Core` | Runtime, contracts, definition, plan, lifecycle, diagnostics | None |
| `SmartPipe.Extensions.DependencyInjection` | Keyed registration, registry/provider, run scopes | Core |
| `SmartPipe.Extensions.Hosting` | Generic Host orchestration | Core, DependencyInjection |
| `SmartPipe.Extensions.HealthChecks` | Liveness/readiness adapters | Core, DependencyInjection |
| `SmartPipe.Extensions.OpenTelemetry` | `Meter`/`ActivitySource` registration | Core |
| `SmartPipe.Extensions.Json` | System.Text.Json files, framing, dead letters | Core |
| `SmartPipe.Extensions.Csv` | CSV sources, sinks, transforms | Core |
| `SmartPipe.Extensions.Http` | HTTP transport | Core |
| `SmartPipe.Extensions.Http.Json` | JSON codecs over HTTP | Http, Json |
| `SmartPipe.Extensions.Dapper` | Dapper query, command, and batch integration | Core |
| `SmartPipe.Extensions.EntityFrameworkCore` | Provider-neutral EF Core streaming | Core |
| `SmartPipe.Extensions.Mapster` | Mapster transforms | Core |
| `SmartPipe.Extensions.Polly` | Resilience decorator | Core |
| `SmartPipe.Extensions.Transforms` | BCL transforms | Core |
| `SmartPipe.Extensions.DataAnnotations` | DataAnnotations validation | Core, Transforms |
| `SmartPipe.Extensions.Logging` | Payload-safe logging sink | Core |
| `SmartPipe.Extensions.Channels` | Channel helpers | Core |
| `SmartPipe.Testing` | Framework-neutral test components | Core |
| `SmartPipe.Extensions` | Compatibility facade and convenience bundle | Official integration packages |

## Allowed dependency direction

Dependencies flow outwards from Core to leaf packages. The only allowed lateral package relationships are:

- `Hosting -> DependencyInjection`;
- `HealthChecks -> DependencyInjection`;
- `Http.Json -> Http + Json`;
- `DataAnnotations -> Transforms` while the existing `ToFilter` compatibility API requires it.

Any additional dependency requires an allowlist change and architectural review explaining the need.

## Forbidden dependencies

- Core must not reference `SmartPipe.Extensions` or any `SmartPipe.Extensions.*` package.
- A leaf package must not reference the `SmartPipe.Extensions` facade.
- Unrelated leaf packages must not reference one another.
- Transport packages must not hide additional retry layers; in particular, `Http` must not depend on `Polly`.
- Exporter SDKs and concrete exporters must not be embedded in Core.
- `SmartPipe.Testing` must never become a runtime dependency.

## Core/definition/run separation

The execution model is `PipelineDefinition -> PipelineExecutionPlan -> PipelineRun`:

- `PipelineDefinition` is immutable, thread-safe, reusable, and contains factories/descriptors rather than live scoped resources.
- `PipelineExecutionPlan` is the structurally validated internal graph and contains no live scoped components.
- `PipelineRun` is single-use and owns its `RunId`, activated runtime-owned components, channels, cancellation, completion, and snapshots.

The current instance-based `PipelineBuilder` APIs remain compatible and adapt to this single runtime implementation; they do not retain a parallel lifecycle engine.

## PipelineKey identity

`PipelineKey` is the canonical identity across DI keys, registry entries, named options, hosting, health checks, telemetry, and diagnostics. Its value is non-empty, compared ordinally and case-sensitively, and never silently normalized. Duplicate full values are startup errors. `RunId` identifies an execution but is not a metrics dimension; stage keys are unique within one definition.

## DI scope ownership

One `AsyncServiceScope` is created per run by default and disposed only after Core finishes that run. Registration is synchronous and performs no I/O; asynchronous initialization belongs to pipeline lifecycle after resolution. Singletons must not capture scoped instances. Component descriptors explicitly identify runtime-owned, activation-scope-owned, or externally owned resources. Initialization is forward-order; rollback and disposal are reverse-order, best effort, idempotent, and preserve the primary exception.

## Compatibility/type forwarding

Published namespaces and full type names remain stable. A moved public type is implemented in its destination package and exposed from the facade through type forwarding where binary identity permits it. Thin obsolete wrappers are allowed only when a composite legacy type cannot be safely forwarded. Each move requires source and binary consumer validation, including consumers compiled against 2.1.2 and ambiguous `null/default` call sites. Public API analyzer baselines are updated per packable project.

## AOT and trimming policy

AOT/trimming is a per-package, evidence-based contract, not an ecosystem-wide claim. Reflection-free overloads are primary. Reflection-dependent APIs carry `RequiresUnreferencedCode` and/or `RequiresDynamicCode` when applicable. A package claiming compatibility enables trim and AOT analyzers and passes a real trimmed and NativeAOT consumer publish/run for a concrete RID. Mixed packages keep analyzers enabled but do not set blanket `IsAotCompatible` until the consumer proof succeeds.

## Role of SmartPipe.Extensions

`SmartPipe.Extensions` references the official integration set as a convenience bundle, forwards moved public types, and retains only necessary obsolete compatibility wrappers, aliases, and migration diagnostics. It receives no new feature implementations, reflection scanning, registry, or dependencies not represented by a dedicated package. New applications should reference only the specific packages they use.

## Alternatives rejected

- **Keep monolithic Extensions:** prevents independent dependency, AOT, ownership, and review boundaries and continues installing unrelated technologies.
- **Create `SmartPipe.Abstractions` now:** adds another public contract and package migration before a demonstrated independent consumer requires it; Core already owns the stable contracts.
- **Reflection-based plugin discovery:** weakens deterministic startup, trimming, AOT, and explicit dependency review.
- **Add `SmartPipe.Extensions.All` beside the existing package:** creates two overlapping bundles and an unclear compatibility owner; the existing facade already fills that transition role.
- **One package per class:** optimizes for file-level isolation instead of coherent technology and lifecycle boundaries, producing excessive package/version overhead.
- **Embed exporters in Core:** reverses the dependency direction and couples the runtime to vendor/exporter SDKs.
- **Exact upper-bounded internal package ranges:** unnecessarily block compatible future patch/minor resolution and complicate lockstep servicing; a minimum version is sufficient by default.
- **Hidden multiple retry layers:** can multiply attempts, violate timeout/cancellation expectations, and obscure failure ownership; resilience composition must remain explicit.

## Consequences

The release creates more projects and CI/consumer gates, and type moves require deliberate compatibility evidence. In return, consumers install only needed integrations, dependency and lifecycle ownership become reviewable, Core stays technology-neutral, and AOT/trimming claims can be stated precisely. The compatibility facade remains intentionally broad and is not the recommended dependency for new applications.

## Enforcement

CI validates the package dependency allowlist, project references, public API baselines, type-forwarding and 2.1.2 binary consumers, trim/NativeAOT consumers for claimed packages, and package metadata/version consistency. Pull requests follow the governance policy and must not merge while required evidence or owner/admin branch protection is absent.

## Supersession policy

This ADR is normative for the 2.2.0 architecture. A MUST or MUST NOT decision may be changed only by a later accepted ADR that names this ADR, documents compatibility and migration effects, updates the architecture plan and relevant CI allowlists, and receives the reviewer categories required by the governance policy. The newer ADR must mark whether this document is wholly or partially superseded.
