# Timed attempt ownership plan (F2/F5/F6)

Base: `8951a4db1312bda2e21417cfe6c2745f22b736e5`, verified by CI run `37815529580`.
Branch: `fix/timed-attempt-ownership-2026-10-08`, depends on PR #148.

## Design

Cancelling a wait does not finish the underlying stage execution. Transfer its CTS and execution to the late-attempt registry before propagating caller cancellation or a cancellation-callback error. Registry observation owns the CTS until execution exits. Completion respects the finalization budget; disposal waits for deferred components without changing an already published Completion outcome.

Existing detached timeout outcomes retain their suppression of late stage errors. Attempts abandoned because of caller cancellation or another wait failure retain unexpected task faults and structured stage failures. Record errors before removing an attempt and wait for observation bookkeeping, so cleanup cannot race error collection. Consume each late fault once through Completion or subsequent deferred disposal.

Infinite cooperative grace means wait until completion or caller cancellation. Finite timeout durations accept zero through 4,294,967,294 milliseconds; also accept the exact InfiniteTimeSpan sentinel and null optional budgets. Reject other negative values, oversized budgets and undefined retry modes before activation. No public API, dependencies, version or baseline changes.

## Implementation and validation

- [x] Add 19 regression cases with fake time, barriers and bounded failure deadlines.
- [x] Clean RED: commit `1475fd8d217a1d3c4a3d47aa3048ae14d76447c9`, CI `37819739713`, format/build success; 19 cases, 14 failed, 5 passed. Earlier fixture compiler failures are not RED evidence.
- [x] Transfer every abandoned timed execution to registry ownership.
- [x] Preserve cancellation-origin task and structured-result failures through finalization/deferred disposal.
- [x] Correct infinite grace and validate policy snapshots.
- [ ] Extend registry race/callback coverage and run full hosted CI.
- [ ] Review diff and attach exact candidate evidence. No merge or release.

Expanded candidate coverage: 41 integration/policy cases (Cancel/Abort/Dispose, concurrency 1/2, cancellation before timeout/in grace, callback errors and deferred cleanup) plus 5 registry fault-observation cases. Full CI acceptance pending.


Review follow-up: caller cancellation during grace is classified by caller token,
including completion racing the cancelled wait. A custom timer completes execution
while the cancellation promise cleans up, covering both task and structured faults.
Disposal collects cancellation-callback errors and continues through run join and
deferred cleanup; source-origin and stage-origin throwing callbacks are covered.
Candidate now contains 47 timed integration/policy/race cases and 5 registry cases.

Candidate CI `37820874428` stopped before build because the generic workflow
contract accepted the literal substring `--minimum-expected-tests 1`, inadvertently
accepting 19 but rejecting 41. It now checks a positive integer, with zero,
negative and malformed-value rejection mutations. Exact-head GREEN still pending.
