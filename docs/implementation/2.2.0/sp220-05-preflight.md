# SP220-05 preflight

Date: 2026-08-13. Working directory: `C:\tmp\S5`. Branch: `sp220/05-health-checks`.

## Exact integration base

- `origin/release/2.2.0`: `eb8337345b6838f87c31377c4aa0eddfc1f79106`, tree `029be9c8e8d5e6a7fe740b2dc14ffefb088adfaf`.
- `origin/sp220/checkpoint-c`: `54e5f68d4f1af601c8f4c235390317cffb87f173`, tree `a32709921513f148c2e6210551d716259231071d`.
- `release/2.2.0` is an ancestor of checkpoint C.
- Accepted SP220-03 head `d4b968fe00da219e1dc1b65550ed04455ec18097` and accepted SP220-04 head `95bf83c6943dcfd766320d5c60eabf0342b2437c` are both ancestors of checkpoint C.
- GitHub PR #53 is merged into checkpoint C. No SP220-05 branch or pull request existed before this implementation branch was created.

The implementation branch was created from the exact checkpoint SHA above. The linked worktree was moved to the short path `C:\tmp\S5` after a longer isolated path made the NativeAOT linker exceed its practical Windows input-path limit. This changed neither branch nor HEAD.

## Frozen contracts verified with RoslynCodeLens

- `PipelineKey` remains an ordinal, case-sensitive value in `SmartPipe.Core`.
- `SmartPipeRunIdentity` has required `PipelineKey PipelineKey` and `Guid RunId` members.
- `SmartPipeRunSnapshot` has required identity, input/output types, UTC start time, state, immutable metrics, and effective positive input/output capacities.
- `ISmartPipeRunRegistry.GetActiveRuns(PipelineKey)` returns a defensive ordered active-only snapshot. `SmartPipeRunRegistry` removes lifetime entries when runs complete and exposes no terminal history.
- `ISmartPipeRegistry` exposes registration-order snapshots and exact-key lookup.
- `ISmartPipeFactoryProvider` resolves typed run factories by exact key and type pair.
- `SmartPipe.Extensions.Hosting` directly depends on Core and DependencyInjection. No HealthChecks-to-Hosting dependency exists.

No frozen prerequisite is missing and no compatibility stub is required.

## Legacy health compatibility inventory

The following public identities remain physically in `SmartPipe.Extensions` and are frozen for SP220-05:

- `SmartPipe.Extensions.SmartPipeHealthSnapshot`, including its existing positional constructor/properties plus `StartedAtUtc` and `LastActivityAtUtc`.
- `SmartPipe.Extensions.ISmartPipeRunHealthMonitor<TInput,TOutput>.CaptureSnapshot()`.
- `SmartPipe.Extensions.SmartPipeRunHealthMonitor<TInput,TOutput>`, its `(string, PipelineRuntimeOptions)` constructor, `Track(PipelineRun<TOutput>)`, delegate `Track`, and `CaptureSnapshot`.
- `SmartPipe.Extensions.SmartPipeHealthCheckOptions`, including queue, stale, not-started, initial-activity, and `TimeProvider` properties plus `Validate()`.
- `SmartPipeServiceCollectionExtensions.AddSmartPipeHealthCheck<TInput,TOutput>(IHealthChecksBuilder, string?, HealthStatus?, IEnumerable<string>?, TimeSpan?)`.
- `SmartPipeFactory<TInput,TOutput>` retains the shipped constructor that accepts the facade monitor.

The exact entries are captured in `src/SmartPipe.Extensions/PublicAPI.Shipped.txt` and the 2.1.2 baseline assets. Source references are confined to the facade health implementation, facade registration/factory path, and `tests/SmartPipe.Extensions.Tests/HealthCheckTests.cs`. SP220-05 will neither move nor type-forward these identities.

## Package and dependency preflight

`eng/package-graph.json` already reserves `SmartPipe.Extensions.HealthChecks` as planned SP220-05 scope. Activation must change it to an active package and extend both current and release allowlists to the complete plan contract:

- required SmartPipe packages: `SmartPipe.Core`, `SmartPipe.Extensions.DependencyInjection`;
- allowed external packages: `Microsoft.Extensions.Diagnostics.HealthChecks`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.DependencyInjection.Abstractions`;
- forbidden dependencies include `SmartPipe.Extensions.Hosting` and the broad `SmartPipe.Extensions` facade.

Canonical DI-only and Hosting consumers already forbid `SmartPipe.Extensions.HealthChecks`, proving that SP220-05 must not leak into those packages.

## Fresh preflight evidence

All commands ran against checkpoint C before production changes:

- `dotnet restore SmartPipe.Core.slnx --locked-mode`: passed.
- `dotnet build SmartPipe.Core.slnx -c Release --no-restore -warnaserror`: passed with 0 warnings and 0 errors.
- Core tests: 1,253 passed.
- DependencyInjection tests: 32 passed.
- Hosting tests: 98 passed. The sandboxed `dotnet test` path attempted Windows EventLog and failed on permissions; the same built MTP executable passed outside the sandbox.
- baseline provision/integrity and subsequent offline integrity: passed.
- `verify-package-graph --mode current --source-only`: `packages=19 active=5 planned=14` and passed.
- `pack-packages`: five checkpoint packages passed.
- `verify-package-ownership`: 157 types passed.
- `run-consumers --set current`: 19/19 passed, including source/binary facade, trim, and NativeAOT scenarios.

The first root-checkout consumer attempt was correctly rejected because an unrelated ignored `benchmarks/SmartPipe.CompetitorBenchmarks` directory contains local package versions. No user artifact was changed or removed; the clean linked worktree eliminated it from the validation surface.

## SP220-05 file-scope allowlist

Implementation may create or modify only the detailed plan's scoped surfaces:

- `src/SmartPipe.Extensions.HealthChecks/**` and `tests/SmartPipe.Extensions.HealthChecks.Tests/**`;
- observation/lifecycle files and API baselines in `src/SmartPipe.Extensions.DependencyInjection/**` and targeted tests in `tests/SmartPipe.Extensions.DependencyInjection.Tests/**`;
- legacy health quarantine files/API baselines and targeted tests in `src/SmartPipe.Extensions/**` and `tests/SmartPipe.Extensions.Tests/**`;
- `SmartPipe.Core.slnx`, `Directory.Packages.props`, package graph/ownership/consumer schemas and manifests, repository checks, and release-validation workflow where SP220-05 requires them;
- HealthChecks consumer scenarios under `tests/Consumers/Scenarios/**`;
- package/readme/migration/health/package-ownership documentation, root `README.md`, and `CHANGELOG.md`;
- this preflight document and the active ExecPlan.

Core runtime, Hosting behavior, JSON behavior, unrelated extension packages, benchmarks, and release publication are outside scope.
