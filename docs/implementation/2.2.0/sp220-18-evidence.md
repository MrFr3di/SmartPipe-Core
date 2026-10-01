# SP220-18 candidate evidence

Status: implementation prepared; local candidate validation in progress, exact-head remote validation and independent maintainer acceptance pending. This report does not accept checkpoint G or authorize publication. [Owner gates and publication sequence](sp220-18-release-readiness.md) remain normative follow-up.

## Scope and provenance

SP220-18 is stacked on SP220-17 (draft PR [#114](https://github.com/MrFr3di/SmartPipe-Core/pull/114)); both target `sp220/checkpoint-g`. It adds package-specific release notes for the other19 IDs, strict release-mode gates, Windows replay of the Linux producer artifact, explicit artifact mode/source-commit verification and rejecting workflow/artifact fixtures. Normal CI remains current mode. No runtime/package dependency/SDK changes, baseline changes, new compatibility suppressions, tag or publication.

Pinned SDK:10.0.303, Linux x64. Local producer source commit: `76398f058f4e8d4e5ce924ad5fcd74c8e20633c1`. Later documentation commits require their own exact-head CI; local artifact provenance remains this recorded source commit.

## Verified checks

| Check | Evidence |
|---|---|
| Locked restore | Passed |
| Full Release build, warnings as errors | Passed,0 warnings/0 errors |
| RepositoryChecks | Full635/635,0 skipped in an init/reaping container with the exact pinned SDK; earlier634/635 failed because the child selected a different SDK, corrected by PATH without code changes |
| PostgreSQL integration18.6 |238/238,0 skipped, real server |
| PostgreSQL integration17.11 |238/238,0 skipped, real server |
| Workflow contracts |5 new tests plus all existing workflow mutation cases passed; rejecting cases observed failing before implementation |
| Artifact fixtures | Passed: current/release modes, missing/wrong source commit, planned inventory, hash/version/path/link/duplicate/extra/missing archive cases; wrong/missing commit RED observed before fix |
| Actionlint1.7.12 | Passed after normalizing static matrices to YAML sequences; axes and check names preserved |
| Vulnerability/deprecation scan |0 reported vulnerable packages,0 deprecated packages, transitive scope; existing audit policy passed |
| Package metadata | Initial release RED19 SPMETA006; after notes GREEN20 packages |
| Format | Full solution passed before SP220-17 follow-up; final check pending |
| README/migration links |68 relative file targets checked before final evidence links; final check pending |

The full local RepositoryChecks run includes all30 ProcessRunner fixtures. Its container used SDK10.0.303 mounted from the workspace and `--init`; there is no repository test exclusion or CI workaround.

## Candidate gates pending completion

Final immutable20 nupkg/20 snupkg feed, manifest hashes, current/release graph/ownership/metadata/version and offline baseline integrity will be recorded after the final pack. Full70 packed consumers include63 non-PostgreSQL scenarios (source/meta/binary/trim/NativeAOT) and7 PostgreSQL scenarios with18.6. Windows validation must come from remote CI.

## Remote evidence and acceptance boundary

SP220-17 ancestor `bfe755a` passed Windows CI36863358246, Linux CI36863356844, CodeQL and Dependency Review. Its SonarCloud findings are addressed by follow-up `c505eaba99bc054853ad4392448cdb76e053683f`; exact-head reruns are pending. These ancestor runs are historical SP220-17 evidence, not SP220-18 candidate acceptance.

SP220-18 remote run IDs, exact head, producer artifact ID/digest and Windows/PostgreSQL reuse evidence are pending. Required independent maintainer approval, release ruleset approval-count verification, environment/ref policy and external NuGet Trusted Publishing verification remain open. See the linked readiness report for observed state and owner actions.
