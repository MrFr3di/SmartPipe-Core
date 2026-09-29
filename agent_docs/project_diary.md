# Project Diary

Record only durable decisions, discarded approaches, and reusable lessons.

## Decisions and Lessons

- 2026-08-13 — The durable package boundary is Core → Extensions.Json and
  Core → Extensions; the broad Extensions package keeps a JSON compatibility
  forwarding bridge, while JSON implementations live in the leaf package.
  This is documented in `docs/architecture.md` and the project files.
- 2026-08-13 — Runtime contracts deliberately distinguish in-process bounded
  processing from durable/distributed/exactly-once systems. Documentation must
  preserve those limitations.
- 2026-08-13 — The active plan
  `.agent/exec-plans/active/2026-08-13-sp220-05-health-checks.md` records a
  feature branch and checkpoint different from the live checkout (`release/2.2.0`,
  `eb83373`). Reconcile branch/head truth before treating that plan's progress
  as current execution state.
- 2026-08-13 — `Directory.Build.props` and central package versions report
  `2.2.0`, while README install examples still show `2.1.2`; treat the README
  examples as a documentation consistency item, not as current package-version
  evidence.
- 2026-08-13 — Critical review selected RepositoryChecks as the single
  deterministic context compiler: task context, failures-only verification,
  compact evidence, and on-demand diagnostics belong in the existing project.
  This is a plan decision, not an implementation claim.
- 2026-08-13 — Keep task scope in the active ExecPlan and keep only executable
  gate recipes in tracked `eng/verification-profiles.json`; package, baseline,
  ownership, and consumer manifests remain policy truth. Avoid a second task
  manifest and avoid one skill per SP220 plan.
- 2026-08-13 — NuGet audit policy is already explicit and covered by current
  repository validation. Do not add a redundant pruning checker; preserve SDK
  pruning diagnostics and add the workflow after HealthChecks functionality,
  before repeating final acceptance gates.
- 2026-08-14 — SP220-05 uses the existing DI active-run registry as the only
  active source and a per-key monotonic latest-terminal store. Observation
  values deliberately omit exceptions, delegates, service providers, and
  pipeline-run references so cleanup and retention cannot keep runtime graphs
  alive.
- 2026-08-14 — Liveness and readiness are separate canonical HealthChecks
  policies. Readiness reports the selected rule in a bounded description;
  all non-cancellation capture/provider failures are sanitized, while a
  caller-token cancellation remains observable as cancellation.
- 2026-08-14 — Root review fixed cancellation/capacity handling, registration
  rollback, readiness descriptions, observation invariants, workflow test
  count guards, and real-runtime concurrency/failure evidence. The deployment
  is paused before Task 20 final acceptance by explicit user instruction. The
  scoped implementation was committed locally as `60ca5b5`; no push, PR,
  merge, or acceptance claim is made here.
- 2026-08-14 — RepositoryChecks became the deterministic Agent Context API in
  the existing executable. The active ExecPlan remains the sole task-context
  source; profiles compose existing atomic checks; baseline acquisition is
  explicit; consumer failures expose only bounded repository-relative evidence;
  and the reusable workflow replaces only duplicate source gates.
- 2026-08-14 — External upload of arbitrary consumer logs was deliberately
  deferred without bypass: bounded `result.json` artifacts remain uploadable
  and retained logs stay local until a separate explicit approval exists.
- 2026-08-14 — The RepositoryChecks workstream completed its root review/fix
  pass with 543/543 tests, warn-as-error builds, workflow mutation checks,
  repeated-output determinism, blocked-network profile proof, and diff hygiene.
  Task 20 local acceptance subsequently passed. The final local sequence uses
  explicit baseline provisioning followed by offline integrity verification;
  full baseline comparison remains diagnostic because the 2.2.0 tree is not
  expected to match immutable 2.1.2 API/dependency snapshots. Three existing
  test files received whitespace-only formatter changes, and prior package
  artifacts were archived reversibly before fresh packing. Remote security,
  exact-head CI, and checkpoint contribution remain separate gates.
- 2026-08-14 — Remote SP220-05 publication stopped before mutation: the local
  handoff commit `c93ad2ec` is clean and two commits ahead of
  `origin/sp220/checkpoint-c`, but the `MrFr3di` GitHub token is invalid and
  HTTPS push has no credentials. The checkpoint PR route also does not itself
  trigger the required CodeQL, Dependency Review, and PR-only CI lanes. Resume
  only after authentication and the governance route are explicitly resolved.
