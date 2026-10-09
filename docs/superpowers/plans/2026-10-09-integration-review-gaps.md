# Integration review R1/R2

Base: bb30a5a76d8a9f8f7f99f100c7d8528e5635b92c, PR #151.
Detailed review and integration stop gates: PR #151.

## Tasks and contract

1. Hosted RED: reliable capacity 1 terminal stage fault (concurrency 1/2, structured/task fault); explicit Borrowed/ScopeOwned timed attempts after finalization budget, task/structured late faults. Eight bounded cases must fail for deadlock/lost observation, not build/format errors.
2. R1: establish terminal fault before bounded observer notification. Evaluate retry once per attempt; maintain notification ordering, recoverable retries, Drain and primary-error preservation. Audit sink fault reporting.
3. R2: join late-attempt observation on disposal independently of ownership; Core disposal rights unchanged. Add real scoped DI lifecycle coverage.
4. Hosted GREEN: focused and full existing gates, packages/consumers/security/docs and repeated concurrency. Fresh final review. No merge/release.

## Execution ledger

Pre-flight: R1 callback stop and R2 observation joins share shutdown but use separate ownership boundaries. Neither may cancel graceful processing or dispose external components.
Ruling: clean managed checkout on a new feature branch, no extra worktree or local dotnet. Hosted baseline CI 37885828410 is green on exact base.
Task 1: eight regression cases prepared; RED pending.
