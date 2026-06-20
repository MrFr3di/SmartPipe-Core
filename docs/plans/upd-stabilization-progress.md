# upd Stabilization Progress

Status: temporary progress file
Last updated: 2026-06-20
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

- Snapshot found transitional typed runtime compatibility aliases and one legacy
  metrics export compatibility member still present in public API baselines.

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
- Updated `SinkBackedPipeline_10000Items_DefaultOutputPolicy_CompletesWithoutReadingOutputs` to exercise the runtime default rather than the obsolete compatibility alias.
- Added required output-policy wording to:
  - `README.md`
  - `docs/configuration.md`
  - `docs/runtime-contracts.md`
  - `docs/recipes/bounded-output.md`
  - `CHANGELOG.md`
- Validation:
  - Targeted output-policy tests passed: 35/35 tests.

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

## Phase 5 Progress

### Public API Cleanup for 2.0

- Decision: use transitional 2.0 for the currently shipped typed runtime aliases.
  Removing them now would be a public API break beyond this stabilization step.
- Updated `docs/configuration.md` so the main configuration surface shows only
  primary `OutputPolicy` and `MaxConcurrency` settings.
- Updated `docs/migration/legacy-to-typed.md` as the named compatibility surface
  for transitional aliases and the unsupported ordering compatibility value.
- Updated `CHANGELOG.md` so release notes do not present obsolete compatibility
  names as primary user-facing API.
- Added/verified required output-policy, concurrency, conflict, alias, and
  unsupported-ordering tests for the Phase 5 contract.
- Documentation check:
  - obsolete compatibility names appear in user docs only in `docs/migration/legacy-to-typed.md`.
- Validation:
  - `dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore --filter "<Phase 5 exact test names>"` passed: 9/9 tests.

## Phase 6 Progress

### Circuit Breaker Public Contract

- Decision: lease-based half-open probes are authoritative for runtime
  execution; `AllowRequest()` remains a documented compatibility/simple gate
  without adding a new obsolete warning in this stabilization pass.
- Updated public XML documentation on `CircuitBreaker.AllowRequest()` and
  `CircuitBreaker.TryAcquireHalfOpenProbe(out CircuitBreakerProbe)`.
- Updated `docs/resilience.md` with probe usage, probe disposal, and precise
  `AllowRequest()` compatibility behavior.
- Added/verified required half-open probe, public compatibility, rejection,
  opened, closed, and no-retry regression tests.
- Validation:
  - `dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore --filter "<Phase 6 exact test names>"` passed: 10/10 tests.

## Phase 7 Progress

### Output Channel Reader Contract

- Decision: use the single logical output reader contract.
- Verified `PipelineChannelFactory.CreateOutputOptions(...)` sets
  `SingleReader = true`.
- Verified docs already state that `PipelineRun<T>.Outputs` is intended for one
  consumer and callers needing fan-out must implement it explicitly:
  - `README.md`
  - `docs/runtime-contracts.md`
  - `docs/recipes/bounded-output.md`
- Added/verified required tests:
  - `OutputChannel_IsSingleReaderByContract`
  - `PipelineRunOutputs_DocumentedSingleConsumer`
- Validation:
  - `dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OutputChannel_IsSingleReaderByContract|FullyQualifiedName~PipelineRunOutputs_DocumentedSingleConsumer"` passed: 2/2 tests.

## Phase 8 Progress

### Minimum P0 Contract Tests Before xUnit v3

- Added/renamed only minimum contract tests needed by the plan before the xUnit
  migration phase.
- Added/verified required Core tests for runtime defaults, sink-safe default
  output behavior, factory/instance errors, drain source-block behavior, and
  meter instrument names.
- Added/verified required Extensions tests for DI scoped disposal race, hosted
  service MarkUnhealthy health reporting, and hosted-service option defaults.
- Validation:
  - `dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore --filter "<Phase 8 Core exact test names>"` passed: 6/6 tests.
  - `dotnet test tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-restore --filter "<Phase 8 Extensions exact test names>"` passed: 3/3 tests.

