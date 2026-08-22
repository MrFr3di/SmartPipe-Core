# Contributing

Before opening a change, run the validation tier that matches the affected
surface.

For runtime or public API changes:

```powershell
dotnet restore SmartPipe.Core.slnx --locked-mode
dotnet build SmartPipe.Core.slnx -c Release --no-restore
dotnet test --project tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build
dotnet test --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build
dotnet pack src\SmartPipe.Core\SmartPipe.Core.csproj -c Release --no-build -o artifacts\packages
dotnet pack src\SmartPipe.Extensions\SmartPipe.Extensions.csproj -c Release --no-build -o artifacts\packages
```

## Test Runner And Coverage

Test projects use xUnit v3 on Microsoft Testing Platform. The repository
`global.json` selects the MTP runner, so pass projects with `--project`:

```powershell
dotnet test --project tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build
dotnet test --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build
```

Executable runner mode is supported for both test projects:

```powershell
dotnet run --project tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build
dotnet run --project tests\SmartPipe.Extensions.Tests\SmartPipe.Extensions.Tests.csproj -c Release --no-build
```

Coverage uses the MTP code coverage extension, not VSTest data collectors:

```powershell
dotnet run --project tests\SmartPipe.Core.Tests\SmartPipe.Core.Tests.csproj -c Release --no-build -- --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml
```

Do not use `--collect:"XPlat Code Coverage"` with the MTP runner.

Consumer smoke must install `SmartPipe.Core` and `SmartPipe.Extensions` from the
local `artifacts/packages` directory, with nuget.org present only as the
external dependency source. Smoke code must use the typed-only API:
`IPipelineSource<T>`, `IPipelineTransformer<TInput,TOutput>`,
`IPipelineSink<T>`, `ProcessingEnvelope<T>`, `StageResult<T>`,
`PipelineRun<T>`, and typed DI factories.

Do not reintroduce removed pre-typed APIs in tests, docs, or smoke programs.

## Concurrency And Lifecycle Tests

Concurrency tests must be deterministic. Avoid sleep-based synchronization.
Use explicit coordination primitives and treat timeouts as guards only.

Backpressure tests should use intentionally small bounded capacities and assert
final runtime state, output behavior, and disposal behavior where relevant.

## Fault Scenario Tests

Fault tests should assert the final `PipelineRunState`, output behavior,
observer or failure events where applicable, metrics where applicable, and
disposal behavior where applicable.

Do not create a separate fault-injection matrix document. Keep compact coverage
notes in the existing runtime, resilience, and contributing docs.

## Performance Gate

Use the existing BenchmarkDotNet project for release performance checks:

```powershell
dotnet run -c Release --project benchmarks\SmartPipe.Benchmarks\SmartPipe.Benchmarks.csproj -- --filter "*RuntimePipelineBenchmarks*" --noOverwrite
```

The release gate compares local results against the previous local baseline.
Document any throughput regression above 15%, obvious allocation spike, or
unbounded-memory symptom in progress notes.

README examples are intentionally minimal. CI consumer smoke is the executable
check for the public quick-start scenarios.

## Dedicated Windows runner operations

The same-repository Windows jobs use the exact labels
`self-hosted`, `Windows`, `X64`, and `smartpipe-cleanup-v1`. The installation
root is deliberately fixed at `C:\SmartPipe-Runner`; do not point the hook at a
developer checkout, `_tool`, the runner binaries, or a shared temporary root.

Install or remove the repository-owned hook only while the runner is idle:

```powershell
gh auth status
pwsh -NoProfile -File eng\runner\install-runner.ps1
pwsh -NoProfile -File eng\runner\uninstall-runner.ps1
```

The scripts resolve the exact runner name from `.runner` (`agentName`); an
optional `-RunnerName` is accepted only when it exactly matches that value.
They fail closed for missing or ambiguous configuration. The installer checks
the repository, queued/in-progress Actions runs, and remote runner state before
mutation. It writes only the hook's `.env` entry, copies the hook plus its
safety helper into the runner's `hooks` directory, registers exactly
`smartpipe-cleanup-v1` through the GitHub runner-label API while preserving
other labels, stops listeners tied to the exact root, launches one hidden
`run.cmd`, and waits for exactly one online, idle listener. Uninstall removes
only that custom label and the owned entry/copies, preserves unrelated labels
and `.env` lines, then performs the same bounded one-listener restart. A failed
operation reports recovery guidance; never convert the runner to a service as
part of this operation. The second owned `.env` entry points
`DOTNET_INSTALL_DIR` at `_work\_tool\dotnet`, giving `actions/setup-dotnet` a
writable persistent directory without granting access to
`C:\Program Files\dotnet`.

The post-job hook accepts only `MrFr3di/SmartPipe-Core`, verifies the checkout
remote, and canonicalizes every target beneath the dedicated runner root. It
removes the exact checkout and the known `SmartPipe.Core`, `SmartPipe-Core`,
`CodeQL`, and `codeql` directories below `RUNNER_TEMP`. Missing targets are
successful. Any outside path, broad root, reparse point, unsafe repository, or
deletion error fails closed before removal; the existing workflow cleanup jobs
remain as defense in depth.

For a compact, transition-only pull-request view:

```powershell
pwsh -NoProfile -File eng\runner\monitor-pr.ps1 -PullRequest 123 -MaxPolls 120
```

The monitor uses `gh pr view`, prints only a changed head/state/merge/check
summary, and stops at `MERGED`, `CLOSED`, or the poll bound. For each newly
failed head it retrieves one failed-run log, prints a bounded first-causal
slice, and removes its task-specific temporary log directory on exit. `-Once`
is useful for a single snapshot. It does not upload logs or alter GitHub state.

The optional diagnostic dispatch runs one exact commit and one internal
consumer scenario without changing normal push or pull-request behavior:

```powershell
gh workflow run ci.yml --repo MrFr3di/SmartPipe.Core --ref sp220/checkpoint-d `
  -f diagnostic-sha=0123456789abcdef0123456789abcdef01234567 `
  -f diagnostic-scenario=dependency-injection-nativeaot `
  -f diagnostic-repeat=1
```

The SHA must be 40 lowercase hexadecimal characters, the scenario must use
lowercase letters, digits, and hyphens, and repeat must be `1` through `5`.
The job restores, builds, and packs once, then reports bounded run snippets in
the step summary without artifacts. Normal jobs run when all three inputs are
empty.

If rollout must be reverted, stop the idle listener, run the uninstaller,
restart the listener, and revert the workflow change with a normal commit.
Do not delete the runner root or use `git clean`; safe cleanup is intentionally
recoverable and scoped to the exact approved boundaries.
