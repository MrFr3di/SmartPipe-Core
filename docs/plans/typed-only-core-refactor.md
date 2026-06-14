# Typed-Only Core Refactor

Status: active
Date: 2026-06-11

## Scope

This plan tracks the repository-local execution of `.work/plane/plan.txt`.
The working rule is not "delete legacy first"; it is:

1. formally identify useful legacy behavior;
2. move or re-express that behavior in the typed/envelope runtime;
3. remove the legacy surface only after typed replacement coverage exists.

## Current Execution Window

Step 0 through Step 7 have corrective coverage for this pass:

- Step 0: decision and progress tracking;
- Step 1: legacy surface inventory and preservation map;
- Step 2: typed runtime options and output policy names;
- Step 3: bounded runtime channel factory contracts;
- Step 4: worker/executor split and output emitter boundaries;
- Step 5: typed concurrency and input backpressure;
- Step 6: typed output policy and bounded output behavior;
- Step 7: lifecycle drain/cancel/abort/dispose behavior.

Step 8 and later remain deferred until these gates are reviewed.

## Out Of Scope

- deleting `SmartPipeChannel<TInput,TOutput>`;
- deleting `ProcessingContext<T>` or `ProcessingResult<T>`;
- deleting Extension package legacy integrations;
- rewriting DI/hosting;
- local-first, SQLite, durable queues, outbox/inbox, distributed runtime, or
  cloud orchestration;
- benchmark tuning.

## Execution Gates

Every implementation step must leave:

- tests or inventory proving the behavior being changed;
- docs updated for the new contract;
- a review note in `docs/plans/typed-only-core-refactor-progress.md`;
- validation commands recorded.
