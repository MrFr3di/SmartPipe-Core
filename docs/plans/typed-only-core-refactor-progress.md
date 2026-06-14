# Typed-Only Core Refactor Progress

Status: active
Date: 2026-06-11

## Step 1-7 corrective review

### Changed files

- `src/SmartPipe.Core/PipelineRuntimeOptions.cs`
- `src/SmartPipe.Core/TypedPipelineRuntime.cs`
- `src/SmartPipe.Core/Runtime/Channels/PipelineChannelFactory.cs`
- `tests/SmartPipe.Core.Tests/Engine/RuntimeOptionsPassTests.cs`
- `tests/SmartPipe.Core.Tests/Engine/TypedPipelineOutputModeTests.cs`
- `tests/SmartPipe.Core.Tests/Engine/TypedPipelineConcurrencyTests.cs`
- `tests/SmartPipe.Core.Tests/Engine/TypedPipelineDrainTests.cs`
- `tests/SmartPipe.Core.Tests/Engine/PipelineChannelFactoryTests.cs`
- `README.md`
- `CHANGELOG.md`
- `docs/architecture.md`
- `docs/configuration.md`
- `docs/runtime-contracts.md`
- `docs/recipes/bounded-output.md`
- `docs/migration/legacy-to-typed.md`
- `docs/plans/typed-only-core-refactor.md`
- `docs/plans/typed-only-core-refactor-progress.md`

### Architecture review

- The shipped public shape of `PipelineRuntimeOptions.OutputCapacity` remains
  `int?`.
- `OutputCapacity = null` now maps to the typed runtime bounded default
  capacity (`1024`) instead of selecting an unbounded sink-attached path.
- Typed output channel creation goes through `PipelineChannelFactory`, and the
  factory declares output readers as multi-reader compatible.
- Parallel typed input now honors `InputCapacity` independently from
  `MaxConcurrency`.
- Drain semantics were tightened for accepted buffered input: drain stops new
  acceptance and completes already accepted buffered work.

### Correctness review

- Sink-attached `EmitAll` runs now have the same bounded-output backpressure
  contract as output-only runs.
- Sink-only callers remain supported by consuming `PipelineRun<T>.Outputs` or
  selecting `SuppressSuccessWhenSinkAttached` /
  `SuppressAllWhenSinkAttached`.
- The old `Task.Delay`-based backpressure/drain assertions were replaced with
  deterministic `TaskCompletionSource` gates and timeout assertions.
- The previous `InputCapacity` guard that capped capacity by concurrency was
  removed and covered by a regression test.

### Concurrency/lifecycle review

- Output channel assumptions now match the public reader surface: multiple
  runtime writers and external output consumers are supported.
- Parallel producer buffering is controlled by `InputCapacity`; worker count is
  controlled by `EffectiveMaxConcurrency`.
- Drain no longer depends on wall-clock sleeps to prove accepted-work
  completion.

### Public API review

- No shipped public API signature was changed.
- No shipped public API was removed.
- The change is observable runtime behavior, not a public signature change, and
  is documented in runtime contracts, configuration, recipe, migration guide,
  README, and changelog.

### Tests added/updated

- `RuntimeOptions_DefaultWithSinkAndEmitAll_ShouldRequireOutputConsumer`
- `RuntimeOptions_DefaultWithSinkAndSuppressSuccess_ShouldCompleteWithoutOutputConsumer`
- `RuntimeOptions_BoundedOutput_WithSinkAndUnreadOutputs_ShouldRequireOutputConsumer`
- `TypedPipeline_OutputPolicyEmitAll_BoundedOutputBlocksWhenReaderSlow`
- `TypedPipeline_OutputPolicyEmitAll_DefaultOutputBlocksWhenReaderSlow`
- `TypedPipeline_InputCapacity_GreaterThanMaxConcurrency_IsHonored`
- `DrainAsync_WithBufferedInput_ShouldCompleteAcceptedBufferedItems`
- `PipelineChannelFactory_Output_AllowsMultipleWritersAndReaders`
- sleep-based drain/backpressure assertions in affected tests were replaced
  with deterministic gates.

### Documentation updated

- Runtime contracts, configuration, architecture, README, changelog, migration
  guide, bounded-output recipe, and typed-only plan/progress now describe the
  bounded default output contract and full `InputCapacity` behavior.

### Remaining risks

- This is an intentional behavior change for sink-attached `EmitAll` runs that
  previously relied on unread unbounded outputs. Migration guidance is to read
  `PipelineRun<T>.Outputs` or use a suppressing output policy.
- No AOT smoke project exists under `tests`; the validation used the
  Release build with trim/AOT analyzers enabled by the project file.

### Commands run

```powershell
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore --filter "RuntimeOptions_DefaultWithSinkAndEmitAll|TypedPipeline_InputCapacity_GreaterThanMaxConcurrency|DrainAsync_WithBufferedInput"
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore --filter "RuntimeOptions_DefaultWithSinkAndEmitAll|RuntimeOptions_DefaultWithSinkAndSuppressSuccess|RuntimeOptions_BoundedOutput|OutputPolicy|PipelineChannelFactory|TypedPipeline_InputCapacity|DrainAsync_WithBufferedInput|DrainAsync_ShouldStopAcceptingNewSourceItemsAtEnvelopeBoundary|DrainAsync_WhenSourceIsBlockedInMoveNextAsync"
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build
dotnet restore SmartPipe.Core.slnx --locked-mode
dotnet build SmartPipe.Core.slnx -c Release --no-restore -warnaserror
dotnet test SmartPipe.Core.slnx -c Release --no-build
dotnet pack src\SmartPipe.Core\SmartPipe.Core.csproj -c Release --no-build -o artifacts\packages
dotnet pack src\SmartPipe.Extensions\SmartPipe.Extensions.csproj -c Release --no-build -o artifacts\packages
git diff --check
```

### Result

- [x] Pass
- [ ] Needs follow-up before next step

## Step 17 final audit

### Changed files

- `src/SmartPipe.Core/**`
- `src/SmartPipe.Extensions/**`
- `tests/SmartPipe.Core.Tests/**`
- `tests/SmartPipe.Extensions.Tests/**`
- `benchmarks/SmartPipe.Benchmarks/**`
- `.github/workflows/ci.yml`
- `README.md`
- `CHANGELOG.md`
- `docs/**`
- `docs/plans/typed-only-core-refactor-progress.md`

### Architecture review

- Active source, tests, benchmarks, CI smoke, README, changelog, and active docs no longer reference removed legacy runtime APIs.
- The current implementation exposes the typed/envelope runtime path, typed adapters, typed DI factory, typed hosted service, typed health checks, typed metrics snapshots, and typed package consumer smoke.
- Historical plan and migration documents retain legacy names only as historical or migration context.

### Correctness review

- `rg` audit found no removed legacy runtime symbols in active source, tests, benchmarks, active docs, changelog, README, or CI workflow.
- `dotnet format --verify-no-changes --no-restore` is clean after applying formatter fixes.
- `git diff --check` is clean; Git reports line-ending normalization warnings only.

### Concurrency/lifecycle review

- Full no-build test commands return success after the typed lifecycle, observer, metrics, DI, adapter, and health-check coverage added during steps 8-16.
- Core and Extensions source projects build independently with zero warnings after final formatting.

### Public API review

- Public API baselines were updated for typed-only Core and Extensions surface.
- Removed public legacy APIs are no longer present in active source or active docs.

### Tests added/updated

