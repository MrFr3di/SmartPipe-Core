# Changelog

## [2.2.0] — Development

### Core definition and lifecycle model

- Added immutable typed definitions with explicit pipeline/stage keys, per-run
  activation context, ownership descriptors, cached structural compilation,
  ordered rollback, readiness-aware startup, and exact run identity.
- Existing `PipelineBuilder` signatures now route through the sole legacy adapter
  into the generic compiler, activator, start operation, and executor. Factory
  chains remain reusable and legacy stage/ID behavior is preserved.
- Legacy instance sources, stages, sinks, and observers are intentionally
  single-use, including instances marked `Reusable` or `SingletonExternal`.
  Repeated and concurrent runs must use `FromFactory`, `TransformFactory`, and
  `ToFactory`; losing starts fail before activation.
- Non-generic definition metadata now exposes defensive read-only collections and
  rejects duplicate stage IDs with the shared structural topology validator.

### Build and package infrastructure

- Central package management, lock-file reconciliation, package graph and
  ownership manifests now drive current and release validation.
- Consumer smoke workspaces use fail-closed source mapping and bounded package
  archive extraction; package metadata validation rejects CI version drift.
- Added contributor and architecture guides for package authoring and release
  gates.

## [2.1.2] — 2026-07-15

Patch release that separates JSON integrations into a dedicated package while
preserving the SmartPipe.Extensions 2.x compatibility contract.

### Added

- **Explicit JSON layouts** — options-based APIs add root-array, NDJSON, and
  batch-JSON-lines modes while preserving the 2.1.1 batch default.
- **Streaming JSON contracts** — sources stream root arrays and multiple
  top-level values, enforce strict null handling and depth limits on both
  reflection and source-generated paths, and expose complete source-generated
  metadata paths. Line-framed records and unframed documents have separate
  encoded-size limits.
- **Framed recovery** — `SkipAndLog` is available only at safe line boundaries
  (`Ndjson` and `BatchJsonLines` records); root arrays and other unframed
  document streams remain throw-only.
- **Append framing** — JSON and dead-letter sinks preserve an existing partial
  final row and insert a missing LF before the next appended record.
- **Dead-letter write integrity** — the sink serializes once through Core's
  `IDeadLetterSerializer<T>`, uses deterministic retry timing, idempotent
  lifecycle state, and requires readable, seekable append destinations so a
  failed record can roll back to its checkpoint.
- **Sink disposal coordination** — concurrent and reentrant `DisposeAsync`
  callers share one completion task, including disposal failures.

- **SmartPipe.Extensions.Json** — dedicated package for System.Text.Json file
  sources and sinks, transforms, and JSON dead-letter persistence.
- **JSON package validation** — dedicated unit tests, direct-package and
  Extensions-only consumers, a binary consumer compiled against 2.1.1, and
  trimming/NativeAOT package smoke coverage.
- **Migration documentation** — package selection, source-generated metadata,
  file-format, and 3.0 bridge-removal guidance.

### Changed

- **JSON implementation ownership** — `JsonFileSource<T>`,
  `DeadLetterSource<T>`, `JsonFileSink<T>`, `DeadLetterSink<T>`,
  `DeadLetterWriteFailureMode`, `DeadLetterWriteException`, and
  `JsonTransform<TInput,TOutput>` now live in `SmartPipe.Extensions.Json`.
- **SmartPipe.Extensions compatibility bridge** — the broad package retains
  type forwarders and a transitive JSON dependency throughout the 2.x line.
- **JSON input limit contract clarified** — the unframed input cap is renamed
  from `MaxDocumentSizeBytes` to `MaxUnframedInputSizeBytes` (default 256 MiB).
  Framed formats (`Ndjson`, `BatchJsonLines`) keep an independent per-record cap
  (`MaxRecordSizeBytes`); root arrays and auto-detected legacy top-level value
  sequences use the shared unframed input cap. `MaxDepth` now applies uniformly
  to reflection constructors, source-generated constructors, the framed-record
  validator, the root-array validator, and the legacy unframed stream. A
  separate per-top-level-value reader (`TopLevelJsonValueReader`) is intentionally
  deferred to 2.2.
- **Release train** — Core, JSON Extensions, and Extensions use version 2.1.2;
  validation and tag publishing cover all three packages in dependency order.
- **Runtime time semantics** — default `CircuitBreaker` construction uses
  `TimeProvider.System` monotonic timestamps. The options-based constructor
  requires an explicit, non-null `TimeProvider`; the six `TimeProvider`-first constructors published
  in 2.1.1 remain as compatibility overloads. Buffered observer write timeouts
  use the runtime provider when `TimeProviderPipelineClock` is configured.

### Compatibility

- Existing JSON namespaces are unchanged. `SmartPipe.Extensions` 2.1.2 keeps a
  dependency on `SmartPipe.Extensions.Json` and type-forwards the JSON types
  that existed in 2.1.1, preserving source and binary resolution for those
  consumers.
- 2.1.2 intentionally adds format, limit, recovery, open-mode, sink, and
  circuit-breaker options/APIs. Append-capable injected streams now fail fast
  unless they are readable, seekable, and writable; append framing and shared
  disposal can change exception timing and lifecycle observations.
- `JsonFileSink<T>` output is documented as batch JSON Lines: one JSON array
  per flushed line, not conventional one-object-per-line NDJSON.
