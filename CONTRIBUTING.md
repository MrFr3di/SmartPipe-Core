# Contributing to SmartPipe.Core

Thank you for helping improve SmartPipe.

SmartPipe is a low-level .NET pipeline runtime with explicit lifecycle,
ownership, package-boundary, compatibility, and release-evidence contracts.
Small fixes are welcome, but changes that affect public API, runtime semantics,
type identity, package ownership, or release validation need stronger review
because applications can depend on those behaviors across package upgrades.

## Contribution decision tree

```text
Security vulnerability?
├─ yes -> follow SECURITY.md; do not open a public exploit report
└─ no
   |
   Docs / typo / test clarity only?
   ├─ yes -> focused PR + docs/link validation
   └─ no
      |
      Public API, runtime lifecycle, ownership/cancellation,
      package boundary, type forwarding/removal, or compatibility change?
      ├─ yes -> governing issue/ADR/plan first
      └─ no
         |
         Release workflow / package graph / provenance / security gate?
         ├─ yes -> contract tests + exact-head CI evidence
         └─ no
            |
            Hot-path / performance change?
            ├─ yes -> measurement plan + repeatable benchmark evidence
            └─ no -> focused PR + targeted tests
```

## Start here

Before opening a pull request:

1. Search existing issues and pull requests.
2. Read the nearest package README and the relevant runtime/architecture
   document before changing behavior.
3. For a small bug fix, test improvement, or documentation correction, a
   focused pull request is usually enough.
4. For new public behavior, public API, package movement, compatibility
   treatment, security boundary, or release-process changes, link the governing
   issue/ADR/EPIC plan first.
5. Keep one pull request focused on one logical problem.
6. Record the commands and evidence actually run. Do not claim checks that were
   not executed.

Draft pull requests are appropriate for early design or compatibility review.

Release notes are curated for release-facing changes. Do not add noisy
changelog entries for unrelated internal cleanup unless the change is
user-visible or the active release plan requires it.

## Development setup

Requirements:

- .NET SDK `10.0.303` as pinned by `global.json`;
- Git;
- PowerShell 7 for repository scripts that are explicitly `.ps1`;
- provider/server tooling only when the affected integration requires it.

Restore, format, and build:

```bash
dotnet restore SmartPipe.Core.slnx --locked-mode
dotnet format SmartPipe.Core.slnx --verify-no-changes --no-restore
dotnet build SmartPipe.Core.slnx -c Release --no-restore -warnaserror
```

The repository uses Microsoft Testing Platform. Run affected test projects with
`--project`:

```bash
dotnet test --project tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj -c Release --no-build
dotnet test --project tests/SmartPipe.Extensions.Tests/SmartPipe.Extensions.Tests.csproj -c Release --no-build
```

Run the smallest deterministic test set while iterating. Run the proportional
repository/package checks before considering the change complete.

Hosted CI is authoritative for merge/release evidence, and exact-head rules
apply where the governance policy requires them.

## Choose the right contribution path

| Change | Before coding | Expected validation |
| --- | --- | --- |
| Docs, typo, comments | focused PR is usually enough | link check + relevant build/docs checks |
| Bug fix with no contract change | issue helpful | targeted regression test + affected project tests |
| Core lifecycle / ownership / cancellation | issue or plan first | correctness + concurrency + lifecycle tests |
| New public API or changed signature | design/issue first | API baseline + package validation + consumers |
| Package move / dependency edge | package graph/ownership review first | graph + metadata + ownership + packed consumers |
| Type forwarding / compatibility facade | compatibility plan/matrix required | source and unchanged old-binary consumers |
| Intentional removal | accepted ADR/ownership record required | targeted ApiCompat suppression + migration docs |
| DI / Hosting / Health / telemetry | identify lifetime and ownership impact | focused tests + direct/package consumers + trim/AOT where claimed |
| HTTP / JSON / Polly / database integration | identify I/O ownership and failure semantics | focused tests + package consumers + real integration lane where applicable |
| NativeAOT / trim-sensitive change | identify exact package claim | trim/AOT consumer validation; no blanket claim widening |
| Security / parser / resource-bound change | issue or private report depending on sensitivity | hostile-input/resource tests + security review |
| Release workflow / provenance | release/CI review first | workflow mutation contracts + exact-head hosted validation |
| Hot-path / performance change | hypothesis and baseline first | repeatable BenchmarkDotNet baseline/candidate evidence |

If the right path is unclear, open an issue describing the problem rather than
starting with a large implementation.

## Architecture and compatibility rules

