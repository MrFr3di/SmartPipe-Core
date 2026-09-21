# Core soak / leak validation — verify pass 1

Date: 2026-09-21

## Purpose

This pass validates the deterministic soak infrastructure and final lifecycle invariants before any 30/60/120-minute leak-trend run.

It is **not** a long-duration leak verdict.

Targets:

- SmartPipe 2.1.2 baseline SHA `8e79902d22de714f493582946f7c260462b0895e`;
- SmartPipe 2.2.0 candidate SHA `61ceef6bf69aef0a4f79b25384352d238979200f`;
- harness SHA `27b752e4ae38ec869c7485a0553a57e29b04f5f1`.

Scenario class: `evolution`.

Comparison policy: `side-by-side-no-cross-version-ratio`.

## Workflow

GitHub Actions run: `35628244956`

Artifact: `perf-core-soak-27b752e4ae38ec869c7485a0553a57e29b04f5f1`

Artifact digest: `sha256:d5b507bf63769d67cb00db21e73abf14ee7b5292663b800691a3b676819430d3`

Run id: `20260921T165328Z-27b752e4ae38`

Profile: `verify`.

Each target runs for approximately 30 seconds with:

- batches of 16 concurrent runs;
- 250 items per run;
- bounded input/output capacities;
- exact checksum validation;
- explicit component create/dispose accounting;
- periodic runtime/process snapshots.

## Hard lifecycle result

| Target | Completed runs | Errors | Active runs final | Components created | Components disposed | TP pending final |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 2.1.2 | 102,832 | 0 | 0 | 308,496 | 308,496 | 0 |
| 2.2.0 | 100,096 | 0 | 0 | 300,288 | 300,288 | 0 |

Both targets passed the final lifecycle invariant:

`completedRuns > 0 && errors == 0 && activeRuns == 0 && createdComponents == disposedComponents`.

No run/resource cleanup failure was detected.

## Validation-only runtime snapshots

The verify profile emitted seven snapshots per target.

2.1.2:

- managed memory: 5,292,920 B → 13,022,200 B;
- post-warmup managed-memory slope: -4.757 MiB/min;
- GC-heap slope: +0.472 MiB/min;
- working-set slope: +2.355 MiB/min;
- Gen2 collections delta: 0;
- ThreadPool threads: 4 → 6;
- handles/fds: 53 → 57.

2.2.0:

- managed memory: 5,298,792 B → 6,064,672 B;
- post-warmup managed-memory slope: -9.632 MiB/min;
- GC-heap slope: +0.229 MiB/min;
- working-set slope: +2.245 MiB/min;
- Gen2 collections delta: 0;
- ThreadPool threads: 4 → 7;
- handles/fds: 53 → 57.

These short-run slopes are retained only to prove snapshot/report plumbing. They are **not** evidence that either version leaks or does not leak memory.

In particular, process working set and handle/fd start/end values can change during runtime warmup and runner initialization. A leak conclusion requires the dedicated 30/60/120-minute profiles and trend history.

## Result

PERF-09 soak/leak infrastructure is operational:

- immutable package-bound target builds work;
- snapshot JSONL is retained;
- lifecycle cleanup is fail-closed;
- trend normalization/reporting works;
- `verify` is explicitly separated from long-duration leak evidence.

The next meaningful soak step is a deliberate `30m` profile, not repeated 30-second verify runs.
