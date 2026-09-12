# Project Overview

## Purpose

SmartPipe.Core provides typed, in-process streaming pipelines for .NET. A
pipeline connects a source, zero or more transforms, and an optional sink;
runtime work is represented by typed processing envelopes and bounded
channels. The repository describes this as an in-process runtime, not a
distributed workflow engine, broker, durable queue, or exactly-once system.

## Scope

The current repository version is `2.2.0` (`Directory.Build.props`). The
source packages in this checkout are:

- `SmartPipe.Core`: runtime, pipeline definitions/builders, lifecycle,
  resilience, dead-letter contracts, diagnostics, and metrics.
- `SmartPipe.Extensions`: broad HTTP, database, mapping, resilience, hosting,
  and health-check facade with compatibility forwarders for moved public types.
- `SmartPipe.Extensions.Json`: JSON source/transform/sink and JSON dead-letter
  persistence, with source-generated metadata paths for trimming and AOT.
- `SmartPipe.Extensions.Channels`: narrow `ChannelMerge` APIs over Core.
- `SmartPipe.Extensions.Transforms`: composite, conditional, compression,
  filter, and framework-free rule-validation transforms.
- `SmartPipe.Extensions.Logging`: `LoggerSink<T>` and bounded safe logging
  options over Core and logging abstractions.
- `SmartPipe.Extensions.DataAnnotations`: compatibility validation transforms
  and filter extensions over Core and Transforms.

SP220 keeps narrow leaves independently installable while the broad
`SmartPipe.Extensions` facade forwards moved public types so existing source,
binary, and reflection consumers retain their identities. HealthChecks remains
a leaf over Core and DependencyInjection; DependencyInjection owns the active
run registry and bounded latest-terminal observations, and HealthChecks
evaluates immutable observations through separate liveness, readiness,
activity, queue, multi-run, and aggregate policies. The legacy facade
health API remains physically owned by `SmartPipe.Extensions`.

Repository checks, consumer scenarios, package baselines, and package
scaffolding live under `eng/`; tests and benchmarks are separate projects.
RepositoryChecks now also exposes a local deterministic Agent Context API for
task context, failures-only task verification, and exact-tree evidence.

## Architecture

The core execution path is `PipelineBuilder`/`PipelineDefinition` to
`PipelineRuntime`, `TypedPipelineExecutor`, producer/workers, `StageExecutor`,
optional `SinkExecutor`, output emission, and `PipelineRun<TOutput>`.
`PipelineRun<T>` owns one execution; runtime-owned components are cleaned up by
the runtime and borrowed components remain caller-owned. Input, output, and
buffered observer channels are bounded. Stage chains are sequential within one
envelope; multiple envelopes may run concurrently, so cross-envelope ordering
is not guaranteed.

## Main Workflows

Consumers build instance pipelines for single-use runs or factory-based
definitions for reusable runs. Runtime options configure concurrency,
backpressure, output policy, observers, adaptive admission, and clocks.
Lifecycle operations include drain, cancel, abort, completion, and idempotent
disposal. Retry, timeout, circuit-breaker, failure-action, and dead-letter
behavior is stage-local. Activity sources, metrics snapshots, and meter
instruments provide observability.

## Major Decisions

- Core owns `DeadLetterEnvelope<T>`, `IDeadLetterSerializer<T>`, and the
  standard JSON-lines serializer; JSON integration implementations belong to
  `SmartPipe.Extensions.Json`.
- `SmartPipe.Extensions` retains a JSON compatibility forwarding bridge for
  the 2.x package boundary; new JSON consumers should reference the JSON leaf.
- `PipelineOutputPolicy.SuppressSuccessWhenSinkAttached` is the safe default
  for sink-backed runs; `EmitAll` requires an active output consumer.
- RepositoryChecks is the deterministic Agent Context API: it compiles task
  context from the active ExecPlan, emits failures-only verification results,
  and produces compact exact-tree evidence from existing package, baseline,
  ownership, and consumer state instead of making an agent reconstruct state
  from raw logs. Agent-context commands are local-only; the release workflow
  uses the tracked `sp220-05` verification profile as a CI gate.
