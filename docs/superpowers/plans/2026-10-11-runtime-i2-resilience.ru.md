# I2 Opt-in Resilience Implementation Plan

> **For agentic workers:** Use `superpowers:executing-plans` after approval.
> Execute each task sequentially; commit/review retry policies before late capacity.

**Goal:** Ограничить shared retry amplification и outstanding timed executions,
добавив deterministic-testable jitter без изменения default runtime behavior.

**Architecture:** Run-owned token bucket и timed admission limiter используют
resolved clock. Reservations commit только на actual execution start и передают
timed lifetime в existing late registry. Policies содержат configuration, не state.

**Tech Stack:** SDK 10.0.401, C#, FakeTimeProvider, xUnit v3/MTP, bounded async waits.

**Spec:** [I/O design](../specs/2026-10-11-runtime-io-2.2.1-design.ru.md), I2a–I2c.

## Global Constraints

Все [roadmap constraints/checks](2026-10-11-runtime-io-2.2.1.ru.md) действуют.
Nullable budget/admission выключены; Jitter None. Existing RetryPolicy constructor
и GetDelay unchanged. Validate/snapshot до factory activation. API additions только
Unshipped; VersionPrefix **2.2.1**, baseline **2.2.0**, release publication отдельно.

## Review Focus

Reserved-delay refill burst; cancelled delay/deadline/callback before invocation;
reusable definition run isolation; concurrent detach cap; scope-owned observation
после bounded finalization. Tests распределены по tasks ниже.

### Task 1: configuration and jitter

**Files:** Modify `src/SmartPipe.Core/RetryPolicy.cs`, `PipelineRuntimeOptions.cs`,
`Runtime/Definitions/PipelineRuntimeOptionsSnapshot.cs`, `PublicAPI.Unshipped.txt`;
create `RetryBudgetOptions.cs`, `TimedAttemptAdmissionOptions.cs`,
`Runtime/Execution/RetryDelayPlanner.cs`;
test `tests/SmartPipe.Core.Tests/Resilience/RetryDelayPlannerTests.cs`,
`tests/SmartPipe.Core.Tests/Definitions/PipelineDefinitionImmutabilityTests.cs`.

**Interfaces:** Exact public configuration signatures и enum values из design.
Internal `IRetryRandomSource.NextInt64(long exclusiveUpperBound)` и
`RetryDelayPlanner.GetDelay(RetryPolicy policy, int nextAttempt) -> TimeSpan`.
Production source delegates to Random.Shared, tests scripted source.

- [ ] Добавить `FullJitter_UsesSingleSampleWithinBackoffCap`: base 100 ticks,
  scripted 0/99 → exact delays 0/99; one RNG call на decision. None → 100,
  no RNG call. Проверить exponential overflow через existing MaxDelay behavior.
- [ ] Добавить snapshot/validation tests: invalid enum; Capacity 0/-1;
  refill -1/NaN/infinities; refill 0 valid; admission limit 0/-1; malformed
  modern/legacy options не вызывают user factories. Materialize возвращает
  defensive options, Jitter копируется, existing constructor signature retained.
- [ ] Build focused tests → compile RED новых types; реализовать minimal API,
  snapshot и delay planner; build → focused green.
- [ ] Commit: `feat(core): add opt-in resilience configuration and retry jitter`.

### Task 2: token bucket reservations

**Files:** Create `src/SmartPipe.Core/Runtime/Execution/RunRetryBudget.cs`;
test `tests/SmartPipe.Core.Tests/Runtime/Execution/RunRetryBudgetTests.cs`.

**Interfaces:** `RunRetryBudget(RetryBudgetOptions options, PipelineTime time)`;
`bool TryReserve(out RetryBudgetLease? lease)`;
`RetryBudgetLease.Commit()` и idempotent `Dispose()` для refund до commit.

- [ ] Добавить `Budget_IsSharedAndStartsFull`: capacity 2/refill 0,
  два reserves успешны, третий false; commit расходует, Dispose uncommitted
  возвращает один token, repeated Dispose не дублирует refund.
- [ ] Добавить `DelayedReservations_DoNotAccumulateRefillBurst`: capacity 2,
  refill 1/sec, hold 2 reservations, advance 10 sec → reserve false;
  commit обе → immediate reserve false; advance 1 sec → один reserve true.
- [ ] Добавить concurrent reserve/commit/refund с barriers: live reservation
  count <= capacity; при remaining credit exactly нужное число winners.
  Проверить fractional refill, capped idle refill, timestamp accounting при
  refund и independent state двух runs одного definition.
- [ ] Compile RED; реализовать short-lock lazy refill, ceiling
  Capacity-reserved; без background timer/tasks; clock update до mutation.
- [ ] Build → focused green; commit `feat(core): bound shared run retry admission`.

### Task 3: retry integration and actual-start accounting

**Files:** Modify `src/SmartPipe.Core/Runtime/Execution/StageExecutor.cs`,
`src/SmartPipe.Core/TypedPipelineRuntime.cs`;
create `src/SmartPipe.Core/Runtime/Execution/StageAttemptAdmission.cs`;
test `tests/SmartPipe.Core.Tests/Engine/RetryAdmissionRegressionTests.cs`.

**Interfaces:** `StageAttemptAdmission` carries optional RetryBudgetLease and,
после Task 4, TimedAttemptLease. `MarkExecutionStarted()` commits retry;
`Dispose()` refunds only not-started retry и releases не переданный timed slot.
Actual-start call располагается непосредственно перед `stage.ExecuteAsync`,
после проверки effective attempt timeout. No lease allocation on disabled path.

