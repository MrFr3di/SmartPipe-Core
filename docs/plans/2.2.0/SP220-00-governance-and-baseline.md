# SmartPipe.Core 2.2.0 — SP220-00 Governance and Baseline Implementation Plan

Связанные нормативные документы: [ADR-0001](../../adr/0001-smartpipe-2.2-package-boundaries.md), [branch and review policy](../../governance/2.2.0-branch-and-review-policy.md), [master architecture plan](../2.2.0-extension-architecture.md).

> **Порядок исполнения:** используйте subagent-driven development (предпочтительно) либо последовательное исполнение утверждённого плана и выполняйте задачи строго по порядку. Все шаги имеют checkbox для фиксации выполнения.

**Цель:** создать неизменяемую, воспроизводимую и автоматически проверяемую стартовую точку релиза SmartPipe 2.2.0 до любых изменений runtime, public API и package architecture.

**Архитектура:** этап вводит защищённую интеграционную ветку `release/2.2.0`, нормативные ADR/governance-документы и кроссплатформенный repository-check tool. Tool восстанавливает опубликованные пакеты 2.1.2, проверяет подписи и содержимое, формирует канонические снимки API, assemblies и dependency graph и затем используется CI как fail-closed baseline gate.

**Tech Stack:** C# 14, .NET SDK 10.0.302, Microsoft.Testing.Platform/xUnit v3, `System.Text.Json`, `System.IO.Compression`, `System.Reflection.Metadata`, `HttpClient`, PowerShell 7 только для orchestration, GitHub Actions, NuGet V3 Service Index.

## Global Constraints

- Базовый repository: `MrFr3di/SmartPipe-Core`.
- Проверенный `main` SHA на момент составления: `8e79902d22de714f493582946f7c260462b0895e`.
- Текущая опубликованная версия: `2.1.2`.
- Целевой релиз: `2.2.0`; tag в конце релиза: `v2.2.0`.
- Target framework: `net10.0`.
- SDK берётся только из `global.json`; ожидаемый baseline — `10.0.302` с `rollForward: disable`.
- Этап SP220-00 НЕ меняет production runtime, public API, поведение sources/transforms/sinks или dependency graph production packages.
- Никакие `.nupkg`/`.snupkg` не коммитятся в Git. Коммитятся только manifest, канонические snapshots, schema и human-readable report.
- Все проверки fail-closed: неизвестный формат, отсутствующий package, несовпадающий hash, подпись, API или dependency graph являются ошибкой.
- Все текстовые hashes вычисляются после канонизации UTF-8: BOM удаляется, `CRLF`/`CR` преобразуются в `LF`; binary package hashes вычисляются по исходным bytes.
- Никаких `TBD`, временных suppressions, пропуска signature verification или ручного редактирования сгенерированных snapshot-файлов.
- Все network-зависимые операции отделяются от deterministic verification: CI сначала fetches artifacts, затем выполняет offline verification по локальным files.
- Все новые infrastructure classes покрываются unit tests; network и GitHub API не используются в unit tests.

---

# 1. Результат этапа

После завершения SP220-00 repository должен содержать:

```text
release/2.2.0                         protected integration branch

.github/workflows/
  ci.yml                              release branch included in push/PR triggers
  codeql.yml                          release branch included in push/PR triggers
  dependency-review.yml               release branch included in PR triggers

 docs/
  adr/
    0001-smartpipe-2.2-package-boundaries.md
  governance/
    2.2.0-branch-and-review-policy.md
  plans/
    2.2.0-extension-architecture.md
    2.2.0/
      SP220-00-governance-and-baseline.md

eng/
  baselines/
    baseline.schema.json
    README.md
    2.1.2/
      manifest.json
      public-api.json
      package-assets.json
      package-dependencies.json
      repository-dependencies.json
      baseline-report.md
  SmartPipe.RepositoryChecks/
    SmartPipe.RepositoryChecks.csproj
    Program.cs
    Commands/
      CaptureBaselineCommand.cs
      VerifyBaselineCommand.cs
    Baselines/
      BaselineManifest.cs
      BaselineManifestSerializer.cs
      BaselineCaptureService.cs
      BaselineVerificationService.cs
      BaselineVerificationResult.cs
    NuGet/
      NuGetServiceIndexClient.cs
      NuGetPackageFetcher.cs
      NuGetPackageSignatureVerifier.cs
      NuGetPackageReader.cs
    Repository/
      RepositorySnapshotReader.cs
      PublicApiSnapshotReader.cs
      ProjectDependencySnapshotReader.cs
      WorkflowPolicyReader.cs
    Infrastructure/
      CanonicalJson.cs
      CanonicalText.cs
      Hashing.cs
      ProcessRunner.cs
      ExitCodes.cs

tests/
  SmartPipe.RepositoryChecks.Tests/
    SmartPipe.RepositoryChecks.Tests.csproj
    Baselines/
    NuGet/
    Repository/
    Fixtures/
```

Local/downloaded binaries use only ignored paths:

```text
artifacts/baselines/2.1.2/
  SmartPipe.Core.2.1.2.nupkg
  SmartPipe.Extensions.Json.2.1.2.nupkg
  SmartPipe.Extensions.2.1.2.nupkg
```

## 1.1. Definition of Done SP220-00

- [ ] `release/2.2.0` points to the approved exact `main` SHA.
- [ ] Exact-head CI has been manually dispatched and is successful for that SHA.
- [ ] Branch/ruleset policy is applied or explicitly documented as an owner-blocking action.
- [ ] ADR is approved and contains package boundaries, dependency direction and compatibility policy.
- [ ] The full 2.2.0 architecture plan is committed under `docs/plans`.
- [ ] All three published 2.1.2 packages are fetched from NuGet V3, signature-verified and SHA-256 pinned.
- [ ] Baseline snapshots are deterministic and verified on Linux and Windows.
- [ ] Public API, package assets and direct/transitive dependency snapshots are committed.
- [ ] Negative tests prove that hash/API/dependency/workflow mutations are detected.
- [ ] CI contains an explicit `baseline-contract` gate.
- [ ] Production source directories have no changes from the baseline SHA.
- [ ] Full current test suite remains green.

---

# 2. Анализ и принятые улучшения исходного шага

Исходный EPIC содержал семь коротких пунктов. Для production-grade релиза их недостаточно. Ниже зафиксированы улучшения.

## 2.1. Ветка — не просто long-lived branch

Используется двухуровневая модель:

```text
main
  └── release/2.2.0                  защищённая integration branch
        ├── sp220/00-governance
        ├── sp220/01-package-infrastructure
        ├── sp220/02-core-definition
        └── ...
```

Правила:

- task branches создаются от актуального `release/2.2.0`;
- PR каждого EPIC направляется в `release/2.2.0`;
- direct pushes в `release/2.2.0` запрещены;
- окончательный PR `release/2.2.0 -> main` проходит полный release validation;
- `release/2.2.0` не rebase/force-push после первого принятого EPIC;
- обновление из `main` выполняется merge commit только после анализа причин hotfix;
- task branch удаляется после merge.

Это снижает риск огромного неревьюируемого PR и одновременно сохраняет один интеграционный контур 2.2.0.

## 2.2. Baseline SHA должен иметь exact-head CI

У текущего changelog-only SHA может отсутствовать check suite. Поэтому нельзя считать «предыдущий PR был зелёным» достаточным доказательством.

Перед созданием release branch создаётся branch/tag ref, указывающий на candidate SHA, и для этого ref выполняется `workflow_dispatch`. Полученный run принимается только при точном совпадении возвращённого `headSha` с candidate SHA. В manifest фиксируются:

- commit SHA;
- workflow run ID и URL;
- conclusion каждого required workflow;
- SDK version;
- дата проверки только в human report, не в canonical hash.

## 2.3. Пакеты не хранятся в Git

Binary NuGet packages не коммитятся, потому что:

- они уже immutable-пublished на NuGet.org;
- binaries раздувают repository;
- Git review для zip payload бесполезен;
- Package Validation умеет использовать published baseline.

В Git фиксируются:

- package ID/version/source;
- SHA-256 package bytes;
- signature requirement;
- nuspec identity/dependencies;
- assembly assets/exported types;
- deterministic snapshot hashes.

CI повторно скачивает packages, verifies repository signature и сравнивает SHA-256.

## 2.4. Public API baseline — это не только `PublicAPI.Shipped.txt`

Фиксируются два независимых представления:

