# SP220-07 Channels / Transforms / DataAnnotations / Logging strict A/B evidence — pass 1

Date: 2026-09-21

## Scope

This pass compares the latest pre-2.2.0 baseline (`2.1.2`, product SHA `8e79902d22de714f493582946f7c260462b0895e`) with the immutable SP220-01..11 candidate (`2.2.0`, product SHA `61ceef6bf69aef0a4f79b25384352d238979200f`).

SP220-12 / Mapster is outside this lab revision.

Scenario: `channels-transforms-logging-dataannotations`.

Scenario class: **strict-ab**.

Unlike the DI/Hosting/HealthChecks/OpenTelemetry evolution lanes, this lane measures only public operations whose observable semantics are deliberately held equivalent across 2.1.2 and 2.2.0. Cross-version timing and allocation deltas are therefore permitted by the lab contract.

GitHub-hosted timing remains **informational / non-authoritative**.

## Strict workload

The same shared benchmark source is compiled against both package graphs.

Covered operations:

- synchronous `FilterTransform<T>` successful pass;
- `ConditionalTransform<T>` false/pass-through path;
- `ConditionalTransform<T>` true/child-transform path;
- initialized `CompositeTransform<T>` with three equivalent transforms;
- successful `ValidationTransform<T>` DataAnnotations + custom rule path;
- Brotli `CompressionTransform` over 1 KiB;
- legacy-compatible `LoggerSink<T>(ILogger)` path with `NullLogger`;
- two-reader `ChannelMerge.Merge` with deterministic item count and checksum.

Candidate-only APIs such as multi-reader merge, token-aware filter overloads, safe logging options, and other newly expanded behavior are intentionally excluded from the strict ratio.

## Correctness / Dry validation

GitHub Actions run: `35608129473`.

Both targets completed the immutable materialization, Release build, package provenance verification, semantic correctness oracle, and BenchmarkDotNet Dry run.

The correctness oracle additionally verifies:

- exact transform output values;
- initialized composite success-path semantics;
- successful DataAnnotations result;
- Brotli decompression reproduces the original bytes;
- ChannelMerge produces the exact expected item count and checksum.

## BenchmarkDotNet Short — pass 1

GitHub Actions run: `35608644883`

Artifact: `perf-extensions-strict-ab-bench-f8bdb3cdfb3215edb5344f374a9193ee6f26948a`

Artifact digest: `sha256:e66e1aceae69a14a277eb262a8a94f0ea84a0f74b2c56d86061b99f83ba494d5`

Harness SHA: `f8bdb3cdfb3215edb5344f374a9193ee6f26948a`

Run id: `20260921T135709Z-f8bdb3cdfb32`

Order: `2.1.2 → 2.2.0 → 2.2.0 → 2.1.2`

Job: BenchmarkDotNet `ShortRun`.

The table uses the median of the two invocation means for each target. Repeat drift is the absolute difference between those two invocation means relative to their center.

| Method | 2.1.2 Mean | 2.2.0 Mean | Time delta | 2.1.2 alloc | 2.2.0 alloc | Allocation delta | Drift 2.1.2 | Drift 2.2.0 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| FilterSyncPass | 65.90 ns | 68.06 ns | +3.28% | 0 B/op | 0 B/op | n/a | 2.14% | 12.32% |
| ConditionalFalsePassThrough | 58.03 ns | 35.69 ns | -38.50% | 0 B/op | 0 B/op | n/a | 0.88% | 2.94% |
| ConditionalTrueTransform | 91.57 ns | 35.95 ns | -60.74% | 0 B/op | 0 B/op | n/a | 0.23% | 0.08% |
| CompositeThreeTransforms | 197.59 ns | 264.10 ns | +33.66% | 240 B/op | 240 B/op | 0.00% | 2.57% | 2.94% |
| ValidationValid | 519.14 ns | 585.17 ns | +12.72% | 1000 B/op | 1000 B/op | 0.00% | 1.66% | 1.51% |
| CompressionBrotli1KiB | 9.786 µs | 9.820 µs | +0.35% | 1400 B/op | 1400 B/op | 0.00% | 0.20% | 0.27% |
| LoggerSinkLegacyDisabled | 47.13 ns | 44.71 ns | -5.14% | 88 B/op | 88 B/op | 0.00% | 12.47% | 0.12% |
| ChannelMergeTwoReaders | 12.698 µs | 12.663 µs | -0.27% | 7608 B/op | 7728 B/op | +1.58% | 1.20% | 0.14% |

## Interpretation

### Stronger signals in this hosted pass

The conditional-transform paths are materially faster in the candidate while preserving zero-allocation behavior. The candidate implementation avoids the baseline's unnecessary async state-machine path when it can directly return the child transform or a completed `ValueTask`.

The composite success path is materially slower in this pass, while allocation is unchanged. This aligns with the candidate's stronger lifecycle ownership checks and initialized/disposed state coordination. The strict semantic workload therefore exposes a real cost worth tracking rather than hiding it behind package-split measurements.

The successful validation path is also slower in this pass with unchanged allocation. This is sufficiently repeatable in this run to warrant a second independent Short pass before deciding whether it is a persistent performance effect.

Brotli compression is effectively unchanged at the scale of this hosted pass.

Two-reader ChannelMerge timing is also effectively unchanged, while the candidate allocates 120 additional bytes per operation (+1.58%). The candidate implementation routes the two-reader compatibility overload through the generalized multi-reader machinery; this allocation difference should be tracked.

### Noisy observations

`FilterSyncPass` candidate repeat drift is 12.32%, so the measured +3.28% timing delta is not treated as a reliable directional result.

The baseline `LoggerSinkLegacyDisabled` repeat drift is 12.47%, so the -5.14% candidate timing result is likewise not treated as a reliable directional result.

## Result

SP220-07 now has a valid first strict A/B evidence pass.

The candidate shows both improvements and costs:

- substantially faster conditional dispatch;
- materially higher composite success-path cost;
- higher validation success-path cost;
- effectively unchanged compression;
- effectively unchanged two-reader merge timing with a small allocation increase.

Because this run executed on a GitHub-hosted runner, these timing deltas are evidence for follow-up rather than release-blocking thresholds. A second independent Short pass is required before promoting any timing result into a repeatable regression/improvement claim.