- Added targeted regression coverage for stage execution retry/timeout/circuit/dead-letter behavior.
- Added observer dispatcher failure propagation coverage.
- Added metrics recorder and snapshot coverage.
- Added typed DI/factory/hosted-service coverage.
- Added typed adapter coverage.
- Added typed health-check coverage.
- Disabled test parallelization in Core tests to keep lifecycle coverage deterministic.

### Documentation updated

- Active docs, README, changelog, CI, contributing/release docs, health checks, observers, hosting, DI, observability, runtime contracts, and architecture docs now describe the typed-only runtime.

### Remaining risks

- Full solution restore/build remains blocked in this environment by `NU1301`/SSL access to `https://api.nuget.org/v3/index.json`. A follow-up full CI run with package restore access is still required.
- `dotnet test ... --no-build --no-restore` returns exit code 0 but emits minimal/no test-count output after the restore state was disturbed; earlier full no-build solution tests in this run reported Core and Extensions pass counts before the NuGet restore failure.

### Commands run

```powershell
rg -n "SmartPipeChannel|SmartPipeChannelOptions|ProcessingContext|ProcessingResult|legacy runtime|legacy channel|ChannelPool|AdaptiveParallelism|AdaptiveMetrics|UpdateAdaptive|ITransformer<|ISource<|ISink<|MiddlewareTransformer|RetryQueue|RetryItem|PipelineCancellation|RunInBackground" src tests benchmarks README.md CHANGELOG.md docs .github\workflows\ci.yml --glob "*.cs" --glob "*.md" --glob "*.yml" --glob "!docs/plans/**" --glob "!docs/migration/1.0-to-1.1.md" --glob "!docs/migration/legacy-to-typed.md"
dotnet format SmartPipe.Core.slnx --no-restore
dotnet format SmartPipe.Core.slnx --verify-no-changes --no-restore
dotnet build src\SmartPipe.Core\SmartPipe.Core.csproj -c Release --no-restore -v:minimal
dotnet build src\SmartPipe.Extensions\SmartPipe.Extensions.csproj -c Release --no-restore -v:minimal
dotnet test SmartPipe.Core.slnx -c Release --no-build --no-restore -v:minimal
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build --no-restore -v:normal
dotnet test tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build --no-restore -v:normal
dotnet pack src\SmartPipe.Core\SmartPipe.Core.csproj -c Release --no-build --no-restore -o artifacts\packages
dotnet pack src\SmartPipe.Extensions\SmartPipe.Extensions.csproj -c Release --no-build --no-restore -o artifacts\packages
git diff --check
```

### Result

- [x] Pass with environment-limited restore/build caveat
- [ ] Needs follow-up before next step

## Step 14 review

### Changed files

- `src/SmartPipe.Core/PipelineRun.cs`
- `src/SmartPipe.Core/SmartPipeMetrics.cs`
- `src/SmartPipe.Core/TypedPipelineRuntime.cs`
- `src/SmartPipe.Core/PublicAPI.Unshipped.txt`
- `src/SmartPipe.Extensions/SmartPipeHealthCheck.cs`
- `src/SmartPipe.Extensions/SmartPipeHealthCheckOptions.cs`
- `src/SmartPipe.Extensions/SmartPipeServiceCollectionExtensions.cs`
- `src/SmartPipe.Extensions/SmartPipeTypedFactory.cs`
- `src/SmartPipe.Extensions/PublicAPI.Unshipped.txt`
- `tests/SmartPipe.Extensions.Tests/HealthCheckTests.cs`
- `README.md`
- `docs/hosting.md`
- `docs/health-checks.md`
- `docs/plans/typed-only-core-refactor-progress.md`

### Architecture review

- Health checks now use typed run state and immutable `SmartPipeMetricsSnapshot` values.
- `AddSmartPipe` registers a typed health monitor alongside the immutable definition and per-run factory.
- The health monitor tracks state and metrics delegates; it does not register or expose a mutable runtime singleton.
- Queue health is capacity-aware using configured input/output capacities.

### Correctness review

- `PipelineRun<T>` exposes the current immutable metrics snapshot.
- The typed runtime records processed, retry, dead-letter, and terminal failure counters through `SmartPipeMetricsRecorder`.
- Health rules report faulted runs as unhealthy, not-started/high-queue/stale runs as degraded, and normal running/completed runs as healthy.
- `SmartPipeFactory` keeps the old constructor and adds a health-monitor overload, preserving the previous public constructor signature.

### Concurrency/lifecycle review

- Health monitor updates are protected by a small lock and snapshot capture invokes copied delegates outside the lock.
- Runtime-per-run DI remains intact; scoped source/stage/sink ownership is unchanged.
- No polling loop or `Task.Delay` synchronization was added.

### Public API review

- Core PublicAPI adds `PipelineRun<T>.Metrics` and `SmartPipeMetricsSnapshot.Empty`.
- Extensions PublicAPI adds typed health-check options, snapshots, monitor interfaces, monitor type, factory overload, and `AddSmartPipeHealthCheck`.
- PublicAPI build completed with zero warnings after baseline updates.

### Tests added/updated

- `HealthCheck_NotStarted_Degraded`
- `HealthCheck_Running_Healthy`
- `HealthCheck_Faulted_Unhealthy`
- `HealthCheck_QueueHigh_Degraded`
- `HealthCheck_Stale_Degraded`
- `DI_FactoryStart_UpdatesHealthMonitorWithTypedRunState`

### Documentation updated

- Added `docs/health-checks.md`.
- Updated hosting docs and README with typed health-check registration and behavior.

### Remaining risks

- Full solution restore is blocked in this environment by NuGet SSL/NU1301 access to `https://api.nuget.org/v3/index.json`.
- The required network restore was retried with escalation but the environment rejected the escalation due usage limit.
- Core and Extensions packages were produced after project-level local-cache restores with `NuGetAudit=false` because the remote vulnerability feed was unavailable.

### Commands run

```powershell
dotnet build SmartPipe.Core.slnx -c Release --no-restore -v:minimal
dotnet test tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build --filter HealthCheck
dotnet test SmartPipe.Core.slnx -c Release --no-build
dotnet restore SmartPipe.Core.slnx --locked-mode
dotnet restore SmartPipe.Core.slnx --locked-mode --ignore-failed-sources
dotnet test tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build --no-restore --filter HealthCheck
dotnet test SmartPipe.Core.slnx -c Release --no-build --no-restore
dotnet restore src\SmartPipe.Core\SmartPipe.Core.csproj --locked-mode --ignore-failed-sources -p:NuGetAudit=false -p:TreatWarningsAsErrors=false
dotnet pack src\SmartPipe.Core\SmartPipe.Core.csproj -c Release --no-build --no-restore -o artifacts\packages
dotnet restore src\SmartPipe.Extensions\SmartPipe.Extensions.csproj --locked-mode --ignore-failed-sources -p:NuGetAudit=false -p:TreatWarningsAsErrors=false
dotnet pack src\SmartPipe.Extensions\SmartPipe.Extensions.csproj -c Release --no-build --no-restore -o artifacts\packages
```

### Result

- [x] Pass
- [ ] Needs follow-up before next step

## Step 13 review

### Changed files

