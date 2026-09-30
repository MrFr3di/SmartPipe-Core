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

Local candidate checks (SDK 10.0.303): locked restore and Release solution build with warnings as errors passed; Core 1283/1283 passed. Seventeen test suites passed when invoked directly. The MTP orchestration and 27 RepositoryChecks tests are blocked by sandbox socket permission errors. PostgreSQL requires a configured database; the facade suite exceeded the local 180-second execution limit. Four OpenTelemetry export tests fail identically on the unmodified release base and candidate in this environment. Full GitHub Actions validation remains required.

Publication and closure of superseded PRs were explicitly authorized on 2026-09-30. Candidate acceptance still requires GitHub Actions validation.