## Phase 9 Progress

### xUnit v3 Migration

- Added `global.json` with `test.runner = Microsoft.Testing.Platform`.
- Migrated both test projects to executable xUnit v3/MTP project mode:
  - `OutputType=Exe`
  - `TestingPlatformDotnetTestSupport=true`
  - `UseMicrosoftTestingPlatformRunner=true`
  - `xunit.v3.mtp-v2` `3.2.2`
- Kept `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` for
  compatibility with documented `dotnet test`/IDE paths.
- Replaced the old Core coverage collector path with
  `Microsoft.Testing.Extensions.CodeCoverage` `18.8.0` and MTP
  `dotnet run --project ... -- --coverage`.
- Removed `coverlet.collector` from both test projects; `--collect:"XPlat Code
  Coverage"` is not used with the MTP runner.
- Removed `FsCheck.Xunit` and converted property-based tests to deterministic
  `[Theory]`/`[MemberData]` coverage to avoid mixed xUnit v2/v3 references.
- Removed the remaining explicit `Xunit.Abstractions` using from
  `CircuitBreakerTests`; xUnit v3 `ITestOutputHelper` resolves from `Xunit`.
- Suppressed `xUnit1051` in both test projects as migration debt. The warning
  currently affects existing cancellation-token-heavy tests broadly; both test
  projects still pass their `-warnaserror` build gates.
- Updated CI and publish workflows to use `dotnet test --project`; CI coverage
  now uses the MTP executable runner.
- Validation:
  - `dotnet restore SmartPipe.Core.slnx --force-evaluate --source "H:\Download\Google chrome" --source https://api.nuget.org/v3/index.json` passed using the local CodeCoverage package source.
  - `dotnet build SmartPipe.Core.slnx -c Release --no-restore` passed with 0 warnings, 0 errors.
  - `dotnet build tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj --no-restore -c Release -warnaserror` passed with 0 warnings, 0 errors.
  - `dotnet build tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj --no-restore -c Release -warnaserror` passed with 0 warnings, 0 errors.
  - `dotnet test --project tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore` passed: 668/668 tests.
  - `dotnet test --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-restore` passed: 191 passed, 1 skipped, 192 total.
  - `dotnet run --project tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore` passed under `xUnit.net v3 Microsoft.Testing.Platform v2 Runner`: 668/668 tests.
  - `dotnet run --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-restore` passed under `xUnit.net v3 Microsoft.Testing.Platform v2 Runner`: 191 passed, 1 skipped, 192 total.
  - `dotnet run --project tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore -- --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml` passed and produced `tests\SmartPipe.Core.Tests\bin\Release\net10.0\TestResults\coverage.cobertura.xml`.

## Phase 10 Progress

### Fixture Catalog, Manifest, And Generated Small Fixtures

- Added `tests/SmartPipe.Testing/Fixtures/FixtureCatalog.cs` as a shared test
  fixture helper and linked it into `SmartPipe.Extensions.Tests`.
- Added `FixtureEnvironment` gates for:
  - `SMARTPIPE_FIXTURES_ROOT`
  - `SMARTPIPE_ENABLE_REAL_FIXTURES`
  - `SMARTPIPE_ENABLE_LARGE_FIXTURES`
  - `SMARTPIPE_ENABLE_HUGE_FIXTURES`
  - `SMARTPIPE_SOC_POKEC_PATH`
- Added catalog discovery for `.csv`, `.txt`, `.json`, `.jsonl`, and
  `.ndjson`, with plan threshold classification, cheap BOM/newline detection,
  and default SHA256 hashing only for small/medium fixtures.
- Added `tests/SmartPipe.Testing/Fixtures/fixture-manifest.json` with relative
  metadata-only entries for capital-plan CSV/JSON, business licences CSV/JSON,
  and `soc-pokec-relationships`.