- [ ] Добавить two-stage/two-worker budget tests: first attempts free;
  total additional transformer starts ограничены capacity/refill;
  budget denial emits RetryExhausted exactly once и применяет OnRetryExhausted
  сохраняя исходный error. RetryOn one call per failed attempt.
- [ ] Добавить `ReservedRetryCancellation_RefundsWithoutCallback`;
  stage deadline expires during delay; throwing OnRetry/StageStarted observer;
  breaker opens before retry starts. Нет transformer invocation/commit для
  этих paths, reservation refunded; rejected admission не записывает breaker
  sample. Проверить штатные stage failure и retry event sequence отдельно.
- [ ] Получить runtime RED на existing implementation; затем добавить run-owned
  budget/delay planner и opt-in retry preparation. Не менять disabled branch.
  Повторная deadline/admission проверка не вызывает RetryOn и RNG снова.
- [ ] OnRetry вызывается после delay и успешного admission; callback OCE без
  requested token остаётся fault. New internal reason BudgetExhausted не
  подменяет первоначальный stage error и не создаёт новый public event.
- [ ] Build → focused green + existing retry/fault/observer suites.
- [ ] Commit: `feat(core): apply retry budget at actual attempt admission`.

### Task 4: timed attempt capacity before start

**Files:** Create `src/SmartPipe.Core/Runtime/Execution/TimedAttemptAdmissionLimiter.cs`;
modify `Runtime/Execution/StageAttemptAdmission.cs`, `LateStageAttemptRegistry.cs`,
`StageExecutor.cs`, `TypedPipelineRuntime.cs`;
test `tests/SmartPipe.Core.Tests/Runtime/Execution/TimedAttemptAdmissionLimiterTests.cs`,
`tests/SmartPipe.Core.Tests/Engine/TimedAttemptAdmissionRegressionTests.cs`.

**Interfaces:** `TimedAttemptAdmissionLimiter(TimedAttemptAdmissionOptions options, PipelineTime time)`;
`ValueTask<TimedAttemptLease?> AcquireAsync(TimeSpan? remainingStageTimeout, CancellationToken ct)`:
Reject/full returns null; Wait cancellation throws requested OCE;
deadline throws TimeoutException. `TimedAttemptLease.Dispose()` releases once.
Extend registry Register with nullable timed lease; observation owns release
after result-error observation and CTS disposal. Unlimited existing paths pass null.

- [ ] Unit red: limit 1, Acquire succeeds once, Reject second null;
  Wait second pending until release; cancel/timeout waiter leaves capacity intact;
  releasing one slot admits only one waiter; repeated lease disposal harmless.
- [ ] Runtime red: concurrently timeout cap-sized set; cap never exceeded,
  CTS/components stay alive. Cooperative completion releases; detach does not.
  Repeat Cancel/Abort/Dispose and throwing callbacks before/during/after grace.
- [ ] `ExternalLateAttempt_HoldsAdmissionUntilObservation`: Borrowed/ScopeOwned,
  late task/result fault, finalization timeout then Dispose pending until gate;
  slot retained until observation; late error reported with existing semantics.
- [ ] Reject → permanent Category TimedAttemptAdmission/OnPermanentFailure once;
  no callback/start/retry loop/breaker sample. Wait deadline → existing timed
  outcome, no start. Active+detached bound includes first and retry timed attempts;
  retry budget refund on all pre-start failures. Untimed paths unchanged.
- [ ] Implement reserve **до user invocation и StageStarted для opt-in path**.
  Compute remaining StageTimeout again after waiting. Transfer lease/CTS/task
  together on every abandonment path; release after actual observation only.
- [ ] Direct registry regression: rejected registration после seal сохраняет
  observation task/CTS/lease ownership до actual completion и освобождает lease
  ровно один раз. Нельзя освобождать slot просто потому, что Register бросил.
- [ ] Build → new suites green, full existing F + scope regressions green.
- [ ] Commit: `feat(core): bound outstanding timed executions safely`.

### Task 5: compatibility, fault injection and docs

**Files:** `docs/runtime-contracts.md`, `CHANGELOG.md`, `VERSIONING.md`,
`.github/workflows/reusable-release-validation.yml`; existing package consumers.

- [ ] Fault injection under retry storms: sync/async stages, multiple envelopes,
  all terminal actions, empty/full capacity, caller cancellation, disposal fault,
  repeated starts. Assert starts/leases/errors, не только final state.
- [ ] Документировать token refill, jitter zero delay, cap **active+detached**,
  Wait/Reject и potentially indefinite wait/Dispose для noncooperative code.
- [ ] Записать targeted exception из MINOR правила VERSIONING для I/O 2.2.1
  по явному решению пользователя; не менять общий SemVer policy для будущих работ.
- [ ] Source/old-binary compatibility, API/package/snapshot/consumer/trim/AOT
  checks; проверить constructor ABI сохранён, Core не зависит от new leaf.
- [ ] Benchmark disabled versus parent и enabled under fixed fault stream:
  initial/additional starts, retries/item, peak active+detached, B/item,
  throughput и item p99. Не использовать observer drop count вместо actual starts.
- [ ] Full exact-head CI и docs gates; update minimum case count новых suites.
- [ ] Commit targeted docs/evidence; остаёмся в 2.2.1, релиз здесь не публиковать.

Focused test command pattern (после build):

```bash
dotnet test --project tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj -c Release --no-build --filter-class SmartPipe.Core.Tests.Engine.RetryAdmissionRegressionTests --minimum-expected-tests 1
dotnet test --project tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj -c Release --no-build --filter-class SmartPipe.Core.Tests.Engine.TimedAttemptAdmissionRegressionTests --minimum-expected-tests 1
```
