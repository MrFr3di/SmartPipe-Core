# SP220-18 release readiness

Implementation and candidate validation do not accept checkpoint G or authorize publication. Acceptance is governed by the [branch/review policy](../../governance/2.2.0-branch-and-review-policy.md). See [candidate evidence](../evidence/sp220-18-evidence.md) and the [implementation plan](../plans/SP220-18-implementation.md).

## Owner verification (2026-10-02)

Read-only GitHub inspection found:

| Gate | Observed state | Required follow-up |
|---|---|---|
| Release ruleset | ID19148428, `release-2.2.0-protection`, rechecked active on2026-10-02 for exact `release/2.2.0`; deletion/non-fast-forward blocked; PR, latest push, conversation resolution and strict current status checks required | Required approving review count is still0. Enforce at least1, or2 for CI/security changes when a second qualified reviewer is available, as required by repository policy |
| Required status contexts | `validation / build-test-pack`, `json-file-windows`, `Baseline contract (Windows)`, `dependency-review`, `analyze`, `CodeQL` | Verify actual final-head check names/results before promotion |
| Bypass | Owner actor271974079, always | Any use requires the existing emergency bypass audit; this task uses no bypass |
| Release-tag authorization | Repository ruleset inventory contains branch-target rulesets only; no tag-target rule protects `v*`/`v2.2.0` | Add an owner-controlled tag rule and/or enforce `nuget-production` required reviewers plus a release-tag ref policy before publication; ancestry validation proves the commit, not the actor's authorization |
| `nuget-production` | Last readable inspection on2026-10-01 found the environment present with `protection_rules: []` and no deployment branch policy; the current connector cannot re-read that endpoint | Owner must re-verify and enforce release approval/ref restrictions before publication; environment existence alone provides no reviewer/ref gate |
| NuGet identity | Secrets metadata query returned403 (`Resource not accessible by integration`) | Verify `NUGET_USER` exists without exposing its value |
| NuGet Trusted Publishing | External account policy was not available to inspect | Verify package ownership and trusted repository `MrFr3di/SmartPipe-Core`, workflow `publish-nuget.yml`, environment `nuget-production`; verify ref claims for `v2.2.0` |
| Human review | Task PRs require independent maintainer approval | API/package/documentation review for SP220-17; CI/release/security review for SP220-18. Automated review does not replace approval |

Security settings were not changed. The release branch has active protection; the numeric review-count discrepancy, missing tag-target authorization, environment protection/ref policy and external publishing policy remain open owner gates.

## Candidate validation and publication sequence

1. Accept SP220-17 first, then SP220-18 into `sp220/checkpoint-g` through reviewed merges. The SP220-18 branch already contains final SP220-17 head `1941efbc11051f4947f63f305f2af88f7560dbcc` as a real ancestor through merge `01593c0821d92c8ea3560875f614c5128aad7fc5`; merging #114 first lets the #115 diff collapse to its SP220-18-only delta without rewriting history.
2. Dispatch `ci.yml` on the exact G candidate ref with `release-validation=true`; require matching `headSha` and successful Linux producer, full Windows replay, all consumers, PostgreSQL18.6/17.11 integration, baseline, audits and required security checks. Preserve run IDs, producer artifact ID/digest and package manifest. A later merge requires validation of its new SHA.
3. Review/promote G to `release/2.2.0`, validate the resulting exact head, then review the final release PR to `main`. Preserve merge history. Do not reuse ancestor-head results as final-head evidence.
4. Once owner gates and release approvals are satisfied, the separately authorized release action creates tag `v2.2.0` at the accepted release commit. The publication workflow fetches `main` and `release/2.2.0` and fails unless the tag SHA is already contained in `main` and contains the current `release/2.2.0` head. `eng/validate-release-version.sh` and RepositoryChecks additionally enforce version2.2.0 and the complete20-ID graph.
5. The tag-triggered publication workflow creates one release-mode Linux package artifact. Windows and PostgreSQL download its exact ID, validate manifest hashes/version/mode/source SHA, and run packed consumers. Windows uploads reports only. The publisher waits for all three validations, downloads that same artifact and revalidates it before obtaining OIDC credentials in `nuget-production`.
6. Publish those manifest-listed20 nupkg files in `publishOrder`; immediately before each operation, recheck both primary and symbol archive hashes from the immutable manifest. Never repack in replay or publisher. After publication, verify all20 nupkg and20 snupkg artifacts within one shared15-minute NuGet propagation deadline and compare each ZIP payload with the immutable producer package, ignoring only the single permitted NuGet repository-signature entry. Default publication rejects duplicates. An explicitly selected recoverable rerun GET-downloads any already-published nupkg/snupkg before OIDC login and records only payload-equivalent entries as published. Recovery skips those verified entries, pushes each missing primary with automatic symbols disabled, and pushes each missing manifest-listed snupkg explicitly. It does not use duplicate suppression: a new conflict after preflight fails closed instead of accepting a race, and an already-existing primary cannot suppress recovery of missing symbols.

The pre-tag CI artifact proves the candidate. Publication creates its own single validated artifact at the accepted tag SHA; the same-artifact guarantee is within that publication run. This task creates neither a tag nor a NuGet publication.