Read [Architecture](docs/architecture.md),
[Runtime contracts](docs/runtime-contracts.md), and the relevant ADR before
editing compatibility-sensitive code.

The following are repository contracts:

- `SmartPipe.Core` is the single typed in-process runtime. It does not become a
  broker, durable queue, distributed coordinator, or hidden replay engine.
- Core must not depend on `SmartPipe.Extensions*`.
- Narrow integration leaves must not depend back on the broad
  `SmartPipe.Extensions` compatibility facade.
- Package edges, lifecycle state, AOT contract, and publish order are governed
  by `eng/package-graph.json`.
- Public type ownership/movement is governed by the ownership manifest and
  compatibility evidence. Do not add a wrapper or forwarder merely to make a
  failing compatibility check disappear.
- The 2.1.2 compatibility baseline is immutable comparison material. Do not
  regenerate it to fit the candidate implementation.
- A type-forwarded identity must resolve to the intended implementation
  assembly. Source compatibility alone is not binary compatibility.
- Approved removals require their recorded migration path and targeted
  compatibility suppression; do not hide them behind broad warning suppression.
- `SmartPipe.Extensions.PostgreSql` is optional and stays outside the broad
  facade. `SmartPipe.Testing` is test-only and must not become a production
  dependency.
- Runtime input, output, and buffered-observer channels remain bounded.
- Instance pipelines and definitions retaining borrowed state are single-use.
  Reusable definitions use per-run component descriptors.
- Runtime-owned resources are disposed by SmartPipe; borrowed resources remain
  caller-owned.
- Cancellation is cooperative. Do not pretend user code can be forcibly stopped
  safely in-process.
- Cleanup must continue across owned resources even after an earlier cleanup
  failure; primary processing/mandatory-cleanup failures retain precedence over
  abort/cancel/completion.
- AOT/trimming support is package-specific. Do not widen a package's claim from
  a verified narrow path to a blanket statement without matching consumer
  evidence.
- Performance work must preserve observable runtime/compatibility semantics
  unless an explicitly reviewed contract change says otherwise.

If a change intentionally alters one of these contracts, make the break and
migration impact explicit in the issue and pull request.

## Current 2.2 package boundaries

The 2.2 release line contains the runtime, narrow leaves, the compatibility
facade, and the separate test package.

Key rules:

- `SmartPipe.Core`: runtime only.
- `SmartPipe.Extensions.Json`: JSON files, transforms, dead-letter
  persistence.
- `SmartPipe.Extensions.Http`: transport only; no implicit Polly or JSON
  dependency.
- `SmartPipe.Extensions.Http.Json`: source-generated JSON codecs on top of
  HTTP + JSON leaves.
- `SmartPipe.Extensions.Polly`: transform resilience decorator; application
  owns the typed Polly pipeline.
- `SmartPipe.Extensions.DependencyInjection`, `.Hosting`,
  `.HealthChecks`, and `.OpenTelemetry`: separate infrastructure leaves.
- `SmartPipe.Extensions.PostgreSql`: provider-specific binary COPY and
  LISTEN/NOTIFY; optional and outside the facade.
- `SmartPipe.Extensions`: compatibility facade/bundle, not the preferred
  dependency for new applications.

See [Package authoring](docs/contributing/package-authoring.md) for the complete
graph/ownership/consumer contract.

## Compatibility with 2.1.2

The immutable facade baseline has 42 relevant public identities:

- 23 retained through type forwarding;
- 13 retained physically in the compatibility facade;
- 6 intentionally removed.

The deliberate removals are the four legacy HTTP selector/sink types,
`HttpSelectorStreamingMode`, and the old no-op
`PollyResilienceTransform<T>`.

Those removals require migration and recompilation. Do not restore them through
local wrappers, aliases, or blanket ApiCompat suppression.

For details, read:

- [2.1.2 → 2.2.0 migration](docs/migration/2.2.0-integration-packages.md)
- [Compatibility matrix](docs/implementation/2.2.0/sp220-17-compatibility-matrix.md)
- [ADR-0004](docs/adr/0004-smartpipe-2.2-breaking-migration.md)

## Branches and release-train work

Do not work directly on protected integration/release branches.

For ordinary focused branches, use a descriptive type and short slug:

```text
fix/<short-slug>
feat/<short-slug>
perf/<short-slug>
docs/<short-slug>
test/<short-slug>
ci/<short-slug>
```

SP220/2.2 maintainer work follows the stricter checkpoint graph in
[2.2 branch and review policy](docs/governance/2.2.0-branch-and-review-policy.md):