- `JsonLinesDeadLetterSerializer<T>` remains in `SmartPipe.Core`.

## [2.1.1] — 2026-07-08

Patch release for post-review correctness, compatibility, CI, and documentation
fixes found after `v2.1.0`.

### Correctness Fixes

- **Typed runtime terminal outcomes** — cancel and abort requests are tracked as
  immutable intent until finalization, so processing faults and mandatory
  cleanup failures keep `Faulted` precedence while successful cancellation or
  abort finalization publishes the requested terminal state.
- **Retry callbacks** — `RetryPolicy.OnRetry` now runs after the retry delay and
  before the next attempt starts. Callback failures fault the run, and
  cancellation during retry delay does not invoke the callback.
- **Provider-backed runtime time** — `TimeProviderPipelineClock` now drives
  runtime retry delays, attempt timeouts, drain waits, and late-attempt
  finalization waits through its provider instead of limiting fake-time support
  to timestamps.
- **Observer dispatch validation and failures** — reliable buffered observer
  dispatch rejects lossy full modes, best-effort completion flush rejects lossy
  full modes, flush markers are internal control messages, and buffered
  observer failures are reported to remaining observers with
  `ObserverFailedEvent`.
- **JsonFileSink checkpointed writes** — path-backed sinks append through a
  seekable async `FileStream`, write one UTF-8 JSON array per flushed batch,
  roll back in-process write exceptions to the pre-write checkpoint, and keep
  failed batches buffered for retry.
- **HttpSelector log redaction and JSON AOT** — request URI logs remove
  userinfo, query strings, and fragments; unparseable absolute URIs log
  `[unparseable-uri]`. Reflection JSON constructors are annotated for trimming
  and NativeAOT risk, while `JsonTypeInfo` constructors remain the recommended
  path.
- **SecretScanner fail-closed scanning** — added `SecretScanner.Scan()` and
  `SecretScanResult` with `Clean`, `SecretFound`, and `Indeterminate`.
  `HasSecrets()` now returns `true` for indeterminate scans, and `Redact()`
  returns `***REDACTION_INDETERMINATE***` when regex, input-size,
  decode-budget, or recursion limits prevent safe redaction.

### Documentation Corrections

- **JSON file terminology** — current docs describe `JsonFileSink<T>` output as
  newline-delimited JSON batches, one JSON array per line, rather than generic
  one-record-per-line NDJSON.
- **Dead-letter persistence** — current docs clarify that dead-letter writers
  preserve replay context and provide in-process checkpoint rollback on
  seekable streams, but crash durability remains the responsibility of the
  configured sink and storage.
- **Queue depth and health checks** — current docs keep queue depths as
  observational point-in-time pressure signals. Hosted pipelines with no
  initial activity remain healthy by default unless `RequireInitialActivity` is
  enabled.
- **Timeout semantics** — current docs describe timeouts as cooperative runtime
  guards. Detached timeout modes do not forcibly stop user code in-process.
- **CuckooFilter and ObjectPool claims** — older changelog entries remain
  historical release notes. Current docs do not restate ObjectPool ABA or
  Cuckoo insertion guarantees beyond what the current source and tests cover.

### Testing & Release Validation

- **Public API baselines** — Core and Extensions baselines were refreshed for
  analyzer-visible public surface, including record-generated members, so
  Release warning-as-error builds validate the `2.1.1` contract.
- **CI coverage split** — long-running Core stress tests now run outside the
  coverage job, keeping concurrency regression checks in CI without
  destabilizing coverage collection.
- **Package and publish gates** — release workflows continue to validate
  packages through local consumer, trim, NativeAOT, vulnerability, and
  deprecated-package checks before publish.

## [2.1.0] — 2026-06-27

### Stabilization Behavior Changes

- **Typed factory async startup** — `ISmartPipeFactory<TInput,TOutput>.StartAsync` no longer bridges through the synchronous `Start` method by default. The default interface implementation throws `NotSupportedException` unless an implementation explicitly supports async startup, avoiding sync-over-async and Start/StartAsync recursion traps.
- **DapperSelector connection ownership** — externally supplied connections are left open by default. Use the explicit `DbConnection` overload with `leaveOpen: false` when the selector should own and dispose the connection.
- **DapperSelector async reads** — the `DbConnection` path uses asynchronous open and reader operations. Non-`DbConnection` `IDbConnection` implementations remain a synchronous compatibility fallback.
- **EfCoreSelector tracking policy** — read queries use no-tracking by default for pipeline source scenarios. Use `.WithTracking()` to opt into EF Core change tracking when returned entities should remain tracked by the supplied `DbContext`.
- **OutputMode compatibility policy** — `PipelineOutputMode` and `PipelineRuntimeOptions.OutputMode` remain available as obsolete compatibility APIs. New code should use `PipelineOutputPolicy` and `PipelineRuntimeOptions.OutputPolicy`.

### New Features

- **Adaptive parallelism admission** — typed runtime options can opt into completion-based adaptive admission control driven by latency and failure pressure for bounded, backpressure-aware parallel execution.
- **ChannelMerge cancellation-aware overload** — `ChannelMerge.Merge(first, second, options, cancellationToken)` is available for bounded or backpressure-sensitive merges.
- **EfCoreSelector.WithTracking** — EF Core selector callers can opt back into change tracking explicitly.

### Core Runtime