- Removed legacy Core runtime/API files: `SmartPipeChannel`, `SmartPipeChannelOptions`, `ProcessingContext`, `ProcessingResult`, `ISource`/`ITransformer`/`ISink`, `LegacyAdapters`, `MiddlewareTransformer`, `RetryQueue`, and `PipelineCancellation`.
- Removed unused adaptive/channel-pool public surface and tests.
- Migrated Extensions sources, transforms, and sinks to `IPipelineSource<T>`, `IPipelineTransformer<TInput,TOutput>`, `IPipelineSink<T>`, typed envelopes, and `StageResult<T>`.
- Replaced output result shape with `PipelineResult<T>` and updated `PipelineOutput<T>`, `PipelineRun<T>`, runtime output emission, and result reading.
- Fixed dead-letter persistence and replay to use `DeadLetterEnvelope<T>` and `OriginalPayload` instead of stale legacy result JSON.
- Added `SmartPipeActivitySource` and typed run/transform activities.
- Updated PublicAPI baselines and typed-only documentation.
- Serialized `SmartPipe.Core.Tests` to avoid full-suite scheduler contention in lifecycle tests.

### Architecture review

- One runtime model remains in source, tests, and benchmarks: the typed envelope runtime.
- No source, test, or benchmark references to removed legacy APIs remain.
- Adaptive marketing/runtime stack and public `ChannelPool` were removed; concurrency is represented by `MaxConcurrency`.

### Correctness review

- Extension components now consume and emit `ProcessingEnvelope<T>` and `StageResult<T>`.
- Dead-letter replay now reads `OriginalPayload` with pipeline, run, trace, metadata, and failure-time context.
- Activity tracing is restored for typed run and transform boundaries.

### Concurrency/lifecycle review

- `SingleInputBuffer<T>` creates bounded channels directly with synchronous continuations disabled.
- Core tests are serialized because full-suite parallel execution caused rotating deterministic-gate timeouts while isolated lifecycle tests passed.
- Drain, cancel, abort, and output lifecycle tests pass in the full Core suite.

### Public API review

- Core and Extensions PublicAPI files were updated for breaking legacy removal and typed signatures.
- Build passes with zero PublicAPI analyzer warnings.

### Tests added/updated

- Extension selector, sink, and transform tests migrated to the typed envelope API.
- Dead-letter source/sink tests were rewritten for `DeadLetterEnvelope<T>`.
- Activity test proves the `SmartPipe.Core` `ActivitySource` emits `Pipeline.Run` and `Transform`.
- Legacy, adaptive, and channel-pool tests were removed with their deleted APIs.

### Documentation updated

- README and active docs were rewritten or updated for typed-only runtime and migration.
- Active docs grep is clean for removed legacy names, excluding historical plan and 1.0 migration source files.

### Remaining risks

- Historical docs under `docs/plans` and `docs/migration/1.0-to-1.1.md` still mention removed APIs as history; Step 16 will perform the final documentation audit.
- Step 14 still needs typed health checks because the old `SmartPipeHealthCheck` was removed with the legacy runtime.

### Commands run

```powershell
dotnet build SmartPipe.Core.slnx -c Release --no-restore -v:minimal
dotnet test tests/SmartPipe.Extensions.Tests/SmartPipe.Extensions.Tests.csproj -c Release --no-build --filter "DeadLetter"
dotnet test tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SmartPipeActivityTests.RunAsync_ShouldEmitStableActivitySourceAndProcessingActivities"
dotnet test tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj -c Release --no-build
dotnet test tests/SmartPipe.Extensions.Tests/SmartPipe.Extensions.Tests.csproj -c Release --no-build
dotnet test SmartPipe.Core.slnx -c Release --no-build
dotnet pack src/SmartPipe.Core/SmartPipe.Core.csproj -c Release --no-build -o artifacts/packages
dotnet pack src/SmartPipe.Extensions/SmartPipe.Extensions.csproj -c Release --no-build -o artifacts/packages
rg -n "<legacy pattern>" src tests benchmarks --glob "*.cs" --glob "*.md"
rg -n "<legacy pattern>" README.md docs --glob "*.md" --glob "!docs/plans/**" --glob "!docs/migration/1.0-to-1.1.md"
```

### Result

- [x] Pass
- [ ] Needs follow-up before next step

## Step 12 review

### Changed files

- `src/SmartPipe.Core/PipelineAdapters.cs`
- `src/SmartPipe.Core/PublicAPI.Unshipped.txt`
- `tests/SmartPipe.Core.Tests/Engine/PipelineAdaptersTests.cs`
- `docs/getting-started.md`
- `docs/migration/legacy-to-typed.md`
- `docs/api-reference.md`
- `README.md`
- `CHANGELOG.md`
- `docs/plans/typed-only-core-refactor-progress.md`

### Architecture review

- Added typed convenience adapters over `IPipelineSource<T>`,
  `IPipelineTransformer<TInput,TOutput>`, and `IPipelineSink<T>`.
- The adapters do not use `SmartPipeChannel`, `ProcessingContext`, or legacy
  source/transformer/sink interfaces.

### Correctness review

- `PipelineSource.FromAsyncEnumerable` emits typed envelopes with stable
  pipeline/run ids and monotonic trace ids.
- `PipelineTransformer.FromFunc` converts payload results to valid
  `StageResult<TOutput>.Success`.
- `PipelineSink.FromFunc` writes payloads from typed envelopes.

### Concurrency/lifecycle review

- Runtime cancellation tokens are passed to async enumerable enumeration,
  transform delegates, and sink delegates.
- Adapter disposal is no-op because these wrappers do not own additional
  resources.

### Public API review

- `PublicAPI.Unshipped.txt` was updated for `PipelineSource`,
  `PipelineTransformer`, and `PipelineSink`.

### Tests added/updated

- `Adapters_FromAsyncEnumerable_Works`
- `Adapters_TransformerFromFunc_Works`
- `Adapters_SinkFromFunc_Works`
- `Adapters_CancellationTokenIsPassed`

### Documentation updated

- Updated getting started, migration guide, API reference, README, and
  changelog with typed convenience adapter examples.

### Remaining risks

- The adapters intentionally stay minimal. Additional overloads for sync
  delegates or envelope-aware delegates are deferred until required by users.

### Commands run

```powershell
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore --filter Adapters
dotnet test SmartPipe.Core.slnx -c Release --no-restore
```

### Result

- [x] Pass
- [ ] Needs follow-up before next step

## Step 11 review

### Changed files

- `src/SmartPipe.Extensions/SmartPipeTypedFactory.cs`
- `src/SmartPipe.Extensions/SmartPipeServiceCollectionExtensions.cs`
- `src/SmartPipe.Extensions/SmartPipeHostedService.cs`
- `src/SmartPipe.Extensions/PublicAPI.Unshipped.txt`
- `tests/SmartPipe.Extensions.Tests/Extensions/SmartPipeTypedDiTests.cs`
- `tests/SmartPipe.Core.Tests/Diagnostics/SmartPipeActivityTests.cs`
- `docs/dependency-injection.md`
- `docs/hosting.md`
- `docs/configuration.md`
- `docs/migration/legacy-to-typed.md`
- `README.md`
- `CHANGELOG.md`
- `docs/plans/typed-only-core-refactor-progress.md`

### Architecture review

- Added immutable typed definitions plus `ISmartPipeFactory<TInput,TOutput>`
  so DI starts a fresh typed runtime per run.
- The typed factory creates an async service scope per run and resolves
  source, stage, and sink inside that scope.
- Legacy `AddSmartPipe` compatibility overloads no longer register
  `SmartPipeChannel` as a singleton runtime; they route through the scoped
  legacy channel factory.

### Correctness review

- Scoped typed components are wrapped as externally owned components so the
  runtime does not dispose DI-owned instances directly.
