# SP230 — SmartPipe 2.3.0 API consolidation and allocation review

**Status:** proposed, tests-first. **Reviewed:** 2026-10-15. **Source baseline:** `main` `693b04e5f2a7e63344eaa092b29c398478cf41e7`.
**Release:** 2.3.0 (a deliberately breaking MINOR under SmartPipe's updated documented policy; **not strict SemVer minor compatibility**).
This is a plan, not evidence of implemented source changes.

## Decision and guardrails

- Batch approved API removals in **one 2.3.0 release**, rather than incrementing MAJOR for every public contract cleanup.
- Keep 2.2.1 immutable: a partially completed NuGet publication must be reconciled using its original producer artifact. No 2.3.0 package may reuse 2.2.1 bytes or rewrite its history.
- Maintain a *per-symbol* compatibility ledger comparing published 2.2.1 (once confirmed) to candidate 2.3.0: source, binary, behavior, config, public surface, package graph, NativeAOT, perf. If 2.2.1 cannot be established as published and verified, stop release acceptance; do not silently pretend the baseline is 2.2.1.
- Package Validation and PublicApiAnalyzers remain **ON**. Explicit suppressions only for accepted breaks, scoped by package and release. Do not regenerate historical baselines or suppress entire packages.
- Preserve canonical runtime, stage ownership, backpressure, typed definition model, source-generated logs, stable metrics and packages. Do not add a second scheduler, obsolete compatibility shim, new library-wide DI service locator or broad dependency.
- Deletion is justified by actual code removal, incorrect semantics, security, support burden or proven runtime cost. Removing a public method does not by itself reduce per-item allocation.
- Only GitHub-hosted CI; branches / small PRs; fail-closed release validation; no self-hosted runners.

## Prioritized candidate matrix

| ID | Actual source / evidence | Decision | Risk / performance truth |
| --- | --- | --- | --- |
| LOG-01 | `src/SmartPipe.Extensions.Logging/LoggerSink.cs`: 1-arg legacy logger and `UnsafeRaw` both log `{@Value}` | **REMOVE unsafe semantics and enum member**. Retain 1-arg signature, route to safe options-based implementation | Behavior break; deleted enum member is source break; source generation already in place; avoids boxing/alloc of legacy extension logger path when enabled |
| OPT-01 | `PipelineRuntimeOptions.OutputMode`, `PipelineOutputMode`, `IsOutputModeConfigured`, `PipelineOutputEmitter`, snapshot | **REMOVE bridge**, keep `OutputPolicy`; add canonical `SuppressAll` before removal | **Critical:** legacy `OutputMode.SuppressAll` suppresses even without sink; current `OutputPolicy` has no exact equivalent. Preserve its semantics explicitly |
| OPT-02 | `PipelineRuntimeOptions.MaxDegreeOfParallelism` and `EffectiveMaxConcurrency` fallback / validation / snapshot | **REMOVE alias**, use `MaxConcurrency` | Simplifies config/read paths; no guaranteed measurable allocation savings |
| OPT-03 | `AdaptiveParallelismOptions.SampleInterval` and `Cooldown` aliases | **REMOVE aliases**, use `EvaluationInterval` / `AdjustmentCooldown` | Backing fields already shared; deletion chiefly API/snapshot cleanup |
| OPT-04 | `PipelineOrderingMode.PreserveInputOrder`, `OrderingMode` | **PROPOSE REMOVE entire non-functional option**, after proving no supported distinct behavior | Parallel mode throws; sequential mode already naturally ordered. Never silently promise a parallel ordering capability |
| BRK-01 | `CircuitBreaker.AllowRequest()` discards lease from `AcquirePermit()` | **PROPOSE REMOVE unsafe convenience API**, replace with scoped `TryAcquireHalfOpenProbe` / permit | A standalone successful half-open allowance does not return a releasable lease; tests must prove no probe-slot leak |
| BRK-02 | many `CircuitBreaker` positional constructors + IClock-based constructor | **CONSOLIDATE to** `CircuitBreaker(CircuitBreakerOptions, TimeProvider)`, preserve only a demonstrably useful convenience ctor | Simpler API; remove legacy UTC-based time source if all users/tests migrated; performance largely unchanged |
| MET-01 | `SmartPipeMetrics.ExportPrometheus()` aliases non-Prometheus diagnostic text | **REMOVE** method | Prevents misleading format contract; not a hot path |
| MET-02 | public `SmartPipeMetrics` compatibility facade delegates to `SmartPipeMetricsRecorder` | **INVESTIGATE** removal only after consumer and constructor graph check | One object per facade instance, not per-envelope; performance payoff small; do not make release-blocker |
| FAC-01 | `SmartPipe.Extensions`: 13 retained legacy DI/Hosting/Health identities and explicit facade-only dependency cluster | **RECOMMEND retire physical cluster** if closure/consumer migration green; retain 23 forwarding identities as long as still necessary | Deletes substantial obsolete implementation and package dependency burden; large binary break; isolate in independent PR |
| DATA-01 | `DapperSelector<T>` reflection row mapper and `DbSink<T>` auto-generated SQL | **RECOMMEND REMOVE legacy types** in favor of explicit `DapperPipelineComponents` and mappings | Reflection/AOT and correctness issues; reflection `PropertyInfo.SetValue` + activator occurs per row in legacy selector. Confirm replacement for each supported scenario |
| DATA-02 | legacy `EfCoreSelector<T>`, `CsvFileSource/Sink<T>`, `CsvTransform<TInput,TOutput>` | **CANDIDATE**, migrate to factory APIs only after semantic parity tests | Important type-forwarder, provider/query and CSV mapping differences; do not promise performance savings from deletion alone |
| DATA-03 | legacy `MapsterTransform<TIn,TOut>` | **CANDIDATE** retirement after explicit config/mapper parity | Dynamic code remains in Mapster-based modern methods; deletion itself does not make Mapster AOT-safe |
| JSON-01 | reflection/`JsonSerializerOptions` constructors of `JsonFileSource/Sink` | **CANDIDATE** targeted deletion, keep `JsonTypeInfo` APIs | NativeAOT benefit; file-framing and historic format compatibility must be preserved |
| CLOCK-01 | `IClock`, `TimeProviderClock` and custom UTC-clock bridges | **INVESTIGATE** unification on `TimeProvider`/existing `IPipelineClock` | Current `DeduplicationFilter` uses internal `IClock`; must move its TTL tests before deletion; do not confuse wall-time and monotonic intervals |
| BUILDER-01 | `PipelineBuilder` -> `LegacyPipelineDefinitionAdapter` -> canonical definition compiler | **KEEP for 2.3.0**, measure first | Widely exercised, construction-time objects mostly; deleting this builder would cause disproportionate migration with no shown per-item gain |
| ALLOC-01 | `MetadataBag.Set` constructs a dictionary then `new MetadataBag(copy)` copies again | **OPTIMIZE internal copy path** without removing API | Concrete redundant dictionary allocation per Set; preserve immutability/no externally mutable state |
| ALLOC-02 | `ProcessingEnvelope<T>.Create(payload)` allocates `Guid.NewGuid().ToString("N")` per item; CSV and Dapper sources use it | **TEST correlation / profile**, then redesign source-created envelope identity if safe | Per-record GUID/string work confirmed. But input envelope IDs, replay and `PipelineRun.RunId` require a defined contract before change |

## SP230-00 — governed breaking-minor contract

**Files:** `VERSIONING.md`, `eng/SmartPipe.Versions.props`, `eng/package-graph.json`, `docs/reference/compatibility/*`, `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` in touched packages.

1. Define 2.3.0 as explicit API-consolidation minor release; release notes declare it **not backward-compatible** and **not strict SemVer**. New PATCH versions maintain compatibility with 2.3.0.
2. Inventory every proposed removed/changed symbol. Per-symbol entry includes consumer usage, replacement, source/binary/behavior status, AOT exposure, exact package owner, suppression reason, and migration code.
3. Compare candidate with an immutable published baseline. Lock package versions consistently across 20 packages; eliminate public API analyzers errors only via an approved explicit delta.
4. Stage version update only after 2.2.1 publication resolved. All PR CI runs compare to the actual baseline.

**Acceptance:** explicit signed-off break manifest; no unknown breaks; CI/source/binary checks distinguish intended failures from regressions.

## SP230-01 — safe logging, no legacy execution path

**Files:** `src/SmartPipe.Extensions.Logging/LoggerSink.cs`, `LoggerSinkOptions.cs`, logging API baseline; specialized tests; direct/native-AOT/facade consumers; benchmark and docs.

- Retain 1-arg constructor *signature* but delegate to `LoggerSinkOptions<T>` safe defaults. Make options field non-nullable.
- Remove both raw `_logger.LogInformation(...{@Value}, envelope.Payload)` branches and enum `UnsafeRaw=2`. Reject numeric value 2 using existing options validation.
- Keep `None` and explicit `Formatted`. Formatter output is caller-owned data exposure, not guaranteed redaction. Keep enabled-level short circuit and bounded output.
- Continue using existing `[LoggerMessage]` safe methods; keep event IDs and safe event names unchanged. Test log state in addition to formatted text.
- Preserve type forwarder identity `SmartPipe.Extensions.Sinks.LoggerSink<T>`; remove old raw execution benchmark, update current tests/samples and preserve historical reports/baselines.

**Acceptance:** old and new constructors cannot send raw payload by default, zero executable `UnsafeRaw` in sources; `logging-direct` NativeAOT and facade consumers succeed; precise declared breaking API delta.

## SP230-02 — config/control-flow simplification

**Files:** `PipelineRuntimeOptions.cs`, `PipelineRuntimeOptionsSnapshot.cs`, `PipelineOutputEmitter.cs`, `AdaptiveParallelismOptions.cs`, output/immutability/adaptive test suites.

- Add `PipelineOutputPolicy.SuppressAll` as an explicit canonical option; only after proving semantic parity, delete `PipelineOutputMode`, `OutputMode`, compat flags and dual-policies branch.
- Remove `MaxDegreeOfParallelism`, effective-fallback logic, contradictory-value check; `EffectiveMaxConcurrency` becomes sole `MaxConcurrency`, preserving default 1.
- Remove adaptive alias properties `SampleInterval` and `Cooldown`; snapshots store only canonical values.
- For ordering, prove: unordered parallel and ordered sequential are the only implemented behaviors; if confirmed, remove `OrderingMode` + enum instead of retaining an unsupported setting. Don't inadvertently change stage-level ordering.
- Include a table-driven migration test for all four legacy output modes × {sink/no sink} and all new policies. Test snapshot, compiler and runtime output semantics; not just build.

**Acceptance:** one execution path for output filtering, no compatibility branches in emitter; all supported previous behavior either mapped or intentionally documented as removed; no unexpected output loss.

## SP230-03 — breaker and clock API rationalization

**Files:** `CircuitBreaker.cs`, `IClock.cs`, `DeduplicationFilter.cs`, resilience tests.

- Remove `AllowRequest` convenience path if replacement permit/probe API proves correct half-open behavior. Test lease disposal under exceptions, cancellation, saturation, concurrent probe admission.
- Consolidate overloads into `CircuitBreakerOptions` + `TimeProvider`, first migrating all existing named/positional tests and samples. Keep precise sampling behavior and current exact default. Do not alter breaker algorithm.
- Evaluate whether `IClock` and `TimeProviderClock` can be retired without affecting DeduplicationFilter TTL internals and tests. If so use monotonic `TimeProvider` for elapsed-time decisions; use UTC only for wall-clock metadata.
- If scope is too large, defer overload/clock removal; do not block core safety fixes.

**Acceptance:** breaker state machine tests green, one authoritative probe-ownership API, deterministic time tests, no new concurrency allocations.

## SP230-04 — legacy DI/Hosting/Health facade quarantine removal

**Files:** `src/SmartPipe.Extensions/SmartPipeTypedFactory.cs`, `SmartPipeHostedService*.cs`, `SmartPipeHealthCheck*.cs`, `SmartPipeServiceCollectionExtensions.cs`, facade csproj, package graph, forwarders/consumers.

- The legacy facade retains **13 physical identities** and a coupled dependency graph. Primary canonical replacements live in `SmartPipe.Extensions.DependencyInjection`, `.Hosting`, `.HealthChecks`.
- Map each of the 13 public identities (including ctor signatures and monitor ownership) to a canonical contract. Migrate active source consumers. Preserve unchanged historical binary fixtures to prove the expected incompatibility, not to falsely label them compatible.
- Delete implementation and obsolete facade-owned registration graph *only if* canonical behavior/source consumer parity is proven; recalculate direct/transitive dependencies and package graph, remove unnecessary direct references if tool verifies package closure.
- Do **not** automatically remove `SmartPipe.Extensions` itself: 23 type forwarders still provide stable type identities and broad meta-package selection. Deleting the bundle would create additional, unrelated incompatibilities.
- Run separate hosting/health/state/lifetime tests after deletion.

**Acceptance:** 13/13 explicit migration entries, old facade-only lifecycle code absent, leaf DI/Hosting/Health tests green, facade metadata and package closure checks updated intentionally. If cost excessive, split into 2.4.0 — don't block 2.3.0 indefinitely.

## SP230-05 — legacy data integration API retirement

**Files:** Dapper `Legacy/DapperSelector.cs`, `Legacy/DbSink.cs`; EF `Legacy/EfCoreSelector.cs`; CSV `Legacy/*`; Mapster `Legacy/MapsterTransform.cs`; selective JSON old constructors.

- Group changes by package: Dapper first (per-row reflection mapping and automatic INSERT generation); EF/CSV next only if typed factory options cover provider/query/filter/delimiter/encoding/ownership differences; Mapster only after configured mapping parity; JSON only after `JsonTypeInfo` framing/format parity.
- Remove abandoned type forwarders **only for actual removed identities**, not unrelated forwarded types.
- Update public API baselines, package/ownership manifests, current source consumers and AOT diagnostics. Preserve frozen historical binary fixtures and mark expected major/minor breaking cases explicitly.
- Avoid falsely claiming AOT compatibility from removing legacy paths while still relying on provider/CsvHelper/Mapster dynamic code.
- Every removal must have an executable replacement recipe and feature-parity test. If parity is unavailable, **NO-GO** for that type.

**Acceptance:** no dead legacy implementation for approved removals; demonstrated supported paths and precise migration matrix. Favor individual packages/PRs over one all-in-one refactor.

## SP230-06 — concrete allocation work (not cosmetic API deletions)

**ALLOC-01 MetadataBag:** `Set` copies via `Dictionary` then the public copying ctor. Add a **private take-ownership constructor** used only with newly created unshared dictionaries. Keep public constructor defensive copying and read-only wrapper. Test immutability under caller mutation, Empty singleton, serialized round-trip and multi-set behavior. Benchmark allocations/op and CPU for n=0,1,8,64. GO when reduction measured and semantic parity exact.

**ALLOC-02 envelope correlation:** `ProcessingEnvelope.Create(payload)` currently creates a new GUID/string on every input. Strict CSV and Dapper query sources call this for every record. Do not simply change `RunId` to a static string or silently rewrite externally supplied replay correlation. First test and document relations between envelope IDs, canonical pipeline key/run id and dead-letter replay. Compare explicit source-created correlation vs generated defaults, measure B/record and time/record. If semantics can be safely normalized at source activation, implement a narrow per-run correlation factory and preserve replay IDs. Require no extra stateful allocations in the critical row-reading loop. Otherwise close as NO-GO with evidence.

**Metrics:** `SmartPipeMetrics.ExportJson` allocates diagnostic dictionary/buffer by design, but isn't part of runtime per-envelope path; don't optimize/remove just for a benchmark. Drop misleading `ExportPrometheus`; consider replacing old `SmartPipeMetrics` facade only after compatibility inventory.

## SP230-07 — telemetry, resilience and deterministic correctness

- Fix automatic `Activity.SetStatus(Error, ex.Message)` and `result.Error.Message` disclosure in `TypedPipelineRuntime.cs` via metadata-safe status; retain original `SmartPipeError`/exceptions for application callers. Test secret-canary in stages/run spans and OpenTelemetry.
- Layered retry tests: Core retry applies to stages; the built-in HTTP sink isn't retried by Core automatically. Distinguish thrown exceptions and `StageResult.Failure` within Polly; count actual network attempts and safe POST idempotency.
- Small deterministic concurrency state tests: stop/drain/abort/failure/dispose, late attempts, observer backpressure, scopes; `TaskCompletionSource(RunContinuationsAsynchronously)`, fake time when appropriate and replayable seeds. No new public scheduler or invented force-stop guarantee.
- Do not rewrite `StageExecutor`, `CircuitBreaker`, metrics or lineage implementation without concrete correctness/perf need.

## SP230-08 — release and docs

- Complete 2.2.1 reconciliation first: 40/40 payload verification against frozen producer, resume failed-job-only; no repack or new producer. Then improve recoverable publish verification (partial 27/40, 429/5xx, payload mismatch, symbols) with a script or harness, keeping fail-closed behavior.
- Update `VERSIONING.md` to clearly disclose MINOR breaking-release policy. Reconcile `docs/reference/compatibility`, public API baselines, package manifest, changelog, package-specific README, migration examples and source/binary/NativeAOT consumer matrices.
- For 2.3.0 create a single `2.2.1 -> 2.3.0` removed API migration table. Preserve immutable 2.1.2 and 2.2.0 historical evidence.
- Exact-head CI/CodeQL; package validation with approved suppression list, Windows & Linux consumer paths, PostgreSQL where affected, trim/NativeAOT claims, one release-mode dry run; release only on immutable accepted package producer.
- For publishing, require explicit message: **"2.3.0 deliberately contains source/binary/behavioral API removals; this is not SemVer-compatible MINOR."** Document pinning or review-before-update for affected consumers.

## Execution order / bounded scope

| Slice | Depends on | Deliverable | Gate |
| --- | --- | --- | --- |
| SP230-00 policy + inventory | read-only HEAD | This roadmap and `VERSIONING.md` governance PR | Documented minor breaking policy; no runtime changes |
| REL221 recovery | frozen existing publisher | Verified 2.2.1 and provenance | No new same-version producer |
| SP230-01 Logging | SP230-00 | safe logger, remove raw mode | privacy + AOT + API checks |
| SP230-02 Core options | SP230-00 | one output policy, no obsolete aliases | output matrix and snapshots |
| SP230-06A MetadataBag | independent | no redundant double-copy | allocation benchmark and immutability |
| SP230-03 breaker | options groundwork | permit API, optional overload reduction | state machine |
| SP230-04 facade | migration inventory | optional removal 13 wrapper identities | consumer & package graph parity |
| SP230-05 integrations | per-leaf parity | approve only well-substituted legacy leaves | consumer migration |
| SP230-06B envelope | source correlation tests | optional per-run identity optimization | no replay/correlation regression |
| SP230-07 security/QA | independent | telemetry / resilience / concurrency coverage | CI |
| SP230-08 release | accepted branches and REL221 | 2.3.0 rc/GA | checks + exact-head provenance |

**Explicitly out of scope unless profiled:** weighted memory budget, observer-kind mask, new adaptive feedback controller, new generic batching engine, second runtime, blanket source-generated logging conversion, blanket NativeAOT claim.

## Official Microsoft documentation read

- Library versioning / SemVer recommendations: https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/versioning
- NuGet version conventions: https://learn.microsoft.com/en-us/nuget/concepts/package-versioning
- Breaking changes and package effects: https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/breaking-changes
- SDK package/API compatibility tooling: https://learn.microsoft.com/en-us/dotnet/fundamentals/apicompat/overview
- Package Validation: https://learn.microsoft.com/en-us/dotnet/fundamentals/apicompat/package-validation/overview
- Compile-time source-generated logging: https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/source-generation
