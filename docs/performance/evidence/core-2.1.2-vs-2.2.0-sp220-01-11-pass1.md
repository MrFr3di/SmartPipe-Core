# Core comparative evidence — Pass 1

Date: 2026-09-21

## Scope

This evidence compares:

- baseline: SmartPipe 2.1.2, Git SHA `8e79902d22de714f493582946f7c260462b0895e`;
- candidate: SmartPipe 2.2.0 SP220-01…11, Git SHA `61ceef6bf69aef0a4f79b25384352d238979200f`;
- SP220-12 / Mapster is outside this lab revision.

The Core runtime workload is classified as `strict-ab`: both targets compile the same
`CorePipelineAbBenchmarks.cs` workload source. Package identity is bound to immutable
target artifacts through exact product SHA, local-feed Package Source Mapping, NuGet lock
`contentHash`, restored-source metadata, and package SHA-256 provenance.

GitHub-hosted wall-clock timing is **informational only**. Correctness, package provenance,
resource lifecycle, boundedness, and deterministic stress invariants are hard evidence.

## End-to-end validation

Workflow run `35586018413` completed successfully.

It proved the complete path:

`2.1.2 baseline provision/verify -> exact candidate worktree -> Release build -> pack ->
package graph/metadata verification -> source-mapped restore -> NuGet-native contentHash
verification -> locked restore -> both A/B executables -> BenchmarkDotNet Dry`.

The candidate package graph reported 19 package nodes, 14 active packages, and 5 planned
packages. Fourteen active candidate packages were packed and metadata-verified.

## BenchmarkDotNet Short — Pass 1

Workflow run: `35586307626`

Harness SHA: `a2bae8089d856dba8734e4809b1e5b1ea08c796d`

Order: `v212 -> v220 -> v220 -> v212`

Job: BenchmarkDotNet `ShortRun`, three measurement iterations per invocation. The table
uses the median of the two invocation means for each target. Repeat drift is the absolute
difference between those two invocation means relative to their center.

| Items | Concurrency | 2.1.2 mean | 2.2.0 mean | Time delta | 2.1.2 alloc | 2.2.0 alloc | Alloc delta | 2.1.2 repeat drift | 2.2.0 repeat drift |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1,000 | 1 | 1.992 ms | 2.069 ms | +3.85% | 719.97 KiB/op | 728.27 KiB/op | +1.15% | 0.12% | 4.52% |
| 1,000 | 4 | 2.287 ms | 2.331 ms | +1.93% | 1,481.58 KiB/op | 1,523.63 KiB/op | +2.84% | 9.54% | 12.89% |
| 1,000 | 16 | 2.943 ms | 2.963 ms | +0.68% | 1,564.67 KiB/op | 1,595.38 KiB/op | +1.96% | 6.43% | 9.69% |
| 100,000 | 1 | 192.602 ms | 195.926 ms | +1.73% | 71,105.52 KiB/op | 71,114.45 KiB/op | +0.01% | 0.37% | 1.87% |
| 100,000 | 4 | 188.884 ms | 190.230 ms | +0.71% | 171,397.26 KiB/op | 170,222.59 KiB/op | -0.69% | 9.08% | 3.78% |
| 100,000 | 16 | 192.702 ms | 197.016 ms | +2.24% | 171,363.54 KiB/op | 171,315.79 KiB/op | -0.03% | 6.43% | 4.28% |

### Pass-1 interpretation

The candidate measured about 0.7% to 3.9% slower in this single hosted Short pass. That is
not a confirmed runtime regression. In the concurrency 4 and 16 cases, repeat drift inside
the same target is materially larger than the measured cross-version delta. The 100,000-item
allocation profiles are effectively flat; the 1,000-item cases show a small candidate
allocation increase that should be checked for reproducibility.

No release threshold is derived from this pass.

## Deterministic Core stress

Workflow run: `35587136157`

Harness SHA: `14b3f56965426793c00bb4e973ac7bb382f21a7d`

Environment captured by the run:

- Ubuntu 24.04.5 LTS;
- x64;
- .NET runtime 10.0.11;
- 4 logical processors on the GitHub-hosted runner.

| Profile | Target | Completed / expected | Errors | Checksum | Working set after run | ThreadPool threads | Pending work |
| --- | --- | ---: | ---: | --- | ---: | ---: | ---: |
| parallel32 | 2.1.2 | 32,000 / 32,000 | 0 | exact | 60.7 MB | 4 | 0 |
| parallel32 | 2.2.0 | 32,000 / 32,000 | 0 | exact | 61.3 MB | 5 | 0 |
| sequential1000 | 2.2.0 | 1,000,000 / 1,000,000 | 0 | exact | 70.1 MB | 6 | 0 |
| sequential1000 | 2.1.2 | 1,000,000 / 1,000,000 | 0 | exact | 70.0 MB | 8 | 0 |

Hosted elapsed time intentionally points in different directions
(`parallel32`: candidate slower; `sequential1000`: candidate faster), so it is retained as
diagnostic context rather than used as a gate.

The confirmed result from this stress pass is narrower and stronger: both targets completed
all deterministic work with exact item counts/checksums, zero reported errors, zero pending
ThreadPool work at the end of each profile, and similar post-run working set.

## Current conclusion

Pass 1 does not establish a Core performance regression in 2.2.0. It establishes that the
comparative harness and immutable-target chain are operational, and that SP220-01…11
preserves the tested Core correctness/lifecycle properties under the first deterministic
stress set. Timing needs a second independent Short pass before its direction is treated as
reproducible evidence.
