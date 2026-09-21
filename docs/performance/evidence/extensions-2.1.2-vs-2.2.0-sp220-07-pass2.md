# SP220-07 strict A/B reproducibility — pass 2

Date: 2026-09-21

## Scope

This is the second independent GitHub-hosted ShortRun for the SP220-07 strict A/B workload.

Baseline: SmartPipe 2.1.2, SHA `8e79902d22de714f493582946f7c260462b0895e`  
Candidate: SmartPipe 2.2.0 SP220-01..11, SHA `61ceef6bf69aef0a4f79b25384352d238979200f`  
Harness SHA: `33a17b53b32ad34991784e6133d03a6af1673ab9`  
GitHub Actions run: `35629283617`  
Run id: `20260921T170316Z-33a17b53b32a`  
Order: `2.1.2 → 2.2.0 → 2.2.0 → 2.1.2`  
Timing authority: **non-authoritative / informational**

## Pass 2 normalized results

| Method | 2.1.2 Mean | 2.2.0 Mean | Time delta | Allocation delta | Drift 2.1.2 | Drift 2.2.0 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| FilterSyncPass | 67.68 ns | 59.64 ns | -11.87% | n/a | 10.79% | 0.25% |
| ConditionalFalsePassThrough | 54.13 ns | 33.01 ns | -39.01% | n/a | 11.32% | 3.51% |
| ConditionalTrueTransform | 85.26 ns | 33.10 ns | -61.18% | n/a | 2.51% | 2.66% |
| CompositeThreeTransforms | 205.92 ns | 244.57 ns | +18.77% | 0.00% | 0.05% | 2.40% |
| ValidationValid | 574.61 ns | 597.65 ns | +4.01% | 0.00% | 15.79% | 0.66% |
| CompressionBrotli1KiB | 8.860 µs | 8.854 µs | -0.07% | 0.00% | 0.61% | 0.07% |
| LoggerSinkLegacyDisabled | 46.42 ns | 47.21 ns | +1.71% | 0.00% | 0.79% | 0.96% |
| ChannelMergeTwoReaders | 12.978 µs | 12.193 µs | -6.05% | +1.58% | 9.63% | 1.05% |

Artifact digest: `sha256:245cbdf2a945da806e0dd57fa38847af4d5f3ddebb56e71e9a16e1423319d522`.

## Reproducibility against pass 1

| Method | Pass 1 | Pass 2 | Reproducibility assessment |
| --- | ---: | ---: | --- |
| ConditionalFalsePassThrough | -38.50% | -39.01% | **Strongly reproduced.** Same direction and almost identical magnitude. |
| ConditionalTrueTransform | -60.74% | -61.18% | **Strongly reproduced.** Same direction and almost identical magnitude. |
| CompositeThreeTransforms | +33.66% | +18.77% | **Direction reproduced.** Candidate is materially slower in both passes, but magnitude is runner-sensitive. |
| ValidationValid | +12.72% | +4.01% | Direction repeated, but pass-2 baseline drift is 15.79%; **not yet a stable magnitude claim**. |
| CompressionBrotli1KiB | +0.35% | -0.07% | **No meaningful difference.** Both passes are effectively parity. |
| ChannelMergeTwoReaders timing | -0.27% | -6.05% | Candidate direction is not sufficiently stable because pass-2 baseline drift is 9.63%. |
| ChannelMergeTwoReaders allocation | +1.58% | +1.58% | **Exactly reproduced structural allocation increase.** |
| FilterSyncPass | +3.28% | -11.87% | Direction flipped; **noise / no conclusion**. |
| LoggerSinkLegacyDisabled | -5.14% | +1.71% | Direction flipped; **noise / no conclusion**. |

## Conclusions

Two performance effects are now strongly reproducible on the hosted lab:

1. `ConditionalTransform<T>` is substantially faster in 2.2.0 on both tested branches:
   - false/pass-through path: about 39% faster in both passes;
   - true/child-transform path: about 61% faster in both passes.

2. `CompositeTransform<T>` is materially slower in 2.2.0 in both passes, although the exact hosted magnitude varies substantially (+33.66% then +18.77%). This deserves implementation-level follow-up and a controlled-runner confirmation before any release gate.

A third structural effect is also reproducible:

- the two-reader compatibility `ChannelMerge` path allocates 120 extra bytes per operation in 2.2.0 (+1.58%) in both passes.

Validation still trends slower but is not stable enough for a precise claim. Filter, LoggerSink, and ChannelMerge timing are currently too runner-sensitive to interpret directionally.

Because all timings are from GitHub-hosted runners, none of these timing effects are yet a hard release threshold. Allocation differences are substantially more deterministic and can be treated with higher confidence.