- The factory disposes the async scope after run completion or explicit run
  disposal.
- Hosted service can now run through `ISmartPipeFactory<TInput,TOutput>` and
  drains/disposes the typed run on shutdown.

### Concurrency/lifecycle review

- `Start()` returns a new `PipelineRun<TOutput>` each time.
- `ValidateScopes=true` coverage proves scoped stages and sinks are resolved
  within an owned run scope, not from the root provider.
- A full-suite activity test failure exposed global `ActivityListener`
  cross-test interference; the diagnostics test now identifies its own run by
  a unique parallelism tag.

### Public API review

- `SmartPipe.Extensions/PublicAPI.Unshipped.txt` was updated for typed
  definitions, factories, builder, hosted constructor, and typed registration
  overloads.
- Existing legacy DI APIs remain present for migration compatibility.

### Tests added/updated

- `DI_AddSmartPipe_DoesNotRegisterRuntimeAsSingleton`
- `DI_FactoryCreatesNewRuntimePerStart`
- `DI_ScopedStageResolvedWithinScope`
- `DI_ScopedSinkDisposedWithScope`
- `DI_ValidateScopes_DoesNotThrow`
- `HostedService_CreatesRuntimeFromFactory`
- Hardened `SmartPipeActivityTests.RunAsync_ShouldEmitStableActivitySourceAndProcessingActivities`
  against full-suite activity listener interference.

### Documentation updated

- Added dependency injection and hosting docs.
- Updated configuration, migration guide, README, and changelog.

### Remaining risks

- The typed DI builder currently supports the single-stage `TInput -> TOutput`
  registration shape required by this step. Multi-stage DI registration remains
  deferred to later typed API expansion.
- A pre-existing RS0027 warning remains on a legacy `AddSmartPipe` optional
  parameter overload and will be removed with the legacy surface.

### Commands run

```powershell
dotnet test tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-restore --filter "DI_|HostedService_CreatesRuntimeFromFactory"
dotnet test tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-restore --filter DI
dotnet test tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-restore --filter Hosted
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SmartPipeActivityTests.RunAsync_ShouldEmitStableActivitySourceAndProcessingActivities"
dotnet test SmartPipe.Core.slnx -c Release --no-restore
```

### Result

- [x] Pass
- [ ] Needs follow-up before next step

## Step 10 review

### Changed files

- `src/SmartPipe.Core/SmartPipeMetrics.cs`
- `src/SmartPipe.Core/SmartPipeChannel.cs`
- `src/SmartPipe.Core/PublicAPI.Shipped.txt`
- `src/SmartPipe.Core/PublicAPI.Unshipped.txt`
- `src/SmartPipe.Extensions/SmartPipeHealthCheck.cs`
- `tests/SmartPipe.Core.Tests/Engine/SmartPipeMetricsRecorderTests.cs`
- `tests/SmartPipe.Core.Tests/Engine/SmartPipeMetricsTests.cs`
- `tests/SmartPipe.Core.Tests/Engine/SmartPipeMetricsExportTests.cs`
- `docs/observability.md`
- `docs/runtime-contracts.md`
- `README.md`
- `CHANGELOG.md`
- `docs/plans/typed-only-core-refactor-progress.md`

### Architecture review

- Metrics state is now owned by `SmartPipeMetricsRecorder`; the legacy
  `SmartPipeMetrics` type is a compatibility facade over the recorder.
- `SmartPipeMeter` centralizes the public `SmartPipe.Core` meter and preserves
  the existing instrument names for processed, failed, duplicate, retry, and
  latency measurements while adding dead-lettered items.
- Health checks read an immutable metrics snapshot instead of a live mutable
  metrics object.

### Correctness review

- Public mutable metrics fields were replaced by read-only properties and
  explicit mutation methods.
- `SmartPipeMetricsSnapshot` is a get-only `sealed record` with explicit
  constructor initialization, avoiding init setters.
- Snapshot export preserves legacy keys and adds typed runtime queue depth,
  dead-letter, latency, and timestamp fields.

### Concurrency/lifecycle review

- Recorder counters use `Interlocked`; queue depths and double current-state
  values use `Volatile`.
- Concurrent snapshot/export tests cover hot updates while snapshots are being
  captured.
- No lifecycle behavior was changed in this step.

### Public API review

- This step intentionally changes the metrics surface from public fields to
  read-only properties as required by the typed-only refactor plan.
- `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` were updated for the
  metrics field removal, new recorder, new snapshot shape, and `SmartPipeMeter`.

### Tests added/updated

- `Metrics_ConcurrentRecordProcessed_ProducesCorrectCounters`
- `Metrics_SnapshotIsImmutable`
- `Metrics_QueueDepthReflectsInputOutputQueues`
- `Metrics_LastProcessedUtc_UpdatesAfterSuccess`
- `Metrics_NoPublicMutableFields`
- Existing metrics export and meter tests were updated for the recorder-backed
  snapshot/export contract.

### Documentation updated

- Added `docs/observability.md`.
- Updated runtime contracts, README, and changelog with recorder, snapshot, and
  meter semantics.

### Remaining risks

- Later typed-only removal steps must route typed runtime dead-letter events
  through the recorder consistently after the legacy runtime surface is removed.

### Commands run

```powershell
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore --filter Metrics
dotnet test SmartPipe.Core.slnx -c Release --no-restore
```

### Result

- [x] Pass
- [ ] Needs follow-up before next step

## Step 7 review

### Changed files

- `src/SmartPipe.Core/TypedPipelineRuntime.cs`
- `src/SmartPipe.Core/SmartPipeChannel.cs`
- `tests/SmartPipe.Core.Tests/Engine/TypedPipelineLifecycleTests.cs`
- `tests/SmartPipe.Core.Tests/Engine/TypedPipelineDrainTests.cs`
- `README.md`
- `docs/runtime-contracts.md`
- `docs/recipes/graceful-shutdown.md`
- `docs/plans/typed-only-core-refactor-progress.md`

### Architecture review

- Typed lifecycle ownership remains inside `TypedPipelineExecutor` and
  `PipelineLifecycleController`.
- `CancelAsync` now uses asynchronous cancellation dispatch and completes the
  typed output channel with `OperationCanceledException`, matching the
  documented observable shutdown contract.
- Lifecycle-induced typed output channel closure during cancellation is treated
  as cancellation, not as a pipeline fault.
- Full solution validation exposed an unrelated legacy runtime defect:
  `SmartPipeChannel` used `_adaptiveParallelism.Current` as consumer count even
  when adaptive mode was disabled. This could ignore
  `MaxDegreeOfParallelism = 1` and reorder or delay `RunInBackground` output.
  The consumer count now uses adaptive state only when adaptive mode is enabled.

### Correctness review

- `DrainAsync` stops accepting new source items at item boundaries and finishes
  accepted work.
- Drain timeout and drain-call cancellation do not mark the run as cancelled.
- `CancelAsync` and `AbortAsync` have distinct tested states.
- `AbortAsync` completes outputs with `OperationCanceledException`.
- `DisposeAsync` cancels a running typed run and disposes runtime-owned
  components once.
- The previous drain cancellation test was tightened so it first proves the
  source is blocked inside `MoveNextAsync`; it no longer races against valid
  source-boundary drain completion.

### Concurrency/lifecycle review

- No busy polling was added.
- Typed cancellation no longer risks converting cancellation-time output writer
  closure into `Faulted`.
- Legacy non-adaptive runs now respect configured `MaxDegreeOfParallelism`
  instead of accidentally using adaptive current parallelism.