1. source baselines: `PublicAPI.Shipped.txt` и `PublicAPI.Unshipped.txt`;
2. фактически опубликованные assembly assets и exported type identities из `.nupkg`.

Это обнаруживает расхождения вида:

- source baseline обновлён неверно;
- тип случайно не попал в package;
- type forwarder отсутствует в packed assembly;
- project output и published package различаются.

## 2.5. Dependency graph — два уровня

Фиксируются:

- package-level direct dependencies из `.nuspec` опубликованных 2.1.2 packages;
- repository restore graph через `dotnet package list --include-transitive --format json --output-version 1`.

Первый snapshot защищает public package contract. Второй показывает build/test transitive graph и используется для обнаружения незапланированного dependency drift.

## 2.6. Governance должен проверяться кодом

Документ без CI легко устаревает. Поэтому verifier проверяет:

- все обязательные docs существуют;
- baseline SHA совпадает;
- workflows включают `release/2.2.0`;
- production files не изменены в SP220-00;
- manifest и snapshots согласованы;
- package identities и hashes совпадают.

---

# 3. Branch, review и commit policy

## 3.1. GitHub ruleset для `release/2.2.0`

Предпочтительно использовать GitHub ruleset, а не отдельную legacy branch-protection rule, потому что rulesets видимы read-only пользователям и могут наслаиваться с существующими правилами.

Требуемая конфигурация:

| Setting | Значение |
|---|---|
| Target | branch `release/2.2.0` exact match |
| Enforcement | Active |
| Restrict deletions | Enabled |
| Block force pushes | Enabled |
| Require pull request | Enabled |
| Required approvals | 1 минимум; 2 для Core/API/CI security при наличии второго reviewer |
| Dismiss stale approvals | Enabled |
| Require review of latest push | Enabled |
| Require conversation resolution | Enabled |
| Require status checks | Enabled |
| Require branch up to date | Enabled либо merge queue |
| Required checks | `CI / validation`, Windows JSON lane, CodeQL, Dependency Review, baseline contract |
| Linear history | Не включать, если merge commits используются для интеграции hotfix из `main` |
| Bypass | Только repository owner; bypass обязан быть документирован в PR |

Если repository plan не поддерживает нужный ruleset, используется branch protection с максимально эквивалентными правилами.

## 3.2. Commit policy

Один task — один reviewable commit или небольшая последовательность TDD commits. Разрешённые prefixes:

```text
docs:
chore(governance):
build(baseline):
test(baseline):
ci(baseline):
```

Запрещено:

- `fix stuff`, `update`, `misc`;
- смешивать runtime refactor с governance;
- amend уже reviewed commit после approval без повторного review;
- force-push integration branch;
- включать generated packages или `artifacts/`.

---

# 4. Baseline manifest contract

## 4.1. Canonical JSON rules

- UTF-8 без BOM;
- LF line endings;
- indentation 2 spaces;
- property order задаётся serializer model, не reflection order;
- arrays сортируются по stable identity;
- package IDs сравниваются case-insensitive, но сохраняют canonical casing;
- SHA-256 записывается lowercase hexadecimal, 64 chars;
- absolute local paths запрещены;
- timestamps запрещены в canonical snapshots;
- URL source должен быть HTTPS;
- unknown schema version — hard failure.

## 4.2. Manifest model

### Root manifest

```csharp
namespace SmartPipe.RepositoryChecks.Baselines;

internal sealed record BaselineManifest
{
    public required int SchemaVersion { get; init; }

    public required string BaselineName { get; init; }

    public required string TargetRelease { get; init; }

    public required RepositoryBaseline Repository { get; init; }

    public required IReadOnlyList<PackageBaseline> Packages { get; init; }

    public required SnapshotReference PublicApi { get; init; }

    public required SnapshotReference PackageAssets { get; init; }

    public required SnapshotReference PackageDependencies { get; init; }

    public required SnapshotReference RepositoryDependencies { get; init; }
}
```

### Repository and workflow evidence

```csharp
internal sealed record RepositoryBaseline
{
    public required string FullName { get; init; }

    public required string DefaultBranch { get; init; }

    public required string CommitSha { get; init; }

    public required string SdkVersion { get; init; }

    public required string SolutionPath { get; init; }

    public required IReadOnlyList<WorkflowBaseline> RequiredWorkflows { get; init; }
}

internal sealed record WorkflowBaseline
{
    public required string Name { get; init; }

    public required long RunId { get; init; }

    public required Uri Url { get; init; }

    public required string Conclusion { get; init; }
}
```

### Package and snapshot references

```csharp
internal sealed record PackageBaseline
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public required Uri Source { get; init; }

    public required string FileName { get; init; }

    public required string Sha256 { get; init; }

    public required bool RequireRepositorySignature { get; init; }
}

internal sealed record SnapshotReference
{
    public required string Path { get; init; }

    public required string Sha256 { get; init; }
}
```

`manifest.json` не редактируется вручную. Он создаётся `capture-baseline` и повторно проверяется `verify-baseline`.

## 4.3. Package set

В baseline входят ровно:

```text
SmartPipe.Core                2.1.2
SmartPipe.Extensions.Json     2.1.2
SmartPipe.Extensions          2.1.2
```

Новые 2.2.0 packages не имеют package baseline version, но их compatibility с legacy broad package проверяется позднее через forwarding/binary consumers.

---

# 5. Exit code contract repository-check tool

```csharp
namespace SmartPipe.RepositoryChecks.Infrastructure;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int UsageOrConfigurationError = 2;
    public const int ExternalSourceUnavailable = 3;
    public const int IntegrityOrSignatureFailure = 4;
    public const int RepositorySnapshotMismatch = 5;
    public const int UnexpectedInternalFailure = 10;
}
```

Правила:

- expected contract failure не печатает stack trace по умолчанию;
- `--verbosity diagnostic` включает exception details;
- секреты, query strings и local home paths редактируются в logs;
- network failure отличается от integrity mismatch;
- неизвестный exception возвращает 10.

---

# 6. Карта файлов и ответственность

| File | Responsibility |
|---|---|
| `docs/adr/0001-smartpipe-2.2-package-boundaries.md` | Нормативные package/layer decisions и запрещённые зависимости |
| `docs/governance/2.2.0-branch-and-review-policy.md` | Branch/ruleset/PR/commit/reviewer policy |
| `docs/plans/2.2.0-extension-architecture.md` | Полный master plan 2.2.0 |
| `docs/plans/2.2.0/SP220-00-governance-and-baseline.md` | Этот детальный исполнимый план |
| `eng/baselines/baseline.schema.json` | JSON Schema manifest v1 |
| `eng/baselines/2.1.2/manifest.json` | Canonical root baseline contract |
| `eng/baselines/2.1.2/public-api.json` | Source + package exported API snapshot |
| `eng/baselines/2.1.2/package-assets.json` | Nupkg file/assembly/type-forwarder inventory |
| `eng/baselines/2.1.2/package-dependencies.json` | Direct dependencies from nupkg nuspec |
| `eng/baselines/2.1.2/repository-dependencies.json` | Restored direct/transitive repository graph |
| `eng/baselines/2.1.2/baseline-report.md` | Human review report, CI run IDs, findings |
| `eng/SmartPipe.RepositoryChecks` | Capture/verify CLI without production references |
| `tests/SmartPipe.RepositoryChecks.Tests` | Unit/integration tests for capture/verify logic |
| `.github/workflows/*` | Run CI/security checks for integration branch |

---

# 7. Detailed implementation tasks

## Task SP220-00.1 — Preflight: confirm exact baseline and clean repository

**Files:** none initially.

**Consumes:** current clone of `MrFr3di/SmartPipe-Core`.

**Produces:** verified exact SHA, clean worktree, exact SDK and successful local baseline commands.

- [ ] **Step 1: detect whether execution is already isolated**

Run:

```bash
GIT_DIR="$(cd "$(git rev-parse --git-dir)" && pwd -P)"
GIT_COMMON="$(cd "$(git rev-parse --git-common-dir)" && pwd -P)"
BRANCH="$(git branch --show-current)"
SUPERPROJECT="$(git rev-parse --show-superproject-working-tree 2>/dev/null || true)"
printf 'git_dir=%s\ngit_common=%s\nbranch=%s\nsuperproject=%s\n' \
  "$GIT_DIR" "$GIT_COMMON" "$BRANCH" "$SUPERPROJECT"
```

Expected:

- current state is understood;
- no nested worktree is created;
- submodule is not mistaken for worktree.

- [ ] **Step 2: fetch and verify current main**

```bash
git fetch origin main --tags --prune
MAIN_SHA="$(git rev-parse origin/main)"
printf '%s\n' "$MAIN_SHA"
```

Expected at plan creation time:

```text
8e79902d22de714f493582946f7c260462b0895e
```

If SHA differs, STOP. Re-run repository review for changes since this plan and update the baseline SHA in the generated manifest. Do not silently continue with a stale SHA.

- [ ] **Step 3: ensure no local modifications**

```bash
git status --short
```

Expected: no output.

If output exists, do not stash automatically. Report changed files and require an explicit decision.

- [ ] **Step 4: verify SDK pin**

```bash
dotnet --version
dotnet --info
git show origin/main:global.json
```

Expected:

```text
10.0.302
```

and `rollForward` equals `disable`.

- [ ] **Step 5: restore in locked mode**

```bash
dotnet restore SmartPipe.Core.slnx --locked-mode
```

Expected: exit code 0 and no lock-file changes.

```bash
git status --short -- '**/packages.lock.json'
```

Expected: no output.

- [ ] **Step 6: run exact local baseline**

```bash
dotnet format SmartPipe.Core.slnx --verify-no-changes --no-restore
dotnet build SmartPipe.Core.slnx -c Release --no-restore -warnaserror
dotnet test --project tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj \
  -c Release --no-build --verbosity normal
dotnet test --project tests/SmartPipe.Extensions.Json.Tests/SmartPipe.Extensions.Json.Tests.csproj \
  -c Release --no-build --verbosity normal
dotnet test --project tests/SmartPipe.Extensions.Tests/SmartPipe.Extensions.Tests.csproj \
  -c Release --no-build --verbosity normal
```

Expected: all commands exit 0.

- [ ] **Step 7: record test counts in working notes**

Record actual discovered/passed/skipped counts. Do not hard-code counts in permanent governance policy because tests will grow; the baseline report may record them as informational evidence.

**Failure rule:** if baseline fails, SP220-00 stops. Existing failures are fixed in a separate pre-release PR before branch creation, not hidden in the architecture work.

**Commit:** none.

---

## Task SP220-00.2 — Create isolated integration worktree and branch model

**Files:**

- Create: `docs/governance/2.2.0-branch-and-review-policy.md` later in Task 4.

**Consumes:** approved `MAIN_SHA` from Task 1.

**Produces:** `release/2.2.0` branch and `sp220/00-governance` task branch in an isolated worktree.

- [ ] **Step 1: choose a safe worktree directory**

```bash
if [ -d .worktrees ]; then
  git check-ignore -q .worktrees && WORKTREE_ROOT=.worktrees
elif [ -d worktrees ]; then
  git check-ignore -q worktrees && WORKTREE_ROOT=worktrees
fi
WORKTREE_ROOT="${WORKTREE_ROOT:-C:/tmp/SmartPipe-Core-SP220-00}"
printf '%s\n' "$WORKTREE_ROOT"
```

- [ ] **Step 2: preserve the exact baseline**

The approved baseline remains exactly `8e79902d22de714f493582946f7c260462b0895e`. If neither repository-local root is already ignored, use the external `C:\tmp` path from Step 1. Do not change `.gitignore` or `main` merely to enable worktrees, and do not create untracked worktree contents inside the repository.

- [ ] **Step 3: create integration branch at exact baseline**

If branch does not exist:

```bash
git branch release/2.2.0 "$MAIN_SHA"
git push origin release/2.2.0
```

If it exists:

```bash
test "$(git rev-parse origin/release/2.2.0)" = "$MAIN_SHA"
```

Expected: equality. Any different existing SHA requires review; never force-update it.

- [ ] **Step 4: create task worktree**

```bash
git worktree add \
  "$WORKTREE_ROOT/sp220-00-governance" \
  -b sp220/00-governance \
  release/2.2.0
cd "$WORKTREE_ROOT/sp220-00-governance"
```

If already in a harness-managed worktree, create only the task branch in the managed workspace and do not nest worktrees.

- [ ] **Step 5: verify branch state**

```bash
git branch --show-current
git merge-base --is-ancestor release/2.2.0 HEAD
git status --short
```

Expected:

```text
sp220/00-governance
```

and clean status.

**Commit:** none.

---

## Task SP220-00.3 — Obtain exact-head CI evidence and protect release branch

**Files:**

- Modify: `.github/workflows/ci.yml`
- Modify: `.github/workflows/codeql.yml`
- Modify: `.github/workflows/dependency-review.yml`
- Create later: `docs/governance/2.2.0-branch-and-review-policy.md`

**Consumes:** `release/2.2.0` branch.

**Produces:** branch-specific CI coverage and recorded exact-head run evidence.

### Interfaces

- Required workflow names remain unique.
- Existing workflow implementation must not be duplicated.
- `reusable-release-validation.yml` remains the single main validation implementation.

- [ ] **Step 1: write failing workflow policy tests**

Create test data expectations in:

```text
tests/SmartPipe.RepositoryChecks.Tests/Repository/WorkflowPolicyReaderTests.cs
```

Initial test contract:

```csharp
[Fact]
public void RequiredWorkflows_TargetReleaseBranch()
{
    var snapshot = WorkflowPolicyReader.Read(_repositoryRoot);

    snapshot.RequireWorkflow("CI")
        .ShouldTargetPushBranch("release/2.2.0")
        .ShouldTargetPullRequestBranch("release/2.2.0");

    snapshot.RequireWorkflow("CodeQL")
        .ShouldTargetPushBranch("release/2.2.0")
        .ShouldTargetPullRequestBranch("release/2.2.0");

    snapshot.RequireWorkflow("Dependency Review")
        .ShouldTargetPullRequestBranch("release/2.2.0");
}
```

At this point the test project may not yet exist. Commit the test together with the minimal parser in Task 10; for Task 3 perform an explicit textual precheck and keep the test requirement in the plan.

- [ ] **Step 2: update CI triggers**

`ci.yml` target lists must become:

```yaml
on:
  workflow_dispatch:
  push:
    branches: [main, upd, release/2.2.0]
  pull_request:
    branches: [main, upd, release/2.2.0]
```

Do not copy the validation job.

- [ ] **Step 3: update CodeQL triggers**

```yaml
on:
  push:
    branches: [main, upd, release/2.2.0]
  pull_request:
    branches: [main, release/2.2.0]
```

Keep schedule unchanged.

- [ ] **Step 4: update Dependency Review trigger**

```yaml
on:
  pull_request:
    branches: [main, release/2.2.0]
```

- [ ] **Step 5: validate YAML and whitespace**

```bash
git diff --check
git diff -- .github/workflows
```

Build/test workflows must remain SHA-pinned and `persist-credentials: false` where checkout is used.

- [ ] **Step 6: dispatch exact-head CI on integration branch**

After commit/push of workflow trigger changes, dispatch:

```bash
gh workflow run ci.yml --ref release/2.2.0
```

Then obtain run:

```bash
gh run list \
  --workflow ci.yml \
  --branch release/2.2.0 \
  --limit 5 \
  --json databaseId,headSha,status,conclusion,url,event,createdAt
```

Select only a run whose `headSha` equals the branch head. Do not accept a successful run for an older SHA.

- [ ] **Step 7: require security workflows on PR**

Open the SP220-00 PR into `release/2.2.0`; verify CI, CodeQL and Dependency Review attach to this PR.

- [ ] **Step 8: repository owner applies ruleset**

This is an owner/admin action. Capture evidence in baseline report:

```text
Ruleset name
Ruleset ID
Target branch
Enforcement status
Required checks
Bypass actors
Verification date
```

If ruleset cannot be applied because of plan/permission limitations, the PR is not merge-ready until an equivalent branch protection rule is active.

- [ ] **Step 9: commit workflow changes**

```bash
git add .github/workflows/ci.yml \
        .github/workflows/codeql.yml \
        .github/workflows/dependency-review.yml
git commit -m "ci(baseline): validate the 2.2.0 integration branch"
```

---

## Task SP220-00.4 — Write ADR, governance policy and commit master plan

**Files:**

- Create: `docs/adr/0001-smartpipe-2.2-package-boundaries.md`
- Create: `docs/governance/2.2.0-branch-and-review-policy.md`
- Create: `docs/plans/2.2.0-extension-architecture.md`
- Create: `docs/plans/2.2.0/SP220-00-governance-and-baseline.md`
- Modify: documentation index/README if the repository has one.