- 2026-08-15 — The checkpoint trigger gap was corrected locally with the
  minimum change: add `sp220/checkpoint-c` only to the `pull_request` filters
  of CI, CodeQL, and Dependency Review. A YAML 1.2 oracle now locks the exact
  trigger shape and rejects removal or mutation; checkpoint rulesets and other
  event triggers remain unchanged. No remote mutation was performed.
- 2026-08-15 — The authorized SonarCloud follow-up was kept causal and narrow:
  the failed gate was four new issues (regex timeout, manual monitor lifetime,
  and two consumer namespace findings). The fix adds the regex timeout,
  replaces manual monitor ownership with structured locking and rollback, and
  gives both consumer helpers named namespaces. Root review repaired the
  registration rollback/lock ordering race. Local RepositoryChecks, HealthChecks,
  consumer trim/AOT, warn-as-error, format, and diff checks passed; remote
  Sonar must still inspect the new commit before the checkpoint merge.
- 2026-08-15 — A focused DI disposal-race follow-up confirmed that explicit
  concurrent `DisposeAsync` calls share one cleanup task, cancel the run, and
  publish one cancelled terminal observation. The test now awaits the
  cancellation contract before sampling completion, eliminating scheduler
  dependent assertions; the focused loop and full DI suite passed.
- 2026-08-15 — Final exact-head remote validation passed for `b0570b4`:
  CI, CodeQL, Dependency Review, and SonarCloud were green. PR #58 was marked
  ready and merged only into `sp220/checkpoint-c` as `7249f6f3`; release and
  package publication remain separate authorization boundaries.
- 2026-08-15 — Post-merge F1 hardening snapshots all aggregate selection and
  evaluation inputs before capture. The deterministic regression was observed
  RED at expected 1/actual 2 and then GREEN; documentation narrows broad
  artifact claims to aggregate count-only descriptions because per-pipeline
  sanitized descriptions may contain an exact `PipelineKey`. The isolated
  contribution is exactly four tracked files at `b97ff0e` on
  `sp220/05-postmerge-hardening`, with no API, dependency, project, package,
  or workflow changes.
- 2026-08-15 — The post-merge hardening deployment is paused before governance
  mutation. The checkpoint endpoint remains unprotected; ruleset `19148428`
  protects only `release/2.2.0`. The proposed checkpoint ruleset requires
  zero approvals plus an owner emergency bypass because there is exactly one
  collaborator, and the safety layer rejected the POST before action. F4
  `RecordTerminal` cross-check remains deferred as YAGNI, and F5 Dependabot
  main-only updates remain excluded.

- 2026-08-22 — PR #60 was merged as `8739bee`; Checkpoint C was true-merged
  into `release/2.2.0` as `6604355e`; and SP220-07 was true-merged into
  protected Checkpoint D as `97c9fcb5`. Checkpoint D remains the open boundary
  for SP220-08.
- 2026-08-22 — SP220-07 keeps one physical implementation per moved public
  type. Channels, Transforms, Logging, and DataAnnotations are independently
  installable leaves; the broad facade uses type forwarding so existing source,
  binary, and reflection identities remain stable. DataAnnotations retains its
  explicit reflection/RUC boundary and uses Transforms for the NativeAOT-safe
  rule path.
- 2026-08-22 — The final deployment contract requires five current consumers,
  seven reproducible benchmark paths, package graph/ownership metadata, and
  compatibility/trim/NativeAOT checks. Exact-head CI `32583155237`, Dependency
  Review `32583155152`, CodeQL `32583155196`, SonarCloud, CodeRabbit, and all
  bounded cleanup jobs passed at contribution head `14bb61a`.
- 2026-08-22 — Same-repository PR-only routing to the Windows self-hosted
  runner was authorized to preserve the required checks after hosted minutes
  were exhausted. Non-PR hosted routes remain unchanged; Linux PR coverage is
  a documented temporary gap, not a silently weakened contract.
- 2026-08-22 — Closure cleanup is bounded by exact paths and name prefixes:
  deployment worktrees and runner workspace were removed, the runner tool
  cache was preserved, and matching temporary paths were verified absent.
  Unrelated `.codex_workflow_hidden_resources/` and pre-existing `agent_docs/`
  content remain preserved.
- 2026-08-23 — The CI-efficiency deployment is paused on the exact base
  `97c9fcb5e78d4fd5376e28b3c3cc460c600a091c` in worktree
  `C:\Reposit\SmartPipe.Core\.work\wt-ci-efficiency`. Local commit `6ef844c`
  adds exact-SHA diagnostics, fail-fast package/consumer ordering, the
  `--scenario` selector, runner cleanup/install/uninstall/monitor tooling, and
  workflow/fixture contracts; the candidate is clean and one commit ahead of
  `origin/upd-ci-efficiency-self-hosted`.
