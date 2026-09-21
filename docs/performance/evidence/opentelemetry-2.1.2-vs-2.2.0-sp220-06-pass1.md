# SP220-06 OpenTelemetry evolution evidence — pass 1

Date: 2026-09-21

## Scope

This pass compares the latest pre-2.2.0 baseline (`2.1.2`, product SHA `8e79902d22de714f493582946f7c260462b0895e`) with the immutable SP220-01..11 candidate (`2.2.0`, product SHA `61ceef6bf69aef0a4f79b25384352d238979200f`).

SP220-12 / Mapster is outside this lab revision.

Scenario class: **evolution**. Cross-version percentage deltas are intentionally not reported.

The common observable contract is OpenTelemetry registration of the existing SmartPipe diagnostics sources:

- metric source: `SmartPipe.Core`;
- activity source: `SmartPipe.Core`.

The implementations are intentionally different:

- 2.1.2: equivalent application-owned manual `AddMeter("SmartPipe.Core")` plus `AddSource("SmartPipe.Core")`;
- 2.2.0: `SmartPipe.Extensions.OpenTelemetry.AddSmartPipeInstrumentation()`.

Both benchmark apps use the same OpenTelemetry provider stack. Exporters are excluded from the timed benchmark region.

## Provenance and correctness validation

Corrected OpenTelemetry Dry workflow run: `35604838654`.

It proved:

- immutable baseline and exact-candidate package feeds;
- Package Source Mapping for every resolved `SmartPipe.*` package;
- NuGet-native lock `contentHash` validation;
- restored-source metadata validation;
- generated lock followed by locked restore;
- Release/warnings-as-errors build for both adapters;
- BenchmarkDotNet JSON with statistics, measurements, and non-zero measured operations;
- application-owned MeterProvider and TracerProvider composition;
- exact builder identity returned by `AddSmartPipeInstrumentation()`;
- collection-local idempotence of repeated candidate registration;
- live metric listener activation and live ActivitySource listener activation;
- successful InMemory export of one metric and one trace in the correctness oracle.

The exporter is used only by the correctness oracle, outside the timed benchmark methods.

## BenchmarkDotNet Short — pass 1

GitHub Actions run: `35607148028`  
Harness SHA: `4993e4f070eb2772968c76e2c1b61c1bc645aa3e`  
Run id: `20260921T134227Z-4993e4f070eb`  
BenchmarkDotNet: `0.15.8`  
SDK: `.NET SDK 10.0.303`  
Host: `.NET 10.0.12`  
Runner: `ubuntu-24.04`, 4 logical processors  
Timing authority: **non-authoritative / informational**

Counter-balanced order: `2.1.2 → 2.2.0 → 2.2.0 → 2.1.2`.

| Method | 2.1.2 Mean | 2.2.0 Mean | 2.1.2 allocation | 2.2.0 allocation | 2.1.2 repeat drift | 2.2.0 repeat drift |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| BuildResolveDisposeProviders | 40.055 µs | 39.208 µs | 67.28 KiB/op | 68.51 KiB/op | 8.69% | 20.79% |
| RegisterInstrumentation | 0.575 µs | 0.654 µs | 2.84 KiB/op | 3.23 KiB/op | 0.67% | 2.07% |
| ResolveProviders | 0.037 µs | 0.038 µs | 0 B/op | 0 B/op | 1.00% | 2.14% |

## Interpretation

The registration helper itself is sub-microsecond in both versions. The candidate helper carries a small absolute setup/allocation cost relative to equivalent manual `AddMeter/AddSource` composition, while providing the SmartPipe-specific registration contract and idempotence guarantee.

Provider resolution is effectively the same tens-of-nanoseconds scale and allocates nothing in this pass.

Provider build/resolve/dispose is much noisier on the candidate side: repeat drift reached 20.79%. The observed absolute center is slightly lower for 2.2.0, but this is not precise enough to support a directional performance conclusion.

Because the scenario is `evolution`, none of these absolute differences are treated as a strict regression or improvement gate.

## Invalidated earlier run

Workflow run `35604438034` is invalid as product performance evidence.

The initial candidate benchmark app called `AddSmartPipeInstrumentation()` and then attempted to resolve `MeterProvider` and `TracerProvider` without composing those providers in the application. The resulting setup failure was:

`InvalidOperationException: No service for type 'OpenTelemetry.Metrics.MeterProvider' has been registered.`

That expectation was incorrect. `SmartPipe.Extensions.OpenTelemetry` only registers the SmartPipe meter and activity source into the OpenTelemetry builder; it does not create providers, exporters, background work, resources, samplers, processors, or readers.

The benchmark apps were corrected so provider composition is explicitly application-owned on both sides. The lab also rejects BenchmarkDotNet artifacts with missing statistics or measurements, preventing setup failures from being mistaken for valid evidence.

## Result

SP220-06 OpenTelemetry has a valid first evolution evidence pass.

The current evidence supports the package architecture: SmartPipe 2.2.0 adds a thin, exporter-neutral registration layer over diagnostics already emitted by Core, while application-owned provider/export composition remains outside the package.
