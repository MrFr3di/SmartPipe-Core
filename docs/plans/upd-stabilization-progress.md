# upd Stabilization Progress

Status: temporary progress file
Last updated: 2026-06-18
Source plan: `.work/plane/plan2.md`

This file records execution evidence for the `upd` release-candidate stabilization pass. It is temporary and must be removed or moved during final release documentation cleanup.

## Phase 0 Current-State Lock

### Branch Snapshot

- Branch: `upd`
- Commit SHA: `69c036d7c54544c5cf43d46d757c9eb47bb3e6da`
- Working tree at snapshot start: `git status --short --branch` returned only `## upd...origin/upd`

### Package Version

- `src/SmartPipe.Core/SmartPipe.Core.csproj`: `2.0.0`

### P0 Blocker Snapshot

- CI consumer smoke restore: multi-source `dotnet add package` is not present in `.github/workflows/ci.yml`; consumer smoke writes `artifacts/consumer-smoke/NuGet.Config`. Remaining drift: consumer smoke still hardcodes `--version 2.0.0` instead of using a single `PACKAGE_VERSION` source.
- Default output policy: source default is `PipelineOutputPolicy.SuppressSuccessWhenSinkAttached` in `PipelineRuntimeOptions`.
- Factory/instance API mixing: `TransformFactory` and `ToFactory` reject instance pipelines; `ToFactory` error tells callers to use `PipelineBuilder.FromFactory` or `.To(sink)`.
- DI scoped run disposal: `SmartPipeFactory.StartAsync` wraps the inner run in `ScopedPipelineRun`; completion and manual disposal both call the same idempotent async disposal path.
- Drain cancellation split: `TypedPipelineExecutor` has `_sourceCts` and `_processingCts`; `RequestDrain()` cancels `_sourceCts` only, while dispose/cancel paths cancel both through linked cancellation.

### Test Framework Packages

- SDK: `10.0.204`
- No `global.json` was present at snapshot time.
- `tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj`:
  - `coverlet.collector` `10.0.1`
  - `FluentAssertions` `8.10.0`
  - `FsCheck.Xunit` `3.3.3`
  - `Microsoft.NET.Test.Sdk` `18.6.0`
  - `NSubstitute` `5.3.0`
  - `xunit` `2.9.3`
  - `xunit.runner.visualstudio` `3.1.5`
  - `Moq` `4.20.72`
- `tests/SmartPipe.Extensions.Tests/SmartPipe.Extensions.Tests.csproj`:
  - `coverlet.collector` `10.0.1`
  - `FluentAssertions` `8.10.0`
  - `Microsoft.Data.Sqlite` `10.0.8`
  - `Microsoft.EntityFrameworkCore.InMemory` `10.0.8`
  - `Microsoft.Extensions.Logging.Abstractions` `10.0.8`
  - `Microsoft.Extensions.Logging.Console` `10.0.8`
  - `Microsoft.NET.Test.Sdk` `18.6.0`
  - `xunit` `2.9.3`
  - `xunit.runner.visualstudio` `3.1.5`
  - `Moq` `4.20.72`

### Obsolete Public API Aliases

- `PipelineOutputMode`
- `PipelineRuntimeOptions.OutputMode`
- `PipelineRuntimeOptions.MaxDegreeOfParallelism`
- `PipelineOrderingMode.PreserveInputOrder`
- `SmartPipeMetrics.ExportPrometheus()`

### Documentation Sync Targets

- Existing docs expected to need synchronization during this plan:
  - `README.md`
  - `CHANGELOG.md`
  - `docs/configuration.md`
  - `docs/runtime-contracts.md`
  - `docs/resilience.md`
  - `docs/observability.md`
  - `docs/dependency-injection.md`
  - `docs/hosting.md`
  - `docs/health-checks.md`
  - `docs/recipes/bounded-output.md`
  - `docs/recipes/graceful-shutdown.md`
  - `docs/migration/legacy-to-typed.md`
  - `docs/release.md`
  - `docs/contributing.md`
