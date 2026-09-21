# SP220-02 Definition Model v2.2-only characterization — pass 1

Date: 2026-09-21

## Scope

This pass characterizes the new SmartPipe 2.2.0 definition/runtime model on the immutable SP220-01..11 candidate:

- candidate version: `2.2.0`;
- candidate SHA: `61ceef6bf69aef0a4f79b25384352d238979200f`;
- harness SHA: `20c80df43935f4cd7a5410e4848bae1d48e7ed4e`;
- SP220-12 / Mapster: excluded from this lab revision.

Scenario class: **v220-only**.

There is intentionally no 2.1.2 baseline ratio for this workload. The 2.2.0 definition model has no honest one-to-one pre-2.2.0 equivalent, so the lab reports absolute cost, allocations, repeat drift, and intra-version scaling only.

## Public-API workload

The corrected workload uses only public APIs.

Covered methods:

- `Build_ZeroStage`;
- `Build_OneStage`;
- `Build_TenStages`;
- `BuildAndStart_ZeroStage`;
- `BuildAndStart_OneStage`;
- `BuildAndStart_TenStages`;
- `StartAndComplete_ZeroStage`;
- `StartAndComplete_OneStage`;
- `StartAndComplete_TenStages`;
- `LegacyBuilder_StartAndComplete_OneStage`.

An earlier Dry attempt used the internal-only `GetExecutionPlan` path and was rejected by the harness. The benchmark was rewritten around public `Build`, `StartAsync`, completion, and compatibility-builder APIs before evidence collection.

## Correctness / Dry validation

Corrected public-API Dry:

- GitHub Actions run: `35625636467`;
- conclusion: **success**.

The Dry proves:

- immutable candidate package materialization;
- verified NuGet content hash/source provenance;
- Release build;
- no internal definition-model API dependency;
- complete BenchmarkDotNet JSON/statistics for all ten methods.

## BenchmarkDotNet Short — pass 1

GitHub Actions run: `35626724832`

Artifact: `perf-definition-model-v220-bench-20c80df43935f4cd7a5410e4848bae1d48e7ed4e`

Artifact digest: `sha256:14e38c7ff30eb98b9165fb546deaddb49ac51e8675b68e89e26d57cf20a4bf6d`

Run id: `20260921T163847Z-20c80df43935`

The benchmark is repeated twice on the same GitHub-hosted runner. The table reports the median of the two invocation means.

| Method | Mean | Allocation | Repeat drift |
| --- | ---: | ---: | ---: |
| Build_ZeroStage | 407.32 ns | 1.586 KiB/op | 2.55% |
| Build_OneStage | 664.87 ns | 2.297 KiB/op | 0.45% |
| Build_TenStages | 3.711 µs | 9.211 KiB/op | 2.57% |
| BuildAndStart_ZeroStage | 16.835 µs | 13.561 KiB/op | 2.33% |
| BuildAndStart_OneStage | 32.675 µs | 15.606 KiB/op | 18.42% |
| BuildAndStart_TenStages | 51.130 µs | 33.241 KiB/op | 2.52% |
| StartAndComplete_ZeroStage | 17.874 µs | 11.447 KiB/op | 7.05% |
| StartAndComplete_OneStage | 30.586 µs | 12.520 KiB/op | 0.68% |
| StartAndComplete_TenStages | 50.281 µs | 21.839 KiB/op | 12.33% |
| LegacyBuilder_StartAndComplete_OneStage | 28.271 µs | 17.260 KiB/op | 22.71% |

## Intra-version scaling

Build:

- 1 stage vs 0 stage time: `1.632x`;
- 10 stages vs 0 stage time: `9.110x`;
- 10 stages vs 0 stage allocation: `5.808x`.

Build + first start:

- 1 stage vs 0 stage time: `1.941x`;
- 10 stages vs 0 stage time: `3.037x`;
- 10 stages vs 0 stage allocation: `2.451x`.

Warm reusable-definition start + completion:

- 1 stage vs 0 stage time: `1.711x`;
- 10 stages vs 0 stage time: `2.813x`;
- 10 stages vs 0 stage allocation: `1.908x`.

## Interpretation

Definition construction itself is sub-microsecond for zero/one-stage definitions in this hosted pass. Ten-stage construction remains only a few microseconds, but its cost and allocation scale materially with stage count.

The more important application-visible cost is run startup/completion. A reusable ten-stage definition completed in about `50 µs` and allocated about `21.8 KiB/op` in this pass, compared with about `17.9 µs` / `11.4 KiB/op` for a zero-stage definition.

The ten-stage scaling is substantially less than linear for full run startup/completion compared with raw definition construction, which is consistent with a meaningful fixed per-run lifecycle cost plus per-stage work.

Several startup observations are noisy on the GitHub-hosted runner:

- `BuildAndStart_OneStage`: 18.42% repeat drift;
- `StartAndComplete_TenStages`: 12.33%;
- `LegacyBuilder_StartAndComplete_OneStage`: 22.71%.

Those methods are retained as evidence, but their absolute timing should not be used as a numeric release gate from this single hosted pass.

## Result

SP220-02 definition-model now has a valid first **v2.2-only** characterization pass.

This evidence establishes a baseline for future 2.2.x / 2.3.x changes without inventing a fake 2.1.2 comparison. Future controlled-runner runs can use these method groups to detect changes in definition construction, first-start cost, reusable-definition startup, allocations, and stage-count scaling.
