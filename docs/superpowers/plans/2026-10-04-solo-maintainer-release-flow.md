# Solo-maintainer Release Flow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace impossible independent-review governance with truthful solo-maintainer machine gates and make SmartPipe releases main-driven, dry-runnable, NuGet-published, tagged, and published as GitHub Releases using the exact curated CHANGELOG section.

**Architecture:** Keep the existing 20-package single-producer/replay pipeline and `publish-nuget.yml` identity, but change its entry contract from tag-push to explicit `workflow_dispatch` on `main`. Add a small deterministic release-notes extractor, harden structural workflow tests, and make tag/GitHub Release creation the final post-NuGet step. Owner-only GitHub/NuGet settings remain explicit configuration gates rather than being faked in code.

**Tech Stack:** .NET 10 / C#, GitHub Actions YAML, PowerShell/Bash, Python structural workflow tests, NuGet Trusted Publishing/OIDC, GitHub Releases.

**Spec:** `docs/superpowers/specs/2026-10-04-solo-maintainer-release-governance-design.md`

## Global Constraints

- SmartPipe 2.2.0 stays version `2.2.0`; do not renumber this release.
- Do not change runtime APIs, package boundaries, dependencies, or the pinned SDK `10.0.303`.
- Keep all 20 publishable packages synchronized to the repository release version.
- Keep the existing immutable single-producer artifact, Windows replay, PostgreSQL replay, baseline, audit, CodeQL and Dependency Review gates.
- Keep `.github/workflows/publish-nuget.yml` as the trusted-publishing workflow filename.
- Normal release publication must be explicit `workflow_dispatch` from `main`; tag pushes must never publish.
- `publish_nuget=false` is a full dry-run with zero package/tag/release side effects.
- `publish_nuget=true` is the explicit solo-maintainer release authorization.
- GitHub Release notes come from the exact dated `CHANGELOG.md` section for the requested version.
- Package-specific `PackageReleaseNotes` remain separate and must not be replaced by repository release prose.
- Recovery never rebuilds the same version and fails closed on payload mismatch.

## Review Focus

- Dispatch from any ref other than `main` must fail before publishing credentials or publication side effects.
- A requested version with build metadata, noncanonical numeric components, or mismatched repository/package-graph version must fail.
- A missing, `Development`-only, duplicated, or empty changelog section must not produce a publishable release.
- An existing tag that points to another commit must fail and must never be moved.
- Recoverable publication must distinguish primary and symbol package state and fail on any published-payload mismatch.

---

### Task 1: Encode the solo-maintainer governance contract

**Files:**
- Modify: `docs/maintainers/governance/2.2.0-branch-and-review-policy.md`
- Modify: `docs/maintainers/2.2.0/readiness/sp220-18-release-readiness.md`
- Modify: `VERSIONING.md`
- Modify: GitHub issue `#120` after the branch changes exist

**Interfaces:**
- Consumes: existing exact-head/checkpoint governance and 2.2.0 release branch sequence.
- Produces: normative policy used by later workflow changes and #120 acceptance.

- [ ] **Step 1: Update governance text for a single maintainer**

Replace mandatory independent approval language with:
- required approving review count `0`;
- PR + required machine gates remain mandatory;
- review-thread resolution remains mandatory;
- self-review is permitted but not described as independent assurance;
- no normal-path emergency bypass ceremony solely because no second maintainer exists;
- prefer no bypass actors where GitHub configuration permits.

- [ ] **Step 2: Update release-readiness owner gates**

Change #120/readiness semantics so a second reviewer is not an acceptance condition. Keep only real owner/admin gates:
- release branch protection matches policy;
- tag immutability ruleset exists for `refs/tags/v*`;
- `nuget-production` is restricted to the intended main-driven publication path;
- NuGet Trusted Publishing matches repo/workflow/environment;
- release immutability is enabled/verified.

- [ ] **Step 3: Update VERSIONING.md**

Document:
- synchronized package versions;
- repository `Version` + package-graph `releaseVersion` equality;
- SemVer policy for post-2.2 releases;
- release PR / dry-run / publish / GitHub Release flow;
- exact CHANGELOG section as GitHub Release notes;
- 2.2.0 remains the already-established transition release.

- [ ] **Step 4: Run documentation verification**

Run:
```bash
dotnet run --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj --configuration Release -- verify-docs --repo-root .
```

Expected: `SP220_DOCS_OK`.

- [ ] **Step 5: Commit**

```bash
git add docs/maintainers/governance/2.2.0-branch-and-review-policy.md \
        docs/maintainers/2.2.0/readiness/sp220-18-release-readiness.md \
        VERSIONING.md
git commit -m "docs(release): adopt solo-maintainer governance"
```