- Plan-listed doc currently missing:
  - `docs/recipes/csv-json-fixtures.md`

### Local Fixtures

- `.work/sandbox/json`: present
- `.work/sandbox/csv`: present
- `.work/sandbox/csv/soc-pokec-relationships.txt`: present

## Phase 1 Progress

### Step 1.1 Consumer Smoke Restore

- `.github/workflows/ci.yml` uses `artifacts/consumer-smoke/NuGet.Config` with a local `smartpipe-local` package source for `SmartPipe.*` and NuGet for third-party packages.
- `rg -n -- 'dotnet add .*--source' .github docs README.md CHANGELOG.md` found no matches.
- Consumer smoke uses package references, not project references.
- Consumer smoke covers:
  - source -> transform -> sink;
  - output consumer mode;
  - `DrainAsync`;
  - DI factory;
  - one Extensions component through `FilterTransform`.

### Step 1.2 Package Version Source

- Added CI step `Set package version`:
  - `PACKAGE_VERSION=$(dotnet msbuild src/SmartPipe.Core/SmartPipe.Core.csproj -getProperty:Version)`
  - `echo "PACKAGE_VERSION=$PACKAGE_VERSION" >> "$GITHUB_ENV"`
- Updated consumer smoke `dotnet add package` commands to use `$PACKAGE_VERSION`.
- Updated JSON/dead-letter AOT smoke generated project to use `$PACKAGE_VERSION`.
- Updated `docs/release.md` package smoke sample to read the version from `SmartPipe.Core.csproj`.
- `dotnet msbuild src\SmartPipe.Core\SmartPipe.Core.csproj -getProperty:Version` returned `2.0.0`.

### Step 1.3 Sink-Safe Default Output Policy

- Added/verified required tests:
  - `PipelineRuntimeOptions_Defaults_AreReleaseContract`
  - `SinkBackedPipeline_DefaultOutputPolicy_DoesNotEmitSuccessOutputs`
  - `SinkBackedPipeline_DefaultOutputPolicy_WritesAllItemsToSink`
  - `SinkBackedPipeline_10000Items_DefaultOutputPolicy_CompletesWithoutReadingOutputs`
  - `OutputConsumerPipeline_NoSink_DefaultPolicy_EmitsSuccessOutputs`
  - `SinkBackedPipeline_EmitAll_WithoutOutputReader_Backpressures`
  - `EmitFailuresOnly_EmitsOnlyFailures`
- Updated `SinkBackedPipeline_10000Items_DefaultOutputPolicy_CompletesWithoutReadingOutputs` to exercise the runtime default rather than the obsolete `OutputMode` compatibility alias.
- Added required output-policy wording to:
  - `README.md`
  - `docs/configuration.md`
  - `docs/runtime-contracts.md`
  - `docs/recipes/bounded-output.md`
  - `CHANGELOG.md`
- Validation:
  - `dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TypedPipelineOutputModeTests|FullyQualifiedName~SinkBackedPipeline_10000Items_DefaultOutputPolicy_CompletesWithoutReadingOutputs"` passed: 35/35 tests.

## Phase 2 Progress

### Factory vs Instance Pipeline Contract

- Added/verified required tests:
  - `FactoryPipeline_CreatesFreshComponents_PerStart`
  - `FactoryPipeline_SecondStart_CreatesNewSourceStageSink`
  - `FactoryPipeline_DisposesComponentsPerRun`
  - `FromInstance_TransformFactory_ThrowsClearError`
  - `FromInstance_ToFactory_ThrowsClearError`
  - `FromFactory_TransformFactory_ToFactory_AllowsMultipleStarts`
  - `InstancePipeline_SecondStart_ThrowsClearError`