```text
main
  └── release/2.2.0
        └── accepted checkpoint
              └── active checkpoint integration branch
                    └── task branches
```

Task branches target the active checkpoint, not `main` or
`release/2.2.0` directly. Checkpoint promotion preserves reviewed history;
do not squash/rebase away ancestry required by accepted evidence.

Do not force-push or rewrite protected release history.

## Pull requests

### Title

Use Conventional Commits-style imperative titles:

```text
<type>[optional scope][!]: <imperative description>
```

Common types:

- `feat` — user/developer-visible capability;
- `fix` — bug fix;
- `perf` — measured performance improvement;
- `refactor` — behavior-preserving structural change;
- `docs` — documentation only;
- `test` — tests/fixtures only;
- `build` — build/package/dependency mechanics;
- `ci` — CI automation;
- `chore` — maintenance not covered above;
- `revert` — revert an earlier change.

Examples:

```text
fix(core): preserve drain timeout validation
feat(http): add bounded response reader
docs: clarify facade migration
ci(release): harden immutable artifact replay
```

Use `!` only for an intentional breaking change and explain the compatibility
impact and migration path in the PR body.

### Body

A reviewable PR explains:

- the problem and related issue/plan/ADR;
- important design or implementation choices;
- how ownership, cancellation, cleanup, and failure semantics are affected;
- public API and package-graph impact;
- source/binary compatibility impact when relevant;
- AOT/trimming impact when relevant;
- security/provenance implications when relevant;
- validation commands and hosted evidence actually obtained;
- benchmark methodology when making a performance claim;
- follow-up work intentionally left out.

Use `Closes #N` only when the PR fully completes the issue. Otherwise use
`Refs #N`.

Do not mix unrelated formatting, dependency upgrades, refactors, and product
changes into the same pull request.

## Tests and evidence

Tests must prove the contract being changed, not only increase coverage.

### Normal code changes

Start with:

```bash
dotnet restore SmartPipe.Core.slnx --locked-mode
dotnet build SmartPipe.Core.slnx -c Release --no-restore -warnaserror
```

Then run the affected test projects with Microsoft Testing Platform.

### Repository contract gate

After a clean Release build:

```bash
dotnet run --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj -c Release --no-build -- verify --profile sp220-05 --format github --failures-only
```

This profile is the reusable CI repository-contract gate. Do not describe a
different local command as equivalent CI evidence.

### Package and compatibility changes

Package work is graph-driven. A typical current-mode validation sequence
includes:

```bash
dotnet run --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj -c Release --no-build -- pack-packages --mode current --configuration Release --package-version 2.2.0 --output artifacts/packages --manifest artifacts/packages/manifest.json

dotnet run --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj -c Release --no-build -- verify-package-graph --mode current --packages artifacts/packages

dotnet run --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj -c Release --no-build -- verify-package-metadata --package-directory artifacts/packages --mode current

dotnet run --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj -c Release --no-build -- verify-package-ownership --baseline eng/baselines/2.1.2 --packages artifacts/packages --mode current

dotnet run --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj -c Release --no-build -- run-consumers --set current --package-directory artifacts/packages --package-version 2.2.0
```

Use a fresh package version when an ad-hoc consumer restore could otherwise hit
NuGet's local package cache.

Consumer scenarios are isolated workspaces and are the executable evidence for
source, binary, trim, NativeAOT, metadata, and integration claims.

### Compatibility-sensitive changes

Depending on the surface, evidence may include:

- public API baseline review;
- 2.1.2 source and unchanged old-binary consumers;
- type-forwarder destination/assembly identity validation;
- current/release package graph and ownership validation;
- package metadata and Source Link/repository commit validation;
- trim/NativeAOT publish-and-run consumers;
- Windows-specific path/runtime consumers;
- provider-backed integration tests;
- release artifact replay on another operating system.

Do not replace a failing compatibility test by regenerating the baseline or
widening suppression without an accepted contract change.

### Concurrency and lifecycle

Concurrency tests must be deterministic. Prefer explicit coordination
primitives; use timeouts as guards, not synchronization.

For runtime/lifecycle changes, assert the relevant combination of:

- `PipelineRunState`;
- output-channel behavior;
- observer/failure events;
- metrics when part of the contract;
- component initialization/disposal;
- drain/cancel/abort semantics;
- primary vs cleanup failure precedence;
- late timed-out attempt cleanup.

### Integration packages

Provider/transport tests must distinguish SmartPipe behavior from external
provider behavior.

