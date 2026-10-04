# Solo-maintainer release governance and GitHub Release design

Status: Approved design
Date: 2026-10-04
Scope: SmartPipe 2.2.0 release finalization and reusable post-2.2 release flow

## Problem

SmartPipe currently combines a solo-maintainer repository with governance text that requires independent human approvals. The live release ruleset therefore either contradicts the repository policy or requires repeated owner bypasses even when every machine gate is green. This creates ceremony without adding an independent security principal.

The current publication workflow is also tag-driven. A pre-existing `v*` tag starts NuGet publication, while GitHub Release creation and curated release notes are outside the workflow. As a result, the package publication and GitHub Release surfaces are not one atomic, source-controlled release process.

ChunkShift demonstrates a better fit for this repository owner model: zero required human approvals, mandatory automated gates, manual release dispatch from `main`, one validated artifact, NuGet Trusted Publishing, tag creation after publication, and GitHub Release notes extracted from the curated `CHANGELOG.md` section.

## Goals

1. Make governance truthful for a single human maintainer.
2. Keep pull requests and exact-head automated validation mandatory.
3. Remove independent-review and emergency-review-bypass language from the normal release path.
4. Make release authorization an explicit owner action: workflow dispatch with `publish_nuget=true`.
5. Make the publication workflow run only from `main`, not from arbitrary tag pushes.
6. Keep SmartPipe's stronger multi-package producer/replay design: one immutable producer artifact, Windows replay, PostgreSQL replay, package/compatibility checks, and fail-closed recovery.
7. Generate GitHub Release notes from the exact current-version section of `CHANGELOG.md`.
8. Create the immutable `v<version>` tag and GitHub Release only after successful NuGet publication.
9. Keep package-specific `PackageReleaseNotes` separate from repository-level release notes.
10. Make the workflow generic for future SmartPipe versions instead of hard-coding publication to 2.2.0.

## Non-goals

- No runtime/API/package-boundary changes.
- No dependency or SDK upgrade.
- No change to the already accepted 2.2.0 package graph.
- No automatic choice of the next version number.
- No generated release notes from commit history as the source of truth.
- No requirement for an additional human reviewer while the repository has one maintainer.
- No weakening of CodeQL, Dependency Review, package validation, baseline checks, Windows checks, PostgreSQL checks, audit checks, or immutable-artifact validation.

## Governance model

SmartPipe is a solo-maintainer repository. Normal PR policy therefore uses:

- required approving reviews: 0;
- pull request required;
- required status checks remain mandatory;
- unresolved review threads block merge;
- non-fast-forward and deletion protection remain enabled on protected release branches;
- no policy text claims an independent human approval exists when it does not;
- self-review is allowed but is not treated as an independent security control;
- owner decisions are auditable through the PR, exact-head checks, release workflow run and release artifacts.

The policy must not require a permanent owner bypass merely to satisfy an impossible reviewer rule. Repository rules should prefer no bypass actors when the configured machine gates and branch model permit it. If GitHub administration constraints require a bypass for exceptional recovery, it must be documented as an administrative capability, not as the normal merge path.

## Version model

SmartPipe uses synchronized NuGet package versions.

Authoritative release-version inputs:

1. repository-level `<Version>` in `Directory.Build.props`;
2. `eng/package-graph.json.releaseVersion`.

These two stable-core versions must be identical. A requested prerelease such as `2.3.0-rc.1` is valid when its SemVer core `2.3.0` equals both repository values; the prerelease suffix becomes the package version for that release.

For releases after the already-established 2.2.0 transition, follow SemVer 2.0:

- PATCH: backward-compatible fixes;
- MINOR: backward-compatible features;
- MAJOR: breaking public contract;
- prerelease suffixes: `-alpha.N`, `-beta.N`, `-rc.N`.

2.2.0 remains the accepted transition release; this design does not renumber it.

## Changelog and release-note contract

`CHANGELOG.md` is curated user-facing release history and the source of truth for GitHub Release notes.

Development form:

```text
## [2.2.0] — Development
```

Final release form:

```text
## [2.2.0] - YYYY-MM-DD
```

Before a publishable release, the current version must have a non-empty dated section. The release workflow extracts only that section, stopping at the next version heading, removes link-reference definitions, and writes `RELEASE_NOTES.md`.

The GitHub Release body uses exactly `RELEASE_NOTES.md`. Generated PR lists may be used while curating the changelog but are not published as the authoritative notes.

Package-specific `PackageReleaseNotes` remain package-scoped summaries for NuGet UI. They are not required to duplicate the repository changelog verbatim.

## Release workflow

Keep the existing filename `.github/workflows/publish-nuget.yml` so the NuGet Trusted Publishing policy does not need a gratuitous workflow-name migration.

The workflow becomes `workflow_dispatch` only, with inputs:

- `version`: exact SemVer package version;
- `publish_nuget`: boolean, default false.

Recovery is intentionally not a dispatch input; it uses GitHub **Re-run failed jobs** on the original release run.

### Entry gate

The workflow must fail unless:

- it is dispatched from `refs/heads/main`;
- `version` is canonical SemVer without build metadata;
- the stable SemVer core of `version` equals `Directory.Build.props` Version;
- the same stable SemVer core equals `eng/package-graph.json.releaseVersion`;
- a non-empty dated changelog section exists for that exact version;
- for a normal run, `v<version>` does not already exist;
- package publication state is compatible with the selected normal/recovery mode.