### Public API review

- No public signatures changed.
- No shipped public API was removed.

### Tests added/updated

- `TypedPipeline_Drain_StopsReadingNewSourceItems`
- `TypedPipeline_Drain_FinishesInFlightItems`
- `TypedPipeline_Drain_DoesNotSetCancelledOnTimeout`
- `TypedPipeline_Drain_TimeoutThrowsTimeoutException`
- `TypedPipeline_Cancel_CancelsSourceAndWorkers`
- `TypedPipeline_Cancel_StateIsCancelled`
- `TypedPipeline_Abort_CompletesOutputsWithOperationCanceledException`
- `TypedPipeline_Abort_StateIsAborted`
- `TypedPipeline_Dispose_CancelsAndDisposesComponents`
- `DrainAsync_WithCancellation_ShouldRespectCancellationToken`

### Documentation updated

- Runtime contracts now document typed drain, cancel, abort, dispose, and output
  completion behavior.
- Added `docs/recipes/graceful-shutdown.md`.
- README now links the shutdown recipe and summarizes typed lifecycle controls.

### Remaining risks

- `dotnet restore --locked-mode` could not be completed in the sandbox because
  NuGet access failed with `NU1301`; validation used existing restored assets
  with `--no-restore`.

### Commands run

```powershell
dotnet test tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj -c Release --filter "TypedPipeline_Drain|TypedPipeline_Cancel|TypedPipeline_Abort|TypedPipeline_Dispose"
dotnet build tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj -c Release --no-restore
dotnet test tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj -c Release --no-build --filter "TypedPipeline_Drain|TypedPipeline_Cancel|TypedPipeline_Abort|TypedPipeline_Dispose"
dotnet test tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj -c Release --no-build --filter Drain
dotnet test tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj -c Release --no-build --filter Cancel
dotnet test tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj -c Release --no-build --filter Abort
dotnet test tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj -c Release --no-build --filter RunInBackground
dotnet test tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj -c Release --no-build
dotnet test SmartPipe.Core.slnx -c Release --no-restore
dotnet build SmartPipe.Core.slnx -c Release --no-restore -warnaserror
git diff --check
```

### Result

- [x] Pass
- [ ] Needs follow-up before next step

## Step 8 review

### Changed files

- `src/SmartPipe.Core/Runtime/Execution/StageExecutor.cs`
- `tests/SmartPipe.Core.Tests/Engine/StageExecutorTests.cs`
- `tests/SmartPipe.Core.Tests/Engine/ModernPipelineRuntimeTests.cs`
- `docs/resilience.md`
- `docs/runtime-contracts.md`
- `docs/configuration.md`
- `CHANGELOG.md`
- `docs/plans/typed-only-core-refactor-progress.md`

### Architecture review

- Stage failure handling remains owned by `StageExecutor`.
- Retry, timeout, circuit breaker recording/rejection, dead-letter routing,
  and terminal failure action selection are now covered by focused typed
  runtime tests.
- Circuit breaker rejection is terminal for the current item. It does not
  schedule retry attempts into an already open breaker.

### Correctness review

- RED coverage exposed that open-breaker rejection retried itself until retry
  budget exhaustion.
- The fix prevents retry scheduling for a failure that opens the breaker and
  for later open-breaker rejections.
- With a configured `RetryPolicy`, breaker-open terminal handling uses
  `OnRetryExhausted`; otherwise it uses `OnPermanentFailure`.
- Legacy tests that encoded retrying open-breaker rejection were updated to the
  Step 8 contract.

### Concurrency/lifecycle review

- Retry delays still respect cancellation through `Task.Delay(delay, ct)`.
- The drain/retry regression test uses a transformer-owned
  `TaskCompletionSource` gate rather than observer timing or wall-clock
  polling.
- Timeout handling still disposes linked timeout tokens through `using var`.

### Public API review

- No public signatures changed.
- No shipped public API was removed.

### Tests added/updated

- `StageExecutor_Retry_RetriesConfiguredAttempts`
- `StageExecutor_Retry_StopsAfterMaxAttempts`
- `StageExecutor_Timeout_ProducesTimeoutFailure`
- `StageExecutor_CircuitBreaker_OpensAfterPolicy`
- `StageExecutor_CircuitBreaker_RejectionIsNotRetriedForever`
- `StageExecutor_DeadLetter_WritesTerminalFailure`
- `StageExecutor_Drain_CompletesAcceptedRetryPolicy`
- Updated existing typed circuit-breaker tests to assert terminal
  open-breaker rejection.

### Documentation updated

- `docs/resilience.md`
- `docs/runtime-contracts.md`
- `docs/configuration.md`
- `CHANGELOG.md`

### Remaining risks

- Circuit breaker threshold behavior still delegates to the existing
  `CircuitBreaker` implementation. Step 8 only fixed typed runtime
  retry/rejection orchestration around that breaker.

### Commands run

```powershell
dotnet build SmartPipe.Core.slnx -c Release --no-restore
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore --filter StageExecutor
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore --filter CircuitBreaker
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build --filter StageExecutor
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build --filter CircuitBreaker
dotnet test SmartPipe.Core.slnx -c Release --no-restore
```

### Result

- [x] Pass
- [ ] Needs follow-up before next step

## Step 9 review

### Changed files

- `src/SmartPipe.Core/PipelineObserverDispatcher.cs`
- `tests/SmartPipe.Core.Tests/Engine/ObserverDispatcherTests.cs`
- `docs/resilience.md`
- `docs/runtime-contracts.md`
- `docs/configuration.md`
- `CHANGELOG.md`
- `docs/plans/typed-only-core-refactor-progress.md`

### Architecture review

- Inline and buffered observer dispatch still share the same registration
  policy classifier.
- Buffered dispatch remains bounded and opt-in; no unbounded fire-and-forget
  queue was introduced.
- `CompleteAsync` is the reliable buffered failure surfacing point when
  `FlushOnCompletion = true`.

### Correctness review

- RED coverage showed buffered observer failures configured to fault the run
  could be replaced by `ChannelClosedException`.
- `BufferedPipelineObserverDispatcher.EmitAsync` now rethrows the stored
  observer fault when the worker has already closed the queue because of that
  fault.
- `UseRegistrationPolicy` now has focused coverage for registration-level
  `FaultPipeline`, critical observers, and `RemoveObserver`.
- Global `Ignore` coverage proves critical/fault-policy observers do not fault
  the run when the global failure mode says to ignore.

### Concurrency/lifecycle review

- Buffered worker failures are observed through `CompleteAsync`; no
  fire-and-forget exception path was added.
- Remove-observer behavior disables later callbacks without cancelling an
  in-progress callback.

### Public API review

- No public signatures changed.
- No shipped public API was removed.

### Tests added/updated

- `BufferedObserver_UseRegistrationPolicy_FaultPipelineObserverFaultsRun`
- `BufferedObserver_UseRegistrationPolicy_CriticalObserverFaultsRun`
- `BufferedObserver_UseRegistrationPolicy_RemoveObserverDisablesObserver`
- `BufferedObserver_IgnoreMode_DoesNotFaultRun`
- `InlineObserver_FaultPipelineObserverFaultsRun`
- `ObserverDispatcher_CompleteAsync_PropagatesBufferedFault`

### Documentation updated

- `docs/resilience.md`
- `docs/runtime-contracts.md`
- `docs/configuration.md`
- `CHANGELOG.md`

### Remaining risks