- **Factory sync-over-async removal** — `StartAsync` paths no longer depend on sync-over-async bridges.
- **Hosted-service stack traces** — hosted-service failure rethrow paths preserve the original exception stack trace.
- **Lifecycle state transitions** — drain, cancel, complete, and abort transitions are hardened against racing terminal-state updates.
- **Source-stop classification** — graceful source-stop detection now uses a snapshot/reason model rather than live multi-flag checks.
- **Observer event emission** — fire-and-forget observer emission is routed through the shared best-effort `TryEmitAsync` path, making observer emission failures observable through drop metrics.
- **Buffered observer faults** — buffered observer dispatch records the first pipeline fault, preserves original exception rethrow semantics, and avoids broad worker-exception swallowing during disposal.
- **Output policy compatibility validation** — equivalent legacy and canonical output policies can be configured together, while non-equivalent combinations remain rejected.

### Extensions

- **DapperSelector async provider path** — `DbConnection` providers use asynchronous open and read operations with cancellation support.
- **DapperSelector ownership-safe construction** — externally supplied connections are preserved by default; selector-owned disposal is opt-in through explicit ownership configuration.
- **EfCoreSelector no-tracking default** — read-only selector queries use `AsNoTracking()` by default, with `.WithTracking()` available for tracking scenarios.
- **ChannelMerge cancellation and completion** — merge pumps pass cancellation to source reads and output writes, propagate input faults, complete the merged output with cancellation/fault information, and validate input readers synchronously.
- **DeadLetterSink test hooks removed** — production dead-letter sink code no longer contains test-only writer/failure injection hooks.

### Documentation

- **Release notes** — documented stabilization behavior changes introduced after 2.0.0.
- **Output filtering deprecation plan** — documented `OutputMode` to `OutputPolicy` migration guidance, conflict rules, and future-major-only removal policy.
- **Selector behavior docs** — documented Dapper connection ownership and EF Core no-tracking defaults.
- **ChannelMerge docs** — documented the cancellation-aware overload and bounded/backpressure-sensitive use cases.

### Testing & Quality

- **Observer dispatcher tests** — removed reflection polling of private dispatcher state and now test observable dispatcher behavior.
- **Observer polling timeout** — async polling helpers pass timeout cancellation tokens into `EmitAsync`.
- **ChannelMerge tests** — added null-reader validation coverage for both compatibility and cancellation-aware overloads.
- **Regression coverage** — added coverage for bounded merge preservation, cancellation completion, pre-cancelled tokens, input fault propagation, selector ownership, and no-tracking selector reads.

## [2.0.0] — 2026-06-20

### Breaking Changes

- **Typed-only runtime** — removed the legacy `SmartPipeChannel<TInput,TOutput>` runtime model and the old `ISource<T>`, `ITransformer<TInput,TOutput>`, `ISink<T>`, `ProcessingContext<T>`, `ProcessingResult<T>`, retry queue, channel pool, middleware transformer, and legacy cancellation surfaces.
- **New pipeline API** — the public runtime surface is now `PipelineBuilder`, `PipelineRun<T>`, `IPipelineSource<T>`, `IPipelineTransformer<TInput,TOutput>`, `IPipelineSink<T>`, `ProcessingEnvelope<T>`, `StageResult<T>`, and `PipelineResult<T>`.
- **Runtime output contract changed** — sink-backed pipelines now default to `PipelineOutputPolicy.SuppressSuccessWhenSinkAttached`. Successful sink writes are not published to `PipelineRun<T>.Outputs` unless the caller explicitly opts into `EmitAll`.
- **Single logical output reader** — `PipelineRun<T>.Outputs` is a single-reader output channel by contract. Callers that need fan-out must build it explicitly in application code.
- **Lifecycle semantics changed** — `DrainAsync`, `TryDrainAsync`, `CancelAsync`, `AbortAsync`, and `DisposeAsync` now have distinct typed-runtime meanings and observable states.
- **Compatibility aliases are transitional only** — `OutputMode`, `PipelineOutputMode`, and `MaxDegreeOfParallelism` remain as obsolete typed-runtime aliases for migration, but new code should use `OutputPolicy` and `MaxConcurrency`.
- **Legacy docs moved out of the primary path** — migration notes now live under `docs/migration/legacy-to-typed.md`; current docs describe the typed runtime only.

### New Features

- **PipelineRun\<T\>** — typed runtime handle with completion task, output reader, lifecycle state, metrics snapshot, drain, cancel, abort, and async disposal.
- **ProcessingEnvelope\<T\>** — strongly typed payload envelope with pipeline id, run id, trace id, metadata, lineage, attempt count, and creation timestamp.
- **StageResult\<T\>** — explicit stage result model for success, failure, filtered, skipped, cancelled, and timed-out stage outcomes.
- **PipelineResult\<T\>** — typed terminal output result with success, failure, filtered, and skipped classifications.
- **PipelineRuntimeOptions** — typed options for `MaxConcurrency`, input/output capacity, bounded channel full modes, output policy, ordering mode, observer dispatch, and runtime clock.
- **TryDrainAsync** — structured non-throwing drain API returning `PipelineDrainResult` with `Completed`, `TimedOutStillRunning`, `CancelledByCaller`, `Faulted`, and `AlreadyCompleted` statuses.
- **DeadLetterEnvelope\<T\>** — replay-safe typed dead-letter payload containing original payload, pipeline/run ids, trace id, stage id/name, metadata, error, attempt, and failure timestamp.
- **JsonLinesDeadLetterSerializer\<T\>** — JSONL dead-letter serializer with source-generated `JsonTypeInfo` support for trim/AOT-sensitive consumers.
- **ObserverDispatchOptions** — inline and bounded buffered observer dispatch with best-effort drop accounting, failure modes, and completion flush behavior.
- **CircuitBreaker.TryAcquireHalfOpenProbe()** — lease-based half-open probe API for concurrent half-open limits.
- **Pipeline health snapshots** — typed health monitor snapshots include state, metrics, input/output capacity, and capture timestamp.