- Task scope remains owned by the active ExecPlan. Tracked verification
  profiles will contain only executable gate recipes for CI and fresh clones;
  manifests remain policy truth and skills remain orchestration, not a second
  architecture source.
- PR #60 was merged as `8739bee2430cb73698c4364228de0a69281e107b`; Checkpoint C
  was true-merged into `release/2.2.0` as
  `6604355e168d9e7d404a585f30f38490d5b05730`; and SP220-07 was true-merged
  into protected Checkpoint D as
  `97c9fcb5e78d4fd5376e28b3c3cc460c600a091c`.
- The F1 aggregate evaluator now snapshots `includeAll`, the included set,
  maximum, policy, and nested options before selection, capture, and
  evaluation. Aggregate descriptions distinguish bounded data/problem
  cardinality from exact `PipelineKey` identity; broad artifact claims are
  limited to aggregate count-only descriptions because per-pipeline
  sanitized descriptions may contain an exact key.
- Checkpoint PRs are now included explicitly in the `pull_request` branch
  filters for CI, CodeQL, and Dependency Review. Their push, schedule, and
  dispatch contracts remain unchanged; the Python YAML 1.2 oracle rejects
  removal or mutation of these triggers.
- SP220-07 delivers Channels, Transforms, Logging, and DataAnnotations leaves,
  facade forwarding, five current consumers, seven benchmarks, package
  governance, and compatibility/trim/NativeAOT contracts.
- Hosted CI is now the canonical path for public-repository validation. The
  retired self-hosted runner and its workflow-only cleanup contracts were
  removed; lock-file-keyed Linux/Windows NuGet caches, public CodeQL, and
  Dependency Review were restored. The exact-SHA diagnostic path remains a
  bounded CI/tooling contract and does not alter the six required checks.
- The servicing train moved the SDK pin to `10.0.303` and the approved
  Microsoft package cohort to `10.0.11`, with lock files regenerated and
  integrity verification kept distinct from immutable 2.1.2 baseline capture.
- SP220-08 adds reusable JSON definitions in `SmartPipe.Extensions.Json`:
  file/dead-letter sources and sinks plus JSON transforms are thin Core
  `RuntimeOwned` adapters. The package links one internal BCL-only UTF-8 line
  framer and privately snapshots resolver-backed serializer metadata; it adds
  no second runtime, reflection fallback, or facade/DI dependency.
- The JSON integration adds direct, JSON+DI, reflection-disabled trim, and
  published NativeAOT consumer coverage, 35 manifest scenarios, and 21
  method-scoped JSON benchmark cases. The completed release promotion is
  `release/2.2.0@0439163f`; SP220-09 is the current paused candidate described below.


## SP220-09 CSV Integration (Paused)

The paused Heavy deployment `sp220_09_csv_20260902_a` is implementing the
2.2 CSV package split on branch `sp220/09-csv-integration`, based on accepted
release head `0439163f`. The candidate adds the independently installable
`SmartPipe.Extensions.Csv` leaf with strict file-oriented RFC4180-style
source/sink definition APIs, while physically moving the three legacy CSV
types into that leaf and forwarding their existing identities from the broad
facade.

The strict path snapshots caller options and map factories, probes BOMs with
strict decoding, bounds logical records before CsvHelper mapping, supports
bounded recovery only at proven record boundaries, and uses explicit borrowed
logging for `SkipAndLog`. The sink supports bounded Create/Append preflight,
formula policy, per-record rollback, and deterministic disposal. It deliberately
does not claim public stream ownership, whole-file atomicity, or blanket
NativeAOT compatibility.

Focused implementation and package evidence is green, but the required binary
2.1.2 facade scenario cannot start because the repository runner stops at the
existing unrelated competitor-benchmark CPM violations (`SPCPM004/005`).
The candidate remains paused until that scope decision, the two facade runner
gates, one independent tester/reviewer pass, and authorized remote acceptance
are completed.
