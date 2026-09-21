# Core deterministic lifecycle stress — expanded pass 1

Date: 2026-09-21

## Scope

This pass validates deterministic lifecycle invariants for the latest pre-2.2.0 baseline and the immutable SP220-01..11 candidate.

- baseline: `2.1.2`;
- baseline SHA: `8e79902d22de714f493582946f7c260462b0895e`;
- candidate: `2.2.0`;
- candidate SHA: `61ceef6bf69aef0a4f79b25384352d238979200f`;
- harness SHA: `caa7aaab84985a19d0e638d504e9d756b46c9a40`.

Timing on GitHub-hosted runners is informational. Correctness, terminal-state classification, deterministic resource cleanup, and queue boundedness are hard gates.

## Workflow

GitHub Actions run: `35627181470`

Artifact: `perf-core-stress-caa7aaab84985a19d0e638d504e9d756b46c9a40`

Artifact digest: `sha256:8b9745edf6e2d76dfa927bb4f553d23cb7b93eae9f6387c5255ade28e2e70f9a`

Run id: `20260921T164323Z-caa7aaab8498`

Environment:

- Ubuntu 24.04.5 LTS;
- x64;
- .NET 10.0.11 runtime;
- 4 logical processors;
- GitHub runner image `ubuntu24 / 20260907.300.1`.

## Profiles

| Profile | Target | Correctness / terminal result | Resource result | TP pending final | Hosted elapsed |
| --- | --- | --- | --- | ---: | ---: |
| parallel32 | 2.1.2 | 32,000 / 32,000 items; checksum exact; 0 errors | lifecycle invariant passed | 0 | 227.90 ms |
| parallel32 | 2.2.0 | 32,000 / 32,000 items; checksum exact; 0 errors | lifecycle invariant passed | 0 | 211.42 ms |
| sequential1000 | 2.2.0 | 1,000,000 / 1,000,000 items; checksum exact; 0 errors | lifecycle invariant passed | 0 | 3570.33 ms |
| sequential1000 | 2.1.2 | 1,000,000 / 1,000,000 items; checksum exact; 0 errors | lifecycle invariant passed | 0 | 3274.45 ms |
| cancel32 | 2.1.2 | 32 / 32 runs reached `Cancelled`; 0 errors | 96 / 96 source/stage/sink disposals | 0 | 97.39 ms |
| cancel32 | 2.2.0 | 32 / 32 runs reached `Cancelled`; 0 errors | 96 / 96 source/stage/sink disposals | 0 | 115.79 ms |
| sourcefailure1000 | 2.2.0 | 1000 / 1000 runs reached `Faulted`; 0 harness errors | 3000 / 3000 component disposals | 0 | 245.53 ms |
| sourcefailure1000 | 2.1.2 | 1000 / 1000 runs reached `Faulted`; 0 harness errors | 3000 / 3000 component disposals | 0 | 156.85 ms |

## Hard-gate result

All deterministic hard gates passed on both targets:

- exact item counts on happy paths;
- exact checksums on happy paths;
- zero workload errors;
- 32/32 deterministic cancellation terminal states;
- 1000/1000 deterministic failure terminal states;
- exact component-disposal counts;
- lifecycle invariant true;
- final ThreadPool pending work items = 0.

The cancellation and failure workloads contain no pacing `Task.Delay`; coordination is event/token driven with bounded watchdog waits only.

## Timing interpretation

The elapsed values above are retained only as diagnostics.

They point in different directions across profiles and are not suitable for release gating on a GitHub-hosted runner. The purpose of this layer is lifecycle robustness, not benchmark ranking.

## Result

The expanded Core lifecycle suite satisfies the deterministic stress acceptance criteria for:

- 32 concurrent successful runs;
- 1000 sequential successful runs;
- 32 concurrent cancellation paths;
- 1000 deterministic source-failure paths;
- exact terminal classification and resource cleanup.

No correctness, lifecycle, disposal, or final ThreadPool queue regression was detected in SmartPipe 2.2.0 in this pass.