### Core Runtime

- **StageExecutor** — centralizes retry, timeout, circuit breaker, dead-letter routing, filtered results, and terminal failure actions.
- **Bounded channel runtime** — input, output, and buffered observer paths use bounded channels with explicit backpressure or lossy modes.
- **Sink-safe output default** — sink-backed runs suppress unread success outputs by default, preventing output-channel backpressure from blocking sink-only workloads.
- **Post-sink success output** — when success outputs are enabled for a sink-backed run, success is emitted only after the sink write succeeds.
- **Filtered terminal state** — `StageResult.Filtered()` does not call the sink, does not write dead letters, and does not increment failed metrics.
- **Drain source/processing split** — drain cancels source reads while allowing already accepted work to finish; cancel and abort cancel both source and processing.
- **Idempotent disposal** — runtime disposal is safe for repeated and racing callers, and disposes runtime-owned components once.
- **Factory/instance separation** — instance pipelines are single-use; factory pipelines create fresh source/stage/sink components per run.
- **Compatibility validation** — conflicting explicit `OutputMode`/`OutputPolicy` and `MaxConcurrency`/`MaxDegreeOfParallelism` combinations fail validation instead of silently choosing one.

### Resilience

- **Retry budget enforcement** — retry delay, cancellation, and whole-stage timeout budgets are enforced together.
- **Circuit-breaker rejection terminality** — open-breaker rejection is terminal for the current item and is not retried back into the open breaker.
- **Failure action routing** — permanent failures and retry exhaustion can emit failure results, skip, stop, fault, or dead-letter according to `StageFailureOptions`.
- **Dead-letter write failures** — dead-letter persistence errors are observable and can fail the run when the configured failure action depends on them.
- **Half-open probe leases** — runtime execution uses probe leases so only the configured number of half-open attempts can run concurrently.

### Observability

- **SmartPipeMetricsRecorder** — mutable per-run recorder with immutable `SmartPipeMetricsSnapshot` capture.
- **New metric counters** — typed runtime records processed, failed, filtered, dropped, output-dropped, observer-dropped, retried, dead-lettered, and duplicate-filtered item counts.
- **Runtime histograms** — stage and sink durations are exported through the `SmartPipe.Core` meter.
- **ActivitySource tracing** — `Pipeline.Run` and `Transform` activities include pipeline id, run id, trace id, stage id, and parallelism tags.
- **Observer event matrix** — lifecycle, stage, sink, retry, dead-letter, drop, circuit-breaker, and observer failure events are covered by typed tests.
- **Drop observability** — lossy input, output, and observer modes emit best-effort drop events and reliable drop metrics.

### Dependency Injection, Hosting, And Health Checks

- **ISmartPipeDefinition\<TInput,TOutput\>** — immutable typed pipeline definition registered in DI.
- **ISmartPipeFactory\<TInput,TOutput\>** — per-run factory that creates fresh runtime components and supports `StartAsync`.
- **Scoped component ownership** — factory-created runs own a DI scope and dispose scoped source/stage/sink components when the run completes or is manually disposed.
- **ValidateScopes compatibility** — DI factory and hosted-service paths work under `ValidateScopes=true`.
- **SmartPipeHostedServiceOptions** — hosted-service failure behavior can stop the host, rethrow, mark unhealthy and keep running, or ignore.
- **Hosted-service fault behavior** — background pipeline faults are no longer silently swallowed; default behavior requests application shutdown.
- **Typed health checks** — health checks read typed run state and immutable metrics without registering a runtime singleton.

### Extensions

- **Typed selectors and sinks** — CSV, JSON, HTTP, EF Core, Dapper, logger, database, and dead-letter components now implement typed envelope interfaces.
- **HTTP client factory support** — `HttpClientFactorySelector<T>` and `HttpClientFactorySink<T>` support named clients and resilience pipelines.
- **Streaming HTTP selector modes** — HTTP selectors can consume JSON arrays or NDJSON using source-generated `JsonTypeInfo<T>`.
- **AOT-safe JSON overloads** — JSON file sources, sinks, transforms, dead-letter sources, and dead-letter sinks provide source-generated serializer overloads where required.
- **FilterTransform terminal filtering** — filtering is represented as a typed non-failure terminal result rather than an exception or failed item.
- **SQLite audit fix** — test dependencies were updated so vulnerable SQLite native packages no longer appear in NuGet vulnerability audit output.
- **Deterministic generated fixtures** — CSV/JSON golden and pipeline tests now use generated tiny fixtures and temporary files instead of tracked real data.

### Packaging, CI, And Release Validation