**Consumes:** approved architecture plan and branch policy.

**Produces:** normative decisions reviewable in Git.

- [ ] **Step 1: write ADR header**

```markdown
# ADR-0001: SmartPipe 2.2 package boundaries and integration model

- Status: Accepted for implementation
- Date: 2026-07-15
- Decision owners: SmartPipe maintainers
- Target release: 2.2.0
- Baseline commit: 8e79902d22de714f493582946f7c260462b0895e
```

The baseline SHA is fixed at the exact reviewed value above; Task 2 uses an external worktree when necessary and does not introduce an ignore commit.

- [ ] **Step 2: ADR must contain all decisions**

Mandatory headings:

```text
Context
Decision
Package map
Allowed dependency direction
Forbidden dependencies
Core/definition/run separation
PipelineKey identity
DI scope ownership
Compatibility/type forwarding
AOT and trimming policy
Role of SmartPipe.Extensions
Alternatives rejected
Consequences
Enforcement
Supersession policy
```

- [ ] **Step 3: document rejected alternatives with reasons**

At minimum:

- keep monolithic Extensions;
- create `SmartPipe.Abstractions` now;
- reflection-based plugin discovery;
- `SmartPipe.Extensions.All` in addition to existing package;
- one package per class;
- embedded exporters in Core;
- exact upper-bounded internal package ranges;
- hidden multiple retry layers.

- [ ] **Step 4: write branch/review policy**

Must include:

- branch graph;
- ruleset settings;
- PR target rules;
- required reviewer categories;
- no force push/deletion;
- merge strategy;
- hotfix sync policy;
- commit naming;
- generated files policy;
- emergency bypass audit requirements;
- definition of ready/review/done for every EPIC.

- [ ] **Step 5: copy master architecture plan unchanged in meaning**

Place the previously approved plan at:

```text
docs/plans/2.2.0-extension-architecture.md
```

Convert external chat-specific language into repository language, but do not remove requirements.

- [ ] **Step 6: place this detailed plan**

```text
docs/plans/2.2.0/SP220-00-governance-and-baseline.md
```

- [ ] **Step 7: link documents**

ADR links to master plan and governance policy. Master plan links to detailed EPIC plans. Avoid duplicate normative text where a stable link is sufficient; if duplicated, ADR wins on architecture decisions.

- [ ] **Step 8: documentation validation**

```bash
git diff --check
```

Run existing link checker locally if available. All links must be relative for repository documents and HTTPS for external sources.

- [ ] **Step 9: commit**

```bash
git add docs/adr \
        docs/governance \
        docs/plans
git commit -m "docs: establish SmartPipe 2.2 architecture governance"
```

---

## Task SP220-00.5 — Define baseline schema and deterministic serialization

**Files:**

- Create: `eng/baselines/baseline.schema.json`
- Create: `eng/baselines/README.md`
- Create: `eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj`
- Create: `eng/SmartPipe.RepositoryChecks/Baselines/BaselineManifest.cs`
- Create: `eng/SmartPipe.RepositoryChecks/Baselines/BaselineManifestSerializer.cs`
- Create: `eng/SmartPipe.RepositoryChecks/Infrastructure/CanonicalJson.cs`
- Create: `eng/SmartPipe.RepositoryChecks/Infrastructure/CanonicalText.cs`
- Create: `eng/SmartPipe.RepositoryChecks/Infrastructure/Hashing.cs`
- Test: `tests/SmartPipe.RepositoryChecks.Tests/Baselines/BaselineManifestSerializerTests.cs`
- Test: `tests/SmartPipe.RepositoryChecks.Tests/Infrastructure/CanonicalTextTests.cs`

**Consumes:** manifest contract in Section 4.

**Produces:** schema v1 and deterministic serializers used by all following tasks.

- [ ] **Step 1: create tool project without third-party runtime dependencies**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: write failing deterministic serialization test**

```csharp
[Fact]
public void Serialize_IsByteStableAcrossRepeatedCalls()
{
    var manifest = BaselineFixtures.CreateManifest();

    var first = BaselineManifestSerializer.Serialize(manifest);
    var second = BaselineManifestSerializer.Serialize(manifest);

    Assert.Equal(first, second);
    Assert.DoesNotContain("\r", first, StringComparison.Ordinal);
    Assert.False(first.AsSpan().StartsWith("\uFEFF"));
}
```

- [ ] **Step 3: run test and confirm failure**

```bash
dotnet test --project tests/SmartPipe.RepositoryChecks.Tests/SmartPipe.RepositoryChecks.Tests.csproj \
  -c Release --filter-class BaselineManifestSerializerTests --minimum-expected-tests 1
```

Expected: compile failure because serializer does not exist.

- [ ] **Step 4: implement source-generated JSON context**

Use explicit serializer options and generated metadata:

```csharp
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BaselineManifest))]
internal partial class BaselineJsonContext : JsonSerializerContext;
```

`Serialize` must append exactly one final LF.

- [ ] **Step 5: implement canonical text**

```csharp
internal static class CanonicalText
{
    public static byte[] ToUtf8Bytes(ReadOnlySpan<byte> input)
    {
        var text = Encoding.UTF8.GetString(input);
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                   .Replace("\r", "\n", StringComparison.Ordinal);

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(text);
    }
}
```

Do not trim whitespace or final newlines: whitespace changes in API files must remain detectable.

- [ ] **Step 6: implement hashing**

```csharp
internal static class Hashing
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static async Task<string> Sha256FileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
```

- [ ] **Step 7: add schema validation tests**

Tests must verify:

- `schemaVersion` required and const 1;
- commit SHA regex `^[0-9a-f]{40}$`;
- hashes regex `^[0-9a-f]{64}$`;
- source URL HTTPS;
- exactly three baseline packages;
- snapshot paths are relative and cannot contain `..`;
- unknown additional properties are rejected in manifest schema.

- [ ] **Step 8: run tests**

```bash
dotnet test --project tests/SmartPipe.RepositoryChecks.Tests/SmartPipe.RepositoryChecks.Tests.csproj \
  -c Release --filter-class BaselineManifestSerializerTests --minimum-expected-tests 1
```

Expected: pass.

- [ ] **Step 9: commit**

```bash
git add eng/baselines \
        eng/SmartPipe.RepositoryChecks \
        tests/SmartPipe.RepositoryChecks.Tests
git commit -m "build(baseline): define the reproducible baseline manifest"
```

---

## Task SP220-00.6 — Implement NuGet V3 package acquisition and signature verification

**Files:**

- Create: `NuGetServiceIndexClient.cs`
- Create: `NuGetPackageFetcher.cs`
- Create: `NuGetPackageSignatureVerifier.cs`
- Create: `ProcessRunner.cs`
- Test: `NuGetServiceIndexClientTests.cs`
- Test: `NuGetPackageFetcherTests.cs`
- Test: `NuGetPackageSignatureVerifierTests.cs`

**Consumes:** package IDs/version from manifest input.

**Produces:** local verified nupkg files.

### Interfaces

```csharp
internal interface INuGetServiceIndexClient
{
    Task<Uri> GetPackageBaseAddressAsync(CancellationToken cancellationToken);
}

internal interface INuGetPackageFetcher
{
    Task<string> FetchAsync(
        string packageId,
        string version,
        string destinationDirectory,
        CancellationToken cancellationToken);
}

internal interface INuGetPackageSignatureVerifier
{
    Task VerifyAsync(string packagePath, CancellationToken cancellationToken);
}
```

- [ ] **Step 1: failing service-index tests**

Cover:

```text
PackageBaseAddress/3.0.0 found
resource type represented as string
resource type represented as array
missing resource -> configuration error
non-HTTPS @id -> configuration error
malformed JSON -> external source error
```

Use a fake `HttpMessageHandler`; no real network.

- [ ] **Step 2: implement service-index parsing**

Default index:

```text
https://api.nuget.org/v3/index.json
```

Find a resource whose `@type` equals or contains `PackageBaseAddress/3.0.0`.

- [ ] **Step 3: failing package URL test**

```csharp
[Theory]
[InlineData(
    "SmartPipe.Extensions.Json",
    "2.1.2",
    "smartpipe.extensions.json/2.1.2/smartpipe.extensions.json.2.1.2.nupkg")]
public void BuildPackageUri_UsesLowercaseFlatContainerPath(...)
```

