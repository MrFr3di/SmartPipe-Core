# Project Progress

Heavy deployment `sp220_09_checkpoint_e_20260911_c` is active at the first
clean-clone acceptance failure. The active ExecPlan is
`.agent/exec-plans/active/2026-09-02-sp220-09-csv-final.md`; its FINAL
migration contract and the live worktree are authoritative.

## Goal

Deliver the 2.2.0 CSV package split: a narrow independently installable
`SmartPipe.Extensions.Csv` with bounded strict file source/sink definitions,
while preserving the namespaces, constructors, defaults, and warning behavior
of the three legacy CSV types through physical leaf ownership and facade type
forwarding.

## Overall Progress

The strict source/sink implementation, legacy move, facade bridge, package
manifests and locks, consumer templates, migration documentation, parser
selector repair, and deterministic focused coverage are present in the local
candidate. The accepted release base is `0439163f43391f219a70a9008e7dafa591d4ae35`.
The current user request authorizes completion through Checkpoint-E merge and
merged-SHA evidence. Release promotion and package publication remain excluded.

## Current Position

- Branch: `sp220/09-csv-integration`; the candidate was based on accepted
  release head `0439163f`.
- Affected CSV build and tests are green; the candidate has not passed the
  final binary facade scenario, clean wide acceptance, or independent review.
- Local safety tag `safety-sp220-09-pre-reconcile-c2d7a27` preserves exact
  snapshot `c2d7a277`. The workflow-managed `.gitignore` change remains
  unrelated, unstaged, and protected.
- The overall deployment is not complete. Checkpoint E remains open for this
  work and later SP220-10/11/12 contributions.

## Verification

Focused implementation evidence recorded in the active ExecPlan:

- warning-free leaf build and CSV project tests: 42/42;
- legacy CSV source/sink tests: 13/13; legacy transform: 1/1;
- repaired JSON lifecycle tests: 2/2; existing JSON definition contracts: 11/11;
- package projects: 3; graph: 19 total, 12 active, 7 planned;
- fresh-feed direct, DI-composition, facade-source, and trimmed diagnostic
  consumers print `CONSUMER_OK`;
- `git diff --check` and edited JSON parsing pass.

The contaminated local checkout historically stopped the runner at `SPCONS019`
because it sees the ignored `benchmarks/SmartPipe.CompetitorBenchmarks/`
directory. The clean exact-SHA clone proves that directory is absent from the
tracked candidate and release base; it is not a product or CPM blocker. The
tracked integration gaps are repaired: schema/oracle now require the exact 40
scenarios and safe dotted IDs; Checkpoint-E PR/push filters, the
reusable CSV suite, and mandatory Windows/Ubuntu CSV file jobs are present.
The focused Python oracle, PowerShell wrapper, JSON/YAML parsing, and diff
hygiene pass.

## Next Milestone

Candidate `22da905b`, tree `1b5cc822`, is frozen and its clean LF clone passed
restore, workflow, format, build, profiles/locks, all required CSV/legacy/JSON
regressions, pack 12, baseline/projects, and graph `19/12/7`. The first tracked
failure was an unhandled null in `verify-package-metadata`; the shared
handle/null repair passes 1/1 and its validator class passes 12/12. The repaired
command exposed that the `--local` clone's filesystem-path origin produced PDBs
without SourceLink. Restoring canonical GitHub origin and performing an explicit
CI-equivalent rebuild makes all 12 package metadata/SourceLink checks pass.

Candidate `cd68f179`, tree `8c43f7c4`, passes pack/Package Validation for 12,
metadata/SourceLink for 12, and ownership for 157. The runner now materializes
commit-sensitive lock hashes only in each disposable workspace from the exact
isolated feed, then proves a second locked restore; tracked locks stay unchanged.
All five CSV consumers pass with dependency counts 2/3/10/3/2, including the
facade binary-2.1.2 replacement and trim diagnostic. Vulnerable/deprecated audit,
audit policy, lock verification, clone cleanliness, and diff hygiene pass.

Freeze the combined runner/manifest/oracle/handoff tree and replay the focused
tail at its exact SHA. Then tag it, reconstruct the eight FINAL commits, review,
and complete Checkpoint-E remote evidence. No remote mutation has occurred.