- **Version 2.0.0** — package metadata and release line are aligned on `2.0.0`.
- **Package validation** — `SmartPipe.Core` and `SmartPipe.Extensions` pack with `EnablePackageValidation=true`.
- **Symbols packages** — packages include `.snupkg` symbols.
- **NuGet audit policy** — repository-level `NuGetAudit`, `NuGetAuditMode=all`, and `NuGetAuditLevel=moderate` are enabled.
- **Central build policy** — deterministic build, CI build, analyzer, code-style, nullable, implicit usings, and Release warning policies are centralized.
- **xUnit v3 / Microsoft.Testing.Platform** — test projects run as executable xUnit v3 MTP projects under the .NET 10 test runner.
- **CI release gate** — CI runs locked restore, format verification, Release build with warnings as errors, full tests, package validation, consumer smoke, trim smoke, NativeAOT smoke, JSON/dead-letter AOT smoke, vulnerability scan, deprecated package scan, and docs link check.
- **Consumer smoke from packages** — smoke tests install `SmartPipe.Core` and `SmartPipe.Extensions` from local `.nupkg` packages, not project references.
- **Security workflows** — CodeQL, Dependency Review, Dependabot, and tag-gated NuGet publish workflows were added or hardened.

### Documentation

- **Typed runtime docs** — README and docs now describe the typed-only runtime, current public API, sink-safe output defaults, lifecycle semantics, DI, hosting, health checks, AOT, and runtime contracts.
- **Migration guide** — removed legacy APIs and transitional aliases are documented in `docs/migration/legacy-to-typed.md`.
- **Runtime contracts** — docs now define output, filtered, drain, cancel, abort, retry, circuit breaker, dead-letter, observer, and metric contracts.
- **Recipes** — bounded output, graceful shutdown, retry/timeout/dead-letter, and testing recipes were updated for the typed runtime.
- **Release docs cleanup** — temporary progress and release-evidence docs were removed from the final release surface.

### Testing & Quality

- **894 tests passed, 1 skipped** in the final full solution test run for the RC gate.
- **Package validation passed** for `SmartPipe.Core.2.0.0` and `SmartPipe.Extensions.2.0.0`.
- **NuGet vulnerability audit passed** for Core, Extensions, benchmarks, and test projects.
- **Format verification passed** with `dotnet format --verify-no-changes`.
- **Release build passed** with warnings as errors.
- **Release search guards passed** for local workbench leakage, fixture gates, tracked real fixture manifests, local absolute paths, and temporary progress references.

## [1.0.6] — 2026-05-15

### Thread Safety (Critical Fixes)

- **CuckooFilter** — Added `Lock _syncRoot` to protect the `_buckets` array. All public methods (`Add`, `Contains`, `Remove`, `Merge`) now synchronize access, preventing data corruption under concurrent consumers.
- **DeduplicationFilter** — Added `Lock _bitsLock` for thread-safe access to `BitArray`. Fixed integer overflow in bucket index calculation by using `long` arithmetic and safe modulo.
- **ReservoirSampler** — Replaced `System.Random` with `ThreadLocal<Random>` for thread safety. Added `Lock _reservoirLock` to protect the reservoir array from concurrent writes.
- **CuckooFilter.EvictAndInsertSlot** — Fixed a bug where an evicted fingerprint was permanently lost if it could not be placed in its alternate bucket. Now restores the original slot value on failure.

### Bug Fixes

- **TransformWithTimeoutAsync** — Added a catch-all `catch (Exception)` block to handle unexpected exception types (e.g., `ArgumentException`, `JsonException`) that previously crashed the consumer task.
- **PipelineCancellation.WithTimeoutAsync** — Added `CancellationToken ct` parameter. Uses `CreateLinkedTokenSource` so the operation can be cancelled externally.
- **DrainAsync** — Added `CancellationToken ct` parameter. Replaced `Complete()` with `TryComplete()` on channels to prevent `ChannelClosedException` when called multiple times.
- **ObjectPool/RetryQueue race** — `HandleTransformResultAsync` no longer returns a context to the pool if it was enqueued for retry. Returns only for successful and filtered items.
- **SmartPipeHostedService** — `StopAsync` now calls `pipeline.DisposeAsync()`. Added handling for `Faulted` state (unhandled exceptions during execution are logged, not rethrown).
- **RetryQueue.TryGetNextAsync** — Removed race between `Count == 0` check and `WaitToReadAsync`. Always waits for items. Reduced `CancellationTokenSource` allocations. Replaced `WriteAsync` with `TryWrite` for re-queueing to avoid blocking.
- **PipelineDashboard** — Changed from mutable `class` to `readonly record struct`. Property `CBState` renamed to `CbState` (auto-generated by record struct). Added `PipelineDashboard.Empty` for default values. Updated `CreateDashboard()` to use constructor instead of property setters.
- **DbSink** — `Execute()` → `await ExecuteAsync()` to avoid blocking the thread pool.
- **DapperSelector** — `ReadAsync` now wraps the reader in a `try/finally` block, ensuring the `IDataReader` is disposed immediately upon early exit or exception.
- **JsonFileSink** — Replaced unbounded in-memory buffering with periodic batch flushing (`flushInterval` parameter). Uses NDJSON format for append-only writes. Cached `JsonSerializerOptions` to avoid per-call allocations.
- **SmartPipeResilienceExtensions** — Removed dead code that created `PollyResilienceTransform` but never added it to the pipeline. Now only registers `ResiliencePipeline` as a singleton.
- **ChannelMerge** — Added optional `BoundedChannelOptions` parameter for bounded output channels, complying with `global-constraints.md`.