- [ ] **Step 4: implement atomic download**

Rules:

- download to `<file>.partial`;
- use `ResponseHeadersRead`;
- max package size default 100 MiB;
- write asynchronously;
- delete partial file on failure/cancellation;
- `File.Move(partial, final, overwrite: true)` only after complete download;
- bounded retry for 408/429/5xx: maximum 3 attempts, respect `Retry-After`, no retry for 404;
- log package ID/version but not query strings.

- [ ] **Step 5: negative download tests**

Cover:

- 404 -> external source unavailable with clear package identity;
- content over size limit -> integrity failure;
- cancellation removes `.partial`;
- second attempt after 503 succeeds;
- exhausted retries leave no final file;
- existing final file is reused only after later hash verification.

- [ ] **Step 6: implement signature verifier through CLI**

Command:

```text
dotnet nuget verify <package> --all --verbosity normal
```

`ProcessRunner` must pass arguments as an argument list, never shell-concatenate paths.

- [ ] **Step 7: signature tests**

Use fake process runner:

- exit 0 -> pass;
- exit non-zero -> integrity/signature failure;
- timeout/cancellation -> external source/tool failure;
- stderr does not leak full home path in normal output.

- [ ] **Step 8: run tests**

```bash
dotnet test --project tests/SmartPipe.RepositoryChecks.Tests/SmartPipe.RepositoryChecks.Tests.csproj \
  -c Release \
  --filter-query "/[Namespace=SmartPipe.RepositoryChecks.Tests.NuGet]" \
  --minimum-expected-tests 1
```

- [ ] **Step 9: commit**

```bash
git add eng/SmartPipe.RepositoryChecks/NuGet \
        eng/SmartPipe.RepositoryChecks/Infrastructure/ProcessRunner.cs \
        tests/SmartPipe.RepositoryChecks.Tests/NuGet
git commit -m "build(baseline): fetch and verify published NuGet packages"
```

---

## Task SP220-00.7 — Read nupkg identities, dependencies, assemblies and exported types

**Files:**

- Create: `NuGetPackageReader.cs`
- Create models: `PackageAssetSnapshot.cs`, `PackageDependencySnapshot.cs`
- Test fixtures: synthetic nupkg zip files generated at test runtime.

**Consumes:** verified local nupkg.

**Produces:** canonical package asset/dependency snapshots.

- [ ] **Step 1: write failing package identity test**

Create nupkg in a temp directory with one nuspec and assert:

```csharp
var result = await reader.ReadAsync(path, cancellationToken);
Assert.Equal("SmartPipe.Core", result.Id);
Assert.Equal("2.1.2", result.Version);
```

- [ ] **Step 2: parse without extracting archive**

Use `ZipArchive` and read entries as streams. Never call `ExtractToDirectory`; this avoids path traversal and unnecessary filesystem state.

Require exactly one root `.nuspec`. Reject:

- no nuspec;
- multiple nuspecs;
- ID/version mismatch with requested package;
- duplicate dependency groups for same TFM;
- invalid XML external entity behavior. Use safe `XmlReaderSettings` with DTD prohibited.

- [ ] **Step 3: snapshot direct dependencies**

Canonical structure:

```json
{
  "packageId": "SmartPipe.Extensions.Json",
  "version": "2.1.2",
  "groups": [
    {
      "targetFramework": "net10.0",
      "dependencies": [
        { "id": "Microsoft.Extensions.Logging.Abstractions", "versionRange": "10.0.8" },
        { "id": "SmartPipe.Core", "versionRange": "2.1.2" }
      ]
    }
  ]
}
```

Sort groups by framework and dependencies by package ID case-insensitively.

- [ ] **Step 4: enumerate package files**

Record for each entry:

```text
path
uncompressedLength
sha256 of entry bytes
category: assembly/xml-doc/pdb/readme/icon/nuspec/other
```

Do not include ZIP timestamps or compression metadata.

- [ ] **Step 5: inspect managed assemblies safely**

Use `PEReader` + `MetadataReader`, not `Assembly.LoadFrom`.

Record:

```text
assembly name
assembly version
culture
public key token
TFM asset path
exported public type full names
type forwarder full names
```

Exclude compiler-generated nested private types. Include public nested types with canonical `Outer+Inner` identity.

- [ ] **Step 6: tests**

Minimum cases:

- normal lib/net10.0 assembly;
- ref assembly and lib assembly both present;
- type forwarders;
- package without assembly;
- invalid PE file in lib path -> hard failure;
- duplicate assembly identity in same TFM -> hard failure;
- order of ZIP entries does not change snapshot;
- ZIP timestamps do not change snapshot;
- XML formatting differences with same nuspec semantics produce same dependency snapshot but different entry hash.

- [ ] **Step 7: run tests and commit**

```bash
dotnet test --project tests/SmartPipe.RepositoryChecks.Tests/SmartPipe.RepositoryChecks.Tests.csproj \
  -c Release --filter-class NuGetPackageReaderTests --minimum-expected-tests 1

git add eng/SmartPipe.RepositoryChecks/NuGet \
        eng/SmartPipe.RepositoryChecks/Baselines \
        tests/SmartPipe.RepositoryChecks.Tests/NuGet
git commit -m "build(baseline): snapshot package API and dependency assets"
```

---

## Task SP220-00.8 — Capture repository public API and restored dependency graph

**Files:**

- Create: `RepositorySnapshotReader.cs`
- Create: `PublicApiSnapshotReader.cs`
- Create: `ProjectDependencySnapshotReader.cs`
- Tests under `tests/.../Repository`.

**Consumes:** clean repository at baseline SHA and successful locked restore.

**Produces:** source API and repository dependency snapshots.

- [ ] **Step 1: enumerate packable projects**

Read `SmartPipe.Core.slnx` and project files. For baseline 2.1.2 expected production packable projects:

```text
src/SmartPipe.Core/SmartPipe.Core.csproj
src/SmartPipe.Extensions.Json/SmartPipe.Extensions.Json.csproj
src/SmartPipe.Extensions/SmartPipe.Extensions.csproj
```

Do not hard-code these only in code; store expectations in generated snapshot and verify any addition/removal explicitly.

- [ ] **Step 2: capture project identities**

For each production project capture evaluated MSBuild properties through:

```bash
dotnet msbuild <project> -nologo \
  -getProperty:PackageId \
  -getProperty:Version \
  -getProperty:TargetFramework \
  -getProperty:IsPackable \
  -getProperty:AssemblyName
```

Use process argument list. Reject empty PackageId/Version/TFM.

- [ ] **Step 3: capture PublicAPI files**

For every packable project discover:

```text
PublicAPI.Shipped.txt
PublicAPI.Unshipped.txt
```

Record:

- relative path;
- canonical text SHA-256;
- line count;
- non-empty API entry count;
- first/last API entry for report only.

Do not sort file contents: order is part of source baseline. Do not auto-move Unshipped to Shipped.

- [ ] **Step 4: capture direct ProjectReference and PackageReference**

Parse project XML for human-friendly direct graph. Record conditions and `PrivateAssets`/`IncludeAssets`/`ExcludeAssets` metadata.

- [ ] **Step 5: capture evaluated restore graph**

Run:

```bash
dotnet package list \
  --project SmartPipe.Core.slnx \
  --include-transitive \
  --format json \
  --output-version 1 \
  --no-restore
```

Canonicalize output:

- remove absolute paths;
- sort projects by relative path;
- sort frameworks;
- sort top-level and transitive packages by ID;
- preserve requested and resolved versions;
- preserve auto-reference marker if present.

- [ ] **Step 6: tests**

Minimum cases:

- CRLF and LF PublicAPI files have same canonical hash;
- UTF-8 BOM ignored;
- whitespace inside API entry changes hash;
- missing Shipped file fails for packable project;
- unexpected public API file outside project is reported;
- ProjectReference condition preserved;
- PrivateAssets metadata preserved;
- absolute paths removed from package-list JSON;
- array order differences canonicalize identically;
- resolved version change changes snapshot hash.

- [ ] **Step 7: commit**

```bash
git add eng/SmartPipe.RepositoryChecks/Repository \
        tests/SmartPipe.RepositoryChecks.Tests/Repository
git commit -m "build(baseline): capture repository API and dependency contracts"
```

---

## Task SP220-00.9 — Implement capture and verification orchestration

**Files:**

