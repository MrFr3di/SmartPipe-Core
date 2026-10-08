# 2.2.1 grouped dependency maintenance — 2026-10-08

Status: proposed, CI-gated. Source PRs: Dependabot #141, #143, #145 (already closed when this work began). Consolidated PR: #147.

## Decision and scope

The repository uses NuGet Central Package Management and committed lock files. Upgrade one coherent OpenTelemetry SDK family rather than mixing 1.17.0 and 1.19.1:

| Central package | Previous | Target |
| --- | --- | --- |
| OpenTelemetry | 1.17.0 | 1.19.1 |
| OpenTelemetry.Api.ProviderBuilderExtensions | 1.17.0 | 1.19.1 |
| OpenTelemetry.Exporter.InMemory | 1.17.0 | 1.19.1 |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.17.0 | 1.19.1 |
| OpenTelemetry.Extensions.Hosting | 1.17.0 | 1.19.1 |
| Microsoft.Testing.Extensions.CodeCoverage | 18.11.2 | 18.12.0 |

Regenerate **current** `packages.lock.json` with the pinned .NET SDK 10.0.401 and `dotnet restore --force-evaluate`; verify with `dotnet restore --locked-mode`. Do not manually invent package hashes. Keep historical/baseline consumer snapshots intact: `tests/Consumers/Scenarios/csv-facade-source/packages.lock.json` was restored to the original contents after NuGet's normal restore unexpectedly rehashed the published 2.2.0 baseline packages.

CodeCoverage 18.12.0 introduces Microsoft.Testing.Platform 2.5.0 and Mono.Cecil 0.11.6 in the Core test lock dependency graph. This must be validated through the existing Microsoft.Testing.Platform runner and coverage step, not assumed compatible because restore succeeds.

## Upstream change analysis

OpenTelemetry 1.18.0 fixes provider/processor lifecycle defects including potential resource leaks on failed provider initialization and collection/diagnostics edge cases. Version 1.19.0 improves wildcard name matching and adds lower-priority host-derived resource defaults via a new `IHostApplicationBuilder` registration extension. Version 1.19.1 fixes wildcard provider initialization failures and memory exhaustion in certain matching patterns. Sources: [OpenTelemetry release notes](https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.19.1/RELEASENOTES.md) and [OpenTelemetry core changelog](https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.19.1/src/OpenTelemetry/CHANGELOG.md).

No opt-in to the new host builder helper is necessary for the SmartPipe exporter-neutral library; it must not register an exporter, alter the user's resource identity, or silently change defaults. Keep the public API and activity/meter names stable. Existing SDK integration tests exercise the package's actual registration, tracing and metrics boundaries.

## Earlier CI incident and diagnostic improvement

PR #146 (2026-10-08) initially failed the Windows `run-consumers` step with:

```text
Unexpected failure: Repository-check process host did not complete its authenticated control protocol.
```

The exact-head job rerun passed, as did all 721 repository-check tests, consumer tests, 10 concurrency regression passes, package gates and PostgreSQL consumers. **The root cause has not been established.** Do not claim the OpenTelemetry or CodeCoverage updates fix it, and do not hide failures with an automatic retry.

The later PR #147 Windows CI run [37802989978](https://github.com/MrFr3di/SmartPipe-Core/actions/runs/37802989978) reproduced a process-host failure in the `json-trim` consumer, specifically in phase `start-target` (`SPCONS030`). The process-host code showed a plausible deadline race: the host's five-second START-read deadline began **before** it sent READY; the controller also allowed only five seconds for STARTED, even when the consumer request still had time remaining. The precise exception reason in that run was unavailable because the first diagnostic patch logged only its type. Do not claim an established root cause.

This PR improves fail-closed behavior and diagnostics:

- Identify the safe process-host phase and a closed vocabulary of failure reasons, without logging user command arguments, nonces, pipe names, environment or credentials.
- Include the failing consumer scenario ID and preserve the original exception chain.
- Split the controller's five-second READY deadline from the 20-second STARTED acknowledgement deadline; allow up to 30 seconds for the host to receive START from a read begun before READY. Both remain bounded by the consumer request timeout and cancellation.
- Preserve mandatory process-tree teardown, cancellation and all other five-second control-operation timeouts; do not mask errors with an automatic retry.
- Prove the race boundary with a deterministic six-second READY→START regression test and an independent acknowledgement-deadline test; retain negative malformed-protocol tests.

Acceptance requires exact-head GitHub Actions CI, not just the absence of code-review objections.

## Merge/release gates

1. `dotnet restore SmartPipe.Core.slnx --locked-mode` and repository lock contract checks.
2. Release build, repository checks, complete .NET tests and coverage with `-warnaserror`.
3. Current and binary compatibility consumer scenarios; Windows concurrency 10-pass repeat.
4. OpenTelemetry Metrics/Tracing SDK and host lifecycle tests; PostgreSQL 17.11/18.6 integration and 18.6 package consumers.
5. CodeQL, Dependency Review and documentation verification.
6. No accidental changes to the 2.2.0 compatibility snapshot; no SmartPipe public API change, tag or NuGet publication.

CI results and exact commit SHA must be filled from GitHub, not inferred from the dependency versions.
