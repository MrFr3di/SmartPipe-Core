# SP220-18 candidate evidence

Status: implementation and local candidate validation complete; final-head hosted evidence is recorded in draft PR [#115](https://github.com/MrFr3di/SmartPipe-Core/pull/115). Independent maintainer acceptance and owner publishing gates remain open. This report does not accept checkpoint G or authorize publication. [Owner gates and publication sequence](sp220-18-release-readiness.md) remain normative follow-up.

## Scope and provenance

SP220-18 includes the finalized SP220-17 head `1941efbc11051f4947f63f305f2af88f7560dbcc` as an actual ancestor through merge commit `01593c0821d92c8ea3560875f614c5128aad7fc5`; both task PRs target `sp220/checkpoint-g`. The synchronization preserved both histories and had no overlapping file changes. SP220-18 adds package-specific release notes for the other19 IDs, strict release-mode gates, Windows replay of the Linux producer artifact, explicit artifact mode/source-commit verification and rejecting workflow/artifact fixtures. Normal CI remains current mode. No runtime/package dependency/SDK changes, baseline changes, new compatibility suppressions, tag or publication.

Pinned SDK:10.0.303, Linux x64. Local producer source commit: `fcbab31b23ebbc019c5305bbb9b527b642a07d9d`. Later documentation commits require their own exact-head CI; local artifact provenance remains this recorded source commit.

## Verified checks

| Check | Evidence |
|---|---|
| Locked restore | Passed |
| Full Release build, warnings as errors | Passed,0 warnings/0 errors |
| RepositoryChecks | Full635/635,0 skipped in an init/reaping container with the exact pinned SDK; earlier634/635 failed because the child selected a different SDK, corrected by PATH without code changes |
| PostgreSQL integration18.6 |238/238,0 skipped, real server |
| PostgreSQL integration17.11 |238/238,0 skipped, real server |
| Workflow contracts |7 new tests plus all existing workflow mutation cases passed, through the CI PowerShell wrapper; rejecting cases observed failing before implementation |
| Artifact fixtures | Passed: current/release modes, missing/wrong source commit, planned inventory, hash/version/path/link/duplicate/extra/missing archive cases; wrong/missing commit RED observed before fix |
| Actionlint1.7.12 | Passed after normalizing static matrices to YAML sequences; axes and check names preserved |
| Vulnerability/deprecation scan |0 reported vulnerable packages,0 deprecated packages, transitive scope; existing audit policy passed |
| Package metadata | Initial release RED19 SPMETA006; after notes GREEN20 packages |
| Format | Full solution passed after SP220-17 follow-up |
| README/migration links |83 relative file targets checked across changed Markdown and all package READMEs; no missing targets after fixing the PostgreSQL README Core link |

The full local RepositoryChecks run includes all30 ProcessRunner fixtures. Its container used SDK10.0.303 mounted from the workspace and `--init`; there is no repository test exclusion or CI workaround.

## Local immutable candidate

Final immutable20 nupkg/20 snupkg feed passed hash/mode/version/source-commit verification across all40 archives. Manifest SHA256: `ccd5af502c29be044dd373947ba7acb1406d62e545823a2860d42623df6c2698`. Current/release graph(20 active), ownership(157 baseline types), metadata(20) and version(2.2.0) gates passed, as did offline baseline integrity. A preliminary local pack with mixed provenance after a concurrent documentation commit was rejected and preserved separately; it is not this candidate. All70 packed consumers passed:63 non-PostgreSQL scenarios (source/meta/binary/trim/NativeAOT) and7 PostgreSQL scenarios with real18.6. Windows validation comes from remote CI. All seven old-binary scenarios retained the consumer DLL hash through deployment metadata refresh and runtime replacement; independent post-run hashes also matched.

## Remote evidence and acceptance boundary

Final SP220-17 head `1941efbc11051f4947f63f305f2af88f7560dbcc` passed exact-head CI36989255683, CodeQL36989255329 and Dependency Review36989255317 before synchronization. Earlier `bfe755a`/`c505eab` runs remain historical evidence only. After the merge synchronization, SP220-18 requires its own new exact-head CI and release-mode dispatch; no ancestor run is treated as acceptance for the combined candidate.

The final-head run IDs, exact head, producer artifact ID/digest and Windows/PostgreSQL reuse results are maintained in PR #115 to avoid treating a subsequent documentation commit as the previously tested SHA. Initial release dispatch36872841718 at `fcbab31` is historical evidence only. Required independent maintainer approval, release ruleset approval-count verification, environment/ref policy and external NuGet Trusted Publishing verification remain open. See the linked readiness report for observed state and owner actions.

## Deep-review recovery hardening

A later independent review also found that the tag-triggered workflow trusted tag placement procedurally. Publication now fetches `main` and `release/2.2.0` before build credentials are needed and rejects a tag SHA that is not already in `main` or does not contain the current accepted release-branch head. This converts the governance sequence (release PR to main, then tag) into a machine gate.

A later independent review found that `--skip-duplicate` alone was insufficient evidence for a recoverable publication: a 409 only proves that the ID/version already exists, and NuGet Client does not automatically continue to the symbol push when the primary package was skipped as a duplicate. Publication recovery now fails closed before OIDC login unless every already-published primary package matches the immutable producer ZIP payload entry-for-entry, ignoring only NuGet.org's repository-signature entry. If the corresponding symbol package already exists, the same payload comparison is applied to the producer snupkg.

Recovery records verified preflight state for primary and symbol packages, then skips only entries already proven equivalent. Preflight uses real GET downloads with `-SkipHttpErrorCheck -PassThru` rather than relying on HEAD support from the gallery symbol endpoint. Missing primaries are pushed with `--no-symbols`, and missing manifest-listed snupkgs are pushed explicitly. No recovery push uses `--skip-duplicate`: a package appearing after preflight causes the push to fail rather than silently accepting a TOCTOU conflict. Immediately before any normal or recovery push, both the nupkg and sibling snupkg path/hash are rechecked against the immutable manifest. Normal first publication keeps the standard primary-package push. The post-publication gate tracks all20 nupkg and20 snupkg artifacts in one shared15-minute propagation deadline, retries only propagation/transient HTTP states, fails immediately on payload mismatch or unexpected status, and compares every downloaded payload with the immutable producer artifact. The OIDC-derived publish credential is exposed to NuGet Client only through `NUGET_API_KEY`/`NUGET_SYMBOL_API_KEY` environment variables; publish commands do not place it in process arguments.

Regression coverage includes matching signed payload plus rejection of mutated payload, extra entries and multiple `.signature.p7s` entries. Workflow mutation tests reject removal of release-tag ancestry, recovery preflight, GET/response handling, environment-only credentials, symbol hash verification, explicit recovery symbol push, the shared propagation deadline or final primary/symbol payload verification. These changes require fresh exact-head hosted validation; the earlier `cefa0788` release dispatch is historical evidence and does not validate this follow-up.

## Hosted link-check hardening

Exact-head CI run `37008206113`, attempt2, completed all build, package, consumer,
correctness, concurrency, coverage, stress, integration, audit and warning gates
successfully before the Windows documentation link check. That final step failed
only because four external `github.com/.../blob/...` references returned HTTP503
in the same two-second Lychee pass. No repository link or test failure was
reported.

Review of the pinned Lychee action showed that its Linux path already supplies
`${{ github.token }}` by default, while the Windows SHA-verified binary had no
equivalent explicit authentication. The workflow now makes the boundary
consistent and auditable: both lanes use only the ephemeral job-scoped
`github.token` under the workflow's `contents: read` permission, never a
long-lived secret or command-line token. Both lanes also use five retries,
two-second retry waits and maximum concurrency eight to reduce transient
GitHub-host failures without accepting HTTP503 as success.

## Final code review

Fresh read-only whole-branch reviewer found no unresolved Critical/Important code finding after the implemented corrections. An Important omission of OpenTelemetry unit tests was fixed with a mandatory reusable step (local24/24 pass). The initially Minor omission of the new mutation suite from CI was treated as an acceptance gap and fixed in the wrapper with both exit codes enforced. New regressions RED3 failures → GREEN7/7 plus the prior workflow suite and actionlint; removal/optional telemetry step and removal of either Windows or PostgreSQL publication dependencies are rejected. Issue-level triage of the six Sonar findings reported below remains an independent acceptance item rather than being silently classified as nonblocking. Hosted execution and owner configuration remain separate evidence gates.

## Reproduction and retained evidence

Use the pinned SDK and normal workflow commands: locked restore, Release build with `-warnaserror`, full format, native validated `pack-packages --mode release --configuration Release --package-version 2.2.0 --output artifacts/packages --manifest artifacts/packages/manifest.json` into a fresh feed. Run offline baseline integrity and graph/metadata/ownership/version in both current and release modes. Validate the artifact with `-ExpectedMode release -ExpectedCommit <full producer SHA>` before consumers. Run `run-consumers --set current` against that feed with a real PostgreSQL18.6 connection and no exclusions. CI executes both workflow suites through the PowerShell wrapper and artifact fixtures.

Local manifest, all40 package archives, consumer result/log files and unchanged old Consumer.dll files are retained as ignored artifacts; they are not committed or published. Regenerable per-consumer NuGet caches were removed after successful results to fit the workspace disk. A discarded mixed-provenance feed is preserved separately and excluded from all reported consumer results.

SonarCloud Quality Gate for PR #115 remains green and reports0 Security Hotspots. The latest PR summary reports6 New issues after the SP220-17 synchronization/release hardening; issue-level details are not exposed by the current GitHub connector, so this evidence does not classify or dismiss them. Independent review must inspect the linked Sonar issue list before acceptance; no Sonar issue is treated as resolved merely because the aggregate gate is green.

## Local package hashes

| Package | nupkg SHA256 |
|---|---|
| `SmartPipe.Core` | `8423203bd6dfd66e8726562839886fb197acc70ff228b908536ef9f30c671263` |
| `SmartPipe.Extensions.Channels` | `50cc5522fbb6eb29adb5b47a3968064f03327791866af54b31afe7a888e83a2a` |
| `SmartPipe.Extensions.Csv` | `3145866cd152757f8e7044a9bef9aad386dac4429f90f74ede9546ec85c780e7` |
| `SmartPipe.Extensions.Dapper` | `f673a5e7248d839f7ab08782f6f8c2fa0ceac3f08d8c02aabc512fb43d3e99d5` |
| `SmartPipe.Extensions.Transforms` | `e360d77165c511bf24378648523c335066c95545b4f151a9b54c95754416e02b` |
| `SmartPipe.Extensions.DataAnnotations` | `edd8036a0cc43ab3b0b1d0a815c1df07c344fb980082c4c03620d4adb5e5cacc` |
| `SmartPipe.Extensions.DependencyInjection` | `1a39da76207eb3b8eb7c0100589a6647ecbd870add5994c5f497a3b616cf2a63` |
| `SmartPipe.Extensions.EntityFrameworkCore` | `6e4081156f4e6f95d98191392d6ee6230d1c3f95c9874c0711c0f092341b5d8e` |
| `SmartPipe.Extensions.HealthChecks` | `b3359f6ecaac3f0bed16de2f47b1c6b4adbef34d975f64a20f8daf0e01f865b1` |
| `SmartPipe.Extensions.Hosting` | `56eeea501767fbed6eebf995045b070befe5705e295adc86fea4bb60e764418a` |
| `SmartPipe.Extensions.Http` | `50be0e3792e4887e2235bb63f10b41a9d8ab53fef94e484033d636c1f5d974d2` |
| `SmartPipe.Extensions.Json` | `d208232c989dffc1fc1bdeee788edf16bdf8a7ed3fcf5d4d1ecefd07302f58c8` |
| `SmartPipe.Extensions.Http.Json` | `2a919b4973cbb0c001a9d6c391135397cc59eabef4eeff0c7813c16b3d69c47b` |
| `SmartPipe.Extensions.Logging` | `7979e840821099cd3cf2859606dac1592e8781ed1e0359bcc9bd553137525290` |
| `SmartPipe.Extensions.Mapster` | `d0146c674a1c89c158a25f23357f607a2683475b916a950d4b0c67b847c710d4` |
| `SmartPipe.Extensions.OpenTelemetry` | `e7c7c2290492c4e6fcc540dce81123fa5efb5dad7b69fdc61e8c9230958697b9` |
| `SmartPipe.Extensions.Polly` | `289f8cabe48c1831c77e905de2f5fe7c416e3a9008d3d208cc97602b550b3e8a` |
| `SmartPipe.Extensions` | `c2e028367205f05467c934a53b8b43054c83bf7c3c699847fc0c5ca2f602b89c` |
| `SmartPipe.Extensions.PostgreSql` | `291fc0222be7120d4ac20b80ac2e34d59d1af978f2c96fdf66b8a08bf5cf93ab` |
| `SmartPipe.Testing` | `933e590ffe96e8636b6a323af2cc9b1c9082caf8adc8d61be81f834d6da8c737` |

## Old-binary consumer hashes

| Scenario | Unchanged DLL SHA256 |
|---|---|
| `csv-facade-binary-2.1.2` | `bee6debf48429c2dd07ca16ce998ce7d206469c94550a01e2eca9d87b46677a3` |
| `dapper-facade-binary-2.1.2` | `1e263316ea0ce487750f0355eb9b251b228a76385a6f71505c0b77c0e719d2c2` |
| `dependency-injection-facade-binary-2.1.2` | `4bc64c626becf95f4d5284a3f3edc54e4af15a05a82b335a6437241183dc1eda` |
| `entity-framework-core-facade-binary-2.1.2` | `79f1d350c2e1cc7c6ee926ede33942440844a9b9edd4f3eb10cedf1b9dbba700` |
| `hosting-facade-binary-2.1.2` | `07e2bfa62e1be3d591bd3467ea01ecb99a14baa8ba5478feeef847362525c68c` |
| `legacy-binary-2.1.2` | `245e7e50097d2a8104d33e4b7310fd20a2ea8c5a3276971edc43015167de8425` |
| `mapster-facade-binary-2.1.2` | `7f43947b586fc1edf0d35f4c8ec2df483331db718e56c68d8cddaba598352cc6` |