- Create: `Program.cs`
- Create: `CaptureBaselineCommand.cs`
- Create: `VerifyBaselineCommand.cs`
- Create: `BaselineCaptureService.cs`
- Create: `BaselineVerificationService.cs`
- Create: `BaselineVerificationResult.cs`
- Create: `ExitCodes.cs`
- Tests: command and end-to-end temp-repository tests.

**Consumes:** all lower-level readers/fetchers.

**Produces:** CLI commands used by maintainers and CI.

## CLI contract

```text
SmartPipe.RepositoryChecks capture-baseline
  --repo-root <path>
  --repository MrFr3di/SmartPipe-Core
  --commit <40-hex>
  --target-release 2.2.0
  --baseline-version 2.1.2
  --packages-dir <path>
  --output-dir eng/baselines/2.1.2
  --workflow-evidence <path-to-json>

SmartPipe.RepositoryChecks verify-baseline
  --repo-root <path>
  --manifest eng/baselines/2.1.2/manifest.json
  --packages-dir artifacts/baselines/2.1.2
  --offline
```

`capture-baseline` may access network. `verify-baseline --offline` must not.

- [ ] **Step 1: failing command parser tests**

Cover:

- missing command;
- unknown command;
- missing required option;
- invalid SHA;
- invalid semantic version;
- output outside repository via `..`;
- `--offline` with missing package;
- duplicate option.

- [ ] **Step 2: implement explicit parser**

Do not add System.CommandLine solely for two commands. Use a small explicit parser with clear errors and tests. No reflection binding.

- [ ] **Step 3: implement capture transaction**

Capture writes into temporary directory:

```text
eng/baselines/.2.1.2.capture-<guid>/
```

Order:

1. validate clean repo and commit;
2. verify SDK/global.json;
3. fetch packages;
4. signature-verify packages;
5. parse package snapshots;
6. capture repository snapshots;
7. generate all JSON files;
8. compute snapshot hashes;
9. write manifest last;
10. verify temp output against itself;
11. atomically replace target directory.

If any step fails, existing baseline directory remains untouched.

- [ ] **Step 4: implement verify service**

Verification order:

1. schema/version/path safety;
2. repository full name/commit/global.json;
3. required files present;
4. snapshot file hashes;
5. package file hashes;
6. package signatures;
7. package identities and package-internal snapshots;
8. source public API snapshot;
9. repository dependency snapshot;
10. workflow policy.

Report all independent mismatches where safe, rather than stopping after first, but stop immediately on unsafe schema/path input.

- [ ] **Step 5: deterministic diagnostic format**

Example:

```text
BASELINE VERIFICATION FAILED
[SPB001] Repository commit mismatch
  expected: 8e79902d22de714f493582946f7c260462b0895e
  actual:   0123456789abcdef...
[SPB014] Public API snapshot mismatch
  path: src/SmartPipe.Core/PublicAPI.Shipped.txt
```

Codes are stable and documented in `eng/baselines/README.md`.

- [ ] **Step 6: end-to-end tests**

Use temp repository fixture and synthetic nupkgs. Test:

- capture then offline verify passes;
- failed capture does not replace existing baseline;
- manifest mutation fails;
- package byte mutation fails before parsing;
- public API mutation fails;
- direct PackageReference mutation fails;
- workflow branch removal fails;
- unknown snapshot file added does not alter baseline unless referenced;
- path traversal in manifest is rejected;
- cancellation cleans temporary capture directory.

- [ ] **Step 7: run tests**

```bash
dotnet test --project tests/SmartPipe.RepositoryChecks.Tests/SmartPipe.RepositoryChecks.Tests.csproj \
  -c Release --verbosity normal
```

- [ ] **Step 8: commit**

```bash
git add eng/SmartPipe.RepositoryChecks \
        tests/SmartPipe.RepositoryChecks.Tests
git commit -m "build(baseline): add capture and verification commands"
```

---

## Task SP220-00.10 — Capture the real 2.1.2 baseline

**Files generated:**

- `eng/baselines/2.1.2/manifest.json`
- `public-api.json`
- `package-assets.json`
- `package-dependencies.json`
- `repository-dependencies.json`
- `baseline-report.md`

**Consumes:** exact packages from NuGet.org and exact repository SHA.

**Produces:** reviewed real baseline.

- [ ] **Step 1: create ignored artifact directory**

```bash
mkdir -p artifacts/baselines/2.1.2
```

Verify `artifacts/` is ignored:

```bash
git check-ignore -q artifacts/baselines/2.1.2
```

If not ignored, add `/artifacts/` to `.gitignore` before downloading.

- [ ] **Step 2: obtain workflow evidence JSON**

```bash
gh run list \
  --branch release/2.2.0 \
  --limit 20 \
  --json databaseId,workflowName,headSha,status,conclusion,url,event,createdAt \
  > artifacts/baselines/2.1.2/workflow-evidence.json
```

Manually verify each recorded run matches current branch SHA. The capture tool must reject mixed head SHAs.

- [ ] **Step 3: run capture**

```bash
dotnet run \
  --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj \
  -c Release -- \
  capture-baseline \
  --repo-root . \
  --repository MrFr3di/SmartPipe-Core \
  --commit "$(git rev-parse HEAD)" \
  --target-release 2.2.0 \
  --baseline-version 2.1.2 \
  --packages-dir artifacts/baselines/2.1.2 \
  --output-dir eng/baselines/2.1.2 \
  --workflow-evidence artifacts/baselines/2.1.2/workflow-evidence.json
```

Expected:

```text
Fetched and verified SmartPipe.Core 2.1.2
Fetched and verified SmartPipe.Extensions.Json 2.1.2
Fetched and verified SmartPipe.Extensions 2.1.2
Baseline captured successfully.
```

- [ ] **Step 4: offline verification**

Disconnect/network-block is preferable for this check where practical:

```bash
dotnet run \
  --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj \
  -c Release -- \
  verify-baseline \
  --repo-root . \
  --manifest eng/baselines/2.1.2/manifest.json \
  --packages-dir artifacts/baselines/2.1.2 \
  --offline
```

Expected exit code 0.

- [ ] **Step 5: human review package snapshots**

Review at minimum:

- package IDs and versions;
- nupkg hashes are non-empty 64-char lowercase hex;
- only expected direct package dependencies;
- expected `lib/net10.0` assemblies;
- type forwarders in broad Extensions;
- no unexpected executable/native payload;
- README/icon/license/nuspec present;
- package assets do not contain local absolute paths.

- [ ] **Step 6: human review PublicAPI snapshot**

Compare source API files and package exported types. Any mismatch must be explained in report; do not normalize it away.

- [ ] **Step 7: human review repository dependencies**

Flag:

- duplicate direct packages with inconsistent versions;
- unexpected transitive packages;
- prerelease dependencies;
- package sources other than approved sources;
- dependencies outside expected target framework.

- [ ] **Step 8: commit generated baseline**

```bash
git add eng/baselines/2.1.2
git commit -m "build(baseline): pin the published 2.1.2 contract"
```

Do not add `artifacts/baselines`.

---

## Task SP220-00.11 — Add comprehensive baseline contract tests

**Files:**

- Complete `tests/SmartPipe.RepositoryChecks.Tests` project.
- Modify `SmartPipe.Core.slnx` to include repository checks tool/test project.

**Consumes:** real baseline and tool.

**Produces:** automated regression suite.

## Mandatory test matrix

### Manifest/schema

- [ ] valid manifest accepted;
- [ ] schema version 0/2 rejected;
- [ ] unknown property rejected;
- [ ] invalid SHA/hash rejected;
- [ ] non-HTTPS source rejected;
- [ ] absolute snapshot path rejected;
- [ ] `..` traversal rejected;
- [ ] duplicate package ID rejected case-insensitively;
- [ ] missing required package rejected.

### Canonicalization

- [ ] repeated serialization byte-identical;
- [ ] LF/CRLF canonical text hash equal;
- [ ] BOM/no-BOM hash equal;
- [ ] content whitespace changes hash;
- [ ] JSON object input order does not affect canonical output where input is semantically modeled;
- [ ] array order is explicitly sorted by identity.

### NuGet fetch/integrity

- [ ] service index parsing;
- [ ] 404 failure;
- [ ] bounded retry;
- [ ] cancellation cleanup;
- [ ] size limit;
- [ ] package hash mismatch;
- [ ] signature verifier non-zero exit;
- [ ] identity/version mismatch;
- [ ] malformed/multiple nuspec;
- [ ] unsafe XML rejected;
- [ ] package ZIP order/timestamps ignored semantically.