- Buffered dispatch intentionally does not emit recursive inline-style
  `ObserverFailedEvent` diagnostics; this remains documented as a 1.1.0
  limitation.

### Commands run

```powershell
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore --filter Observer
dotnet test SmartPipe.Core.slnx -c Release --no-restore
```

### Result

- [x] Pass
- [ ] Needs follow-up before next step

## Step 0 review

### Changed files

- `docs/decisions/0001-remove-legacy-runtime.md`
- `docs/plans/typed-only-core-refactor.md`
- `docs/plans/typed-only-core-refactor-progress.md`
- `README.md`
- `docs/runtime-contracts.md`
- `CHANGELOG.md`

### Architecture review

- The decision explicitly removes legacy as a runtime model, not as an
  immediate deletion patch.
- Useful legacy behavior must be preserved in typed runtime before deletion.
- Local-first, storage, distributed execution, and security audit scope are
  excluded.

### Correctness review

- No runtime behavior is changed in Step 0.

### Concurrency/lifecycle review

- Lifecycle behavior is not changed in Step 0.

### Public API review

- No public API is changed in Step 0.

### Tests added/updated

- None. Step 0 is documentation/control-plane only.

### Documentation updated

- Added decision and local execution plan.
- Added README/runtime-contract/changelog notices.

### Remaining risks

- Legacy inventory must be kept current as code changes.

### Commands run

```powershell
dotnet restore SmartPipe.Core.slnx --locked-mode
dotnet build SmartPipe.Core.slnx -c Release --no-restore
dotnet test SmartPipe.Core.slnx -c Release --no-build
```

### Result

- [x] Pass
- [ ] Needs follow-up before next step

## Step 15 review

### Changed files

- `.github/workflows/ci.yml`
- `artifacts/consumer-smoke/SmartPipe.ConsumerSmoke.csproj`
- `artifacts/consumer-smoke/NuGet.Config`
- `artifacts/consumer-smoke/Program.cs`
- `docs/contributing.md`
- `docs/release.md`
- `README.md`
- `docs/plans/typed-only-core-refactor-progress.md`

### Architecture review

- Consumer smoke now exercises the typed-only API from packages: `IPipelineSource<T>`, `IPipelineTransformer<TInput,TOutput>`, `IPipelineSink<T>`, `ProcessingEnvelope<T>`, `StageResult<T>`, `PipelineRun<T>`, typed DI factory, and health monitor.
- CI already runs on `main`, `upd`, pull requests to `main`, and `workflow_dispatch`.
- CI still includes pack, trimmed consumer smoke, NativeAOT consumer smoke, JSON/dead-letter trim smoke, and JSON/dead-letter AOT smoke.

### Correctness review

- Main consumer smoke validates source -> transform -> sink, output consumer, `DrainAsync`, DI factory, and typed Extensions `FilterTransform`.
- JSON/dead-letter AOT smoke no longer uses `ProcessingContext`, `ProcessingResult`, or legacy `ISource<T>`.
- Dead-letter smoke uses typed `DeadLetterEnvelope<T>` and `ProcessingEnvelope<DeadLetterEnvelope<T>>`.

### Concurrency/lifecycle review

- Consumer smoke validates `DrainAsync` reaches `Completed` without adding timing sleeps or polling.
- DI factory smoke uses `ValidateScopes=true` and scoped source/stage/sink registrations.

### Public API review

- No new public API was introduced in this step.
- The smoke uses package references instead of project references.

### Tests added/updated

- Local `artifacts/consumer-smoke` package consumer smoke was regenerated with typed-only code.
- CI consumer smoke heredoc was rewritten to match the typed-only package smoke.
- CI JSON/dead-letter AOT smoke was migrated to typed envelope/dead-letter contracts.

### Documentation updated

- Added `docs/contributing.md`.
- Added `docs/release.md`.
- Added the new docs to README.

### Remaining risks

- Local consumer restore required package source mapping and an isolated package folder because global NuGet cache contained an older `SmartPipe.Core` 1.1.0 package with the same version.
- Full solution restore remains blocked by nuget.org SSL/NU1301 in this environment; local smoke used `artifacts/packages` for SmartPipe packages and the existing global package cache as an offline source for external dependencies.

### Commands run

```powershell
dotnet new console -n SmartPipe.ConsumerSmoke -o artifacts\consumer-smoke --force
dotnet add artifacts\consumer-smoke\SmartPipe.ConsumerSmoke.csproj package SmartPipe.Core --version 1.1.0 --no-restore
dotnet add artifacts\consumer-smoke\SmartPipe.ConsumerSmoke.csproj package SmartPipe.Extensions --version 1.1.0 --no-restore
dotnet restore artifacts\consumer-smoke\SmartPipe.ConsumerSmoke.csproj --configfile artifacts\consumer-smoke\NuGet.Config --packages artifacts\consumer-smoke\packages --no-cache --force-evaluate -p:NuGetAudit=false
dotnet run --project artifacts\consumer-smoke\SmartPipe.ConsumerSmoke.csproj -c Release --no-restore
rg -n "SmartPipeChannel|ProcessingContext|ProcessingResult|ISource<|ITransformer<|ISink<|RunInBackground|Legacy" .github\workflows\ci.yml artifacts\consumer-smoke\Program.cs docs\contributing.md docs\release.md
```

### Result

- [x] Pass
- [ ] Needs follow-up before next step

## Step 6 review

### Changed files

- `src/SmartPipe.Core/TypedPipelineRuntime.cs`
- `tests/SmartPipe.Core.Tests/Engine/TypedPipelineOutputModeTests.cs`
- `README.md`
- `docs/architecture.md`
- `docs/configuration.md`
- `docs/runtime-contracts.md`
- `docs/recipes/bounded-output.md`
- `docs/plans/typed-only-core-refactor-progress.md`

### Architecture review

- `PipelineOutputEmitter` already owned output policy and channel writes from
  Step 4.
- The public `OutputCapacity` property remains nullable. `null` is now treated
  as automatic bounded capacity selection for typed runs with or without a
  sink.
- This avoids changing the public property type and avoids breaking
  compatibility `OutputMode` behavior by changing the `OutputPolicy` default.

### Correctness review

- `EmitAll` emits success outputs and preserves bounded output backpressure.
- `EmitFailuresOnly` suppresses success outputs and keeps terminal failures.
- `SuppressSuccessWhenSinkAttached` prevents sink-only success-output deadlock
  while still allowing failure outputs.
- `SuppressAllWhenSinkAttached` suppresses both success and failure outputs
  when a sink exists.

### Concurrency/lifecycle review

- Explicit bounded output still backpressures before sink writes when the
  reader is slow.
- Automatic output-only bounded output backpressures when the output reader is
  slow.
- Sink-attached `EmitAll` output now has the same bounded backpressure contract
  as output-only runs. Sink-only callers must consume outputs or use a
  suppressing output policy.

### Public API review

- No public signature changed.
- The observable default changes only in effective runtime behavior:
  typed runs now use bounded output in automatic mode.

### Tests added/updated

- `TypedPipeline_OutputPolicyEmitAll_EmitsSuccessOutputs`
- `TypedPipeline_OutputPolicyEmitFailuresOnly_SuppressesSuccess`
- `TypedPipeline_OutputPolicySuppressSuccessWhenSinkAttached_DoesNotBlockSink`
- `OutputPolicySuppressSuccessWhenSinkAttached_WithSink_ShouldStillEmitFailures`
- `TypedPipeline_OutputPolicySuppressAllWhenSinkAttached_DoesNotEmitOutputs`
- `TypedPipeline_OutputPolicyEmitAll_BoundedOutputBlocksWhenReaderSlow`
- `TypedPipeline_OutputPolicyEmitAll_DefaultOutputBlocksWhenReaderSlow`

