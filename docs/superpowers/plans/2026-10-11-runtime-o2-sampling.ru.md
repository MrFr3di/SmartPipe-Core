# O2 Circuit Breaker Sampling Implementation Plan

> **For agentic workers:** Use `superpowers:executing-plans` after approval.
> Exact cleanup и optional bucket feature — разные reviewed changes.

**Goal:** Измерить sampling memory/contention, улучшить exact cleanup и,
при подтверждённой потребности, добавить explicit opt-in bounded-memory buckets
в рамках 2.2.1.

**Architecture:** Exact FIFO/incremental counts/EWMA остаются default.
Bucket ring — отдельно выбранный approximation mode с oracle и documented
boundary error; half-open state machine и permit поколения одинаковы.

**Tech Stack:** SDK 10.0.401, C#, FakeTimeProvider/manual time source,
xUnit v3/MTP, BenchmarkDotNet, retained-memory/contention diagnostics.

**Spec:** [I/O design](../specs/2026-10-11-runtime-io-2.2.1-design.ru.md), O2.

## Global Constraints

Все [roadmap constraints/checks](2026-10-11-runtime-io-2.2.1.ru.md) действуют.
Не повторять уже выполненное O(n)→incremental counts изменение. Не менять EWMA,
exact boundary, half-open generations и default evaluation/sampling behavior.

## Review Focus

Exact equality at expiry; high-water retained Queue capacity; idle multi-window
rotation; boundary bursts/threshold crossings; concurrent records/reset/probes.
Oracle и tasks ниже проверяют каждый сценарий.

### Task 1: baseline, oracle and memory gate

**Files:** Create `benchmarks/SmartPipe.Benchmarks/CircuitBreakerSamplingBenchmarks.cs`,
`benchmarks/SmartPipe.Benchmarks/CircuitBreakerSampling-results.md`;
reuse `ReleaseHardeningCircuitBreakerBenchmarks` without copying its historical
performance claims. Test `tests/SmartPipe.Core.Tests/Resilience/CircuitBreakerSamplingOracleTests.cs`.

**Interfaces:** `CircuitBreakerSamplingBenchmarks` with threads 1/8/32,
live samples 1,000/100,000; streams steady-expiry and peak→idle→burst.
Oracle replay takes timestamped success/failure operations plus permit/reset
commands and compares observed ratios/states at every command.

- [x] Characterization against current exact: inclusive sampling boundary,
  mixed failures, EWMA synthetic sample insertion, zero-throughput/idle,
  Reset, late previous-generation permit completion, disposed probe без sample.
  Tests pass current behavior, independent reference model owns its counts.
- [x] Baseline on merged-F SHA with same harness. Report retained memory at
  peak and after idle, GC allocations, expiration batch cost, throughput,
  p99 operation latency, lock contention. Queue.Count != retained capacity.
- [x] Не измерять allocation на других threads только thread-local counter;
  MemoryDiagnoser/GC process diagnostics покрывают execution topology.
- [x] Raw evidence records SHAs/host/settings; use commands ниже. No Dry for claims.

```bash
dotnet run -c Release --project benchmarks/SmartPipe.Benchmarks/SmartPipe.Benchmarks.csproj -- --filter '*CircuitBreakerSamplingBenchmarks*' --warmupCount 5 --iterationCount 15 --launchCount 2 --exporters json csv --artifacts artifacts/benchmarks/o2-baseline
```

- [x] Commit harness/oracle before production modifications.

### Task 2: batch exact cleanup

**Files:** Modify `src/SmartPipe.Core/CircuitBreaker.cs` (`CleanupWindow` only);
test `tests/SmartPipe.Core.Tests/Resilience/CircuitBreakerWindowStatisticsTests.cs`
and sampling oracle suite.

**Interfaces:** Existing CleanupWindow/GetWindowStatistics/public breaker APIs;
нет new public signatures в exact optimization.

- [x] Add exact boundary tests at W-1 tick/W/W+1 tick and large expired prefix,
  concurrent record/cleanup/reset with explicit gates. Preserve timestamp reads
  ordered with enqueue; custom clock compatibility follows existing behavior.
- [x] Baseline tests green; profiling from Task 1 proves repeated-lock cost.
  Implement one captured now + one lock to prune expired prefix; maintain
  incremental failures and original elapsed <= W inclusion.
- [x] Build → existing breaker/window/stress/half-open/runtime tests + oracle green.
  Paired benchmark: steady expiry и burst cleanup; retain latency tradeoff
  of holding lock over large prefix. Если burst p99 regresses materially,
  bounded batches допустимы только с accurate full statistics before evaluation.
- [x] Не заявлять уменьшение retained memory от одного batch cleanup.
- [x] Commit: `perf(core): batch exact circuit breaker window cleanup`.

### Task 3: opt-in bucket feature gate

**Files:** Evidence document Task 1; no production bucket diff до gate.

- [x] После exact optimization повторить memory/contention diagnostics.
  Подтверждённая потребность: exact retained bytes растут с live sample count,
  вызывающий это workload входит в поддерживаемый сценарий, и bounded ring
  prototype устраняет эту зависимость без недопустимой latency regression.
  Отчёт содержит measured rate/window и bytes, не только theoretical O(rate×W).