### Assembly/API

- [ ] exported type enumeration;
- [ ] type forwarder enumeration;
- [ ] ref/lib asset distinction;
- [ ] malformed PE fails;
- [ ] duplicate assembly identity fails;
- [ ] source PublicAPI missing fails;
- [ ] source API mutation detected.

### Repository graph

- [ ] direct ProjectReference captured;
- [ ] conditional PackageReference captured;
- [ ] PrivateAssets captured;
- [ ] resolved transitive version mutation detected;
- [ ] absolute paths eliminated;
- [ ] new packable project detected.

### Workflow/governance

- [ ] CI targets release branch for push and PR;
- [ ] CodeQL targets release branch;
- [ ] Dependency Review targets release PR;
- [ ] workflow duplicate name ambiguity reported;
- [ ] required document missing fails;
- [ ] production file modification forbidden by SP220-00 mode.

### End-to-end

- [ ] capture/verify round trip;
- [ ] capture rollback on failure;
- [ ] offline verify performs zero HTTP calls;
- [ ] all mismatch diagnostics stable and deterministic;
- [ ] no secret/absolute-home leakage in normal logs.

- [ ] **Step 1: ensure test discovery cannot be zero**

CI command must use:

```bash
dotnet test --project tests/SmartPipe.RepositoryChecks.Tests/SmartPipe.RepositoryChecks.Tests.csproj \
  -c Release --minimum-expected-tests 1
```

- [ ] **Step 2: run tests on Linux and Windows**

File/path/canonicalization tests must run both OSes. Tests must not assume `/` or case-sensitive filesystem unless marked platform-specific with explicit rationale.

- [ ] **Step 3: full solution verification**

```bash
dotnet restore SmartPipe.Core.slnx --locked-mode
dotnet build SmartPipe.Core.slnx -c Release --no-restore -warnaserror
dotnet test --project tests/SmartPipe.RepositoryChecks.Tests/SmartPipe.RepositoryChecks.Tests.csproj \
  -c Release --no-build --minimum-expected-tests 1
```

- [ ] **Step 4: commit**

```bash
git add SmartPipe.Core.slnx \
        tests/SmartPipe.RepositoryChecks.Tests
git commit -m "test(baseline): cover governance and snapshot failures"
```

---

## Task SP220-00.12 — Add CI baseline-contract gate

**Files:**

- Modify: `.github/workflows/reusable-release-validation.yml`
- Modify: `.github/workflows/ci.yml` Windows job or add a focused cross-platform matrix job.

**Consumes:** committed baseline snapshots and verifier.

**Produces:** fail-closed CI gate.

- [ ] **Step 1: add repository-check tool tests to main validation**

After locked restore/build:

```yaml
- name: Repository baseline tests
  run: >
    dotnet test
    --project tests/SmartPipe.RepositoryChecks.Tests/SmartPipe.RepositoryChecks.Tests.csproj
    --configuration Release
    --no-build
    --minimum-expected-tests 1
```

- [ ] **Step 2: fetch baseline packages in CI cache directory**

Do not cache packages without verifying hash. Cache is optimization only.

```yaml
- name: Fetch and verify 2.1.2 baseline packages
  run: >
    dotnet run
    --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj
    --configuration Release
    --no-build
    --
    verify-baseline
    --repo-root .
    --manifest eng/baselines/2.1.2/manifest.json
    --packages-dir artifacts/baselines/2.1.2
```

Default verify may fetch missing package files and then performs signature/hash verification. A second explicit offline invocation proves deterministic local verification:

```yaml
- name: Verify 2.1.2 baseline offline
  run: >
    dotnet run
    --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj
    --configuration Release
    --no-build
    --
    verify-baseline
    --repo-root .
    --manifest eng/baselines/2.1.2/manifest.json
    --packages-dir artifacts/baselines/2.1.2
    --offline
```

- [ ] **Step 3: add Windows verifier lane**

Run at least:

- canonical text/path tests;
- package hash verification;
- source API snapshot verification;
- workflow policy verification.

This catches case/path/newline assumptions.

- [ ] **Step 4: verify no production changes in SP220-00 PR**

A temporary PR-scope gate may run:

```bash
git diff --name-only "${{ github.event.pull_request.base.sha }}" "${{ github.sha }}" \
  | grep -E '^src/SmartPipe\.(Core|Extensions)' \
  && { echo 'SP220-00 must not change production sources.'; exit 1; } \
  || true
```

Implement this robustly in repository-check tool rather than relying permanently on fragile grep. Scope gate can be removed after SP220-00 merge; baseline verifier remains.

- [ ] **Step 5: unique check names**

Ensure required job/check names are unique across workflows to avoid ambiguous required statuses.

- [ ] **Step 6: run workflow contract tests**

Use existing workflow mutation/contract test mechanism. Add negative mutations:

- remove release branch from CI;
- remove baseline verification step;
- make offline step network-capable;
- remove `--minimum-expected-tests 1`;
- duplicate required job name.

- [ ] **Step 7: commit**

```bash
git add .github/workflows \
        eng \
        tests
git commit -m "ci(baseline): enforce the published 2.1.2 contract"
```

---

## Task SP220-00.13 — Final audit and checkpoint A handoff

**Files:**

- Update: `eng/baselines/2.1.2/baseline-report.md`
- Update: detailed plan checkboxes only after evidence exists.

**Consumes:** completed tasks 1–12.

**Produces:** merge-ready SP220-00 PR and clean handoff to SP220-01.

- [ ] **Step 1: verify branch ancestry**

```bash
git fetch origin main release/2.2.0
git merge-base --is-ancestor "$BASELINE_SHA" HEAD
git log --oneline --decorate "$BASELINE_SHA..HEAD"
```

- [ ] **Step 2: prove no production runtime changes**

```bash
git diff --name-only "$BASELINE_SHA..HEAD" -- \
  src/SmartPipe.Core \
  src/SmartPipe.Extensions \
  src/SmartPipe.Extensions.Json
```

Expected: no output, except project metadata only if explicitly approved. Preferred result is strictly no production path changes.

- [ ] **Step 3: run complete local validation**

```bash
dotnet restore SmartPipe.Core.slnx --locked-mode
dotnet format SmartPipe.Core.slnx --verify-no-changes --no-restore
dotnet build SmartPipe.Core.slnx -c Release --no-restore -warnaserror

dotnet test --project tests/SmartPipe.Core.Tests/SmartPipe.Core.Tests.csproj \
  -c Release --no-build --verbosity normal

dotnet test --project tests/SmartPipe.Extensions.Json.Tests/SmartPipe.Extensions.Json.Tests.csproj \
  -c Release --no-build --verbosity normal

dotnet test --project tests/SmartPipe.Extensions.Tests/SmartPipe.Extensions.Tests.csproj \
  -c Release --no-build --verbosity normal

dotnet test --project tests/SmartPipe.RepositoryChecks.Tests/SmartPipe.RepositoryChecks.Tests.csproj \
  -c Release --no-build --minimum-expected-tests 1

dotnet run --project eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj \
  -c Release --no-build -- \
  verify-baseline \
  --repo-root . \
  --manifest eng/baselines/2.1.2/manifest.json \
  --packages-dir artifacts/baselines/2.1.2 \
  --offline
```

- [ ] **Step 4: inspect git state**

```bash
git status --short
git diff --check "$BASELINE_SHA..HEAD"
git ls-files artifacts
```

Expected:

- clean working tree;
- no whitespace errors;
- no tracked artifacts.

- [ ] **Step 5: review generated files**

Confirm all generated JSON is canonical and no absolute path, access token, email, username or machine-specific information is present.

- [ ] **Step 6: PR description**

PR must include:

```markdown
## Scope
Governance and reproducible 2.1.2 baseline only. No production runtime changes.

## Baseline
- main SHA: ...
- SDK: 10.0.302
- package baseline: 2.1.2
- exact-head CI run: ...

## Evidence
- package signatures verified
- package hashes pinned
- public API snapshot captured
- direct/transitive dependency snapshots captured
- Linux and Windows baseline tests passed

## Excluded
- no Core API changes
- no package split yet
- no dependency version migration yet
```

- [ ] **Step 7: required reviews**

At minimum review categories:

- architecture/package boundaries;
- build/release/security;
- test determinism/cross-platform behavior.

One person may cover multiple categories in a single-maintainer repository, but the PR checklist must explicitly address each category.