The old `push.tags: v*` trigger is removed. An arbitrary tag cannot start package publication.

### Validation / dry-run

Every dispatch performs the complete release validation before any publishing credential is requested:

1. locked restore;
2. Release build/tests;
3. package graph, ownership, metadata and release-version validation;
4. immutable release-mode producer artifact;
5. Windows replay of that exact artifact;
6. PostgreSQL package consumer replay;
7. integration lanes and compatibility/baseline checks;
8. vulnerability/deprecation policy;
9. changelog extraction;
10. SHA-256 inventory for nupkg/snupkg files;
11. upload of the validated artifact and release notes.

When `publish_nuget=false`, the workflow stops after validation. It creates no package publication, tag or GitHub Release.

### Publication

When `publish_nuget=true`:

- the publish job depends on all release validation/replay jobs;
- only the publish job receives `id-token: write`;
- it uses `environment: nuget-production`;
- it downloads the producer artifact by exact artifact ID;
- it revalidates version, mode, source commit and recorded hashes;
- it obtains a short-lived NuGet credential using `NuGet/login`;
- it publishes the manifest-listed 20 packages in dependency order;
- it preserves the existing fail-closed failed-job recovery semantics.

Add GitHub artifact attestation for the release nupkg/snupkg set before publication when supported by the repository/account configuration. Attestation failure is a release failure, not a warning.

### Recovery

Normal publication rejects already-published package identities.

`failed-job recovery` is not a new dispatch mode. After a partial publication failure, use GitHub **Re-run failed jobs** on the same workflow run. Successful producer/Windows/PostgreSQL jobs are not rerun, so the immutable producer artifact is reused. The rerun preflights already-published primary/symbol packages and proves payload equivalence before obtaining publishing credentials. A mismatch fails closed. Existing primary packages never excuse a missing or mismatched symbol package.

Do not use **Re-run all jobs** for release recovery; the version gate rejects a whole-workflow rerun to prevent rebuilding an already validated version.

## Tag and GitHub Release

After NuGet publication succeeds, a separate job with `contents: write`:

1. creates `v<version>` on the exact workflow `GITHUB_SHA` if missing;
2. accepts an existing tag only if it points to that exact SHA;
3. never moves an existing tag;
4. creates a draft GitHub Release;
5. attaches the validated `.nupkg`, `.snupkg`, checksum manifest and release-note artifact;
6. uses `RELEASE_NOTES.md` as the release body;
7. marks prerelease when the version contains a prerelease suffix;
8. publishes the draft only after all assets are attached.

Repository release immutability should be enabled. GitHub recommends draft-first publication because immutable releases freeze the tag and assets after publication.

## Tag ruleset

Create an active tag ruleset for `refs/tags/v*` equivalent to ChunkShift's release-tag policy:

- deletion blocked;
- update blocked;
- non-fast-forward blocked;
- no normal bypass actor.

Because tag pushes no longer trigger publication, the tag ruleset is responsible for immutability, not publication authorization. Publication authorization is the explicit release workflow dispatch plus `main`/version/validation gates.

## Environment and Trusted Publishing

Keep the GitHub environment name `nuget-production`.

For a solo maintainer, the environment must not require an unavailable second person. The required controls are:

- deployment branch/ref restricted to `main` release workflow execution;
- NuGet Trusted Publishing policy restricted to repository `MrFr3di/SmartPipe-Core`, workflow `publish-nuget.yml`, and environment `nuget-production`;
- only the publish job gets OIDC;
- no long-lived NuGet API key.

The explicit `publish_nuget=true` dispatch is the human release authorization.

## 2.2.0 branch sequence

The current checkpoint/release branch history remains valid and is not rewritten:

`sp220/checkpoint-g` -> reviewed promotion PR -> `release/2.2.0` -> exact-head validation -> final PR -> `main`.

Once the final release PR is merged, publication is dispatched from that exact `main` commit. The publication workflow no longer depends on being triggered by a pre-created tag.

Future release trains may still use integration branches, but publication itself is always main-driven.

## Evidence and acceptance

Implementation is accepted when:

- governance docs no longer require independent approval for the solo-maintainer repository;
- workflow-contract tests prove tag pushes cannot publish;
- workflow-contract tests prove dispatch must be from main;
- version/package-graph/changelog mismatch cases fail;
- dry-run performs full release validation and has no publication/tag/release side effect;
- publish mode keeps one immutable producer artifact across replay/publish;
- GitHub Release notes are extracted from the exact changelog version section;
- tag/release creation occurs only after successful NuGet publication;
- recovery remains fail-closed;
- existing CI, CodeQL, Dependency Review, Documentation and repository checks are green;
- owner-only GitHub settings still required (tag ruleset, environment/ref restriction, release immutability, external NuGet Trusted Publishing policy) are recorded explicitly rather than falsely reported as code-complete.

## External alignment

This design follows current Microsoft guidance to use SemVer for NuGet package versions and NuGet Trusted Publishing/OIDC rather than long-lived keys. It follows current GitHub guidance for branch/tag rulesets and immutable releases, including creating a draft release, attaching assets, then publishing it.
