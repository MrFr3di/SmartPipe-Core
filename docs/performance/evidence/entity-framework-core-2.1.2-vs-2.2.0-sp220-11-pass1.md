# SP220-11 Entity Framework Core evolution evidence — pass 1

Date: 2026-09-21

## Scope

Baseline: SmartPipe `2.1.2`, product SHA `8e79902d22de714f493582946f7c260462b0895e`.

Candidate: immutable SmartPipe `2.2.0` SP220-01..11, product SHA `61ceef6bf69aef0a4f79b25384352d238979200f`.

Scenario: `entity-framework-core`.

Scenario class: **evolution**.

Cross-version percentage deltas are intentionally omitted because the integration lifecycle changed:

- 2.1.2 uses the legacy `EfCoreSelector<T>` path over a caller-owned `DbContext`;
- 2.2.0 uses the runtime-owned `EfCorePipelineComponents.QuerySource` path through `PipelineDefinition.StartAsync` and `PipelineRun`.

Both sides use the same relational provider and query semantics:

- `Microsoft.EntityFrameworkCore.Sqlite 10.0.11`;
- one SQLite in-memory database kept alive by an explicit open connection for the fixture lifetime;
- the same deterministic 100-row dataset;
- no-tracking queries.

This provider choice intentionally avoids the EF Core InMemory provider, which Microsoft discourages for realistic query testing and which is not designed for performance or robustness.

## Correctness / Dry validation

EF Core Dry workflow run: `35623704742`.

It validates:

- immutable baseline/candidate package feeds;
- Package Source Mapping for all resolved `SmartPipe.*` packages;
- NuGet-native contentHash verification;
- generated lock followed by locked restore;
- Release/warnings-as-errors build;
- complete BenchmarkDotNet JSON evidence;
- relational SQLite schema creation before measurement;
- 100-row query returns count `100` and checksum `5050`;
- filtered query returns count `1` and checksum `42`;
- no-tracking semantics on both targets.

The original benchmark draft used different EF Core patch levels and the EF InMemory provider. That draft was corrected before the first Dry/Short evidence and is not performance evidence.

## BenchmarkDotNet Short — pass 1

GitHub Actions run: `35624187030`

Artifact: `perf-efcore-evolution-bench-d9c1a1341c7ef9327943e5ddc45045a55a31eb64`

Artifact digest: `sha256:c07eb0f03302b6ca0e96110bca2680ea39512bc1ecec264c0676487c7c358e2d`

Harness SHA: `d9c1a1341c7ef9327943e5ddc45045a55a31eb64`

Run id: `20260921T161518Z-d9c1a1341c7e`

Order: `2.1.2 → 2.2.0 → 2.2.0 → 2.1.2`.

Environment:

- Ubuntu 24.04.5 LTS;
- 4 logical processors;
- benchmark framework: .NET 10.0.11;
- runner image: `ubuntu24 / 20260907.300.1`;
- timing authority: **non-authoritative / informational**.

| Method | 2.1.2 Mean | 2.2.0 Mean | 2.1.2 allocation | 2.2.0 allocation | Drift 2.1.2 | Drift 2.2.0 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| ReadHundredRows | 303.698 µs | 866.607 µs | 102.26 KiB/op | 134.01 KiB/op | 2.03% | 8.53% |
| ReadSingleFiltered | 143.526 µs | 420.247 µs | 60.15 KiB/op | 74.38 KiB/op | 2.59% | 7.57% |

## Interpretation

The candidate has a materially larger absolute cost for both workloads. Because both sides use the same EF Core provider/version, database fixture, seeded data, and no-tracking semantics, this difference is not caused by an EF Core patch-level mismatch or by comparing EF InMemory with a relational provider.

However, it is still not a strict EF Core regression result. The candidate path includes runtime semantics that the 2.1.2 selector does not provide:

- runtime-owned `DbContext` lifecycle;
- public pipeline definition activation;
- `PipelineRun` creation and completion;
- output/result wrapping and streaming;
- scope/lifetime cleanup owned by the 2.2.0 runtime.

The candidate repeat drift is below 9% in both methods, while the absolute side-by-side gap is much larger than that noise. The extra integration/lifecycle cost is therefore real enough to decompose.

Recommended follow-up characterization:

1. context/query-source activation only;
2. first-row latency;
3. per-row streaming cost after activation;
4. generic PipelineRun/output wrapping cost;
5. compiled-query source path versus normal query source;
6. fixed lifecycle overhead versus row-count scaling.

## Result

SP220-11 Entity Framework Core has a valid first evolution evidence pass.

The new provider-neutral, runtime-owned integration is functionally correct on a relational SQLite fixture, but short query runs carry a measurable fixed lifecycle/allocation cost. Optimization work should target that lifecycle decomposition rather than the EF query itself until profiling proves otherwise.