- [ ] **Step 8: merge and re-run integration branch**

Merge SP220-00 PR into `release/2.2.0`, then require exact-head CI on the resulting merge SHA.

- [ ] **Step 9: final report**

Update `baseline-report.md` with:

- final merge SHA;
- run IDs/URLs and conclusions;
- package hashes;
- test counts;
- ruleset evidence;
- deviations (expected none);
- sign-off.

- [ ] **Step 10: checkpoint A exit condition**

SP220-01 may start only if:

```text
baseline verifier = PASS
full CI = PASS
CodeQL = PASS
Dependency Review = PASS
ruleset/protection = ACTIVE
working tree = CLEAN
production source diff = EMPTY
```

---

# 8. Detailed test design rules

## 8.1. No network in unit tests

Unit tests use injected `HttpMessageHandler` and fake process runner. Only CLI integration in CI downloads real packages.

## 8.2. No environment-sensitive snapshots

Snapshots must not include:

- absolute repository path;
- user home;
- NuGet global-packages path;
- temp path;
- machine name;
- process ID;
- timestamps;
- locale-specific strings.

Tests run under at least two cultures (`en-US`, `ru-RU`) for serialization/error code stability where practical.

## 8.3. Test real package bytes only in integration gate

Synthetic packages test parser edge cases. Real SmartPipe 2.1.2 packages test publication integrity.

## 8.4. Mutation tests for contract strength

At least one CI test must copy the baseline into temp and mutate each category, proving verifier fails:

```text
manifest commit SHA
package SHA
nuspec dependency
PublicAPI line
project PackageReference
workflow branch trigger
snapshot hash
```

A test that only verifies the happy path is insufficient.

## 8.5. Cross-platform paths

All paths in manifests use `/`. Runtime conversion to OS paths happens only at file access boundary. Comparisons use ordinal semantics for repository-relative paths and case-insensitive semantics only for NuGet package IDs.

---

# 9. Security rules

- NuGet source must resolve through HTTPS.
- Package signature verification is mandatory before parsing package content as trusted baseline.
- Package hash verification is mandatory even when package file comes from cache.
- No unbounded HTTP response or ZIP entry read.
- XML DTD and external entity resolution are disabled.
- ZIP entries are never extracted during snapshot parsing.
- Process arguments are passed as arrays, not shell strings.
- Logs redact home paths and URLs with query strings.
- GitHub workflow uses minimal `contents: read` permissions; no write token is needed for verification.
- Baseline capture must not require NuGet API key.
- GitHub ruleset bypass is audited in PR/report.

---

# 10. Performance and reliability requirements

Repository checks are build tooling, but must remain efficient:

| Operation | Target |
|---|---:|
| Offline verify warm packages | <= 5 seconds on standard CI runner |
| Package fetch excluding network latency | bounded memory <= 16 MiB buffers |
| Unit test suite | <= 30 seconds |
| Manifest/snapshot size | reviewable; no duplicate assembly bytes |
| Network retry | max 3 attempts |
| Package max size | default 100 MiB, configurable only in code policy |

No performance micro-optimization is required before correctness, but full nupkg files must not be loaded into a single byte array.

---

# 11. Acceptance checklist

## Repository and branch

- [ ] Exact baseline SHA revalidated.
- [ ] Clean baseline test suite passed before changes.
- [ ] `release/2.2.0` created from exact approved SHA.
- [ ] Task worktree/branch isolated.
- [ ] Direct push, force push and deletion protection active.
- [ ] Required status checks active and uniquely named.

## Documentation

- [ ] ADR accepted.
- [ ] Master plan committed.
- [ ] Branch/review policy committed.
- [ ] SP220-00 detailed plan committed.
- [ ] No contradictory package boundary statements.
- [ ] External sources are official and HTTPS.

## Baseline packages

- [ ] Core 2.1.2 downloaded and signature verified.
- [ ] Extensions.Json 2.1.2 downloaded and signature verified.
- [ ] Extensions 2.1.2 downloaded and signature verified.
- [ ] SHA-256 values pinned.
- [ ] Package identity/version parsed from nuspec and matched.
- [ ] Package assets and direct dependencies captured.
- [ ] Exported types/type forwarders captured.

## Repository snapshots

- [ ] Packable projects captured.
- [ ] PublicAPI Shipped/Unshipped captured.
- [ ] Direct ProjectReference/PackageReference captured.
- [ ] Restored direct/transitive package graph captured.
- [ ] No absolute paths or volatile data.
- [ ] Canonical outputs deterministic across repeat run.

## Tests

- [ ] Manifest/schema negative tests.
- [ ] Canonical text/hash tests.
- [ ] NuGet service/fetch/signature tests.
- [ ] Nuspec/ZIP/PE metadata tests.
- [ ] Public API tests.
- [ ] Dependency graph tests.
- [ ] Workflow policy tests.
- [ ] Capture rollback tests.
- [ ] Offline/no-network test.
- [ ] Linux and Windows tests.
- [ ] `--minimum-expected-tests 1` on filtered lanes.

## CI and final audit

- [ ] Repository checks test project built with warnings as errors.
- [ ] Online package verify followed by offline verify.
- [ ] Full Core/Json/Extensions tests remain green.
- [ ] CodeQL green.
- [ ] Dependency Review green.
- [ ] No production runtime source changes.
- [ ] No package binaries tracked.
- [ ] Baseline report contains final exact-head evidence.

---

# 12. Stop conditions

Codex MUST stop and report rather than continue when:

1. `origin/main` differs from the reviewed SHA and changes have not been analyzed.
2. Baseline local tests fail.
3. Published package 2.1.2 is unavailable, unsigned or has unexpected bytes after the manifest has been approved.
4. Package identity/version differs from expected.
5. Public API source and published package contain unexplained mismatch.
6. Release branch already exists at a different SHA.
7. Required branch protection cannot be activated.
8. A required workflow does not execute for release branch.
9. Capture output contains absolute paths, credentials or machine-specific data.
10. SP220-00 introduces production source/API changes.
11. Offline verification performs network access.
12. Any test is skipped to make CI green without an approved issue and explicit rationale.

---

# 13. Suggested commit sequence

```text
a ci(baseline): validate the 2.2.0 integration branch
b docs: establish SmartPipe 2.2 architecture governance
c build(baseline): define the reproducible baseline manifest
d build(baseline): fetch and verify published NuGet packages
e build(baseline): snapshot package API and dependency assets
f build(baseline): capture repository API and dependency contracts
g build(baseline): add capture and verification commands
h build(baseline): pin the published 2.1.2 contract
i test(baseline): cover governance and snapshot failures
j ci(baseline): enforce the published 2.1.2 contract
```

Letters above only illustrate order; commit messages do not include letters.

---

# 14. Handoff to SP220-01

SP220-01 receives these stable interfaces/artifacts:

```text
eng/baselines/2.1.2/manifest.json
eng/baselines/2.1.2/package-dependencies.json
eng/baselines/2.1.2/public-api.json
eng/SmartPipe.RepositoryChecks verify-baseline
release/2.2.0 protected branch
ADR-0001 package boundary rules
```

SP220-01 may extend `SmartPipe.RepositoryChecks` with package graph allowlists and project templates, but may not weaken or replace baseline verification.

---

# 15. Primary references

1. Microsoft Learn — .NET Package Validation: https://learn.microsoft.com/en-us/dotnet/fundamentals/apicompat/package-validation/overview
2. Microsoft Learn — `dotnet nuget verify`: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-verify
3. Microsoft Learn — PackageReference lock files and locked mode: https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#locking-dependencies
4. Microsoft Learn — `dotnet package list`: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list
5. GitHub Docs — protected branches: https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches
6. GitHub Docs — repository rulesets: https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets
7. GitHub Docs — security hardening for Actions: https://docs.github.com/en/actions/security-for-github-actions/security-guides/security-hardening-for-github-actions
8. SmartPipe repository baseline commit: `8e79902d22de714f493582946f7c260462b0895e`; tracked baseline manifest: [eng/baselines/2.1.2/manifest.json](../../../eng/baselines/2.1.2/manifest.json)

---

# 16. Final directive for Codex

SP220-00 is complete only when the 2.1.2 release contract can be reconstructed and verified automatically from a clean clone, using only the committed manifest/snapshots, official package source and exact SDK. A human-readable ADR without a machine-verifiable baseline is not completion. A successful verifier without branch protection and exact-head CI evidence is also not completion.
