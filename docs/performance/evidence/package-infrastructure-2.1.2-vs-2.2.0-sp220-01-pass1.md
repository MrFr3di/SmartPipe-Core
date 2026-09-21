# SP220-01 Package / Infrastructure footprint — pass 1

Date: 2026-09-21

## Scope

This pass measures the package shape of the immutable SmartPipe 2.1.2 baseline and the pinned SmartPipe 2.2.0 SP220-01..11 candidate.

- baseline SHA: `8e79902d22de714f493582946f7c260462b0895e`;
- candidate SHA: `61ceef6bf69aef0a4f79b25384352d238979200f`;
- harness SHA: `30cef18905490745e9714d32d4c0bb7620cffa85`;
- SP220-12 / Mapster scenario: excluded from this lab revision.

Scenario class: **evolution**.

Comparison policy: **package-closure engineering metrics**.

These metrics are derived from the real materialized `.nupkg` archives and their embedded `.nuspec` dependency metadata. They are not runtime performance claims.

## Workflow

GitHub Actions run: `35628952299`

Artifact: `perf-target-provenance-30cef18905490745e9714d32d4c0bb7620cffa85`

Artifact digest: `sha256:0a6aabff2033c1406a172f9a952c1b4e9f7363023fdf87c58e5bbbf50833fd7e`

The run completed:

- immutable baseline verification;
- exact-candidate detached-worktree pack;
- package graph verification;
- package metadata verification;
- real nupkg/nuspec footprint analysis.

## Whole target inventory

| Target | SmartPipe packages | Compressed size | Uncompressed size |
| --- | ---: | ---: | ---: |
| 2.1.2 materialized baseline | 3 | 0.324 MiB | 0.902 MiB |
| 2.2.0 SP220-01..11 candidate | 14 | 0.562 MiB | 1.524 MiB |

This inventory is not a like-for-like consumer footprint comparison. The 2.2.0 row contains the entire split package family, while a real application normally references only the capabilities it needs.

## Representative consumer closures

| Capability | 2.1.2 root | 2.2.0 root | SmartPipe packages | Compressed closure | External dependency IDs |
| --- | --- | --- | ---: | ---: | ---: |
| Core | SmartPipe.Core | SmartPipe.Core | 1→1 | 179.7→195.6 KiB | 1→1 |
| JSON | SmartPipe.Extensions.Json | SmartPipe.Extensions.Json | 2→2 | 252.7→259.3 KiB | 1→1 |
| Dependency Injection | SmartPipe.Extensions | SmartPipe.Extensions.DependencyInjection | 3→2 | 331.6→226.2 KiB | 9→2 |
| Hosting | SmartPipe.Extensions | SmartPipe.Extensions.Hosting | 3→3 | 331.6→254.1 KiB | 9→3 |
| Health Checks | SmartPipe.Extensions | SmartPipe.Extensions.HealthChecks | 3→3 | 331.6→255.3 KiB | 9→4 |
| OpenTelemetry | SmartPipe.Extensions | SmartPipe.Extensions.OpenTelemetry | 3→2 | 331.6→205.8 KiB | 9→2 |
| Channels | SmartPipe.Extensions | SmartPipe.Extensions.Channels | 3→2 | 331.6→208.6 KiB | 9→1 |
| Transforms | SmartPipe.Extensions | SmartPipe.Extensions.Transforms | 3→2 | 331.6→212.1 KiB | 9→1 |
| DataAnnotations | SmartPipe.Extensions | SmartPipe.Extensions.DataAnnotations | 3→3 | 331.6→223.2 KiB | 9→1 |
| Logging | SmartPipe.Extensions | SmartPipe.Extensions.Logging | 3→2 | 331.6→207.6 KiB | 9→1 |
| CSV | SmartPipe.Extensions | SmartPipe.Extensions.Csv | 3→2 | 331.6→246.2 KiB | 9→2 |
| Dapper | SmartPipe.Extensions | SmartPipe.Extensions.Dapper | 3→2 | 331.6→240.1 KiB | 9→2 |
| Entity Framework Core | SmartPipe.Extensions | SmartPipe.Extensions.EntityFrameworkCore | 3→2 | 331.6→224.4 KiB | 9→2 |

## Interpretation

The split-package architecture does what SP220-01 was intended to do for focused consumers:

- most specialized 2.2.0 consumers no longer need the broad `SmartPipe.Extensions` package;
- typical SmartPipe package closure falls from three packages to two;
- the broad 2.1.2 facade exposed nine distinct external dependency IDs;
- specialized 2.2.0 leaf closures generally expose only one to four external dependency IDs;
- compressed SmartPipe closure is materially smaller for DI, Hosting, Health Checks, OpenTelemetry, Channels, Transforms, DataAnnotations, Logging, CSV, Dapper, and EF Core consumers.

The exceptions are expected:

- `SmartPipe.Core` itself grew from 179.7 KiB to 195.6 KiB compressed because 2.2.0 contains the new definition/runtime model;
- JSON remains a two-package closure and is slightly larger in 2.2.0;
- the full family of all 14 candidate packages is larger than the three-package 2.1.2 materialization because it represents the complete modular product, not one application dependency closure.

## Mapster scope note

SP220-12 / Mapster was not benchmarked or characterized as a 2.2.0 scenario in this lab.

The historical/broad `SmartPipe.Extensions` package can still contain a Mapster dependency in its metadata. Excluding SP220-12 means the lab does not claim or measure the planned new Mapster integration work; it does not rewrite the historical package graph.

## Result

SP220-01 has valid package-infrastructure evidence.

The evidence supports the modularization objective: a focused 2.2.0 application can consume a substantially narrower SmartPipe and external dependency closure than a comparable 2.1.2 application that had to reference the broad extensions facade.

This is a packaging/dependency-isolation benefit. It must not be presented as proof that pipeline runtime execution is faster.
