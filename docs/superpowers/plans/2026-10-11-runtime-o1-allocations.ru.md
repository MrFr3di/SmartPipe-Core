# O1 Allocation Gates Implementation Plan

> **For agentic workers:** Use `superpowers:executing-plans` after approval.
> Baseline precedes production changes; tasks execute sequentially.

**Goal:** Убрать ненужные domain-event/output allocations, сохранив sink,
telemetry, failure и output-policy contracts в рамках 2.2.1.

**Architecture:** Observer interest проверяется перед event construction,
result-kind output policy — перед PipelineOutput construction. Оба gate
независимы от metrics/ActivitySource и от исполнения sink.

**Tech Stack:** SDK 10.0.401, C#, xUnit v3/MTP, BenchmarkDotNet MemoryDiagnoser.

**Spec:** [I/O design](../specs/2026-10-11-runtime-io-2.2.1-design.ru.md), O1.

## Global Constraints

Все [roadmap constraints/checks](2026-10-11-runtime-io-2.2.1.ru.md) действуют.
Не меняются public API, envelopes/lineage, default output policy или telemetry.
PipelineResult — struct; оптимизируется PipelineOutput record allocation.

## Review Focus

No observers with active telemetry; removed buffered observers/worker fault;
legacy OutputMode matrix; suppressed success with failing sink; correct per-item
normalization и p99 measurement. Tests/benchmark tasks ниже покрывают каждый.

### Task 1: identical benchmark harness and baseline

**Files:** Create `benchmarks/SmartPipe.Benchmarks/RuntimeIoBenchmarks.cs`;
read/reuse fixtures from `RuntimePipelineBenchmarks.cs`;
create `benchmarks/SmartPipe.Benchmarks/RuntimeIo-results.md` для actual evidence.

**Interfaces:** Benchmark class `RuntimeIoAllocationBenchmarks`, ItemCount=10,000,
OperationsPerInvoke=ItemCount. Params Stages 1/4/16; Observers 0/1; HasSink
false/true; Concurrency 1/8/32; AsyncTransform false/true. 72 configurations.
Canonical factory definition, sync completed ValueTask и genuinely asynchronous
transform await path; считать вызовы/checksum независимо от output suppression.

- [ ] Реализовать harness, который drains output concurrently, затем awaits
  Completion и Dispose. Проверяет received outputs по policy, transformed
  checksum, sink count, final state; не допускает benchmark deadlock.
- [ ] Для основной матрицы default output policy; representative отдельные
  EmitAll/EmitFailuresOnly и buffered observer cases. Item p99 собирать отдельным
  instrumented diagnostic run: start at source emission, finish после sink/output
  обработки. Baseline/candidate имеют одинаковую instrumentation; batch p99
  BenchmarkDotNet не использовать как item p99.
- [ ] Применить тот же harness к merged-F baseline в isolated checkout; зафиксировать
  SHA, host/CPU/OS/runtime/SDK, GC settings, command, raw JSON/CSV. Historical
  SP220/ReleaseHardening numbers не заменяют этот baseline.
- [ ] Run без Dry как performance evidence; Dry допустим только для wiring smoke.

```bash
dotnet run -c Release --project benchmarks/SmartPipe.Benchmarks/SmartPipe.Benchmarks.csproj -- --filter '*RuntimeIoAllocationBenchmarks*' --warmupCount 5 --iterationCount 15 --launchCount 2 --exporters json csv --artifacts artifacts/benchmarks/o1-baseline
```

- [ ] Commit benchmark-only harness/evidence, production untouched.

### Task 2: output policy before allocation

**Files:** Modify `src/SmartPipe.Core/Runtime/Execution/PipelineOutputEmitter.cs`,
`src/SmartPipe.Core/TypedPipelineRuntime.cs` (success/terminal write paths);
test `tests/SmartPipe.Core.Tests/Engine/OutputAllocationRegressionTests.cs`.

