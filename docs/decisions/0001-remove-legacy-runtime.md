# 0001 Remove Legacy Runtime As A Runtime Model

Status: accepted
Date: 2026-06-11

## Decision

SmartPipe.Core uses the typed/envelope runtime as its only runtime model.
Removed runtime behavior must either have a typed replacement or a deliberate
deletion decision.

## Reason

Maintaining two runtime models creates duplicate lifecycle semantics, duplicate
backpressure semantics, duplicate dependency-injection semantics, duplicated
documentation, and higher maintenance risk. The typed/envelope runtime is the
preferred model for metadata-aware processing, observer events, dead-letter
records, and explicit run lifecycle.

## Consequences

- This is a breaking architectural change.
- Typed/envelope runtime owns all runtime behavior.
- Removed APIs are represented by typed replacements, convenience adapters, or
  explicit deletion decisions.
- Security audit, local-first storage, SQLite, outbox/inbox, distributed
  execution, and cloud orchestration are out of scope for this refactor.

## Preserved Capabilities

The refactor must preserve these useful capabilities in typed form:

- source -> transform -> sink composition;
- bounded channels and backpressure;
- real typed concurrency through `PipelineRuntimeOptions.MaxConcurrency`;
- retry, timeout, circuit breaker, and dead-letter behavior;
- observer events and observer failure policy;
- metrics and diagnostics;
- graceful drain, cooperative cancel, and abort semantics;
- dependency-injection, hosted service, and health-check integration;
- convenience adapters for simple source/transform/sink usage.

## Removal Rule

Do not reintroduce a second runtime model. New convenience APIs must adapt to
the typed envelope runtime directly.