### Performance Improvements

- **ExponentialHistogram** — `GetPercentile()` now uses `Volatile.Read` instead of `Interlocked.CompareExchange` for reading bucket values, avoiding cache line invalidations.
- **ExponentialHistogram** — Added percentile caching (`P50`, `P95`, `P99`). Values are recomputed only when `_totalCount` changes.
- **CircuitBreaker.GetMetrics()** — Dictionary now created with exact capacity (4) to reduce internal resizing.
- **AdaptiveMetrics** — Replaced `Environment.TickCount64` with `Stopwatch.GetTimestamp()` to prevent throughput calculation errors after ~49.7 days of uptime. Added guard against abnormally large elapsed time.
- **RetryQueue.TryGetNextAsync** — Single `CancellationTokenSource` per call instead of creating a new one on each poll.
- **ObjectPool<T>** — Added optional `maxCapacity` parameter to prevent unbounded pool growth under sustained load.
- **JsonFileSink** — Periodic flushing prevents unbounded memory growth.
- **CuckooFilter** — Uses `System.Threading.Lock` (instead of `object`) for better performance under high contention.

### API Changes

- **PipelineDashboard** — Now a `readonly record struct`. Property `CBState` renamed to `CbState`.
- **ProcessingContext.EnterPipelineTicks** — Changed from `public` to `internal`.
- **SmartPipeChannelOptions** — `SecretScanner` feature flag now defaults to `false`. Users must explicitly enable it via `options.EnableFeature("SecretScanner")`.
- **AddSource/AddTransformer/AddSink** — Added `ArgumentNullException.ThrowIfNull` validation.
- **RetryQueue** — Added optional `pollTimeoutMs` parameter to constructor (default: 100ms).
- **DeduplicationFilter** — Added optional `TimeSpan? ttl` parameter to constructor for automatic expiration of entries.
- **ObjectPool<T>** — Added optional `maxCapacity` parameter to constructor (default: 1024).
- **JsonFileSink** — Added optional `flushInterval` parameter to constructor (default: 1000).
- **DrainAsync** — Signature changed to `DrainAsync(TimeSpan timeout, CancellationToken ct = default)`.
- **WithTimeoutAsync** — Signature changed to `WithTimeoutAsync<T>(this ValueTask<ProcessingResult<T>> task, TimeSpan timeout, ulong traceId, CancellationToken ct = default)`.

### New Features

- **DeduplicationFilter TTL** — Optional time-to-live for filter entries. Elements are automatically removed after TTL expires, preventing the filter from growing indefinitely.
- **AdaptiveParallelism.GetDecisionReason()** — Returns a human-readable explanation of the last P-controller decision (e.g., "DeadZone: error 3ms < 5ms threshold", "P-controller: adjusted by +2"). Useful for observability and debugging.
- **JsonFileSink periodic flushing** — Items are written to disk in batches, preventing out-of-memory errors on large datasets.
- **ObjectPool max capacity** — Prevents unbounded pool growth under sustained high load.
- **DisposeAsync idempotency** — `SmartPipeChannel.DisposeAsync()` now uses `Interlocked.CompareExchange` to guard against double disposal.

### Testing & Quality

- **628 tests** passed (480 Core + 148 Extensions), 1 skipped (PollyResilienceTransform timeout test)
- All tests pass on Windows. CI should pass on Linux after the `JsonFileSinkTests` fix for cross-platform exception types.
- 585 warnings remaining (all in test files: `CS1591` missing XML docs and `xUnit1031` blocking calls in tests).
- **Breaking Changes** — See above in API Changes.

### Breaking Changes

- `PipelineDashboard` is now a value type (`readonly record struct`). Direct property assignment no longer works; use the constructor instead.
- `PipelineDashboard.CBState` renamed to `CbState`.
- `ProcessingContext.EnterPipelineTicks` is no longer accessible from outside `SmartPipe.Core`.
- `SecretScanner` is **disabled by default**. Enable it via `options.EnableFeature("SecretScanner")`.
- `DrainAsync` and `WithTimeoutAsync` signatures changed — they now accept a `CancellationToken` parameter.
- `RetryQueue` constructor has a new optional `pollTimeoutMs` parameter.
- `ObjectPool` constructor has a new optional `maxCapacity` parameter.

## [1.0.5] — 2026-05-06

### New Features
- **DefaultRetryPolicy** in SmartPipeChannelOptions — per-pipeline retry policy for transient failures
- **RetryBudget per RetryItem** — per-item retry budget with DeadLetterSink routing when exhausted
- **DisposeAsync(CancellationToken)** — graceful shutdown with timeout support
- **AddSmartPipe DI** — three overloads for flexible pipeline registration in IServiceCollection
- **IClock integration** — TimeProviderClock for testable time across CircuitBreaker, RetryQueue, SmartPipeChannel

### Core Improvements
- **CircuitBreaker thread-safety** — _ewmaFailureRate updated via AtomicHelper.CompareExchangeLoop (lock-free)
- **AdaptiveParallelism adaptive alpha** — dynamic EMA alpha based on latency delta for faster convergence
- **ObjectPool ABA protection** — version stamps prevent ABA race conditions under high concurrency
- **CleanupWindow race fix** — TryPeek+TryDequeue replaced with TryDequeue+check pattern
- **P-controller recovery** — currentLatencyMs used directly for error calculation, faster spike response
- **AdaptiveMetrics thread-safety** — _avgLatencyMs updated via Volatile.Read/Write for thread-safe access

