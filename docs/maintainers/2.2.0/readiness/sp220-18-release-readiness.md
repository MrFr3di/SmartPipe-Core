# SP220-18 release readiness

Implementation and candidate validation do not by themselves authorize
publication. Acceptance is governed by the
[branch/review policy](../../governance/2.2.0-branch-and-review-policy.md).
See [candidate evidence](../evidence/sp220-18-evidence.md) and the
[implementation plan](../plans/SP220-18-implementation.md).

SmartPipe is a solo-maintainer repository. Human approval counts are therefore
not a release gate; reviewable pull requests, exact-head machine/security
checks, protected history, explicit release dispatch and immutable artifacts are
the independent controls.

## Owner verification (2026-10-04)

Read-only GitHub inspection found:

| Gate | Observed state | Required follow-up |
|---|---|---|
| Release ruleset | ID `19148428`, `release-2.2.0-protection`, active for exact `release/2.2.0`; deletion/non-fast-forward blocked; PR, conversation resolution and strict current status checks required. Live `require_last_push_approval=true` is incompatible with solo maintenance | Keep approval count `0`; set `require_last_push_approval=false` and `dismiss_stale_reviews_on_push=false`; remove normal-path owner bypass if administratively feasible |
| Required status contexts | `validation / build-test-pack`, `json-file-windows`, `Baseline contract (Windows)`, `dependency-review`, `analyze`, `CodeQL` | Verify the actual final-head check names/results on each promotion PR |
| Checkpoint G release validation | CI #428, `workflow_dispatch`, exact SHA `a8fec4c5d970f492807b9bed745c12d6743373e3`, success | This proves the pre-policy-change G candidate only. Re-run exact-head release validation after this release-flow slice merges because the candidate SHA and release workflow change |
| Release-tag immutability | Repository ruleset inventory contains branch-target rulesets only; no tag-target rule protects `v*` | Add an active tag ruleset for `refs/tags/v*` blocking update, deletion and non-fast-forward before publication |
| `nuget-production` | Last readable inspection on 2026-10-01 found the environment present with `protection_rules: []` and no deployment branch policy; the current connector cannot re-read that endpoint | Restrict deployment to the intended main-driven release workflow/ref. Do not require a nonexistent second reviewer |
| NuGet identity | Secrets metadata query returned 403 (`Resource not accessible by integration`) | Verify `NUGET_USER` exists without exposing its value |
| NuGet Trusted Publishing | External account policy is not inspectable through this connector | Verify owner `MrFr3di`, repository `SmartPipe-Core`, workflow `publish-nuget.yml`, environment `nuget-production` |
| GitHub Release immutability | Not verifiable through the current connector | Enable/verify immutable releases before publishing 2.2.0 |

The previous discrepancy that treated `required_approving_review_count = 0`
as a blocker is intentionally removed. Zero approvals matches the actual
single-maintainer repository model. Security gates are not weakened: publication
still requires exact source/version validation, immutable package artifacts,
Windows/PostgreSQL replay, audit checks and explicit release authorization.

## Checkpoint G evidence

SP220-17 and SP220-18 were accepted into `sp220/checkpoint-g` through true
merges `a47f5a671a621756180099c45480dee329cbe4c3` and
`1c442036a5d33b0e1fa83c002e13f9a9e2c1a893`. Release-document finalization
then merged as PR #119, producing exact checkpoint SHA
`a8fec4c5d970f492807b9bed745c12d6743373e3`.

CI #428 performed the required explicit release-mode dispatch against that exact
SHA and succeeded. Its immutable evidence included:

- producer package artifact ID `11295109708`, digest
  `sha256:0e5398c269934928c317400c91cf87443e9a75813b848a3695701184f3a3c437`;
- Windows release-validation reports ID `11295454409`, digest
  `sha256:59766de0661e4aff9d8c9a711c7abd4f023ff7ddb4ce2c70417f2eb41752a09a`;
- PostgreSQL consumer results ID `11295787564`, digest
  `sha256:d6470a39994dffb7259b15990475796105e56a87b0c1ca2c72da082219b54307`.

That evidence is valid for `a8fec4c5...` only. Any later merge, including the
solo-maintainer release-flow change, creates a new candidate SHA and requires
new exact-head evidence.

## Candidate validation and publication sequence

1. Complete this governance/release-flow slice in a reviewable pull request to
   `sp220/checkpoint-g`; require exact-head CI, CodeQL, Dependency Review and
   Documentation.
2. After merge, dispatch `ci.yml` on the new exact G head with
   `release-validation=true`; require matching `headSha` and successful
   Linux producer, full Windows replay, consumers, PostgreSQL 18.6/17.11
   integration, baseline and audits.
3. Promote G to `release/2.2.0` through a true-merge pull request, then validate
   that resulting exact release-branch head. Do not reuse ancestor evidence.
4. Merge the final reviewed `release/2.2.0 -> main` release-preparation change.
   The final release commit must contain the dated `CHANGELOG.md` section for
   2.2.0 and retain repository/package-graph version `2.2.0`.
5. Dispatch `publish-nuget.yml` from that exact `main` commit with
   `version=2.2.0` and `publish_nuget=false`. This is the dry run: it performs
   full release validation and creates no NuGet publication, tag or GitHub
   Release.
6. After inspecting the dry-run evidence and closing the owner/admin gates,
   dispatch the same workflow from the same accepted `main` state with
   `version=2.2.0` and `publish_nuget=true`.
7. The publication run builds the release-mode producer artifact once. Windows
   and PostgreSQL replay that exact artifact. The publisher revalidates it,
   obtains the short-lived NuGet credential only after validation, and publishes
   the manifest-listed 20 nupkg/snupkg pairs in `publishOrder`.
8. Only after NuGet publication succeeds does the final release job create
   immutable tag `v2.2.0` on the exact workflow SHA, create a draft GitHub
   Release, attach the validated packages/checksums, use the exact
   `CHANGELOG.md` 2.2.0 section as release notes, and publish the draft.

Normal publication rejects duplicate package identities. If publication fails
after publishing any subset, use GitHub **Re-run failed jobs** on that same
release run. The successful producer/replay jobs are not rerun, and the publisher
may skip only already-published primary/symbol artifacts whose downloaded
payload is proven equivalent to the immutable producer artifact. **Re-run all
jobs** is rejected by the release request gate. Recovery never rebuilds 2.2.0,
never moves the tag, and fails closed on any mismatch or publication race.

## Publication authorization

Publication authorization is not a tag push and is not a second-person review.
It is the explicit maintainer action:

```text
publish-nuget.yml
ref: main
version: 2.2.0
publish_nuget: true
```

The workflow must refuse non-`main` dispatches, version drift, missing/undated
release notes, incompatible publication state, or artifact provenance mismatch.
A manually created tag must never be able to start NuGet publication.