### Task 2: Add deterministic CHANGELOG-to-release-notes extraction

**Files:**
- Create: `eng/SmartPipe.RepositoryChecks/Release/ReleaseNotesExtractor.cs`
- Modify: `eng/SmartPipe.RepositoryChecks/Commands/CommandLineParser.cs`
- Modify: `eng/SmartPipe.RepositoryChecks/Commands/CommandOptions.cs` or the existing command-option declaration file used by the parser
- Modify: `eng/SmartPipe.RepositoryChecks/Program.cs`
- Create: `tests/SmartPipe.RepositoryChecks.Tests/Release/ReleaseNotesExtractorTests.cs`

**Interfaces:**
- Produces: `ReleaseNotesExtractor.Extract(string changelog, string version) -> string`.
- Produces CLI: `prepare-release-notes --version <SemVer> --changelog <path> --output <path>`.
- Consumes: existing `ReleaseVersionValidator.ParseTag("v" + version)` for canonical SemVer validation rather than introducing a second SemVer parser.

- [ ] **Step 1: Write failing extractor tests**

Tests:
- `Extract_ReturnsOnlyRequestedDatedSection`;
- `Extract_RejectsDevelopmentHeading`;
- `Extract_RejectsMissingVersion`;
- `Extract_RejectsDuplicateVersionSection`;
- `Extract_RejectsEmptySection`;
- `Extract_StopsBeforeNextVersionHeading`;
- `Extract_RemovesLinkReferenceDefinitions`;
- `Extract_RejectsNonCanonicalVersion`.

Assertions pin:
- accepted heading: `## [2.2.0] - 2026-10-04`;
- rejected development heading: `## [2.2.0] — Development`;
- output contains section body only, not the version heading or following historical release.

- [ ] **Step 2: Run focused tests and verify RED**

Run:
```bash
dotnet test tests/SmartPipe.RepositoryChecks.Tests/SmartPipe.RepositoryChecks.Tests.csproj -c Release --filter ReleaseNotesExtractorTests
```

Expected: FAIL because extractor/command does not exist.

- [ ] **Step 3: Implement the extractor**

Implement:
```csharp
internal static class ReleaseNotesExtractor
{
    internal static string Extract(string changelog, string version);
}
```

Rules:
- canonicalize/validate by calling `ReleaseVersionValidator.ParseTag($"v{version}")`;
- exact level-2 heading only;
- publishable heading requires ISO `yyyy-MM-dd`, not `Development`;
- exactly one matching section;
- stop at next version-shaped level-2 heading;
- omit trailing changelog link-reference definition lines;
- normalized output ends with exactly one newline;
- throw a release-specific deterministic exception for invalid input.

- [ ] **Step 4: Add CLI wiring**

Add a typed command option record and parser branch for:
```text
prepare-release-notes --version <version> --changelog CHANGELOG.md --output artifacts/packages/RELEASE_NOTES.md
```

Paths must resolve within repository root and output parent directories are created deterministically.

- [ ] **Step 5: Run focused tests and parser tests**

Run:
```bash
dotnet test tests/SmartPipe.RepositoryChecks.Tests/SmartPipe.RepositoryChecks.Tests.csproj -c Release --filter "ReleaseNotesExtractorTests|CommandLineParserTests"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add eng/SmartPipe.RepositoryChecks tests/SmartPipe.RepositoryChecks.Tests
git commit -m "build(release): derive release notes from changelog"
```

### Task 3: Refactor publish-nuget.yml into a main-driven release controller

**Files:**
- Modify: `.github/workflows/publish-nuget.yml`
- Modify: `eng/tests/workflow_contract_tests.py`

**Interfaces:**
- Inputs:
  - `version: string`, required;
  - `publish_nuget: boolean`, required, default `false`;
  - `recoverable-rerun: boolean`, required, default `false`.
- Consumes: `prepare-release-notes` command from Task 2.
- Produces: validated `nuget-packages-<version>` artifact containing packages, manifest, `SHA256SUMS`, and `RELEASE_NOTES.md`.

- [ ] **Step 1: Add RED structural contract tests**

Extend `assert_publish_contract` and mutation tests to require:
- no `push.tags` trigger;
- exact workflow-dispatch input set;
- an early `Require main` guard checking `refs/heads/main`;
- requested version is passed through canonical release validation;
- repository Version and package-graph releaseVersion are compared to input;
- release notes are generated before artifact upload;
- `publish` job has `if: inputs.publish_nuget`;
- only publish job gets `id-token: write`;
- publish still depends on producer, Windows and PostgreSQL validation;
- release job depends on successful publish;
- release job alone gets `contents: write`.

