# SP220-18 release readiness

Implementation and candidate validation do not accept checkpoint G or authorize publication. Acceptance is governed by the [branch/review policy](../../governance/2.2.0-branch-and-review-policy.md). See [candidate evidence](sp220-18-evidence.md) and the [implementation plan](../../plans/2.2.0/SP220-18-implementation.md).

## Owner verification (2026-10-01)

Read-only GitHub inspection found:

| Gate | Observed state | Required follow-up |
|---|---|---|
| Release ruleset | ID19148428, `release-2.2.0-protection`, active, exact `release/2.2.0`; deletion/non-fast-forward blocked; PR, latest push, conversation resolution and current status checks required | Required approving review count is0. Confirm/enforce at least1, or2 for CI/security changes when a second qualified reviewer is available, as required by repository policy |
| Required status contexts | `validation / build-test-pack`, `json-file-windows`, `Baseline contract (Windows)`, `dependency-review`, `analyze`, `CodeQL` | Verify actual final-head check names/results before promotion |
| Bypass | Owner actor271974079, always | Any use requires the existing emergency bypass audit; this task uses no bypass |
| `nuget-production` | Environment exists; `protection_rules: []`; no deployment branch policy | Owner must decide and verify release approval/ref restrictions; environment existence alone provides no reviewer/ref gate |
| NuGet identity | Secrets metadata query returned403 (`Resource not accessible by integration`) | Verify `NUGET_USER` exists without exposing its value |
| NuGet Trusted Publishing | External account policy was not available to inspect | Verify package ownership and trusted repository `MrFr3di/SmartPipe-Core`, workflow `publish-nuget.yml`, environment `nuget-production`; verify ref claims for `v2.2.0` |
| Human review | Task PRs require independent maintainer approval | API/package/documentation review for SP220-17; CI/release/security review for SP220-18. Automated review does not replace approval |

Security settings were not changed. The release branch has active protection; the numeric review-count discrepancy and external publishing policy remain open owner gates.

## Candidate validation and publication sequence

1. Accept SP220-17 and SP220-18 task PRs into `sp220/checkpoint-g` through reviewed merges. SP220-18 is stacked on SP220-17 until that dependency merges.
2. Dispatch `ci.yml` on the exact G candidate ref with `release-validation=true`; require matching `headSha` and successful Linux producer, full Windows replay, all consumers, PostgreSQL18.6/17.11 integration, baseline, audits and required security checks. Preserve run IDs, producer artifact ID/digest and package manifest. A later merge requires validation of its new SHA.
3. Review/promote G to `release/2.2.0`, validate the resulting exact head, then review the final release PR to `main`. Preserve merge history. Do not reuse ancestor-head results as final-head evidence.
4. Once owner gates and release approvals are satisfied, the separately authorized release action creates tag `v2.2.0` at the accepted release commit. The publication workflow fetches `main` and `release/2.2.0` and fails unless the tag SHA is already contained in `main` and contains the current `release/2.2.0` head. `eng/validate-release-version.sh` and RepositoryChecks additionally enforce version2.2.0 and the complete20-ID graph.
5. The tag-triggered publication workflow creates one release-mode Linux package artifact. Windows and PostgreSQL download its exact ID, validate manifest hashes/version/mode/source SHA, and run packed consumers. Windows uploads reports only. The publisher waits for all three validations, downloads that same artifact and revalidates it before obtaining OIDC credentials in `nuget-production`.
6. Publish those manifest-listed20 nupkg files in `publishOrder`, with per-file hash checks; never repack in replay or publisher. After publication, download every NuGet.org nupkg and snupkg and compare each complete ZIP payload with the immutable producer package, ignoring only NuGet.org's repository-signature entry. Default publication rejects duplicates. An explicitly selected recoverable rerun first checks any already-published nupkg and snupkg against the producer payload before OIDC login and records that verified state. Recovery skips only entries already proven equivalent, pushes each missing primary package with automatic symbols disabled, and then pushes each missing manifest-listed snupkg explicitly. It does not use duplicate suppression: a new conflict after preflight fails closed instead of accepting a race, and an already-existing primary package cannot suppress recovery of a missing symbol package.

The pre-tag CI artifact proves the candidate. Publication creates its own single validated artifact at the accepted tag SHA; the same-artifact guarantee is within that publication run. This task creates neither a tag nor a NuGet publication.