### Pipeline Management
- **PipelineState.Paused** — Pause()/Resume() now fire OnStateChanged events correctly
- **BoundedCapacity guard** — UseRendezvous=true throws InvalidOperationException (by design)
- **Magic numbers → named constants** — AlphaScaleFactor, MaxDelayMs, throughput/latency thresholds in BackpressureStrategy

### SecretScanner Improvements
- **Evasion detection** — TryDecodeBase64/TryDecodeUrl for Base64 and URL-encoded secrets
- **MaxRecursionDepth=3** — handles triple-encoded payloads with safety margin (found in real penetration tests)
- **Padding fix** — TryDecodeBase64 handles missing Base64 padding correctly
- **AWS key guard** — IsRawAwsAccessKey prevents false Base64 decode on raw AWS keys
- **169 SecretScanner tests** — +26 from v1.0.4

### Code Quality Sweep
- **Broad catch(Exception) → specific types** — 8 catch blocks with Polly, Mapster, CsvHelper, IO exceptions
- **AtomicHelper utility** — 3 duplicate CompareExchange loops extracted into reusable class (internal)
- **Dead code removal** — PipelineTool, ShouldPause/IsCritical
- **Method extraction** — ProcessRetriesAsync, RecordFailure, Merge refactored to ≤3 levels nesting
- **XML documentation** — 0 CS1591 warnings in production code (50+ files documented)
- **ILogger logging** — added to all catch blocks, debug-level logging for cancellation events
- **#nullable enable** — verified in all source files

### Testing & Quality
- **598 tests** (+355 from v1.0.4)
- **Stress tests** — 50-thread CircuitBreaker, 20-thread ObjectPool, 10-producer/10-consumer RetryQueue
- **Property-based tests** — RetryPolicy invariants (monotonicity, boundedness, overflow protection)

### Breaking Changes
- **Removed ShouldPause()/IsCritical()** — replaced by P-controller based throttling. Code using these must migrate to Pause()/Resume() and check ErrorType directly.
- **Removed PipelineTool class** — functionality consolidated into SmartPipeChannel and PipelineBuilder. Use ProcessSingleAsync() for AI agent integration.
- **ChannelPool.Return() → CloseChannel()** — method now calls TryComplete() on writer, does NOT return channel to pool. Update callers.
- **IClock parameter added** to CircuitBreaker, RetryQueue, SmartPipeChannel constructors — optional with TimeProviderClock default, no changes required for existing code.

## [1.0.4] — 2026-04-28

### New Features (22)
- **CsvFileSource\<T\>** + **CsvFileSink\<T\>** — streaming CSV read/write
- **JsonFileSource\<T\>** + **JsonFileSink\<T\>** — JSON array and NDJSON read/write
- **FilterTransform\<T\>** — predicate-based filtering with And/Or/Not combinators
- **ValidationTransform\<T\>** — DataAnnotations validation with custom `.Require()` rules
- **DbSink\<T\>** — database insert via Dapper with auto-generated SQL from attributes
- **HttpSink\<T\>** — HTTP POST sink with optional Polly resilience pipeline
- **ConditionalTransform\<T\>** — apply transform only when condition met
- **DeadLetterSource\<T\>** — replay failed items from DeadLetterSink JSON
- **CompositeTransform\<T\>** — chain multiple transforms into one
- **Filter-to-Validation extension** — `.ToFilter()` method

### Core Improvements (10)
- **P-Controller Parallelism** — discrete P-controller with dead zone and anti-windup (replaces binary thresholds)
- **Double EMA + Prediction** — velocity tracking + `PredictNextLatency()` for proactive control
- **Hybrid CircuitBreaker** — EWMA for fast reaction + Sliding window for accurate decisions + adaptive α
- **P-Controller Backpressure** — continuous throttling replaces binary Pause/Resume (prevents oscillation)
- **Adaptive Pipeline** — controllers linked via `PredictNextLatency()` for coordinated response

### Pipeline Management
- **PipelineState** — `NotStarted → Running → Completed/Faulted/Cancelled` with `OnStateChanged` event
- **Cancel()** — graceful pipeline cancellation
- **CreateDashboard()** — aggregated State + Progress + Metrics + CB info
- **Progress reporting** — `OnProgress(int current, int? total, TimeSpan elapsed, TimeSpan? eta)` delegate

### Observability
- **Metrics.Export()** — Dictionary export + JSON + Prometheus text format
- **CircuitBreaker.GetMetrics()** — CB state, failure ratio, EWMA rate export

### Resilience
- **Auto DeadLetter routing** — exhausted retries → DeadLetterSink automatically (via Options.DeadLetterSink)
- **Filtered category handling** — `Category=="Filtered"` not counted as error
- **Cryptographically secure Jitter** — `Random` → `RandomNumberGenerator` in RetryQueue

### Security
- **4 new OWASP patterns** in SecretScanner — JWT, AWS Key, GitHub Token, OAuth Token

### Testing & Quality
- **243 tests** (+28 from v1.0.3)
- **96.4% line coverage**
- **Algorithm benchmarks** — P-controller, Double EMA, Hybrid CB, Backpressure
- **Performance**: ValueTask_Transform 12% faster (69.12 ns vs 78.81 ns), 0 regressions


## [1.0.3] — 2026-04-27

