# SP220-08 JSON strict A/B evidence — pass 1

Date: 2026-09-21

## Scope

This pass compares the latest pre-2.2.0 baseline (`2.1.2`, product SHA `8e79902d22de714f493582946f7c260462b0895e`) with the immutable SP220-01..11 candidate (`2.2.0`, product SHA `61ceef6bf69aef0a4f79b25384352d238979200f`).

SP220-12 / Mapster is outside this lab revision.

Scenario: `json`.

Scenario class: **strict-ab**.

The same shared benchmark source is compiled against `SmartPipe.Extensions.Json 2.1.2` and `SmartPipe.Extensions.Json 2.2.0`.

GitHub-hosted timing remains **informational / non-authoritative**.

## Strict workload

The benchmark intentionally isolates the CPU/memory cost of `JsonTransform<TInput,TOutput>` and excludes filesystem source/sink IO.

Covered paths:

- source-generated `JsonTypeInfo` round-trip, small payload;
- `JsonSerializerOptions` compatibility round-trip, small payload;
- source-generated `JsonTypeInfo` round-trip, medium payload;
- `JsonSerializerOptions` compatibility round-trip, medium payload.

The correctness oracle verifies every representative field and collection after round-trip.

## Dry validation

GitHub Actions run: `35609053362`.

Both targets completed immutable package materialization, Package Source Mapping, NuGet content-hash/source verification, locked restore, Release build, correctness oracle, and BenchmarkDotNet Dry evidence.

## BenchmarkDotNet Short — pass 1

GitHub Actions run: `35617304881`

Artifact: `perf-json-strict-ab-bench-5e3b4e83ec86902715b12b4d8631b34e267b5c7f`

Artifact digest: `sha256:43250d75bedcbd3d54ce2de342ee06a2b084c557238e53b3b6f4ac4004fd7a95`

Harness SHA: `5e3b4e83ec86902715b12b4d8631b34e267b5c7f`

Run id: `20260921T151421Z-5e3b4e83ec86`

Order: `2.1.2 → 2.2.0 → 2.2.0 → 2.1.2`

Job: BenchmarkDotNet `ShortRun`.

| Method | 2.1.2 Mean | 2.2.0 Mean | Time delta | 2.1.2 alloc | 2.2.0 alloc | Allocation delta | Drift 2.1.2 | Drift 2.2.0 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| SourceGeneratedSmallRoundTrip | 456.01 ns | 469.43 ns | +2.94% | 144 B/op | 144 B/op | 0.00% | 1.11% | 2.73% |
| OptionsSmallRoundTrip | 538.97 ns | 543.15 ns | +0.77% | 144 B/op | 144 B/op | 0.00% | 8.07% | 0.40% |
| SourceGeneratedMediumRoundTrip | 5.939 µs | 5.849 µs | -1.51% | 4640 B/op | 4640 B/op | 0.00% | 0.77% | 2.11% |
| OptionsMediumRoundTrip | 5.995 µs | 6.044 µs | +0.82% | 4952 B/op | 4952 B/op | 0.00% | 2.55% | 2.46% |

## Interpretation

All measured allocation values are exactly unchanged between 2.1.2 and 2.2.0 for the four strict paths.

The three more repeatable timing groups are within roughly ±3%:

- source-generated small: +2.94%;
- source-generated medium: -1.51%;
- options medium: +0.82%.

This pass therefore does not show evidence of a material systematic JSON round-trip regression from the 2.2.0 package work.

`OptionsSmallRoundTrip` reports +0.77%, but its baseline invocation drift is 8.07%, so that directional timing result is not treated as meaningful.

The source-generated path also preserves its expected allocation advantage for the medium payload relative to the options path in both product versions: 4640 B/op versus 4952 B/op. This is a within-version workload characteristic, not a 2.2.0-specific change.

## Result

SP220-08 JSON has a valid first strict A/B evidence pass.

For the tested in-memory `JsonTransform` round-trip paths:

- no allocation regression is observed;
- no material repeatable timing regression is observed;
- source-generated and options-based behavior remain stable across the 2.1.2 → 2.2.0 transition.

A second independent Short pass is still required before turning small timing deltas into repeatable performance claims.
