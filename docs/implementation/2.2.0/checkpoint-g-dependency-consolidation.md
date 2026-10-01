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
cooperative workers are cancelled and drained before the failure escapes. Seven
regression tests cover deadline, cancellation, worker/callback faults, non-cooperative
work/callbacks, and successful completion. Cleanup is best-effort under one five-second
budget, always rethrowing the original error and observing eventual faults.
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
| MTP discovery across 21 test assemblies | 3291 | 3299 |
| Core execution, including stress | 1283 passed; 0 skipped | 1290 passed; 0 skipped |
| Core Cobertura | — | Valid XML; 91.3% line coverage |
| Dapper suite | — | 53 passed; 0 skipped |
| EntityFrameworkCore suite | — | 47 passed; 0 skipped |
| Mapster suite | — | 21 passed; 0 skipped |
| Facade suite | — | 199 passed; 0 skipped |

Discovery comparison groups by test method and counts theory rows because
xUnit 4 changes parameter display escaping/truncation. No previous methods or
row counts were removed; the additions are seven workload regression methods
and one PostgreSQL ambient-heap regression method. Execution adds one dynamically expanded theory row in both versions.
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

## CI-discovered PostgreSQL measurement correction

The first follow-up head `894b6d11367e174d92f325aaa93b5caa4e2349de`
failed the bounded-memory test on PostgreSQL 18.6 in the PR run and on 17.11
in the Linux dispatch, while the opposite legs passed. Both failures sampled
about 81 MB against the unchanged 40 MiB ceiling. `GC.GetTotalMemory(false)`
counts transient dead row strings until the runtime happens to collect them;
this is not evidence that the source retains the result set.

The test now collects before each 5,000-row retained-heap sample. The isolated
collection, heap ceiling, cumulative per-row budget, and early-break budget
remain intact. A controlled no-GC probe measured discarded 50 MiB as 52,477,424
bytes before collection and 48,592 afterward; retained 50 MiB remained
52,483,240 bytes after collection. This distinguishes dead allocation from
live buffering without raising the limit. The reader returns lengths rather
than strings, so the comments now correctly identify early-break allocation
as the eager-materialisation check.

All 237 PostgreSQL tests passed with zero skips in five consecutive full runs
against a local PostgreSQL 18.6 container after the correction. Fresh CI on
both server versions and both validation runners remains the final gate.

## October 1 review follow-up

Two additional review findings on `47c18768e10dfdfdc71c8ac3dca15c89fd7c0bca`
were reproduced before applying corrections:

- A non-cooperative worker replaced caller cancellation with the cleanup timeout;
  a throwing cancellation callback replaced the initiating worker failure.
  A blocked callback also prevented cleanup from reaching its previous deadline.
  Cleanup now waits for worker/callback completion under one five-second budget,
  observes faults in each task and their aggregate, and rethrows the original
  exception. Three new regression tests fail before the fix and pass afterward.
- Absolute retained process heap included existing runner/provider caches. A new
  integration test holding an unrelated 48 MiB cache failed the old 40 MiB check.
  The test now samples retained growth above a collected baseline immediately
  before source creation. Activation/initialization remain inside the scenario's
  budget; negative growth is clamped to zero. The 40 MiB ceiling, cumulative
  allocation budget, and early-break guard remain unchanged. All 238 PostgreSQL
  tests pass locally against PostgreSQL 18.6, without skips.

The full local Core run also reproduced a readiness race in the existing
observer failure test: `DropWrite` can admit only one item before dropping the
remaining burst, so two transformer entries are not guaranteed. Waiting for
one entry preserves the failure-policy, dropped-item, dropped-observer-event,
and successful-completion assertions. The gate releases in `finally`, and the
run is disposed. Production runtime and public API remain unchanged.

The CodeRabbit docstring warning is advisory and still reports the original
`2cd4e69` review. It does not identify a behavioral fault; test projects disable
XML documentation generation. Broad generated docstrings are not introduced.
Final-head CI evidence is recorded in the PR after publication.