- 2026-08-23 — Local parser, workflow, PowerShell fixture, diff-hygiene, and
  locked `SmartPipe.RepositoryChecks.Tests` evidence passed (586/586 on the
  frozen candidate); full Core/Extensions suites were intentionally not run.
  Initial live smoke exposed Windows current-directory deletion and
  setup-dotnet permission failures; local repairs are committed, but live
  installation and exact-head acceptance remain pending.
- 2026-08-23 — The runner installer owns only its hook/.env lines and the
  `smartpipe-cleanup-v1` label, preserves unrelated labels and `_tool`, and
  refuses busy or queued/in-progress runners. The next live attempt must first
  confirm one online/idle listener and no queued/in-progress runs; do not
  re-register the runner or change credentials after the observed broker
  session conflict.
- 2026-08-23 — NativeAOT path exhaustion uses `SPCONS025` because
  `SPCONS024` is already emitted by the existing consumer expectation checker.
  This is a deliberate internal diagnostic decision recorded in the active
  plan and must be reconciled with any external acceptance wording before
  merge.
- 2026-08-24 — SP220-08 is deliberately narrow: reusable JSON definitions
  compose the existing Core lifecycle and hardened JSON implementations; only
  the transport-neutral bounded UTF-8 line framer may be extracted. No generic
  I/O, HTTP, retry, scheduler, or second JSON runtime is in scope.
- 2026-08-24 — The deployment paused before implementation because PR #65 at
  `c08466d` is not an accepted exact base and .NET SDK/runtime servicing must
  land separately. The existing clean `6ef844c` runner repair was preserved for
  authorized remote continuation; the sandbox approval/session boundary is not
  evidence that the user lacks external GitHub authorization.
- 2026-08-24 — No JSON source, tests, package metadata, or configuration were
  changed. The next recoverable entry point is fresh PR #65 exact-head gates,
  then servicing acceptance, then a new isolated JSON worktree from the exact
  accepted Checkpoint-D SHA.
- 2026-08-27 — Once the repository was public again, the retired SmartPipe
  self-hosted runner and its attributable runs/logs were removed. Standard
  GitHub-hosted workflows, public CodeQL/Dependency Review, and native
  lock-file-keyed Linux/Windows caches are the current CI boundary; unrelated
  artifacts and the MuxTV runner were preserved.
- 2026-08-28 — The servicing train is intentionally separate from JSON
  behavior: SDK `10.0.303`, the approved Microsoft `10.0.11` cohort, and
  regenerated locks were accepted before JSON work began. Baseline integrity
  remains distinct from immutable historical baseline capture.
- 2026-08-31 — SP220-08 keeps JSON reusable definitions thin: Core owns
  `RuntimeOwned` activation/cleanup, while JSON owns framing policy, probing,
  validation, path diagnostics, logging, and failure taxonomy. Only the
  internal BCL-only UTF-8 line framer is linked from `src/Shared/JsonFraming`;
  resolver-backed metadata is privately snapshotted and re-resolved.
- 2026-08-31 — The benchmark contract uses method-scoped cases and BDN Dry
  validation rather than a class-level Cartesian matrix. The final 21/21 run
  is advisory coverage; deterministic JSON tests own cancellation, disposal,
  rollback, and failure-order correctness.
- 2026-08-31 — Final frozen acceptance passed after targeted lockfile and
  consumer-inventory repairs. PR #70 true-merged Checkpoint-D
  `741892d4`; merged-tree run `33407952886`, promotion PR #71, and release
  head `0439163f` exact CI `33411352721` / CodeQL `33411352437` are green.


- 2026-09-02 — SP220-09 follows the corrected FINAL migration contract rather
  than the older umbrella CSV section: strict APIs are file-only, the facade
  retains CsvHelper for compatibility, legacy types move physically to the leaf,
  and no new `CsvTransform` warning or blanket AOT claim is introduced.
- 2026-09-02 — The strict CSV boundary is deliberately decoded-character and
  RFC4180-oriented. CsvHelper remains responsible for headers, fields,
  conversions, and maps; the framer owns only logical-record boundaries and
  bounded recovery. No generic JSON/CSV framer abstraction was added.
- 2026-09-02 — Source and sink lifecycle defects found by focused regressions
  were repaired at their shared roots: activation creates and reuses one typed
  logger, enumeration keeps the activation cancellation linked, and BOM-only
  append suppresses duplicate preamble emission.
