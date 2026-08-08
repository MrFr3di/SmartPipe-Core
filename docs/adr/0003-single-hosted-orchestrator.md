# ADR-0003: One hosted orchestrator for canonical pipelines

- Status: Accepted
- Date: 2026-08-01
- Decision owners: SmartPipe maintainers
- Target release: 2.2.0

## Context

Registering one `IHostedService` per pipeline delegates inter-pipeline ordering to
the Generic Host. Concurrent host settings, partial startup, and independent
shutdown callbacks would make readiness, rollback, reverse cleanup, and exception
ordering depend on host scheduling rather than the SmartPipe contract.

## Decision

`SmartPipe.Extensions.Hosting` registers exactly one
`SmartPipeHostedOrchestrator`. `RunAsHostedService` stores immutable,
type-erased registration metadata keyed by `PipelineKey`. The orchestrator starts
the ordered registrations sequentially and stops the successfully started runs in
the exact reverse order.

`ISmartPipeRegistry` is the ordering authority. Hosting sorts by explicit hosted
`Order`, then canonical DI `RegistrationOrder`, then ordinal `PipelineKey`; the
order of `RunAsHostedService` calls is not a second registration order. Missing or
incompatible registry metadata fails materialization instead of falling back.

Startup and shutdown publish one shared task each before work begins. The first
startup caller owns a linked token composed from its caller token and one internal
stop token. If shutdown begins during startup, it takes and cancels that internal
source outside the state lock, then waits for the startup coordinator to perform
the only reverse rollback. The startup coordinator owns the linked source; exactly
one side owns disposal of the internal stop source. External factories,
cancellation callbacks, abort, and disposal never execute while the state lock is
held.

Cancellation provenance remains observable. Caller startup cancellation takes
precedence if caller and stop tokens are both cancelled. A sole stop-owned primary
`OperationCanceledException` is suppressed by shutdown after rollback; caller
cancellation, startup faults, rollback failures, cancellation callback failures,
and the caller shutdown deadline remain errors. `BackgroundService.StopAsync`
receives the caller shutdown token, while mandatory abort and disposal use
`CancellationToken.None`.

The canonical DI run factory remains the only activation and scope owner. Hosting
does not create scopes, resolve components, or introduce a second runtime path.
Legacy Hosting types remain quarantined in `SmartPipe.Extensions` for 2.1 source
and binary compatibility.

## Consequences

Pipeline lifecycle ordering is deterministic even when Generic Host concurrent
start or stop is enabled. Partial startup has one rollback coordinator, and
monitoring has one place to classify completion, faults, and intentional
shutdown. Pipelines cannot be independently restarted by the orchestrator; a new
host lifetime is required for another hosted run.
