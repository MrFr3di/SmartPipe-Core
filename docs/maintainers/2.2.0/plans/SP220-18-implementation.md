# SP220-18 release validation implementation plan

> **For agentic workers:** Use `superpowers:executing-plans` to implement task by task. Keep task PRs separate; do not promote G, tag or publish without acceptance evidence and the required approvals.

**Goal:** Make the complete 2.2.0 candidate verifiable in release mode on Linux/Windows with consumers using one immutable package artifact.

**Architecture:** Extend the existing reusable workflow with release gates and an optional immutable artifact input. Linux produces the artifact once; Windows builds/tests source and runs packed consumers against the downloaded producer artifact; PostgreSQL and publication use the same producer artifact ID. Keep ordinary CI current mode and existing check names.

**Tech stack:** .NET SDK 10.0.303/net10.0, GitHub Actions, PowerShell, Python/ruamel.yaml 0.18.16, existing RepositoryChecks.

**Spec:** [master plan SP220-18](architecture-plan.md#epic-sp220-18--release-validation), [branch/review policy](../../governance/2.2.0-branch-and-review-policy.md), and the user-approved seven-step continuation in this session.

## Global constraints

- Final SP220-17 head `1941efbc11051f4947f63f305f2af88f7560dbcc` is an actual ancestor of SP220-18 through merge `01593c0821d92c8ea3560875f614c5128aad7fc5`. PR #114 remains a separate prerequisite and must be accepted into `sp220/checkpoint-g` before #115; no rebase/squash may erase that evidence.
- Immutable 2.1.2 baseline files and six intentional-removal suppressions remain unchanged.
- All 20 package IDs/version 2.2.0; facade inventory23/13/6 and bundle17direct18closure remain unchanged.
- No dependency/SDK updates, runtime/API changes, new global suppressions or blanket AOT claims.
- Publication requires release mode, full Windows validation and PostgreSQL validation; no repack of a downloaded artifact.
- Only publisher obtains OIDC credentials. Environment protections and NuGet Trusted Publishing configuration are owner gates, not assumed or silently modified.
- Record exact SHA/run IDs/artifact ID/hash manifest; local outputs are not remote acceptance evidence.

## Review focus

- A publication caller selecting current mode must be rejected by workflow mutation tests.
- Windows replay must neither pack nor upload a second package artifact; it must verify hashes before consumers.
- Dropping Windows/PostgreSQL from publication dependencies must be rejected.
- Release artifact inventory with planned IDs, downgraded/unknown mode, wrong hashes or version must fail closed.
- Invalid workflow inputs and missing downloads must fail before any consumer or credential acquisition.
- A release tag must point to accepted history: its SHA is contained in `main` and contains the current `release/2.2.0` head before release validation/publication proceeds.
- A recoverable existing package must be GET-downloaded and proven equivalent to the immutable producer payload before OIDC login and recorded in recovery state; missing entries publish without duplicate suppression, so a post-preflight conflict fails closed and an existing primary cannot suppress recovery of its snupkg.
- Before any push, both nupkg and snupkg hashes must still match the immutable manifest. Post-publication verification uses one shared15-minute propagation deadline for the complete40-archive set rather than a per-package retry budget.
- NuGet credentials remain environment-only; command-line arguments must not contain API-key values.

## Task 1: Package release metadata

Files: `src/*/*.csproj`, `docs/maintainers/2.2.0/evidence/sp220-18-evidence.md`.

- [x] Confirm final SP220-17 feed fails release metadata with19 SPMETA006 only.
- [x] Add package-specific 2.2.0 notes for19 remaining packages, including first-release identities and AOT/compatibility boundaries where material.
- [x] Locked restore/build/pack and metadata release gate pass20 packages; retain all existing validation/baseline settings.
- [x] Commit metadata.

## Task 2: Release workflow and artifact replay

Files: `.github/workflows/reusable-release-validation.yml`, `publish-nuget.yml`, `ci.yml`, `eng/tests/workflow_contract_tests.py`, new `eng/tests/release_validation_contract_tests.py`, `eng/validate-package-artifact.ps1`, `eng/tests/validate-package-artifact.Tests.ps1`.

Interfaces: reusable workflow `validation-mode` string (current/release, default current), `package-artifact-id` string (empty means producer); existing package-version/artifact-name/runner-labels and artifact-id output retained.

- [x] Write contract tests for release-mode publisher, required Windows dependency, artifact replay download/integrity-before-consumers, mutually exclusive pack/package-upload versus report-only replay, release candidate dispatch inputs and mandatory release graph/metadata/ownership/version gates. Run RED against existing YAML.
- [x] Add optional release-mode checks and replay steps without relaxing ordinary CI. Validate inputs. Replay cannot repack; only consumer/audit reports are uploaded by replay.
- [x] Add required Windows caller consuming producer ID to publish DAG; retain PostgreSQL and OIDC/environment guard.
- [x] Add CI release-candidate dispatch route for exact feature-branch SHA and Windows replay; preserve diagnostic and same-repository guards.
- [x] Add rejecting artifact mode/planned-inventory fixtures; implement current/release validator support, retaining current compatibility for existing CI artifacts. Release mode requires an explicit expected source commit, checked against every nupkg/snupkg repository entry.
- [x] Run all workflow mutation tests and PowerShell artifact fixtures GREEN; commit.
- [x] Add post-publication payload equivalence checks and provenance-safe partial-release recovery for both nupkg and snupkg; lock the behavior with rejecting mutation/PowerShell fixtures.
- [x] Harden recovery/publication with GET-based preflight response checks, state-driven fail-closed recovery, environment-only credentials, immediate primary/symbol hash revalidation, duplicate-signature rejection and one shared15-minute propagation window.
- [ ] Re-run exact-head hosted validation after the recovery hardening and record the new SHA/run evidence.

## Task 3: Audits, docs and owner gates

Files: evidence and release readiness documentation, master plan acceptance references; existing audit commands/workflows.

- [x] Run vulnerability/deprecation scans and existing audit policy; report findings by scope without changing dependency versions silently.
- [x] Check README/migration links and package install examples; record package validation/first-release coverage.
- [x] Inspect release branch protection and nuget-production environment; record IDs/enforcement/checks and missing requirements. Recheck on2026-10-02 confirmed release ruleset approving-review count0 and no tag-target ruleset in repository ruleset inventory. NuGet external publishing-policy state remains unverified unless authenticated evidence is available.
- [x] Document exact tag/version and immutable-artifact publication sequence. No tag/publish action in this task.

## Task 4: Candidate verification

- [x] Run locked restore, Release build with warnings-as-errors, format, workflow/PowerShell fixtures and affected RepositoryChecks tests.
- [x] Pack one fresh final feed; baseline offline integrity and graph/ownership/metadata/version current+release checks.
- [x] Run63 non-PostgreSQL consumers; run7 PostgreSQL consumers with real18.6 when available and integration18.6/17.11. Record unavailable server/Windows gates honestly.
- [ ] Prepare/publish reviewable feature PRs and dispatch exact-head CI where credentials permit. Observe results; fix genuine failures with regression coverage.

## Task 5: Review and evidence

- [x] Fresh whole-branch read-only review, then fix Critical/Important findings and verify.
- [ ] Record commits, SDK/OS, command results, artifact hashes and GitHub run URLs/IDs. Clearly distinguish implementation done, local verified, remote verified and owner approval pending.
- [x] Handoff without marking SP220-18/checkpoint G accepted while required gates remain open.
