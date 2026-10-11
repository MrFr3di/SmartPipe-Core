# I1 Processing Supervisor Implementation Plan

> **For agentic workers:** Use `superpowers:executing-plans` sequentially after
> design/plan approval. Track the steps below; no parallel runtime edits.

**Goal:** Извлечь проверенный producer/ordinary-worker ownership algorithm
в internal supervisor без изменения observable default behavior.

**Architecture:** Supervisor owns worker CTS, first worker error, callback errors
и unconditional join. Executor сохраняет source-stop classification и terminal
publication. Late registry/lifetime manager/observer dispatcher не объединяются.

**Tech Stack:** SDK 10.0.401, C#, xUnit v3/MTP, FluentAssertions, manual gates.

**Spec:** [I/O design](../specs/2026-10-11-runtime-io-2.2.1-design.ru.md), раздел I1.

## Global Constraints

Все [roadmap constraints/checks](2026-10-11-runtime-io-2.2.1.ru.md) действуют.
Нет public API, new Task.Run/per-item task registry или изменения sequential path.

## Review Focus

Primary producer error + independent worker errors; full output queue; throwing
Cancel callbacks; graceful Drain; borrowed/scope late observation после timeout.
Каждый сценарий включён в tasks ниже.

### Task 1: characterization boundaries

**Files:** Test `tests/SmartPipe.Core.Tests/Engine/ProcessingSupervisorRegressionTests.cs`;
read существующие TypedPipelineOwnershipRegressionTests, IntegrationReviewRegressionTests,
RuntimeStopSemanticsTests и `Runtime/Execution/PipelineWorker.cs`.

**Interfaces:** Использует existing typed builder/runtime и manually gated components.
Не добавляет production hooks для утверждения факта pending WriteAsync.

- [x] Добавить `ProducerFault_JoinsTwoIndependentlyFailingWorkers`: producer
  exception сохраняет identity первым, обе worker ошибки retained, cleanup
  не стартует до release обоих workers.
- [x] Добавить `FaultWithFullOutput_StopsSiblingBeforeCleanup` и
  `ThrowingStopCallbacks_DoNotBypassJoin`; output capacity 1,
  concurrency 2/8, gates с RunContinuationsAsynchronously.
- [x] Добавить `Drain_PreservesAcceptedWork` и reuse external/scope late
  observation regression: Completion fault не позволяет Dispose завершиться
  до late release, Borrowed/ScopeOwned transformer не disposed Core.
- [x] Запустить characterization: ожидается green на merged F. Это
  behavior-preserving extraction; не выдумывать red для уже исправленного бага.

```bash
dotnet test --project tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj -c Release --no-build --filter-class SmartPipe.Core.Tests.Engine.ProcessingSupervisorRegressionTests --minimum-expected-tests 1
```

### Task 2: extract processing supervisor

**Files:** Create `src/SmartPipe.Core/Runtime/Execution/PipelineProcessingSupervisor.cs`;
modify `src/SmartPipe.Core/TypedPipelineRuntime.cs` (parallel processing group);
test `tests/SmartPipe.Core.Tests/Runtime/Execution/PipelineProcessingSupervisorTests.cs`.

**Interfaces:**
- `PipelineProcessingSupervisor(CancellationToken processingToken, Action requestSourceStop)`;
- `CancellationToken WorkerToken { get; }`;
- `void ReportWorkerFailure(Exception error)`;
- `void RequestStop()` — собирает callback errors, не прерывает последующий join;
- `ValueTask<ProcessingJoinResult> JoinAsync(IReadOnlyList<Task> workers, ExceptionDispatchInfo? producerError)`;
- `void Dispose()` — освобождает owned worker CTS после обязательного join;
- `ProcessingJoinResult` internal readonly record struct с
  `ExceptionDispatchInfo? PrimaryError` и `Exception[] SecondaryErrors`.

- [x] Добавить supervisor unit tests: first-worker error, multiple independent
  errors, requested worker-token OCE filtering, cancellation callback throws.
  Утверждать exact exception identities, а не только AggregateException type.
- [x] Выполнить build/tests; отсутствующий supervisor должен дать compile RED.
- [x] Перенести существующий F1 algorithm. Producer wrapper остаётся в executor:
  graceful source/worker-induced cancellation classification и input completion
  сохраняются. ReportWorkerFailure останавливает source и workers независимо
  от callback failure. Join никогда не отменяется external token.
- [x] Executor вызывает JoinAsync после producer finally во всех обычных
  error paths, затем RuntimeCleanup.ThrowCombined с одним captured primary.
  Worker tasks вызывают ReportWorkerFailure через existing PipelineWorker delegate.
- [x] Seal registry только после processing join; root run/observer pump
  не включать в эту ordinary-worker группу. Late finalization/disposal не переносить.
- [x] Build и обе new suites должны пройти; existing F suites остаются green.
- [x] Commit: `refactor(core): extract processing task supervision`.

### Task 3: lifetime integration acceptance

**Files:** Existing Core/Hosting/DI lifecycle tests;
`docs/runtime-contracts.md` (только targeted architecture description).

- [x] Прогнать existing 17/47/37/27/12 gates, correctness/concurrency и DI/Hosting.
- [x] Проверить disabled-policy allocation baseline: supervisor создаётся на
  run/group, не на envelope; sequential sync ValueTask не превращается в Task.
- [x] Полный candidate CI и repository/docs gates из roadmap.
- [x] Commit только необходимые docs/evidence; не выдавать прежний CI за этот SHA.
