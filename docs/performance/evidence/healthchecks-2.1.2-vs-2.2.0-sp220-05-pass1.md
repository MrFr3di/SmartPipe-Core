# SP220-05 HealthChecks evolution evidence — pass 1

Date: 2026-09-21

## Scope

This pass compares the latest pre-2.2.0 baseline (`2.1.2`, product SHA `8e79902d22de714f493582946f7c260462b0895e`) with the immutable SP220-01..11 candidate (`2.2.0`, product SHA `61ceef6bf69aef0a4f79b25384352d238979200f`).

Scenario class: **evolution**. Cross-version percentage deltas are intentionally not reported.

The common observable contract is one registered, not-started pipeline whose health evaluation is `Degraded`. The implementations are not architecturally equivalent:

- 2.1.2: generic-pair health monitor through `SmartPipe.Extensions`;
- 2.2.0: keyed readiness over canonical DI registration plus bounded run-observation state through `SmartPipe.Extensions.HealthChecks`.

## Valid run

GitHub Actions run: `35604480640`  
Harness SHA: `e65e087e3cde3307afd42f745693fa34c1e60fb9`  
Run id: `20260921T131944Z-e65e087e3cde`  
BenchmarkDotNet: `0.15.8`  
Runtime: .NET 10.0.12 on `ubuntu-24.04` hosted runner  
Timing authority: **non-authoritative / informational**

Counter-balanced order: `2.1.2 → 2.2.0 → 2.2.0 → 2.1.2`.

| Method | 2.1.2 Mean | 2.2.0 Mean | 2.1.2 Alloc | 2.2.0 Alloc | Repeat drift 2.1.2 | Repeat drift 2.2.0 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| CheckRegisteredPipeline | 2.691 µs | 2.707 µs | 3.08 KiB/op | 3.21 KiB/op | 4.41% | 0.20% |
| RegisterAndBuildProvider | 35.272 µs | 65.039 µs | 42.86 KiB/op | 73.88 KiB/op | 1.05% | 3.36% |
| ResolveHealthCheckService | 23.612 ns | 23.100 ns | 0 B/op | 0 B/op | 8.29% | 5.61% |

## Interpretation

The steady health-check evaluation path is at essentially the same absolute scale in this hosted pass. Service resolution is also at the same tens-of-nanoseconds scale and allocates nothing.

The 2.2.0 registration/provider-build path is materially heavier in absolute terms in this pass. That observation includes the additional canonical keyed registration, readiness options/validation, registry, run-registry, and run-observation infrastructure. Because the architecture and contract changed, this is characterization evidence rather than a strict regression percentage.

No correctness failure was observed in the valid pass.

## Invalidated earlier runs

The earlier HealthChecks Dry and Short evidence must not be used.

The benchmark consumer did not explicitly reference the full `Microsoft.Extensions.DependencyInjection` and `Microsoft.Extensions.Logging` implementations. BenchmarkDotNet's transitive graph therefore supplied version 6.0.0 implementations while the SmartPipe/HealthChecks graph used Microsoft.Extensions abstractions 10.0.11. The resulting mixed DI runtime caused a deterministic `NullReferenceException` inside `CallSiteFactory.Populate()` for the 2.2.0 target.

This was a benchmark-app dependency-graph defect, not a confirmed SmartPipe product defect. Both benchmark apps now explicitly pin the full DI and Logging runtime to 10.0.11.

The lab also now rejects BenchmarkDotNet JSON that contains missing `Statistics`, missing `Measurements`, or zero measured operations. This is required because BenchmarkDotNet can still produce a JSON artifact after a benchmark setup failure.

## Result

SP220-05 HealthChecks has a valid first evolution evidence pass.

The important follow-up is broader scenario coverage after the remaining SP220-01..11 integration lanes are established; hosted-run timing remains informational.
