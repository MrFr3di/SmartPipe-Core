# Changelog

## [Unreleased]

### Breaking Changes

- The next release version is `2.0.0` because the typed-only refactor removes
  legacy public runtime APIs that existed for `1.0.x` consumers.
- Removed legacy public runtime concepts are documented in
  `docs/migration/legacy-to-typed.md`.

### Changed

- Default `OutputPolicy` is now `SuppressSuccessWhenSinkAttached` for sink-backed pipelines.
- `PipelineRun<T>.Outputs` output channel is now single-reader by contract.

### Deprecated

- Marked `PipelineOrderingMode.PreserveInputOrder` as obsolete; parallel order preservation is not supported.

### Added

- `ISmartPipeFactory<TInput,TOutput>.StartAsync` as a default interface method for async DI factory startup.
- NuGet audit properties (`NuGetAudit`, `NuGetAuditMode`, `NuGetAuditLevel`) to `Directory.Build.props`.
- Regression tests for DI scoped run disposal idempotency and factory/instance pipeline separation.

### Fixed

- `PipelineBuilder.ToFactory` now throws a clear error when used on instance pipelines.
- Race condition in `TypedPipelineRuntime.DisposeAsync` when disposing cancellation token sources.
- Explicit compatibility `OutputMode` settings are honored when `OutputPolicy` is not set; incompatible explicit settings now fail validation.

### Typed-Only Runtime

- SmartPipe.Core now uses the typed envelope runtime as the only runtime model.
- Removed the old untyped channel runtime surface and replaced it with
  `IPipelineSource<T>`, `IPipelineTransformer<TInput,TOutput>`,
  `IPipelineSink<T>`, `ProcessingEnvelope<T>`, `StageResult<T>`, and
  `PipelineRun<T>`.
- Preserved useful behavior in typed form: bounded input/output channels,
  `MaxConcurrency`, stage retry, timeout, circuit breaker, dead-letter routing,
  observers, metrics, activity tracing, drain, cancel, abort, DI, hosting, and
  health checks.

### Runtime And Resilience

- Added typed runtime option names for `MaxConcurrency`, input capacity, output
  capacity, output policy, ordering mode, observer dispatch, and clock.
- Circuit-breaker rejection is terminal for the current item and no longer
  schedules retries into an already-open breaker.
- Stage retry respects retry delay, cancellation, and stage timeout budget.
- Drain, cancel, abort, completion, and disposal paths are covered by typed
  lifecycle tests.

### Observability

- Added `SmartPipeMetricsRecorder` and immutable `SmartPipeMetricsSnapshot`.
- Added `PipelineRun<T>.Metrics` for current run snapshots.
- Added the `SmartPipe.Core` `Meter` instruments.
- Added typed `ActivitySource` spans for run and transform boundaries.
- Buffered observer dispatch preserves the original observer exception when a
  fault policy fails a run.

### Dependency Injection, Hosting, And Health Checks

- Added immutable typed definitions and per-run factories:
  `ISmartPipeDefinition<TInput,TOutput>` and
  `ISmartPipeFactory<TInput,TOutput>`.
- `AddSmartPipe<TInput,TOutput>()` and
  `AddSmartPipeHostedService<TInput,TOutput>()` create a fresh runtime per run
  and keep scoped components inside an owned scope.
- Added typed health checks that read `PipelineRunState` and immutable metrics
  snapshots without registering a runtime singleton.

### Extensions

- Selectors, transforms, and sinks now use typed envelope interfaces.
- Dead-letter persistence and replay use `DeadLetterEnvelope<T>` and
  `OriginalPayload`.
- JSON, CSV, HTTP, EF Core, Dapper, Mapster, compression, validation, filtering,
  logging, and dead-letter components were migrated to typed contracts.

### Developer And Release Validation

- Consumer smoke installs packages from `artifacts/packages` and uses only the
  typed API.
- CI runs on `main`, `upd`, pull requests to `main`, and manual dispatch.
- CI keeps pack, test, trimmed smoke, and NativeAOT smoke coverage.

## Older Releases

Older `1.0.x` releases documented APIs and implementation details that have
been removed by the typed-only refactor. Use
`docs/migration/legacy-to-typed.md` for migration guidance and the Git history
for detailed historical release notes.
