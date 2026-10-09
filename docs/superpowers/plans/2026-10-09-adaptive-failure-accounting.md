# F4 — adaptive failure accounting

Base: verified PR #150 head `e63a11dc8b99f1bda64c2ec11d315cbbd445f7cd`,
full GitHub-hosted CI `37881816753` success. Dependency chain: #148/#149/#150.
Main is `44368f1014ca9ce801b0e3d9443ea5aaf6272809`; no merge/release.

## Defect and contract

ProcessEnvelopeWithAdaptiveAdmissionAsync inferred failure from a non-null
FailureAction. StageExecutor intentionally returns null for EmitFailureResult
and DeadLetter: both stop processing this envelope without stopping the pipeline.
These failures therefore looked healthy and could grow adaptive concurrency.
Failure Skip and StopPipeline happened to be classified correctly; FaultPipeline
was counted through the exception path. This is an outcome/control-flow conflation.

Use a separate internal boolean terminal failure signal in StageExecutionResult,
carry it in a value-type envelope outcome, and preserve the worker's existing
FailureAction control flow. No shared mutable per-item flag, new hot-path delegate,
public API or allocation is introduced. The worker admission method still returns
only the action; sequential execution reads the same action directly from the outcome,
without adding an async wrapper.

One admitted envelope yields one sample, not one sample per stage/retry. Terminal
failure counts regardless of policy or stage position. Success after retry and
Filtered/Skipped are healthy. Requested processing-token cancellation is shutdown,
not a dependency error, and must release the lease without creating pressure.
Unexpected processing, sink and output exceptions retain failure classification.

## Implementation and acceptance

- [x] Explicit stage/envelope failure outcome and unchanged worker action contract.
- [x] Preserve lease release on every path; exclude requested cancellation samples.
- [x] Runtime contract documentation.
- [x] 27 bounded tests with a manual clock and real typed executor/controller:
  20 terminal failure cases (5 policies × permanent/exhausted retry × first/second stage),
  6 final-outcome cases (success/filter/recovered retry/cancelled result/timeout/exception),
  1 externally cancelled processing case.
- [ ] Focused 27-case hosted format/build/test gate.
- [ ] Existing 17/47/37 gates and full hosted correctness/concurrency/repeats,
  coverage/stress, package/API/consumer/integration/security/docs acceptance.

Tests observe an internal read-only concurrency diagnostic; there is no public
surface change. The canonical graph fixture uses stateless components and a
borrowed dead-letter stream; lifecycle ownership remains covered by earlier slices.

I1/I2/O1/O2 remain future work; this slice does not change controller mathematics,
thresholds, retry policy, package versions or deployment/release behavior.