- 2026-09-02 — The repository runner's dotted scenario selector was repaired to
  accept the registered `csv-facade-binary-2.1.2` and
  `csv-facade-source` IDs while rejecting empty dot segments. Both scenarios
  now reach the common preflight, which remains blocked by unrelated
  competitor-benchmark CPM ownership violations.
- 2026-09-02 — Deterministic CSV evidence is strong but intentionally bounded:
  the affected suite is 42/42, fresh package metadata and ownership checks pass,
  and four consumer paths run from the exact fresh feed. The binary facade
  replacement gate, independent tester/reviewer pass, and remote acceptance
  remain open; the deployment is paused rather than overstated as complete.
- 2026-09-10 — The competitor benchmark CPM failure was checkout contamination,
  not tracked SP220-09 debt: the directory is ignored and absent from the release
  and candidate trees. Preserve it untouched and validate the exact candidate in
  a clean LF checkout with locked restore instead of weakening RepositoryChecks.
- 2026-09-10 — Checkpoint E requires the existing 40-scenario manifest contract,
  safe dotted scenario IDs, explicit CSV test execution, and mandatory Windows/
  Ubuntu CSV file jobs. The accepted tree is rebuilt into the eight FINAL logical
  commits only after one clean wide pass, with tree identity proved before push.
- 2026-09-11 — The clean LF clone eliminated the ignored benchmark/CRLF false
  blocker and passed the acceptance prefix through twelve-package pack and graph.
  The first tracked failure is an unhandled null in package-metadata validation;
  stop there, add focused RepositoryChecks regression evidence, and resume the
  remaining gates rather than repeating the entire wide prefix.
- 2026-09-11 — A `git clone --local` rewrites `origin` to a filesystem path,
  which prevents the SDK SourceLink provider from producing GitHub mappings.
  Restore canonical origin and use an explicit CI-equivalent build in disposable
  acceptance clones; keep the validator strict and make missing metadata a
  bounded diagnostic rather than an unhandled null.
- 2026-09-11 — Exact-head package bytes make checked-in consumer content hashes
  inherently stale. Materialize the lock only in the disposable consumer copy
  from its isolated exact feed, then immediately require a locked restore. For
  binary facade scenarios, keep baseline dependency expectations separate from
  the current runtime-replacement closure.

- 2026-09-20 — A build result is only evidence once the tooling state it depends on has been restored: enabling
  `EnableTrimAnalyzer`/`EnableAotAnalyzer` and then building with `--no-restore` leaves the ILLink analyzer
  uninjected, so the green result proves nothing. The same class of false green appeared when a test suite was
  run against a stale executable and when a hardcoded `projects=3` message was read as a count.
- 2026-09-20 — `pack-packages` refuses to overwrite an existing package manifest (`SPPACK002`). A stale
  `artifacts/packages` directory therefore looks exactly like a product failure and can invalidate an otherwise
  valid dependency probe; clear the output directory before re-packing, and treat a pack failure whose output
  line is missing as environmental until the directory state is known.
- 2026-09-20 — Consumer workspaces map only central-package IDs to NuGet sources, so a consumer template may
  reference only packages that have a `Directory.Packages.props` entry. A transitive native dependency such as
  `SQLitePCLRaw.lib.e_sqlite3` is unmapped (`NU1100`) until it becomes a central version referenced by a scanned
  project; `tests/Consumers` is inside the central-package scan, while `artifacts`, `bin`, `obj`, `packages` and
  `Fixtures` are ignored.
- 2026-09-20 — A type forwarder does not satisfy the compiler on its own: a project that consumes a moved type
  through the facade still needs a direct project reference to the target leaf (`CS1069` names the assembly).
  The facade public-API baseline records forwarded members with the `(forwarded, contained in <assembly>)`
  suffix, while the leaf baseline lists them as ordinary members.
- 2026-09-20 — Moving already-shipped code into a project that enables the trim/AOT analyzers can surface ILLink
  diagnostics the original project never evaluated. Suppress them narrowly at the individual reflective member
  with a justification that names the supported alternative; do not annotate the public API, add `NoWarn`, or
  disable analyzers, because that either breaks consumers or hides the boundary.
- 2026-09-20 — For SQLite, a streaming source and a writing sink must not share one database file: holding the
  read connection for the run while the sink writes the same file produces `SQLite Error 5: 'database is
  locked'`. Point the sink at its own store, mirroring the established in/out file shape of the CSV consumers.
