# F3 — buffered observer shutdown

## Scope and evidence

Base: PR #149, commit `78615165560f278337158a59dd51ba75fddc9462`, full
GitHub-hosted CI `37876995823` passed. Depends on PR #148/#149; main remains
`44368f1014ca9ce801b0e3d9443ea5aaf6272809`. No merge/release in this slice.

The runtime flushes with CancellationToken.None before dispatcher disposal.
Buffered callbacks previously used the worker CTS, cancelled only by disposal.
A callback awaiting its token could therefore hold flush forever. Reliable queue
backpressure can also hold processing before finalization. Dispatcher Dispose
previously awaited an already-started Complete before signalling cancellation,
creating the same circular dependency independently of runtime.

## Design

1. Separate callback CTS from worker CTS; link callback cancellation to the
   runtime root stop token and worker disposal token.
2. Cancel/Abort/Dispose/external run cancellation release callbacks immediately,
   independently of queue capacity. Processing failure signals callback stop
   before flush. Drain uses source-only cancellation and preserves observer delivery.
3. Expected callback cancellation skips that callback and continues remaining
   recipients/messages/barriers. Worker cancellation remains dispatcher teardown.
   Unrequested OperationCanceledException remains an observer failure.
4. Terminal events retain queue ordering and use the cancelled callback token on
   immediate stop. Cooperating recipients may skip; token-independent recipients
   still receive terminal events with flush enabled. No-flush remains best effort.
5. Dispose cancels before joining concurrent Complete. Aggregate callback errors
   only after owned joins; dispose both CTS instances afterward.

## Implementation and acceptance

- [x] Internal dispatcher stop operation and linked callback lifetime.
- [x] Runtime fault-before-flush stop; root token integration.
- [x] Concurrent Complete/Dispose ordering and cancellation-error-safe join.
- [x] Runtime delivery contract documentation.
- [x] 22 deterministic, bounded regression cases with explicit barriers and no sleeps:
  15 immediate stop/fault cases (3 configurations × 5 stop origins),
  3 drain cases, 2 concurrent dispatcher Complete/Dispose cases,
  2 throwing cancellation callback cases.
- [ ] Format/build and focused 22-case GitHub-hosted CI gate.
- [ ] Existing 17/47 ownership gates, full correctness/concurrency/repeated
  concurrency/coverage/stress, package consumers and security/docs checks.

No public API/baseline, dependency, package-version or observer failure-policy changes.
F4 adaptive failure accounting remains the next separate slice.
