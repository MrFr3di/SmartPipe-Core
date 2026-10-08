# Runtime ownership implementation plan

> **For agentic workers:** Use superpowers:executing-plans for sequential implementation. Record actual RED/GREEN CI results; do not mark unexecuted tests complete.

**Goal:** Repair producer/worker ownership and preserve source-enumerator errors.
**Architecture:** Local linked worker CTS plus unconditional join; existing RuntimeCleanup combines primary and cleanup failures.
**Tech Stack:** C# 14, .NET SDK 10.0.401, xUnit v3/Microsoft.Testing.Platform, GitHub-hosted Actions.
**Spec:** [Runtime ownership design](../specs/2026-10-08-runtime-ownership-design.md).

## Global Constraints

- Separate branch from `44368f1014ca9ce801b0e3d9443ea5aaf6272809`; target main.
- No public API, dependencies, package graph, version or baseline changes.
- Preserve graceful drain and explicit ownership; do not use public Cancel for producer faults.
- GitHub-hosted CI only; all tests have cleanup and bounded failure deadlines.

## Review Focus

- Producer failure while a transformer/sink is inside asynchronous cleanup.
- Workers blocked by output capacity when source faults.
- Cancellation callback throws while worker shutdown is requested.
- Concurrent independent worker faults and producer fault: retain all errors.
- Enumerator cleanup fails after public cancellation: Faulted and both causes.

## Task 1: Reproduce F1/F7

**Files:** `tests/SmartPipe.Core.Tests/Engine/TypedPipelineOwnershipRegressionTests.cs`.
**Interfaces:** Public instance builder/source/transformer/sink and Completion.

- [ ] Add barrier regressions at concurrency 2/8 for transformer and sink; assert cancellation precedes disposal, Completion remains pending during cleanup, one disposal each and original IOException identity.
- [ ] Add read-only, cleanup-only and combined exception regressions at concurrency 1/2; assert exception identity/order and one enumerator disposal.
- [ ] Add cancellation plus enumerator cleanup regressions at concurrency 1/2; assert Faulted and both causes.
- [ ] Publish test-first draft PR; run existing CI and read actual failures before runtime edits. A compiler/format error is not RED evidence.

## Task 2: Preserve enumerator primary cause (F7)

**Files:** `src/SmartPipe.Core/Runtime/Execution/PipelineProducer.cs`, `src/SmartPipe.Core/TypedPipelineRuntime.cs`.
**Interfaces:** Existing `RuntimeCleanup.CollectAsync(IEnumerable<Func<ValueTask>>)` and `ThrowCombined(ExceptionDispatchInfo?, IReadOnlyList<Exception>)`.

- [ ] Capture body failure separately from enumerator cleanup in producer and sequential path.
- [ ] Preserve existing graceful OCE classifier and requested-cancellation behavior.
- [ ] Re-run exception matrix; both failures are present, primary first.

## Task 3: Join parallel workers (F1)

**Files:** `src/SmartPipe.Core/TypedPipelineRuntime.cs`; regression test file from Task 1.
**Interfaces:** `PipelineWorker.RunAsync`, existing input/output channels and finalization.

- [ ] Link a local worker CTS to processingToken; use it for every worker.
- [ ] Capture producer failure; close input in every path; cancel workers on faults and retain callback failures.
- [ ] Join all tasks on every producer exit; inspect full aggregate; omit only requested worker OCE after stop.
- [ ] Reset inputReader and dispose local CTS after join; rethrow original or combined exceptions through RuntimeCleanup.
- [ ] Add backpressure, throwing-callback and independent-worker-failure tests.
- [ ] Run full CI including existing drain/cancel/abort/lifecycle suites, package/API/consumer gates and format.

## Task 4: Review and evidence

**Files:** `docs/runtime-contracts.md`, `docs/maintainers/2.2.1/runtime-review-2026-10-08.md`.
**Interfaces:** CI checks attached to exact candidate SHA, public lifecycle contract.

- [ ] Update current runtime contract: worker join and enumerator error ordering.
- [ ] Review full diff for normal/graceful stop, token lifetime and error ordering.
- [ ] Record exact CI runs and unaddressed F2–F6; update draft PR description to final scope.
- [ ] Keep acceptance pending unless required exact-head checks succeed. No merge or release in this task.