### New Features (13)
- **Middleware Transformer** — `Func<T,T>` as lightweight `ITransformer`, zero boilerplate
- **Rendezvous Channel** — `UseRendezvous=true` enables strict Producer-Consumer sync (BoundedCapacity=0)
- **HyperLogLogEstimator** — Count-Distinct with O(1) memory, ~3% accuracy
- **Dual-threshold Watermark** — Pause/Resume thresholds prevent oscillation (System.IO.Pipelines pattern)
- **Liveness/Readiness Health Checks** — Kubernetes-native probes (`SmartPipeLivenessCheck`, `SmartPipeReadinessCheck`)
- **DeadLetterSink** — persists failed items to JSON for later analysis
- **Data Lineage** — provenance tracking via `ProcessingContext.Metadata` keys
- **ChannelMerge** — merge two `ChannelReader<T>` streams into one
- **RunInBackground()** — non-blocking pipeline execution returning `ChannelReader`
- **Hybrid Queue** — `FullMode` option in `SmartPipeChannelOptions` (Wait/DropOldest/DropNewest)
- **AsChannelReader()** — exposes pipeline output for SignalR/gRPC integration
- **Hybrid Queue** — `FullMode` option (Wait/DropOldest/DropNewest)
- **Lambda sources/sinks** — `AddSource(Func)` and `AddSink(Action)` for rapid prototyping

### Testing & Quality
- **215 tests** (up from 186, +29 tests)
- **96.4% line coverage**
- 0 regressions in all benchmarks

## [1.0.2] — 2026-04-27

### Performance
- **Lock-free CircuitBreaker** — `lock()` → `Interlocked.CompareExchange` + `ConcurrentQueue`
  - `AllowRequest()`: 49.30ns → 27.76ns
- **Lock-free RetryQueue** — `Task.Delay(50)` polling → `WaitToReadAsync` + timeout
  - `EnqueueAsync()`: 86.58ns → 69.16ns
- **Adaptive EMA** — dynamic α (0.2 stable, 0.8 spike)
- **Dynamic Watermark** — throughput-based backpressure thresholds
- **TryRead in DrainAsync** — instant drain without 10ms delays

### Core Changes
- **ProcessingContext** — `record class` → mutable `class` with `Reset()` for ObjectPool reuse
- **Meter instruments** — static readonly (OTel singleton compliance)
- **ObjectPool** — factory-based, compatible with ProcessingContext

### Observability
- **SmartPipeEventSource** — EventSource with EventCounters for `dotnet-counters monitor`
  - `items-processed`, `queue-size`, `pool-hit-rate`, `backpressure/sec`, `cb-state`

### Extensions
- **SmartPipeHostedService** — `BackgroundService` for ASP.NET Core with graceful Drain
- **AddSmartPipeResilience()** — DI extension for `IServiceCollection`
- **SmartPipeHealthCheck** — `IHealthCheck` reporting CB state, queue size, failure rate

### Testing & Quality
- **186 tests** (up from 137, +47 tests)
- **96.3% line coverage** (up from 86.5%)
- **81.2% branch coverage** (up from 69.5%)
- **Crap Score reduced** — `ConsumeAsync` refactored into 6 smaller methods
- 0 regressions in all benchmarks

## [1.0.0] — 2026-04-26

### Core Engine
- SmartPipeChannel with System.Threading.Channels
- ValueTask signatures for zero allocations
- AdaptiveParallelism (Little's Law)
- AdaptiveMetrics (EMA smoothing)
- BackpressureStrategy (watermark-based throttling)
- DeduplicationFilter (Bloom filter, O(1) memory)
- CuckooFilter (deduplication with deletion)
- ReservoirSampler (debug sampling from stream)
- ExponentialHistogram (p50/p95/p99 percentiles)
- ObjectPool (lock-free, factory-based)
- JumpHash (deterministic sharding, O(1) memory)
- PipelineCancellation (timeout wrapping)
- ChannelPool (channel reuse between runs)
- PipelineBuilder (fluent API with type safety)
- PipelineSimulator (deterministic testing)
- SecretScanner (OWASP-based secret detection)
- FeatureFlags (runtime feature toggling)
- PipelineTool (AI agent integration)
- Graceful Shutdown (DrainAsync, Pause/Resume)

### Observability
- OpenTelemetry Tracing (ActivitySource)
- OpenTelemetry Metrics (Meter with Counters, Histograms, Gauges)

### Resilience
- RetryQueue with jitter (thundering herd protection)
- RetryPolicy (Fixed, Linear, Exponential backoff)
- CircuitBreaker (sliding window, HalfOpen limits, manual Isolate/Reset)
- TotalRequestTimeout + AttemptTimeout

### Extensions (SmartPipe.Extensions)
- HttpSelector (Polly-integrated HTTP source)
- EfCoreSelector (Entity Framework source)
- DapperSelector (high-performance SQL source)
- JsonTransform (System.Text.Json with source generation)
- CsvTransform (CsvHelper integration)
- MapsterTransform (object-to-object mapping)
- CompressionTransform (Brotli/GZip)
- PollyResilienceTransform (Polly v8 pipeline wrapper)
- LoggerSink (ILogger-based sink)

### Testing
- 137 unit tests
- 8 property-based tests (FsCheck)
- 7 chaos engineering tests
- 5 BenchmarkDotNet benchmarks (0 allocations in hot path)