Add mutation tests that deliberately reintroduce a tag trigger, remove the main guard, move OIDC permission earlier, remove release-note extraction, or allow the release job before publish.

- [ ] **Step 2: Run workflow contracts and verify RED**

Run:
```bash
python eng/tests/workflow_contract_tests.py
```

Expected: FAIL against the old tag-driven workflow.

- [ ] **Step 3: Replace the workflow entry contract**

Change `on` to workflow-dispatch only. Add inputs `version`, `publish_nuget`, `recoverable-rerun`.

The first validation job must:
- require `refs/heads/main`;
- resolve package version from `inputs.version`;
- ensure repository Version equals requested version;
- ensure package graph releaseVersion equals stable core of requested version;
- call existing release-mode package validation using `v<input.version>`;
- generate `RELEASE_NOTES.md`.

Do not use a pre-existing Git tag as an input to publication.

- [ ] **Step 4: Preserve and tighten immutable artifact flow**

Keep:
- one Linux producer;
- Windows consumes producer artifact ID;
- PostgreSQL consumes producer artifact ID;
- publisher consumes producer artifact ID;
- `validate-package-artifact.ps1` checks expected commit/version/mode;
- nupkg/snupkg hashes are checked before each push.

Add a deterministic `SHA256SUMS` file to the producer artifact.

- [ ] **Step 5: Gate publication explicitly**

Set the publication job to run only when `inputs.publish_nuget` is true.

Keep:
- `environment: nuget-production`;
- `id-token: write` only here;
- NuGet login after all downloaded-artifact validation/recovery preflight.

Add artifact attestation before NuGet login:
```yaml
permissions:
  contents: read
  id-token: write
  attestations: write
  artifact-metadata: write
```

Use a SHA-pinned `actions/attest` action and attest all manifest-listed nupkg/snupkg files.

- [ ] **Step 6: Preserve fail-closed recovery**

Adapt existing recoverable-rerun conditions from `github.event_name == 'workflow_dispatch'` to the explicit boolean input.

Normal mode:
- any duplicate target package fails.

Recovery mode:
- existing primary and symbol packages are downloaded;
- payload equivalence is mandatory;
- equivalent entries are skipped;
- missing entries are pushed;
- any race/conflict after preflight fails.

- [ ] **Step 7: Add final tag/GitHub Release job**

Add `github-release`:
- `needs: publish`;
- `contents: write`, no OIDC;
- download the same producer artifact;
- verify `SHA256SUMS`;
- create `v<input.version>` at exact `GITHUB_SHA` if missing;
- fail if an existing tag points elsewhere;
- create a draft release titled `SmartPipe <version>`;
- use `RELEASE_NOTES.md` as notes;
- attach all nupkg/snupkg plus `SHA256SUMS`;
- mark prerelease for versions containing `-`;
- publish the draft after upload;
- if an exact already-published release exists on a recovery rerun, return success without mutation.

- [ ] **Step 8: Run workflow contract tests GREEN**

Run:
```bash
python eng/tests/workflow_contract_tests.py
```

Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add .github/workflows/publish-nuget.yml eng/tests/workflow_contract_tests.py
git commit -m "ci(release): make publication main-driven"
```

### Task 4: Lock release documentation and workflow semantics together

**Files:**
- Modify: `eng/SmartPipe.RepositoryChecks/Documentation/DocumentationVerificationService.cs`
- Modify: `tests/SmartPipe.RepositoryChecks.Tests/Documentation/DocumentationVerificationServiceTests.cs`
- Modify: `docs/releases/2.2.0.md`
- Modify: `docs/maintainers/2.2.0/readiness/sp220-18-release-readiness.md`

**Interfaces:**
- Consumes: current `SPDOC014-SPDOC018` release-document checks.
- Produces: explicit documentation contract that a release section is curated once and reused by GitHub Release.

- [ ] **Step 1: Add documentation mutation tests**

Add tests proving:
- release documentation points to the changelog as GitHub Release source of truth;
- maintainer release procedure names dry-run and publish modes;
- no text claims that pushing a tag starts publication;
- no text requires an independent reviewer.

Use a new diagnostic only if existing SPDOC diagnostics cannot express the invariant cleanly; do not add diagnostics for prose that workflow structural tests already enforce.

- [ ] **Step 2: Reconcile 2.2.0 release docs**

Document the final intended procedure without changing the changelog date yet:
- checkpoint G -> release branch -> main;
- release workflow dry-run on main;
- final changelog date set in the release-preparation change;
- publish dispatch;
- NuGet -> tag -> GitHub Release;
- GitHub Release notes equal exact changelog section.

- [ ] **Step 3: Run docs + repository checks**

Run:
```bash
dotnet test tests/SmartPipe.RepositoryChecks.Tests/SmartPipe.RepositoryChecks.Tests.csproj -c Release
dotnet run --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj -c Release -- verify-docs --repo-root .
```

Expected: PASS and `SP220_DOCS_OK`.

- [ ] **Step 4: Commit**

```bash
git add eng/SmartPipe.RepositoryChecks/Documentation \
        tests/SmartPipe.RepositoryChecks.Tests/Documentation \
        docs/releases/2.2.0.md \
        docs/maintainers/2.2.0/readiness/sp220-18-release-readiness.md