### Documentation updated

- Runtime contracts, configuration, architecture, README, and bounded-output
  recipe now document automatic output capacity, sink-only usage, output
  consumer usage, and slow-reader behavior.

### Remaining risks

- Automatic bounded output can block runs that do not consume
  `PipelineRun<T>.Outputs`; this is now the documented output-consumer
  contract and is covered by regression tests.

### Commands run

```powershell
dotnet build SmartPipe.Core.slnx -c Release --no-restore
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build --filter OutputPolicy
dotnet test SmartPipe.Core.slnx -c Release --no-restore
```

### Result

- [x] Pass
- [ ] Needs follow-up before next step

## Step 5 review

### Changed files

- `tests/SmartPipe.Core.Tests/Engine/TypedPipelineConcurrencyTests.cs`
- `README.md`
- `docs/configuration.md`
- `docs/runtime-contracts.md`
- `docs/plans/typed-only-core-refactor-progress.md`

### Architecture review

- Step 5 runtime structure was already prepared by Steps 2-4:
  `MaxConcurrency`, bounded input channel creation, `PipelineProducer`, and
  `PipelineWorker` are wired into the typed runtime.
- No additional production runtime patch was required after adding the Step 5
  acceptance tests.

### Correctness review

- Added regression coverage for sequential processing, parallel processing,
  exactly-once processing inside the in-memory run boundary, duplicate
  prevention, bounded input backpressure, source exceptions, and worker
  exceptions.
- Backpressure test accounts for async iterator semantics: the source can
  observe one item beyond available worker/queue capacity because the producer
  receives the yielded item before its bounded-channel `WriteAsync` awaits.

### Concurrency/lifecycle review

- `MaxConcurrency = 4` is proven to run four concurrent transform calls with
  deterministic `TaskCompletionSource` gates.
- Worker and source exceptions fault the run and preserve `PipelineRunState.Faulted`.
- Bounded input prevents source over-read while workers are blocked.

### Public API review

- No public API changed in this step.

### Tests added/updated

- `TypedPipeline_MaxConcurrency1_ProcessesAllItems`
- `TypedPipeline_MaxConcurrency4_ProcessesAllItemsExactlyOnce`
- `TypedPipeline_MaxConcurrency4_DoesNotDuplicateItems`
- `TypedPipeline_BoundedInput_AppliesBackpressure`
- `TypedPipeline_SourceException_FaultsRun`
- `TypedPipeline_WorkerException_FaultsRun`

### Documentation updated

- `README.md`
- `docs/configuration.md`
- `docs/runtime-contracts.md`

### Remaining risks

- The exactly-once statement remains scoped to accepted envelopes inside one
  in-memory run with `InputFullMode = Wait`; durable delivery, source replay,
  and cross-process coordination remain out of scope.

### Commands run

```powershell
dotnet build SmartPipe.Core.slnx -c Release --no-restore
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build --filter "TypedPipeline_BoundedInput|TypedPipeline_SourceException|TypedPipeline_WorkerException"
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-restore --filter TypedPipeline_MaxConcurrency
dotnet test SmartPipe.Core.slnx -c Release --no-restore
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build --filter "TypedPipeline_MaxConcurrency|TypedPipeline_BoundedInput|TypedPipeline_SourceException|TypedPipeline_WorkerException"
```

### Result

- [x] Pass
- [ ] Needs follow-up before next step

## Step 4 review

### Changed files

- `src/SmartPipe.Core/Runtime/Execution/PipelineProducer.cs`
- `src/SmartPipe.Core/Runtime/Execution/PipelineWorker.cs`
- `src/SmartPipe.Core/Runtime/Execution/StageExecutor.cs`
- `src/SmartPipe.Core/Runtime/Execution/SinkExecutor.cs`
- `src/SmartPipe.Core/Runtime/Execution/PipelineOutputEmitter.cs`
- `src/SmartPipe.Core/Runtime/Execution/PipelineLifecycleController.cs`
- `src/SmartPipe.Core/TypedPipelineRuntime.cs`
- `tests/SmartPipe.Core.Tests/Engine/ModernPipelineRuntimeTests.cs`
- `docs/architecture.md`
- `docs/plans/typed-only-core-refactor-progress.md`

### Architecture review

- `TypedPipelineExecutor` remains the task coordinator.
- Source reads, worker channel consumption, stage execution policy, sink
  writes, output policy, and run state transitions now have separate internal
  owners.
- `StageExecutor` owns retry, timeout, circuit breaker rejection, dead-letter,
  and terminal stage failure handling while delegating low-level helpers back
  to the executor to keep this step behavior-preserving.

### Correctness review

- Initial validation caught two extraction regressions:
  - retry was incorrectly returned to the outer stage loop instead of staying
    inside the current stage;
  - circuit-breaker terminal rejection with no `StopPipeline` action was
    incorrectly treated as stage success.
- Both were fixed by keeping retry inside `StageExecutor` and adding an
  explicit `StopProcessing` result flag.
- Full solution validation also exposed a drain/state race where `DrainAsync`
  could overwrite `Faulted` with `Draining` after the run had entered its
  fault path but before the run task completed. `PipelineLifecycleController`
  now publishes state with volatile reads/writes and marks `Draining` only via
  compare-exchange from `Running`.

### Concurrency/lifecycle review

- `PipelineLifecycleController` now owns `PipelineRunState` transitions.
- `PipelineWorker` owns per-worker channel consumption and failure recording.
- `SinkExecutor` keeps sink writes serialized through its own write gate.

### Public API review

- All new components are internal.
- No public signatures or shipped API entries changed in this step.

### Tests added/updated

- `TypedRuntime_ComponentSplit_BasicSourceTransformSinkStillWorks`

### Documentation updated

- Added typed runtime component diagram and responsibility boundaries to
  `docs/architecture.md`.

### Remaining risks

- `TypedPipelineExecutor` still contains component disposal, observer
  completion, and retry helper methods; further reduction should happen only
  after preserving the current regression coverage.

### Commands run

```powershell
dotnet build SmartPipe.Core.slnx -c Release --no-restore
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build --filter "TypedRuntime_ComponentSplit_BasicSourceTransformSinkStillWorks"
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build --filter "ModernPipelineRuntimeTests|RuntimeOptionsPassTests|PipelineChannelFactoryTests"
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~DrainAsync_ShouldPreserveCompletionTaskFaultState"
dotnet test SmartPipe.Core.slnx -c Release --no-build
```

### Result

- [x] Pass
- [ ] Needs follow-up before next step

## Step 1 review

### Changed files

- `docs/plans/legacy-surface-inventory.md`
- `docs/migration/legacy-to-typed.md`
- `docs/runtime-contracts.md`
- `docs/plans/typed-only-core-refactor-progress.md`

### Architecture review

- Legacy surface is classified before deletion.
- Useful behavior has typed migration targets or explicit follow-up decisions.

### Correctness review

- Inventory is documentation-only and does not change runtime behavior.

### Concurrency/lifecycle review

- Legacy lifecycle capabilities are marked as move-to-typed, not delete.

### Public API review

- Public API impact is documented as future breaking work.

### Tests added/updated

- None. Step 1 is inventory/documentation.

### Documentation updated

