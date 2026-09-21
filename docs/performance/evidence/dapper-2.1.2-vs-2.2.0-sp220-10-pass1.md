# SP220-10 Dapper evolution evidence — pass 1

Date: 2026-09-21

## Scope

This pass compares the latest pre-2.2.0 baseline (`2.1.2`, product SHA `8e79902d22de714f493582946f7c260462b0895e`) with the immutable SP220-01..11 candidate (`2.2.0`, product SHA `61ceef6bf69aef0a4f79b25384352d238979200f`).

SP220-12 / Mapster is outside this lab revision.

Scenario: `dapper`.

Scenario class: **evolution**.

Cross-version percentage deltas are intentionally omitted because the lifecycle contract is not equivalent:

- 2.1.2: legacy `DapperSelector` path over a fresh per-run SQLite connection;
- 2.2.0: runtime-owned `DapperPipelineComponents.QuerySource` executed through `PipelineDefinition.StartAsync` and `PipelineRun`.

Both sides read the same deterministic named shared in-memory SQLite database, use explicit row mapping, fresh per-run connections, Microsoft.Data.Sqlite `10.0.11`, and identical query intent.

## Correctness / Dry validation

Corrected Dapper Dry workflow run: `35619934229`.

The final Dry proves:

- immutable baseline and candidate package feeds;
- Package Source Mapping for all resolved `SmartPipe.*` packages;
- NuGet-native contentHash verification;
- locked restore after generated lock;
- Release/warnings-as-errors build;
- BenchmarkDotNet JSON with non-null statistics and measurements;
- 100-row query returns count `100` and checksum `5050`;
- parameterized query returns count `1` and checksum `42`;
- SQLite setup transaction is explicitly typed and successfully committed;
- benchmark observation type is public, so BenchmarkDotNet can compile generated code.

Earlier failed Dry attempts were harness defects and are not product evidence.

## BenchmarkDotNet Short — pass 1

GitHub Actions run: `35623295205`

Artifact: `perf-dapper-evolution-bench-4439b55ec695e4ba1b6643085e8e5d75d2ff12d6`

Artifact digest: `sha256:606a9f5809f9062ff6632d79874db8ae773e145d9ae13dbd43e1980d8edbded8`

Harness SHA: `4439b55ec695e4ba1b6643085e8e5d75d2ff12d6`

Run id: `20260921T160723Z-4439b55ec695`

Order: `2.1.2 → 2.2.0 → 2.2.0 → 2.1.2`.

Environment:

- Ubuntu 24.04.5 LTS;
- 4 logical processors;
- .NET SDK 10.0.303;
- Benchmark process framework: .NET 10.0.11;
- installed host: .NET 10.0.12;
- runner image: `ubuntu24 / 20260907.300.1`;
- timing authority: **non-authoritative / informational**.

| Method | 2.1.2 Mean | 2.2.0 Mean | 2.1.2 allocation | 2.2.0 allocation | Repeat drift 2.1.2 | Repeat drift 2.2.0 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| ReadHundredRows | 167.162 µs | 440.190 µs | 26.34 KiB/op | 55.42 KiB/op | 0.87% | 5.68% |
| ReadSingleParameterized | 31.663 µs | 99.731 µs | 2.96 KiB/op | 14.95 KiB/op | 0.22% | 3.98% |

## Provenance

Baseline package graph:

- `SmartPipe.Core 2.1.2`;
- `SmartPipe.Extensions 2.1.2`;
- `SmartPipe.Extensions.Json 2.1.2`.

Candidate package graph:

- `SmartPipe.Core 2.2.0`;
- `SmartPipe.Extensions.Dapper 2.2.0`.

All resolved SmartPipe packages were restored from their immutable local target feeds and verified against NuGet content hashes.

## Interpretation

The candidate path has a clearly larger absolute cost in this workload: both elapsed time and allocation are substantially higher than the legacy selector path.

This is not classified as a strict Dapper regression because the candidate measurement includes new runtime semantics that the baseline does not provide:

- runtime-owned connection/source lifecycle;
- pipeline definition activation;
- `PipelineRun` creation;
- result wrapping and result-stream consumption;
- completion/disposal coordination;
- the 2.2.0 Core run lifecycle around the database source.

The repeat drift is low enough that the absolute side-by-side gap is not just runner noise. It should therefore be treated as a **real integration/lifecycle cost worth decomposing**, rather than ignored.

The next useful performance question is not “is Dapper 2.2.0 slower?” but:

1. how much of the measured cost is the Dapper source itself;
2. how much comes from generic `PipelineRun` activation/result wrapping;
3. how much is fixed per run versus proportional to row count;
4. whether a reusable or lower-overhead run path is appropriate for high-frequency short database queries.

A follow-up characterization should separate source activation, first-row latency, per-row streaming cost, and full run lifecycle.

## Result

SP220-10 Dapper now has a valid first evolution evidence pass.

The new integration is functionally correct and deterministic under SQLite-backed CI, but it exposes a material fixed lifecycle/allocation cost for short queries. That cost is now measured and should be decomposed before considering performance optimization or release thresholds.