git commit -m "docs(release): lock changelog-backed release procedure"
```

### Task 5: Update #120 and owner-setting acceptance

**Files / external state:**
- Modify issue `#120`.
- Read live ruleset `19148428`.
- Owner/manual settings: tag ruleset, `nuget-production`, release immutability, NuGet Trusted Publishing.

**Interfaces:**
- Produces: an auditable list of configuration gates that match the new code instead of the obsolete independent-review model.

- [ ] **Step 1: Rewrite #120 acceptance**

Remove:
- required approval count >= 1;
- second-reviewer availability audit;
- emergency bypass as normal release mechanics.

Require:
- release branch ruleset has PR/machine/status/thread protections and review count 0;
- owner normal-path bypass removed if administratively feasible;
- active tag ruleset covers `refs/tags/v*` and blocks update/deletion/non-fast-forward;
- `nuget-production` is restricted to main-driven deployment and does not require a nonexistent second person;
- NuGet Trusted Publishing points to `MrFr3di/SmartPipe-Core`, `publish-nuget.yml`, `nuget-production`;
- release immutability enabled.

- [ ] **Step 2: Record what cannot be mutated through the connector**

Do not claim code changed GitHub admin or NuGet account settings. Add exact manual owner actions and verification evidence required after code merge.

- [ ] **Step 3: Re-read available live rulesets**

Verify the branch ruleset state and tag-ruleset inventory. Record IDs/enforcement when available.

### Task 6: Whole-branch validation and review

**Files:** all modified files.

**Interfaces:**
- Consumes all previous tasks.
- Produces merge-ready branch and Draft PR into `sp220/checkpoint-g` unless repository state requires a newer integration base.

- [ ] **Step 1: Run format/build/tests**

Run:
```bash
dotnet restore SmartPipe.Core.slnx --locked-mode -p:DisableImplicitLibraryPacksFolder=true
dotnet format SmartPipe.Core.slnx --verify-no-changes --no-restore
dotnet build SmartPipe.Core.slnx -c Release --no-restore -warnaserror
dotnet test tests/SmartPipe.RepositoryChecks.Tests/SmartPipe.RepositoryChecks.Tests.csproj -c Release --no-build
python eng/tests/workflow_contract_tests.py
```

Expected: all PASS.

- [ ] **Step 2: Run repository/release documentation checks**

Run:
```bash
dotnet run --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj -c Release --no-build -- verify --profile sp220-05 --format github --failures-only
dotnet run --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj -c Release --no-build -- verify-docs --repo-root .
```

Expected: success.

- [ ] **Step 3: Perform adversarial diff review**

Check specifically:
- no remaining tag-push publication path;
- dry-run cannot reach OIDC/NuGet/tag/release jobs;
- GitHub release cannot run before publish success;
- recovery cannot rebuild;
- exact source SHA is preserved through artifact validation and tag creation;
- no secret is printed;
- no second-human requirement remains in active governance/readiness docs.

- [ ] **Step 4: Open Draft PR**

Title:
```text
ci(release): adopt solo-maintainer release flow
```

Base: `sp220/checkpoint-g`.

Body must summarize:
- solo-maintainer governance correction;
- ChunkShift-aligned main-driven release UX;
- unchanged SmartPipe multi-package validation strength;
- changelog-backed GitHub Release;
- remaining owner/admin configuration gates.

- [ ] **Step 5: Require exact-head hosted checks before merge**

Do not merge until CI, CodeQL, Dependency Review and Documentation for the exact PR head are green. Record exact head SHA and runs in the PR/issue evidence.

- [ ] **Step 6: After merge, re-run Checkpoint G release validation**

Because this slice changes the candidate SHA and release workflow, run exact-head `ci.yml release-validation=true` again on the resulting `sp220/checkpoint-g` head before promoting G to `release/2.2.0`.
