# Dependency Injection evolution evidence — Pass 1

Date: 2026-09-21

## Scope

This evidence compares the dependency-injection user path available in:

- baseline: SmartPipe 2.1.2, Git SHA `8e79902d22de714f493582946f7c260462b0895e`;
- candidate: SmartPipe 2.2.0 SP220-01…11, Git SHA `61ceef6bf69aef0a4f79b25384352d238979200f`.

SP220-12 / Mapster is outside this lab revision.

The DI scenario is intentionally classified as `evolution`, not `strict-ab`.
The public registration and runtime ownership models changed materially:

- 2.1.2 registers the generic-pair `ISmartPipeFactory<TInput,TOutput>`;
- 2.2.0 registers a keyed `ISmartPipeRunFactory<TInput,TOutput>`, canonical registry,
  run registry/observation state, and creates a DI scope per run.

Cross-version percentage deltas are therefore intentionally omitted. The evidence is
side-by-side absolute cost, allocation, repeat drift, correctness, and provenance.

## Provenance and Dry validation

Workflow run `35596806339` completed successfully.

It proved:

- immutable baseline and exact-candidate package feeds;
- Package Source Mapping for every resolved `SmartPipe.*` package;
- NuGet-native lock `contentHash` validation;
- restored-source metadata validation;
- generated lock followed by locked restore;
- Release/warnings-as-errors build for both adapters;
- async correctness precheck before timed work;
- BenchmarkDotNet JSON evidence for both targets;
- v2.2-only keyed-registration scale Dry for 1, 32, and 256 keys.

The v2.2-only scale Dry is a functional/characterization gate in this pass, not an
authoritative timing measurement.

## BenchmarkDotNet Short — Pass 1

Workflow run: `35597932896`

Harness SHA: `b84293f6d499288046b713e0fea99a9cab215b22`

Order: `v212 -> v220 -> v220 -> v212`

Job: BenchmarkDotNet `ShortRun`.

The table uses the median of the two invocation means for each target. Repeat drift is the
absolute difference between those two invocation means relative to their center.

| Method | 2.1.2 Mean | 2.2.0 Mean | 2.1.2 allocation | 2.2.0 allocation | 2.1.2 repeat drift | 2.2.0 repeat drift |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| RegisterAndBuildProvider | 7.829 us | 9.700 us | 11.69 KiB/op | 18.92 KiB/op | 3.16% | 3.81% |
| ResolveFactory | 0.026 us | 0.033 us | 0.00 KiB/op | 0.00 KiB/op | 5.76% | 1.20% |
| StartCompleteDisposeRun | 15.863 us | 26.398 us | 10.11 KiB/op | 13.48 KiB/op | 23.64% | 21.70% |

## Interpretation

The registration/provider-build and factory-resolution measurements have relatively small
repeat drift on this hosted run. They show that the 2.2.0 DI architecture has a higher
absolute setup/lookup cost than the simpler 2.1.2 generic-pair registration path. This is
consistent with the additional keyed registry and run-observation infrastructure that the
candidate deliberately provides.

`StartCompleteDisposeRun` is much noisier: repeat drift is above 20% for both targets.
Its absolute values are useful as characterization evidence, but this pass is not precise
enough for a fine-grained lifecycle timing conclusion.

The candidate also allocates more during provider construction and a complete empty
run. That is an observable architectural cost to track, especially as the number of
registered pipeline keys grows. It is not labeled a strict performance regression because
the 2.2.0 operation includes lifecycle/ownership/observation semantics absent from the
2.1.2 operation.

## Current DI conclusion

The DI comparative harness is operational end-to-end and preserves the intended
`evolution` semantics. The next DI measurement work should focus on candidate-only scaling
across many keys and on repeatability of lifecycle allocation/cost, rather than converting
these side-by-side numbers into a cross-version percentage gate.