Examples:

- HTTP transport owns request/response lifetime but adds no hidden retry.
- JSON AOT claims require source-generated `JsonTypeInfo<T>` paths.
- PostgreSQL COPY/LISTEN uses an application-owned `NpgsqlDataSource`;
  SmartPipe does not own credentials, TLS policy, or database health.
- Polly executes the real inner transform through an application-owned typed
  resilience pipeline.

### Performance

Do not claim a performance improvement from a stopwatch-only run.

Use the existing BenchmarkDotNet project, compare baseline and candidate on the
same machine, keep semantic output unchanged, and report enough raw data to
review throughput and allocation changes.

Example:

```bash
dotnet run -c Release --project benchmarks/SmartPipe.Benchmarks/SmartPipe.Benchmarks.csproj -- --filter "*RuntimePipelineBenchmarks*" --noOverwrite
```

## Dependencies

Adding or updating a dependency is a package-architecture decision.

A dependency change should explain:

- why the dependency is needed;
- why the capability does not belong in the BCL or existing dependency set;
- which SmartPipe package should own the edge;
- runtime/package-size implications;
- AOT/trimming implications;
- license and security considerations;
- compatibility implications for public signatures and old binaries.

Versions are centralized in `Directory.Packages.props`. Project files use
unversioned `PackageReference` items. Do not introduce floating versions,
local overrides, or unrelated upgrades.

Keep `eng/package-graph.json`, lock files, package ownership, and consumer
scenarios synchronized with the same change when required.

## Documentation

Update documentation in the same PR when observable behavior changes.

At minimum:

- public/runtime behavior -> nearest runtime/topic guide;
- public API -> API docs and API baseline as applicable;
- package boundary/dependency -> package README + package graph documentation;
- compatibility move/removal -> migration guide + compatibility matrix;
- AOT/trimming claim -> package README + AOT guide + executable consumer;
- release-facing change -> release notes/changelog when appropriate.

Package READMEs are shipped with NuGet packages and are validated by hosted link
checks together with the root README and `docs/**/*.md`.

Do not copy implementation detail into multiple documents when one normative
source can be linked instead.

## Claims policy

Do not add unsupported claims such as:

- production-ready without a defined release/support statement;
- zero allocations or lock-free behavior without evidence;
- broad NativeAOT compatibility for a package with only a scoped path;
- exactly-once delivery;
- durable replay after process loss;
- source compatibility as proof of binary compatibility;
- successful CI for a commit other than the exact candidate SHA;
- successful release validation when only current-mode CI ran.

Claims need reproducible evidence from tests, package validators, consumer
scenarios, benchmark artifacts, or exact-head hosted runs.

## Security

Do not open a public issue for a vulnerability that could enable exploitation,
credential exposure, data corruption, denial of service, provenance bypass, or
unsafe parser/resource behavior.

Follow [SECURITY.md](SECURITY.md) and use private vulnerability reporting when
available.

Ordinary correctness bugs that are not security-sensitive use the normal issue
flow.

## AI-assisted contributions

AI-assisted contributions are allowed.

The submitting contributor remains responsible for the result:

- understand the change being submitted;
- review generated diffs before opening a pull request;
- verify tests, benchmarks, and factual claims;
- do not fabricate validation, citations, or performance evidence;
- do not let an automated tool silently change compatibility baselines,
  ownership records, or release evidence.

Routine implementation text does not need to advertise which editor, model, or
agent produced it unless that fact is relevant to review.

## Review and merge

Reviewers prioritize:

1. correctness;
2. runtime lifecycle, ownership, and failure semantics;
3. public API and source/binary compatibility;
4. package boundaries and dependency ownership;
5. security and provenance;
6. bounded-resource/concurrency behavior;
7. package-scoped NativeAOT/trimming behavior;
8. performance and allocations on hot paths;
9. maintainability and documentation.

Before merge:

- required CI checks must pass on the required candidate head;
- required reviewer categories must approve the current head;
- review conversations must be resolved;
- compatibility/performance/security evidence must be present when applicable;
- the pull request must remain one logical change;
- checkpoint/release work must preserve the history required by the active
  governance policy.

For the 2.2 release train, follow the exact merge strategy and reviewer
requirements in
[docs/governance/2.2.0-branch-and-review-policy.md](docs/governance/2.2.0-branch-and-review-policy.md).

## Need help?

If the contribution rules are unclear, open an issue describing the problem,
expected behavior, and affected package or runtime surface.

A precise problem statement is more useful than a premature broad redesign.