- [x] Если gate не подтверждён, остановить bucket delivery: accepted O2 =
  measured baseline + exact optimization; clearly record unshipped experiment.
  Если подтверждён, Tasks 4–5 входят в отдельный feature PR 2.2.1.

### Task 4: explicit bucket sampling and oracle

**Files:** Modify `src/SmartPipe.Core/CircuitBreaker.cs`, `StageFailureOptions.cs`,
`Runtime/Definitions/PipelineRuntimeOptionsSnapshot.cs`, `TypedPipelineRuntime.cs`
(CreateBreaker), `PublicAPI.Unshipped.txt`;
create `Runtime/Execution/CircuitBreakerSamplingWindow.cs`;
test `tests/SmartPipe.Core.Tests/Resilience/CircuitBreakerBucketSamplingTests.cs`.

**Interfaces:** `CircuitBreakerSamplingMode { Exact = 0, Bucketed = 1 }`;
Options/Policy `SamplingMode` default Exact; `SamplingBucketCount` default 64,
valid 2..4096. Internal `ICircuitBreakerSamplingWindow`:
`void AddSample(bool isSuccess)`,
`CircuitWindowStatistics ExpireAndGetStatistics()`, `void Reset()`;
internal readonly record struct CircuitWindowStatistics(long Total, long Failures).
Constructors `ExactCircuitBreakerSamplingWindow(TimeSpan samplingDuration, ICircuitBreakerTimeSource timeSource)`
и `BucketedCircuitBreakerSamplingWindow(TimeSpan samplingDuration, int bucketCount, ICircuitBreakerTimeSource timeSource)`.
Window owns synchronization и читает sample timestamp внутри add lock;
ExpireAndGetStatistics captures now once и возвращает counts после pruning.
Exact и bucket не получают timestamp, заранее прочитанный caller вне lock.
Использовать internal `CircuitBreaker.CreateForTimeSource(options, timeSource)`
для runtime CreateBreaker; static entry point устраняет неоднозначность existing
friend-assembly null tests. Published constructors сохраняют signatures.

- [x] Compile RED public config/window tests; invalid mode/count/nonpositive
  effective bucket window rejected before activation; snapshots retain properties.
  Legacy constructors always Exact; old source/binary consumers unchanged.
- [x] Implement default exact strategy and optional ring; use resolved monotonic
  time via ICircuitBreakerTimeSource.GetElapsedTime, не UTC subtraction.
  width=ceil(W ticks/count), >=1; <=count+1 live buckets; long counters.
  No time-driven background cleanup task.
- [x] Если moved window меняет private fields, перевести прежние reflection
  assertions на sampling-window diagnostics/ordered timestamp oracle, сохранив
  проверку порядка и original regression, без production public test hooks.
- [x] `BoundaryBurst_DivergenceExplainedByOldestBucket`: replay mixed burst/idle
  around every bucket/window boundary; compare exact reference counts and ring
  counts plus oldest partial-bucket contribution. Count/ratio/state divergence
  outside this computed boundary uncertainty is failure.
- [x] Bound временной погрешности <width не превращать в ratio bound 1/count.
  Test large failed burst followed by success/idle; sampling differences can
  legitimately change opening decisions only within oracle uncertainty.
- [x] Advance clock several windows/long idle in one step; Reset clears counts;
  timestamp origin/ceil arithmetic overflow-safe; time source must be monotonic
  as required for this opt-in mode. Deterministic concurrent records/reset tests.
- [x] EWMA update/synthetic insertion unchanged; same half-open permit allocation,
  generations/limits/close-reopen logic. Probe behavior проверяется independently
  от approximate Closed→Open threshold decisions.
- [x] Build → oracle/new suites green; commit
  `feat(core): add opt-in bounded circuit breaker sampling`.

### Task 5: measured bounded-memory and contract acceptance

**Files:** `docs/runtime-contracts.md`, `CHANGELOG.md`, `VERSIONING.md`
(targeted 2.2.1 exception при bucket delivery), benchmark evidence, CI gates.

- [x] Replay identical streams in Exact/Bucketed; 1,000→100,000 live samples
  должно увеличивать exact retained bytes, но ring storage остаётся bounded
  выбранным bucket count. Assert ring storage bound as resource regression.
- [x] Same-host throughput/p99/contention comparisons плюс current baseline;
  <=count+1 buckets доказать, не подменять full runtime memory этим bound.
- [x] Документировать explicit approximation, temporal uncertainty/ratio caveat,
  default Exact и mono-clock assumption. General VERSIONING policy не менять;
  сохранить пользовательскую цель 2.2.1 и immutable baseline 2.2.0.
Перед merge обязательны все roadmap/API/package/source/binary/trim/AOT/docs
проверки и full CI текущего HEAD [PR #158](https://github.com/MrFr3di/SmartPipe-Core/pull/158).
Результат hosted CI фиксируется в Checks/PR description после завершения, чтобы
изменение самого checklist не делало свидетельство предыдущего HEAD устаревшим.
Rejected gate/prototype не публикуется как implemented bucket feature.

Focused command (после build):

```bash
dotnet test --project tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj -c Release --no-build --filter-class SmartPipe.Core.Tests.Resilience.CircuitBreakerSamplingOracleTests --minimum-expected-tests 1
dotnet test --project tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj -c Release --no-build --filter-class SmartPipe.Core.Tests.Resilience.CircuitBreakerBucketSamplingTests --minimum-expected-tests 1
```
