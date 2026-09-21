# SP220-04 Hosting evolution evidence — pass 1

Date: 2026-09-21

## Scope

Baseline: SmartPipe `2.1.2`, product SHA `8e79902d22de714f493582946f7c260462b0895e`.

Candidate: immutable SmartPipe `2.2.0` SP220-01..11, product SHA `61ceef6bf69aef0a4f79b25384352d238979200f`.

Scenario: `hosting`.

Scenario class: **evolution**.

Cross-version percentage deltas are intentionally omitted because the lifecycle model changed:

- 2.1.2 uses one generic hosted service per pipeline;
- 2.2.0 uses a keyed multi-pipeline hosted orchestrator with ordered startup, reverse shutdown, rollback, and per-run scope ownership.

## Correctness / Dry validation

Hosting Dry workflow run: `35597952475`.

It validated immutable package materialization, Release build, the target-specific adapters, complete BenchmarkDotNet evidence, and the hosted lifecycle correctness precheck.

An earlier Short/report attempt `35598821385` failed because of a synthetic reporter self-test fixture defect; it is not product evidence. The fixture was corrected before the normalized Short below.

## BenchmarkDotNet Short — pass 1

GitHub Actions run: `35598941902`

Artifact: `perf-hosting-evolution-bench-19dd8324f11b21fdbf35dc3b3640b820293e67af`

Artifact digest: `sha256:265bc47e71b0d5371189d1322c4fc94ec1cd8a0710328e75ec93ba0176614c93`

Harness SHA: `19dd8324f11b21fdbf35dc3b3640b820293e67af`

Run id: `20260921T122218Z-19dd8324f11b`

Order: `2.1.2 → 2.2.0 → 2.2.0 → 2.1.2`.

Environment:

- Ubuntu 24.04.5 LTS;
- 4 logical processors;
- benchmark framework: .NET 10.0.11;
- runner image: `ubuntu24 / 20260907.300.1`;
- timing authority: **non-authoritative / informational**.

| Method | 2.1.2 Mean | 2.2.0 Mean | 2.1.2 allocation | 2.2.0 allocation | Drift 2.1.2 | Drift 2.2.0 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| RegisterAndBuildProvider | 45.444 µs | 117.987 µs | 38.28 KiB/op | 54.74 KiB/op | 0.28% | 9.56% |
| ResolveHostedServiceGraph | 0.031 µs | 0.032 µs | 0 B/op | 0 B/op | 1.41% | 5.68% |
| StartStopHostedPipeline | 124.840 µs | 505.218 µs | 39.71 KiB/op | 74.98 KiB/op | 16.21% | 1.33% |

## Interpretation

The service-graph resolve operation is effectively the same scale and allocates nothing on both versions.

The candidate has a larger absolute registration/provider-build cost and a substantially larger start/stop lifecycle cost. That result is expected to include the new orchestration semantics absent from 2.1.2: keyed multi-pipeline coordination, ordered startup, rollback bookkeeping, reverse shutdown, run/scope ownership, and the 2.2.0 run lifecycle.

The candidate start/stop repeat drift is low (1.33%), while the baseline repeat drift is 16.21%. The absolute gap therefore deserves decomposition, but this evolution workload cannot attribute that gap to one isolated implementation detail or classify it as a strict regression.

The next performance question is to split:

1. registration metadata/orchestrator construction;
2. host startup orchestration before the first pipeline actually runs;
3. generic PipelineRun creation and scope ownership;
4. rollback/reverse-shutdown bookkeeping;
5. steady-state hosted lifetime after startup.

## Result

SP220-04 Hosting has a valid first evolution evidence pass.

The new hosting architecture is functionally validated and its extra lifecycle cost is now quantified. It should be optimized, if needed, by decomposing fixed orchestration cost rather than comparing it directly with the simpler 2.1.2 hosted-service model.