- Added `GeneratedFixtureData` for normal-CI tiny CSV/JSON fixtures covering
  the Step 10.4 CSV and JSON shapes without depending on `.work/sandbox`.
- Added tests:
  - `FixtureCatalogTests`
  - `GeneratedFixtureDataTests`
- Added CSV golden tests:
  - `CsvGolden_BomAndNoBom_ParseCorrectly`
  - `CsvGolden_CrlfAndLf_ParseCorrectly`
  - `CsvGolden_QuotedCommas_ParseCorrectly`
  - `CsvGolden_MultilineQuotedField_ParseCorrectly`
  - `CsvGolden_EmptyAndNullLikeFields_AreHandled`
  - `CsvGolden_DuplicateHeaders_UseConfiguredPolicy`
  - `CsvGolden_MalformedRows_GoToDeadLetterOrFailurePolicy`
  - `CsvGolden_LongFields_DoNotBreakPipeline`
  - `CsvGolden_UnicodeHeadersAndValues_ParseCorrectly`
- Added JSON golden tests:
  - `JsonFixture_RootArray_StreamsItems`
  - `JsonFixture_TopLevelValues_StreamsItems`
  - `JsonFixture_Ndjson_StreamsItems_IfSupported`
  - `JsonFixture_NullAndMissingProperties_AreHandled`
  - `JsonFixture_NumericValues_AreCultureInvariant`
  - `JsonFixture_MalformedJson_UsesFailurePolicy`
  - `JsonFixture_EmptyArray_CompletesSuccessfully`
  - `JsonFixture_EmptyFile_UsesConfiguredPolicy`
  - `JsonFixture_SourceGeneratedJsonTypeInfo_Works`
  - `JsonFixture_AotSafeOverload_IsUsedWhereRequired`
- Added CSV pipeline tests:
  - `CsvPipeline_ValidRows_WriteToSink`
  - `CsvPipeline_InvalidRows_UseFailurePolicy`
  - `CsvPipeline_FilteredRows_AreNotFailures`
  - `CsvPipeline_OutputBackpressure_DoesNotLoseRows`
  - `CsvPipeline_DropMode_RecordsDroppedRows`
  - `CsvPipeline_DrainMidFile_CompletesAcceptedRows`
  - `CsvPipeline_CancelMidFile_CancelsSourceAndWorkers`
- Added CapitalPlan CSV/JSON parity tests:
  - `CapitalPlan_CsvAndJson_ProduceSameLogicalItemCount`
  - `CapitalPlan_CsvAndJson_HaveCompatibleSchema`
  - `CapitalPlan_CsvAndJson_NumericFieldsMatchWithinTolerance`
  - `CapitalPlan_CsvAndJson_NullFieldsMatchConfiguredPolicy`
  - `CapitalPlan_CsvAndJson_CategoryFieldsMatch`