- Red/green evidence:
  - `dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FromInstance_TransformFactory_ThrowsClearError"` failed before the production change because the exception message did not mention `.Transform(instance)`.
  - Updated `PipelineBuilder.TransformFactory` exception text to mention `PipelineBuilder.FromFactory` and `.Transform(instance)`.
  - `dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FactoryPipeline_|FullyQualifiedName~FromInstance_|FullyQualifiedName~FromFactory_TransformFactory_ToFactory_AllowsMultipleStarts|FullyQualifiedName~InstancePipeline_SecondStart_ThrowsClearError"` passed: 7/7 tests.
- Updated factory/instance contract docs:
  - `README.md`
  - `docs/getting-started.md`
  - `docs/runtime-contracts.md`
  - `docs/dependency-injection.md`
  - `docs/api-reference.md`

## Phase 3 Progress

### DI Scoped Run Disposal

- Refactored the DI scoped run owner to `internal sealed class ScopedPipelineRun<T>`.
- The owner exposes `Inner`, uses `ArgumentNullException.ThrowIfNull` equivalent behavior through constructor null-check, and disposes both inner run and `AsyncServiceScope` through one idempotent async path.
- Completion and manual disposal both route through `ScopedPipelineRun<T>.DisposeAsync()`.
- No synchronous `scope.Dispose()` is used in the async path.
- Added/verified required tests:
  - `DI_Factory_CompletionDisposesScopeOnce`
  - `DI_Factory_ManualDisposeBeforeCompletion_DisposesScopeOnce`
  - `DI_Factory_CompletionAndManualDisposeRace_DisposesScopeOnce`
  - `DI_Factory_ScopedComponentsDisposedWithScope`
  - `DI_Factory_StartFailure_DisposesScope`
  - `DI_Factory_ValidateScopes_RemainsGreen`
- Validation:
  - `dotnet test tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DI_Factory_"` passed: 7/7 tests.

## Phase 4 Progress

### Drain, Cancel, and Abort Lifecycle

- Verified runtime ownership:
  - `_sourceCts` is passed to source reads and producer paths.
  - `_processingCts` is passed to workers, stages, sink writes, output writes, and observer completion.
  - `TryDrainAsync` calls `RequestDrain()`.
  - `RequestDrain()` cancels `_sourceCts` without cancelling `_processingCts`.
  - `CancelAsync` completes outputs as cancelled and cancels the linked root CTS.
  - `AbortAsync` marks lifecycle aborted, completes outputs as aborted, and cancels through the linked root CTS.
- Added/verified required tests:
  - `TryDrainAsync_CancelsSourceRead_ButFinishesAcceptedItems`
  - `TryDrainAsync_SourceBlockedInMoveNext_ReturnsPredictably`
  - `TryDrainAsync_InFlightStageCompletes`
  - `TryDrainAsync_Timeout_ReturnsTimedOutStillRunning`
  - `DrainAsync_Timeout_ThrowsButRunCanStillBeCancelled`
  - `CancelAsync_CancelsSourceAndWorkers`
  - `AbortAsync_CancelsSourceAndWorkersImmediately`
  - `DrainThenCancel_TransitionsPredictably`
- Validation:
  - `dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TryDrainAsync_CancelsSourceRead_ButFinishesAcceptedItems|FullyQualifiedName~TryDrainAsync_SourceBlockedInMoveNext_ReturnsPredictably|FullyQualifiedName~TryDrainAsync_InFlightStageCompletes|FullyQualifiedName~TryDrainAsync_Timeout_ReturnsTimedOutStillRunning|FullyQualifiedName~DrainAsync_Timeout_ThrowsButRunCanStillBeCancelled|FullyQualifiedName~CancelAsync_CancelsSourceAndWorkers|FullyQualifiedName~AbortAsync_CancelsSourceAndWorkersImmediately|FullyQualifiedName~DrainThenCancel_TransitionsPredictably"` passed: 8/8 tests.

## Validation Snapshot

- `dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore` passed: 628/628 tests.
- `dotnet test tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-restore` passed: 189 passed, 1 skipped, 190 total.
- `git diff --check` passed with exit code 0. Git printed CRLF conversion warnings for modified files; no whitespace errors were reported.