- Added legacy inventory and first migration table.

### Remaining risks

- Extensions package still exposes many legacy `ProcessingContext<T>` and
  `ProcessingResult<T>` APIs; replacing them requires a dedicated later step.

### Commands run

```powershell
dotnet restore SmartPipe.Core.slnx --locked-mode
dotnet build SmartPipe.Core.slnx -c Release --no-restore
dotnet test SmartPipe.Core.slnx -c Release --no-build
```

### Result

- [x] Pass
- [ ] Needs follow-up before next step

## Step 2 review

### Changed files

- `src/SmartPipe.Core/PipelineRuntimeOptions.cs`
- `src/SmartPipe.Core/TypedPipelineRuntime.cs`
- `src/SmartPipe.Core/PublicAPI.Unshipped.txt`
- `tests/SmartPipe.Core.Tests/Engine/RuntimeOptionsPassTests.cs`
- `README.md`
- `docs/configuration.md`
- `docs/runtime-contracts.md`
- `docs/plans/typed-only-core-refactor-progress.md`

### Architecture review

- Added typed-only option names without deleting compatibility names.
- `MaxConcurrency` is wired into typed executor behavior.
- `InputCapacity` and `InputFullMode` are validated and wired. The parallel
  input buffer honors `InputCapacity` independently from `MaxConcurrency`.
- `OutputPolicy` is available as the typed-only output policy name while
  `OutputMode` remains compatibility surface.

### Correctness review

- New option values validate eagerly.
- Conflicting concurrency names are rejected.
- Preserve-input-order with parallelism is rejected until reorder buffering is
  implemented.

### Concurrency/lifecycle review

- Parallel worker count now uses effective typed concurrency.
- Parallel input buffer capacity/full mode are explicit runtime options.

### Public API review

- New public enum/property additions are listed in `PublicAPI.Unshipped.txt`.
- No shipped public API is removed in this step.

### Tests added/updated

- `PipelineRuntimeOptions_Defaults_AreTypedOnlySafe`
- `PipelineRuntimeOptions_InvalidMaxConcurrency_Throws`
- `PipelineRuntimeOptions_InvalidInputCapacity_Throws`
- `PipelineRuntimeOptions_InvalidOutputCapacity_Throws`
- `PipelineRuntimeOptions_PreserveOrderWithParallelism_ThrowsUntilImplemented`
- additional compatibility checks for effective concurrency naming

### Documentation updated

- Configuration/runtime contracts/README document the new typed-only option
  names and compatibility names.

### Remaining risks

- Corrective review resolved the previous compatibility-unbounded output
  default and the temporary input-capacity guard.

### Commands run

```powershell
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --filter PipelineRuntimeOptions
dotnet restore SmartPipe.Core.slnx --locked-mode
dotnet build SmartPipe.Core.slnx -c Release --no-restore
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build --filter PipelineRuntimeOptions
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~RunInBackground_ShouldReturnReader|FullyQualifiedName~DrainAsync_ShouldPreserveCompletionTaskFaultState"
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~TypedPipelineConcurrencyTests"
dotnet test SmartPipe.Core.slnx -c Release --no-build
```

### Result

- [x] Pass
- [ ] Needs follow-up before next step

## Step 3 review

### Changed files

- `src/SmartPipe.Core/Runtime/Channels/PipelineChannelFactory.cs`
- `src/SmartPipe.Core/TypedPipelineRuntime.cs`
- `src/SmartPipe.Core/PipelineObserverDispatcher.cs`
- `tests/SmartPipe.Core.Tests/Engine/PipelineChannelFactoryTests.cs`
- `docs/runtime-contracts.md`
- `docs/architecture.md`
- `docs/plans/typed-only-core-refactor-progress.md`

### Architecture review

- Bounded typed runtime channel creation is centralized in
  `PipelineChannelFactory`.
- Input, output, and observer buffer channel cardinality are explicit.
- Typed output uses the bounded runtime default when `OutputCapacity` is null.

### Correctness review

- Factory methods validate capacity and enum full mode before creating
  channels.
- Existing typed output behavior is now bounded when `OutputCapacity` is null.

### Concurrency/lifecycle review

- Input factory uses one producer and multiple workers.
- Output factory supports multiple runtime writers and external output
  consumers. Observer buffer factory uses multiple producers and one consumer.
- Bounded channels disable synchronous continuations.

### Public API review

- `PipelineChannelFactory` is internal; no public API baseline change is
  required.

### Tests added/updated

- `PipelineChannelFactory_Input_AllowsMultipleReaders`
- `PipelineChannelFactory_Output_AllowsMultipleWritersAndReaders`
- `PipelineChannelFactory_BoundedInput_AppliesBackpressure`

### Documentation updated

- Added channel cardinality and backpressure notes to runtime contracts and
  architecture docs.

### Remaining risks

- Corrective review resolved the previous unbounded typed output default and
  the input-capacity guard.

### Commands run

```powershell
dotnet build SmartPipe.Core.slnx -c Release --no-restore
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build --filter PipelineChannelFactory
dotnet test SmartPipe.Core.slnx -c Release --no-build
dotnet test tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~RetryStillSucceeds_WhenDelayAndNextAttemptFitInsideStageTimeout"
dotnet test SmartPipe.Core.slnx -c Release --no-build
```

### Result

- [x] Pass
- [ ] Needs follow-up before next step

## Step 16 review

### Changed files

- `CHANGELOG.md`
- `README.md`
- `docs/observers.md`
- `docs/contributing.md`
- `docs/plans/typed-only-core-refactor-progress.md`

### Architecture review

- Active docs now describe the typed-only runtime as the current architecture.
- Added observer documentation for typed runtime event dispatch and failure policy.
- Historical migration guidance remains isolated in `docs/migration/legacy-to-typed.md`.

### Correctness review

- CHANGELOG was rewritten to current typed-only release notes and no longer presents removed APIs as current features.
- README links include observers, health checks, contributing, and release validation docs.
- Active docs do not claim local-first, durable queue, distributed orchestration, or exactly-once delivery semantics.

### Concurrency/lifecycle review

- Lifecycle docs continue to describe drain, cancel, abort, completion, and disposal as distinct states.
- Observer docs state that observers are diagnostic hooks, not lifecycle synchronization primitives.

### Public API review

- No public API changes were introduced in this docs step.

### Tests added/updated

- No tests added in this docs-only step.

### Documentation updated

- Added `docs/observers.md`.
- Rewrote `CHANGELOG.md`.
- Updated README docs list.
- Rephrased contributing docs to avoid stale removed-API terminology in active docs.

### Remaining risks

- Historical plan files under `docs/plans` and the explicit migration guide still contain removed API names by design.
- Full solution build/restore remains affected by the earlier NuGet SSL/NU1301 restore failure in this environment; already-built no-restore tests still execute.

### Commands run

```powershell
rg -n "SmartPipeChannel|SmartPipeChannelOptions|ProcessingContext|ProcessingResult|legacy runtime|legacy channel|ChannelPool|AdaptiveParallelism|AdaptiveMetrics|UpdateAdaptive|ITransformer<|ISource<|ISink<|MiddlewareTransformer|RetryQueue|RetryItem|PipelineCancellation|RunInBackground" README.md CHANGELOG.md docs --glob "*.md" --glob "!docs/plans/**" --glob "!docs/migration/1.0-to-1.1.md" --glob "!docs/migration/legacy-to-typed.md"
```

### Result

- [x] Pass
- [ ] Needs follow-up before next step