- Updated `Microsoft.Data.Sqlite` to 10.0.9 and added a direct
  `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 reference in
  `SmartPipe.Extensions.Tests` so the vulnerable SQLite native package no
  longer appears in NuGet audit output.
- Added SocPokec fixture helpers and gated, deterministic SocPokec tests:
  - `SocPokec_HugeFixture_IsSkippedUnlessEnabled`
  - `SocPokec_StreamEdges_DoesNotMaterializeFile`
  - `SocPokec_StreamEdges_CountsRows`
  - `SocPokec_StreamEdges_ParsesTwoIdsPerLine`
  - `SocPokec_StreamEdges_ComputesStableRollingDigest`
  - `SocPokec_BoundedPipeline_ProcessesAllEdgesWithoutDrops`
  - `SocPokec_BoundedPipeline_MaxConcurrency4_Completes`
  - `SocPokec_BoundedPipeline_DrainMidFile_CompletesAcceptedEdges`
  - `SocPokec_BoundedPipeline_CancelMidFile_CancelsPredictably`
  - `SocPokec_InvalidLines_GoToFailurePolicy`
  - `SocPokec_ThroughputSmoke_ReportsItemsPerSecond`
  - `StressSummary_ContainsRequiredFields`
- Added shared fixture category constants and explicit opt-in gates for
  real, large, huge, stress, and slow fixture/stress tests. Normal golden tests
  continue to use generated tiny fixtures rather than `.work/sandbox`.
- Validation:
  - `dotnet list tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj package --vulnerable --include-transitive` reported no vulnerable packages.
  - `dotnet build tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-restore` passed with 0 warnings and 0 errors.
  - `dotnet test --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build --filter-class SmartPipe.Extensions.Tests.Fixtures.FixtureCatalogTests` passed: 8/8 tests.
  - `dotnet test --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build --filter-class SmartPipe.Extensions.Tests.Fixtures.FixtureCatalogTests --filter-class SmartPipe.Extensions.Tests.Fixtures.GeneratedFixtureDataTests` passed: 11/11 tests.
  - `dotnet test --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build --filter-class SmartPipe.Extensions.Tests.Fixtures.CsvGoldenFixtureTests` passed: 9/9 tests.
  - `dotnet test --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build --filter-class SmartPipe.Extensions.Tests.Fixtures.JsonGoldenFixtureTests` passed: 10/10 tests.
  - `dotnet test --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build --filter-class SmartPipe.Extensions.Tests.Fixtures.CsvPipelineFixtureTests` passed: 7/7 tests.
  - `dotnet test --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build --filter-class SmartPipe.Extensions.Tests.Fixtures.CapitalPlanParityFixtureTests` passed: 5/5 tests.
  - `dotnet test --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build --filter-class SmartPipe.Extensions.Tests.Fixtures.SocPokecFixtureTests` passed: 12/12 tests.
  - `dotnet test --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build --filter-class SmartPipe.Extensions.Tests.Fixtures.FixtureCatalogTests --filter-class SmartPipe.Extensions.Tests.Fixtures.GeneratedFixtureDataTests --filter-class SmartPipe.Extensions.Tests.Fixtures.CsvGoldenFixtureTests --filter-class SmartPipe.Extensions.Tests.Fixtures.JsonGoldenFixtureTests --filter-class SmartPipe.Extensions.Tests.Fixtures.CsvPipelineFixtureTests --filter-class SmartPipe.Extensions.Tests.Fixtures.CapitalPlanParityFixtureTests --filter-class SmartPipe.Extensions.Tests.Fixtures.SocPokecFixtureTests` passed: 54/54 tests.

## Phase 11 Progress

- Verified existing deterministic runtime coverage for output/backpressure,
  filtered results, and lifecycle/drain/cancel/abort contracts.
- Added `HostedService_FaultBehaviorIgnore_DoesNotStopHost` to cover the
  remaining hosted-service failure behavior.
- Validation:
  - `dotnet build tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-restore` passed with 0 warnings and 0 errors.
  - `dotnet test --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build --filter-class SmartPipe.Extensions.Tests.Extensions.SmartPipeTypedDiTests` passed: 15/15 tests.
  - `dotnet test --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build --filter-class SmartPipe.Extensions.Tests.HealthCheckTests` passed: 7/7 tests.
  - `dotnet test --project tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build --filter-class SmartPipe.Core.Tests.Engine.TypedPipelineOutputModeTests --filter-class SmartPipe.Core.Tests.Engine.TypedPipelineDrainTests --filter-class SmartPipe.Core.Tests.Engine.RuntimeOptionsPassTests` passed: 144/144 tests.

## Validation Snapshot

- `dotnet test --project tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore` passed: 668/668 tests.
- `dotnet test --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-restore` passed: 191 passed, 1 skipped, 192 total.
- `dotnet run --project tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore -- --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml` passed and produced Cobertura coverage.
- `git diff --check` passed with exit code 0 after Phase 10 fixture/golden edits. Git printed CRLF conversion warnings for modified files; no whitespace errors were reported.
