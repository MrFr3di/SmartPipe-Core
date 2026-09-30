# Checkpoint G: dependency PR consolidation

Base: `release/2.2.0` at `cf6f16d4b34db9fc4d090e94c26e364cae5192da`.
Integration target: `sp220/checkpoint-g`.

The open Dependabot proposals target the older `main` layout, where package
versions are inline. Apply their version changes through central package
management on the accepted 2.2.0 baseline; do not replay old project files or
overwrite the package split.

| Original PR | Proposal | Consolidation |
|---|---|---|
| #93 | CodeQL analyze 4.38.1 | Both CodeQL steps pinned to the same reviewed SHA |
| #94 | Dapper 2.1.89 | Central version; release baseline was 2.1.86 |
| #95 | CodeQL init 4.38.1 | Both CodeQL steps pinned to the same reviewed SHA |
| #96 | FluentAssertions 8.11.0 | Central version for existing test references |
| #97 | Mapster 10.0.13 | Central version; release baseline was 10.0.12 |
| #98 | Microsoft.NET.Test.Sdk 18.10.1 | Central version for all existing test projects |
| #99 | Microsoft.Testing.Extensions.CodeCoverage 18.11.2 | Central version |
| #100 | Moq 4.21.0 | Central version for existing test references |
| #101 | NSubstitute 6.2.0 | Superseded: no NSubstitute reference or usage remains in the release branch; do not reintroduce it |
| #102 | SQLitePCLRaw.bundle_e_sqlite3 3.0.5 | Central version; inspect regenerated native dependency closure |
| #103 | xunit.runner.visualstudio 4.0.0 | Central version aligned with xUnit 4 |
| #104 | xunit.v3.mtp-v2 4.0.1 | Central version for existing MTP v2 test projects |

## Compatibility adjustments

- Add Checkpoint G to CI and CodeQL push/PR filters and Dependency Review PR
  filters. The task PR targets G, not the release branch or main.
- Preserve disabled Core test parallelization with xUnit 4's
  `[assembly: Parallelization(Mode = ParallelMode.None)]` with `Xunit.v3` and
  `Xunit.Sdk`. The old
  `CollectionBehavior.DisableTestParallelization` property became obsolete and
  uncallable in xUnit 4; enabling parallelism would change the test contract.
- Regenerate lock files using NuGet restore, then verify locked restore. Do not
  manually invent content hashes or disable lock validation.
- Make the existing twelve timeout tests observe the xUnit test cancellation
  token. Link existing stress-test deadlines to that token; keep run cancellation
  behavior under test unchanged before the test timeout.
- Guard nullable Moq log-state matchers before calling `ToString`; keep the
  existing log level, message, exception and call-count assertions.
- Keep runtime public API and the accepted facade ownership/removal decisions
  unchanged in this maintenance contribution.

## Evidence

The accepted release base has green CI run
[36735414392](https://github.com/MrFr3di/SmartPipe-Core/actions/runs/36735414392).
Candidate validation is recorded in the consolidated PR. Baseline success does
not stand in for candidate checks. This document does not mark SP220-17 or
SP220-18 accepted.

References: [xUnit 4.0.0 release notes](https://xunit.net/releases/v3/4.0.0),
[xUnit 4.0.1](https://xunit.net/releases/v3/4.0.1),
[VS adapter 4.0.0](https://xunit.net/releases/visualstudio/4.0.0),
[SP220-17 analysis](../../plans/2.2.0/SP220-17-facade-and-migration-analysis.md).

## Review follow-up validation

The review follow-up makes both EWMA tests await the complete worker aggregate.
A deadline, test cancellation, or worker fault fails the test; cooperative
workers are cancelled and drained before the failure escapes. Four regression
tests cover deadline, cancellation, failure propagation, and successful completion.
The six pipeline concurrency tests observe test cancellation at their external
barriers and release controlled gates in `finally`, retaining their independent
run cancellation and disposal assertions.

The eight Dapper/EF consumer templates now reference
`SQLitePCLRaw.bundle_e_sqlite3` rather than the legacy raw-package triplet.
The three unused 2.1.12 central minima were removed after reference inventory.
NuGet regenerated the three affected test locks: bundle/core/provider resolve
to 3.0.5 and the native `SQLite` package to 3.53.4. Restore-generated content
hashes and resolved versions remain unchanged; raw packages are transitive.

Local validation uses SDK 10.0.303 on Linux and compares the accepted baseline
`cf6f16d4b34db9fc4d090e94c26e364cae5192da` with the reviewed candidate:

| Check | Accepted baseline | Reviewed candidate |
|---|---:|---:|
| MTP discovery across 21 test assemblies | 3291 | 3295 |
| Core execution, including stress | 1283 passed; 0 skipped | 1287 passed; 0 skipped |
| Core Cobertura | — | Valid XML; 91.3% line coverage |
| Dapper suite | — | 53 passed; 0 skipped |
| EntityFrameworkCore suite | — | 47 passed; 0 skipped |
| Mapster suite | — | 21 passed; 0 skipped |
| Facade suite | — | 199 passed; 0 skipped |

Discovery comparison groups by test method and counts theory rows because
xUnit 4 changes parameter display escaping/truncation. No previous methods or
row counts were removed; the only additions are the four workload regression
methods. Execution adds one dynamically expanded theory row in both versions.
An intentionally blocked 32-start factory probe confirms that test cancellation
now reaches the body cleanup; the original body remains waiting at its barrier.
The probe deliberately fails on its one-second timeout and is not part of the
committed passing suite.

Locked solution restore, Release solution build with warnings as errors
(zero warnings/errors), the `sp220-05` repository verification profile, and
workflow contract tests pass. The graph pack succeeded, and all eight Dapper/EF packed consumers passed
on Linux, including the old 2.1.2 binaries. Their generated lock files confirm
bundle/core/provider 3.0.5 and native SQLite 3.53.4 with no legacy
`SQLitePCLRaw.lib.e_sqlite3` or `SourceGear.sqlite3` package. A separate read-only
review found no blocking issues. Full Windows PR validation and a normal Linux `CI` workflow dispatch
must run against the final candidate; the PR records their immutable run URLs
and head SHA. Those runs validate packed consumers, native SQLite loading,
Dapper direct/old-binary compatibility, Mapster canonical/facade behavior,
coverage, and PostgreSQL 18.6/17.11. Earlier baseline/candidate runs are not
substitutes for final-head validation.

SP220-17/SP220-18 remain future acceptance work. Dependabot's default-branch
target configuration is a separate follow-up.
