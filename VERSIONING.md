# Versioning and compatibility

SmartPipe uses synchronized package versions. The repository-level `Version`
property is the package-version source of truth, while
`eng/package-graph.json` is the source of truth for package lifecycle,
dependency policy, publish order, package-scoped AOT contracts, and the stable
release core.

For a stable release, repository `Version` and
`eng/package-graph.json.releaseVersion` must be identical. For a prerelease
such as `2.3.0-rc.1`, the stable SemVer core `2.3.0` must equal both
repository values while the full prerelease value is the package version used
for that release.

## Compatibility is multi-dimensional

Do not infer compatibility from the package number alone. SmartPipe reviews and
validates compatibility across separate dimensions:

| Dimension | Meaning | Primary evidence |
| --- | --- | --- |
| Source | Existing source still compiles against the new package set | source consumers and build tests |
| Binary | Previously compiled assemblies still bind and run | unchanged old-binary consumers, forwarder/assembly identity checks |
| API | Public signatures remain compatible or a break is explicitly accepted | PublicAPI baselines and package validation |
| Behavioral | Existing API keeps its documented semantics | correctness, lifecycle, integration, and regression tests |
| Package graph | Package IDs, dependency ownership, and lifecycle remain valid | `eng/package-graph.json` and repository checks |
| AOT / trimming | A package keeps only the claim it actually validates | package-specific trim/NativeAOT consumers |
| Release provenance | Published artifacts originate from the accepted source and immutable producer artifact | release validation and artifact provenance checks |

A change can be source-compatible and still be binary-incompatible. Namespace
preservation is not proof of binary compatibility.

## 2.1.2 → 2.2.0

The 2.2 release intentionally contains documented breaking changes from the
2.1.2 compatibility baseline. 2.2.0 is an already-established transition
release and is not renumbered by the stricter versioning policy below.

For the 2.2 transition:

- 23 relevant facade identities are preserved through type forwarding;
- 13 remain physically in the compatibility facade;
- 6 are intentionally removed and require migration/recompilation.

See the
[compatibility matrix](https://github.com/MrFr3di/SmartPipe-Core/blob/main/docs/reference/compatibility/2.1.2-to-2.2.0.md)
and [migration guide](https://github.com/MrFr3di/SmartPipe-Core/blob/main/docs/migration/2.2.0-integration-packages.md).

## Package version policy

All publishable SmartPipe packages in one release use the same package version.
A package may have a different lifecycle or dependency surface, but it does not
choose an independent version inside a release train.

For releases after the established 2.2.0 transition, use Semantic Versioning
2.0.0:

- PATCH for backward-compatible fixes, for example `2.2.0 -> 2.2.1`;
- MINOR for backward-compatible features, for example `2.2.x -> 2.3.0`;
- MAJOR for incompatible public-contract changes, for example `2.x -> 3.0.0`;
- prereleases use canonical suffixes such as `-alpha.1`, `-beta.1`, or
  `-rc.1`.

Build metadata is not part of a publishable SmartPipe package identity and is
rejected by the release workflow.

Do not introduce:

- floating SmartPipe package versions;
- per-project version overrides that bypass repository versioning;
- package dependency versions that disagree with the release artifact manifest;
- a release whose requested stable SemVer core disagrees with repository
  `Version` or `package-graph.json.releaseVersion`;
- a release tag whose source commit is not the exact accepted release commit;
- reuse or movement of an already-published package version or release tag.

## Compatibility facade

`SmartPipe.Extensions` is a compatibility facade/bundle, not the preferred
dependency for new applications.

A moved public type may be kept through a real metadata type forwarder when the
implementation assembly owns the type and binary binding can be proven. A
wrapper is used only when the compatibility contract explicitly requires one.
An intentionally removed type is not restored simply to silence API
compatibility tooling.

Narrow integration leaves must not depend back on the facade.

## Baselines

Compatibility baselines are immutable evidence. A failing compatibility check is
resolved by fixing the implementation or by accepting/documenting a real
breaking change. Do not regenerate a baseline to make the candidate pass.

The 2.1.2 baseline under `eng/baselines/2.1.2/` is comparison material for the
2.2 release and does not by itself define support status.

## Changelog and release notes

`CHANGELOG.md` is the curated repository-level user-facing release history.
The exact dated section for the requested version is the source of truth for the
GitHub Release body.

During development a section may be marked `Development`. A publishable
release requires a dated heading:

```text
## [X.Y.Z] - YYYY-MM-DD
```

The release workflow extracts only that section into `RELEASE_NOTES.md`. It
refuses a missing, duplicate, empty or development-only section.

Package-level `PackageReleaseNotes` remain package-specific summaries rendered
by NuGet. They complement rather than duplicate the repository-level changelog.

## Release procedure

SmartPipe release publication is explicit and main-driven. Pushing or creating a
`v*` tag is not a publication trigger.

1. Complete the release integration branch and exact-head validation.
2. Merge the reviewed release branch into `main`. The accepted main commit
   contains the synchronized version values and the dated changelog section.
3. Dispatch `.github/workflows/publish-nuget.yml` from that exact `main`
   commit with `version=<version>` and `publish_nuget=false`.
4. Inspect the complete dry-run evidence. The dry run performs release-mode
   producer/replay validation but creates no NuGet publication, tag or GitHub
   Release.
5. Close required owner/admin configuration gates and dispatch the same workflow
   with `publish_nuget=true`.
6. The workflow produces one immutable package artifact, replays that exact
   artifact on Windows and PostgreSQL, revalidates it in the publisher, obtains
   a short-lived NuGet credential through Trusted Publishing, and publishes the
   manifest-listed package set.
7. Only after NuGet publication succeeds does the final job create
   `v<version>` on the exact workflow SHA and publish the GitHub Release using
   the extracted `RELEASE_NOTES.md`.

Normal publication rejects duplicate identities. Recovery is explicit,
payload-equivalence checked, never rebuilds the same version, and never moves an
existing tag.

## Release governance

SmartPipe currently has one human maintainer. Human approval count is therefore
not an independent security boundary and is not required by repository policy.
The release boundary is instead composed of:

- reviewable pull requests;
- exact-head CI and security checks;
- protected branch/tag history;
- immutable package artifacts and hash/provenance validation;
- an explicit `publish_nuget=true` dispatch from `main`;
- the `nuget-production` environment and NuGet Trusted Publishing policy.

See
[the 2.2 branch/review policy](docs/maintainers/governance/2.2.0-branch-and-review-policy.md)
for the concrete SP220 release train.

## Release documentation

For every release-facing compatibility change, keep these layers consistent:

1. machine-readable package/ownership/API contracts;
2. executable source/binary/trim/AOT consumers;
3. migration guidance and compatibility matrix;
4. package-specific release notes;
5. repository changelog and GitHub Release notes;
6. exact-head hosted evidence.

Support lifecycle is documented separately in
[SUPPORT.md](https://github.com/MrFr3di/SmartPipe-Core/blob/main/SUPPORT.md).
