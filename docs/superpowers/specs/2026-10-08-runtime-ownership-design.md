# Runtime ownership repair: F1/F7

## Intent and scope

Repair the lifecycle defects in the supplied 2026-10-08 Core review on the
2.2.1 servicing line. The user requested analysis, planning and initial
implementation on a separate branch. This slice addresses producer failure and
source-enumerator cleanup; timeout and observer work remain separately gated.
Base: `44368f1014ca9ce801b0e3d9443ea5aaf6272809`.

## Invariants

1. Every parallel worker remains owned until its task terminates.
2. Component cleanup and public Completion cannot precede ordinary worker exit.
3. Producer faults close input and request worker cancellation, including output
   writes waiting for capacity. This internal stop does not request public Cancel.
4. Graceful source stop still drains accepted work; normal completion stays unchanged.
5. Processing/read failure is preserved by identity and stack when it is the only
   failure. Enumerator cleanup errors are retained after that primary cause.
6. Expected worker cancellations caused by internal/public stop do not replace a
   producer fault. Independent worker failures and cancellation-callback errors
   remain visible, and all workers are joined even when cancellation throws.
7. Runtime-owned components are disposed once. Borrowed/scope-owned ownership
   remains defined by the existing activation ledger.

## Chosen implementation

Use a linked worker CTS local to RunParallelProcessingAsync. Start workers with
its token, capture producer exceptions using ExceptionDispatchInfo, complete
input in the existing finally, request worker cancellation on producer/worker
fault, then await Task.WhenAll outside the producer error path. Inspect its full
Exception aggregate to retain independent errors, filtering only requested
worker cancellation. Reset the input reader after this join. Do not add a task
registry to each synchronous stage.

Use RuntimeCleanup.CollectAsync/ThrowCombined for enumerator cleanup in both
PipelineProducer and sequential execution. Preserve existing graceful OCE
classification inside a surrounding primary-error capture boundary.

## Alternatives

- Join workers without cancellation: preserves ownership but may deadlock on a
  bounded output whose caller awaits Completion instead of consuming it.
- Cancel the public run CTS: releases waits but conflates internal producer faults
  with user cancellation and can change terminal state.
- Full run-supervisor rewrite: useful future direction, too broad for this slice;
  first pin the invariants with regressions and reuse existing finalization.

## Verification and limits

Barrier tests exercise transformer/sink cleanup at concurrency 2/8; fault,
cleanup-only, combined and cancellation-plus-cleanup at concurrency 1/2. Follow
with output-backpressure, throwing cancellation callback and independent worker
fault cases. Use SDK 10.0.401 and existing GitHub-hosted CI, including format,
warn-as-error build, Core lifecycle/concurrency, package/API/consumer checks.
No local SDK is available. A callback/operation that never cooperates can delay
ordinary worker shutdown indefinitely; this slice does not pretend cancellation
terminates arbitrary user code. F2 late attempts have a separate timeout contract.

No public API, package graph, version, dependency, compatibility baseline,
release tag or publication changes. No speedup claim without benchmarks.
