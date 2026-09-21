# SP220-09 CSV strict A/B evidence — pass 1

Date: 2026-09-21

## Scope

This pass compares the latest pre-2.2.0 baseline (`2.1.2`, product SHA `8e79902d22de714f493582946f7c260462b0895e`) with the immutable SP220-01..11 candidate (`2.2.0`, product SHA `61ceef6bf69aef0a4f79b25384352d238979200f`).

SP220-12 / Mapster is outside this lab revision.

Scenario: `csv`.

Scenario class: **strict-ab**.

The benchmark intentionally isolates the CPU/memory round-trip path of the legacy-compatible `CsvTransform<TInput,TOutput>` and excludes filesystem source/sink IO.

The transform source is materially the same on both sides, and both package graphs resolve CsvHelper `33.1.0` with the same NuGet content hash.

GitHub-hosted timing remains **informational / non-authoritative**.

## Strict workload

The same shared benchmark source is compiled against:

- baseline: `SmartPipe.Extensions 2.1.2`;
- candidate: `SmartPipe.Extensions.Csv 2.2.0`.

Covered paths:

- small flat POCO CSV serialize → deserialize round-trip;
- medium flat POCO CSV serialize → deserialize round-trip.

Correctness oracles validate every representative field after round-trip.

## Dry validation

GitHub Actions run: `35617594192`.

Both targets completed immutable materialization, Package Source Mapping, NuGet content-hash/source verification, locked restore, Release build, semantic oracle, and BenchmarkDotNet Dry evidence.

## BenchmarkDotNet Short — pass 1

GitHub Actions run: `35618269053`

Artifact: `perf-csv-strict-ab-bench-ee86f21c7947ce45c968a016f197d0e5669faa81`

Artifact digest: `sha256:f71af56da26ae16a5121346016144b0e5d37644e24e6d37bdfdd4bb26921e4a5`

Harness SHA: `ee86f21c7947ce45c968a016f197d0e5669faa81`

Run id: `20260921T152248Z-ee86f21c7947`

Order: `2.1.2 → 2.2.0 → 2.2.0 → 2.1.2`

Job: BenchmarkDotNet `ShortRun`.

| Method | 2.1.2 Mean | 2.2.0 Mean | Time delta | 2.1.2 alloc | 2.2.0 alloc | Allocation delta | Drift 2.1.2 | Drift 2.2.0 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| SmallRoundTrip | 1.967 ms | 1.974 ms | +0.33% | 98,403.5 B/op | 98,352.5 B/op | -0.05% | 0.96% | 0.52% |
| MediumRoundTrip | 5.002 ms | 5.070 ms | +1.37% | 216,245.5 B/op | 216,836.5 B/op | +0.27% | 0.38% | 1.56% |

## Interpretation

Both timing deltas are small relative to the absolute operation cost and have low A-B-B-A repeat drift.

The small-record path is effectively unchanged in both timing and allocation.

The medium-record path reports +1.37% timing and +0.27% allocation in this hosted pass. This is not large enough, on a GitHub-hosted runner and with only one Short pass, to support a release-blocking regression claim.

Because the wrapper source and CsvHelper version are the same, this lane is primarily a package-split / integration-regression guard. The first pass shows no evidence of a material performance penalty from moving CSV support into `SmartPipe.Extensions.Csv`.

Filesystem source/sink throughput remains intentionally outside this strict timing lane; disk/page-cache variance would weaken the comparison.

## Result

SP220-09 CSV has a valid first strict A/B evidence pass.

For the tested CPU-only legacy-compatible transform path:

- no material repeatable timing regression is observed;
- allocation behavior is effectively unchanged;
- package split did not introduce a meaningful measurable overhead in this pass.

A second independent Short pass remains required before promoting small timing differences into repeatable performance claims.
