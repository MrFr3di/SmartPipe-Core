# REL221-01 — SmartPipe 2.2.1 release-preparation evidence (2026-10-12)

Tracking: [issue #159](https://github.com/MrFr3di/SmartPipe-Core/issues/159). This is a **preparation checkpoint**, not a claim of published release or successful release-mode dry run.

## Immutable input and scope

- Inspected default-branch commit: `dfe58906bdc98157ee47246a62c81d77ec6bc185`.
- Main CI [#38255442657](https://github.com/MrFr3di/SmartPipe-Core/actions/runs/38255442657) SUCCESS and CodeQL [#38255442431](https://github.com/MrFr3di/SmartPipe-Core/actions/runs/38255442431) SUCCESS apply to that **earlier** head only.
- The release train has 20 package IDs in `eng/package-graph.json`, including the compatibility facade and PostgreSQL integration.
- `SmartPipeVersionPrefix=2.2.1`, `SmartPipePreviousStableVersion=2.2.0` in `eng/SmartPipe.Versions.props`. Do not modify either while preparing 2.2.1.
- Published prior release is `v2.2.0`. Maintain its immutable compatibility evidence and the historical 2.1.2 snapshots.
- I1/I2/O1/O2 and the xUnit package updates were merged through PRs #153–#158 before this checkpoint.
- This PR changes release material, package metadata and one deterministic regression-test synchronization. No production code, public API or immutable baseline changes.

## Preflight findings and corresponding fixes

1. `CHANGELOG.md` used a Development heading, rejected by the release-note extractor. It now has the canonical dated heading for 2026-10-12. **If publication is not on that date, correct the heading to the actual release date before publication.**
2. `docs/releases/2.2.1.md` incorrectly stated no new public feature surface. The 2.2.1 opt-in API exception is already documented by `VERSIONING.md` and verified in `src/SmartPipe.Core/PublicAPI.Unshipped.txt`.
3. `eng/SmartPipe.Package.props` claimed no package-specific public API changes for the entire release. The Core package adds opt-in API and now has corrected package-specific `PackageReleaseNotes`.
4. Migration and compatibility notes now explain null budget/admission defaults, deterministic jitter by default, exact sampling by default, and the partial-bucket approximation limitation.
5. Performance notes link paired measured evidence rather than promising universal p99 or throughput improvement.
6. Review follow-up: DocFX reported eight invalid links to repository assets outside its content root; those references now use absolute GitHub file URLs, also suitable for extracted GitHub Release notes. The preexisting `ObserverInterestRegressionTests.NoObservers_PreservesTelemetryAndSinkFailure` occasionally checked root `ActivityStopped` before the callback fired because `RunCoreAsync` completes the public run before disposing its root Activity. The test now awaits its own actual `ActivityStopped` signal (10-second test timeout), with no production behavior change.

## Mandatory go/no-go checks (not yet complete)

- [ ] New preparation PR exact-head CI, CodeQL, Dependency Review and Documentation (if triggered).
- [ ] Release-mode package graph/metadata/ownership checks and `dotnet restore --locked-mode` on GitHub-hosted runners.
- [ ] Compatibility and consumer validation against *published* 2.2.0 packages (source, old-binary, trim/NativeAOT where configured), complete test suite, stress and concurrency checks.
- [ ] Dated release-note extraction contains **only** 2.2.1 text (no 2.2.0 section). Verify with the release-mode dry run.
- [ ] After merge to an accepted exact `main` SHA, manually dispatch `Publish NuGet` with `version=2.2.1` and `publish_nuget=false`; capture the validated immutable package artifact and Linux/Windows/PostgreSQL replay results. Ordinary CI alone does not satisfy this gate.
- [ ] Before any production dispatch, independently verify the protected `nuget-production` GitHub environment, `NUGET_USER` value, and nuget.org Trusted Publishing policy matching owner `MrFr3di`, repo `SmartPipe-Core`, workflow filename `publish-nuget.yml`, environment `nuget-production`. Environment / nuget.org settings cannot be inferred from source YAML.
- [ ] Separate user-approved decision to dispatch `publish_nuget=true`. No tag or release until the packages are verified and the publisher reaches its final job.
- [ ] Verify 20 `.nupkg` and 20 `.snupkg` identities, checksums, source provenance, real consumer restore, GitHub tag and GitHub Release.

## Release implementation contracts

- The `Publish NuGet` workflow accepts an exact canonical SemVer input and rejects non-main dispatch, duplicate tags, invalid changelog headings, mismatched repository version core and whole-workflow reruns.
- It packs **once** in release mode, uploads an immutable artifact, replays on Windows and PostgreSQL, checks hashes, and publishes in dependency order only when `publish_nuget=true`.
- NuGet Trusted Publishing uses OpenID Connect short-lived credentials, not a stored long-lived API key.
- For failure recovery use the workflow's failed-jobs-only procedure and compare each published payload before skipping. Never rebuild and silently republish a published version.
- Source for official external guidance: https://learn.microsoft.com/en-us/dotnet/fundamentals/package-validation/overview and https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing .

## Remaining risk and stop conditions

- The date 2026-10-12 is the current release candidate heading, **not evidence of publication**; date drift requires a final edit.
- No material API break is expected, but the new branch and release artifact still require their own exact-head evidence. Earlier green builds are insufficient.
- Bucketed sampling has bounded *storage* and less than one bucket-width *temporal overhang*, not a guaranteed failure-ratio or breaker-decision error.
- Failed docs-link checks from transient upstream HTTP 503 must be diagnosed rather than masked by ignoring valid links.
