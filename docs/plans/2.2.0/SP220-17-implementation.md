# SP220-17 implementation plan

> **For agentic workers:** Use `superpowers:executing-plans` to implement this plan task by task. Record verification and rulings; do not promote G until SP220-18 is accepted.

**Goal:** Complete and verify the 2.2.0 compatibility facade and migration from 2.1.2.

**Architecture:** Preserve the facade DLL, its 23 type forwarders and 13 frozen public identities. Extend existing metadata/ownership validation; keep the six ADR-0004 removals absent. Complete the runtime bundle without introducing a second manifest or runtime.

**Tech stack:** C#, .NET SDK 10.0.303, net10.0, System.Reflection.Metadata, xUnit/Microsoft.Testing.Platform.

**Spec:** [SP220-17 analysis](SP220-17-facade-and-migration-analysis.md), especially sections 3 and 7; ADR-0001/0002/0004.

## Global constraints

- Base: `sp220/checkpoint-g` at `4083f4e2b881bce0740ebbe29cf2d40d9cdf1c45`.
- Immutable 2.1.2 baseline files and hashes remain unchanged.
- Facade: 23 forwarded + 13 retained + 6 removed baseline identities.
- Runtime bundle: 17 direct SmartPipe dependencies; 18 IDs including facade. Exclude Testing/PostgreSql.
- No SDK/dependency version changes, blanket AOT claims, new legacy overloads or global compatibility suppressions.
- Keep documented direct external compatibility dependencies; removing them requires a separate reviewed decision.

## Review focus

- Wrong forwarder destination must fail even if the expected implementation exists elsewhere. Preserve AssemblyRef name/version/culture/token; allow a higher implementation version, reject incompatible declared identity.
- Missing packages and missing per-asset target types must fail explicitly.
- Unknown facade public types/forwarders must fail; new canonical leaf types remain permitted.
- Nested generic forwarders must resolve their parent ExportedType to AssemblyRef.
- Bundle consumers must prove transitive access; old binary DLL hashes must stay unchanged. Current restored assets and deps.json must match the complete current SmartPipe closure/version; deploy external managed/native/resource dependencies without recompiling.

## Task 1: Compatibility inventory and acceptance

- [x] Derive the 42-row facade matrix from baseline assets and existing ownership assignments in `docs/implementation/2.2.0/sp220-17-compatibility-matrix.md`.
- [x] Clarify SP220-17 acceptance and bundle scope in the master plan; retain ADR-0002 and all six ADR-0004 removals.
- [x] Verify counts 23/13/6 and no ambiguous/unclassified rows; commit documentation.

## Task 2: Metadata and ownership enforcement

Files: `NuGet/ManagedAssemblyInspector.cs`, `NuGet/PackageAssetSnapshot.cs`, `Ownership/TypeForwarderReader.cs`, `Ownership/OwnershipValidator.cs`, corresponding RepositoryChecks tests.

- [x] Write and run rejecting tests for wrong destination, unknown facade implementation/forwarder, missing package, per-asset missing target, resurrected removed identity and nested generic metadata.
- [x] Add destination metadata without changing baseline serialization. Carry individual asset snapshots through ownership validation.
- [x] Reject defects directly, keep member compatibility in native Package Validation.
- [x] Run focused tests and RepositoryChecks (605/605 with ProcessRunnerTests excluded); record the full-suite environment limitation separately; commit.

## Task 3: Facade graph and package metadata

Files: facade csproj/README, `eng/package-graph.json`, `eng/consumer-scenarios.json`, affected lock files and package graph contract tests.

- [x] Add failing release-bundle contract assertions for HealthChecks/OTel and excluded Testing/PostgreSql.
- [x] Add both references, align current/release policy, remove stale direct Http allowance and update source/meta consumer closures (binary baseline closures stay baseline).
- [x] Refresh lock files, prove locked restore and source graph current/release checks; commit.

## Task 4: Compatibility consumers

Files: `tests/Consumers/Scenarios/extensions-meta`, `opentelemetry-facade`, legacy binary scenarios and `tests/SmartPipe.Extensions.Tests/PackageOwnershipTests.cs`.

- [x] Extend meta consumer to exact 23 forwarders and owner checks; access OTel/HealthChecks through facade alone.
- [x] Cover retained constructor graph and representative missing legacy calls, including null/default/named arguments.
- [x] Preserve one 2.1.2 build, deployment metadata refresh, unchanged consumer hash, current runtime replacement and run protocol.
- [x] Run affected source/binary/meta and direct consumers from the packed feed; commit.

## Task 5: Unified migration documentation

- [x] Update facade/root README, package description and `docs/migration/2.2.0-integration-packages.md` with 2.2.0 install, bundle scope, retained API, six removals, lifecycle and troubleshooting.
- [x] Keep historical JSON 2.1.2 guide; link and validate executable examples rather than create another migration entry point.
- [x] Check documentation links and commit.

## Task 6: Verification and handoff

- [x] Release solution build with warnings as errors; format verification; affected test suites. Results and environment limits are recorded in evidence.
- [x] Pack and native baseline validation; graph/ownership current and release checks; package metadata/version checks.
- [x] Record candidate SHA, SDK/OS, package hashes and consumer DLL hash evidence in `docs/implementation/2.2.0/sp220-17-evidence.md`.
- [x] Review complete branch; fix blocking findings and verify their regressions.
- [x] Hand off concrete branch changes and remaining Linux/Windows exact-head CI requirements. SP220-18, promotion, tagging and publishing remain separate.

## Release boundary

Local implementation and verification are recorded in [evidence](../../implementation/2.2.0/sp220-17-evidence.md). Full ProcessRunnerTests, PostgreSQL coverage and Linux/Windows exact-head CI remain unverified here. The aggregate release metadata gate still requires release notes for the other 19 packages (SP220-18); promotion/tagging/publishing have not been performed.