**Interfaces:** Add internal `bool ShouldEmit(PipelineResultKind kind)`;
existing ShouldEmit(PipelineResult<TOutput>) сохраняет boolean classification
даже для default result struct. Оба метода используют общий private policy helper
`bool ShouldEmit(bool isSuccess, bool isFailure)`; kind overload передаёт valid
classification, result overload — existing IsSuccess/IsFailure.
Callers check gate before `new PipelineOutput<TOutput>`; Writer path still
enforces policy defensively. Success creation остаётся после successful sink.

- [ ] Parameterized policy regression для Success/Failure/Filtered/Skipped,
  sink present/absent, modern OutputPolicy и configured legacy OutputMode.
  Assert emitted counts/results/trace identity; explicit overrides сохраняются.
  Отдельно default(PipelineResult<T>) сохраняет старую classification policy.
- [ ] `NoObservers_PreservesTelemetryAndSinkFailure`: throwing sink under
  suppressed success, metrics/events when configured, no success output,
  correct primary failure. Full output/drop modes сохраняют accounting.
- [ ] Выполнить correctness characterization (green на baseline); добавить
  missing kind-gate unit tests → compile RED; measured allocation baseline
  Task 1 является evidence существующего overhead.
- [ ] Implement minimal kind gate + move constructors behind gate. Sink вызывается
  один раз независимо от ShouldEmit; RecordProcessed только после sink success.
- [ ] Build → focused policy/sink/F4 tests green; candidate benchmark показывает
  снижение B/item на suppressed-output cases без смены checksum/state.
- [ ] Commit: `perf(core): gate output allocation before construction`.

### Task 3: event interest before construction

**Files:** Modify `src/SmartPipe.Core/PipelineObserverDispatcher.cs`,
`Runtime/Execution/StageExecutor.cs`, `Runtime/Execution/SinkExecutor.cs`,
`TypedPipelineRuntime.cs` (все domain event construction sites);
test `tests/SmartPipe.Core.Tests/Engine/ObserverInterestRegressionTests.cs`.

**Interfaces:** Add internal `bool HasObservers { get; }` to dispatcher,
immutable initial registration count >0. StageExecutor/SinkExecutor получают
bool observer-interest на run construction. Прямой branch перед `new Event`;
не создавать capturing event factory delegate на item.

- [ ] Characterization: zero observers with MeterListener/ActivityListener
  active, success/retry/failure/sink/terminal/drop events. Metrics counters,
  activity status/tags и primary errors одинаковы baseline.
- [ ] Gate unit compile RED; implement property/branches. Drop accounting,
  StopCallbacks/Flush/Complete/Dispose исполняются независимо от HasObservers;
  observer buffer с нулём registrations не должен давать fake drop metrics.
- [ ] При initially registered, но removed observers gate остаётся true:
  preserve dispatcher Emit fault/state behavior, Flush и terminal delivery.
  Не использовать dynamic count, который мог бы скрыть worker fault.
- [ ] Build → Inline/BufferedReliable/BestEffort semantics и existing observer/
  fault suites green; perf no-observer B/item уменьшается, telemetry работает.
- [ ] Commit: `perf(core): gate domain events when no observers are registered`.

### Task 4: paired performance acceptance

**Files:** `benchmarks/SmartPipe.Benchmarks/RuntimeIo-results.md`, targeted docs.

- [ ] Same-host paired parent/candidate runs с identical harness/settings;
  raw B/item/Gen0/throughput, diagnostic item p99 и semantic checks для 72 cases.
- [ ] Дополнительные controls: observer dispatch modes, telemetry on/off,
  representative failures/filter/skip, LineageMode и I2 enabled/disabled.
- [ ] Allocation claim отделить от elapsed-time noise. Никакой per-item heap
  allocation regression при disabled policies; >5% повторённый throughput/p99
  regression за пределами noise требует исправления до acceptance.
- [ ] All roadmap checks + exact-head CI. Зафиксировать реально полученное
  evidence; если benchmark не выполнялся, не писать estimated GC savings.
