# Comparative Lab Status

Updated: 2026-09-21

- Scope: SmartPipe 2.1.2 versus pinned SmartPipe 2.2.0 SP220-01…11.
- Candidate SHA: `61ceef6bf69aef0a4f79b25384352d238979200f`.
- SP220-12 / Mapster: excluded from this lab revision.
- PERF-00: contracts, schemas, CI smoke, mutation tests implemented.
- PERF-01: immutable baseline/candidate materializer implemented.
- PERF-02: first shared strict A/B Core workload and isolated target projects implemented.
- Core A/B orchestration: counter-balanced A→B→B→A runner implemented and first Short run started.
- Current validation: end-to-end A/B Dry is green; first counter-balanced BenchmarkDotNet Short run requested.

- Deterministic Core stress: parallel32 + sequential1000 runner wired to CI; typed-output defect fixed and evidence rerun requested.

- Reproducibility: second normalized Short A/B run queued after deterministic stress.

- Reproducibility: third Short A/B run requested after executable report self-test passed.

- PERF-04 DI: immutable evolution materializer implemented; first DI evolution Dry requested.

- PERF-04 DI: Dry is green; first DI evolution Short requested with side-by-side/no-ratio reporting.

- PERF-04 DI: correctness-prechecked DI Short queued after the exploratory Short.

- PERF-04 DI: BenchmarkDotNet false-green validation fixed; result artifacts are now mandatory; real DI/scale Dry requested.

- PERF-04 DI: JSON evidence is now mandatory for evolution and v2.2-only scale Dry; validation rerun requested.

- PERF-04 DI: immutable Dry + correctness precheck + JSON evidence + v2.2-only scale are green; first valid DI evolution Short requested.

- PERF-04 DI: multi-method reporter regression fixed and self-test passed; normalized DI Short rerun requested.

- PERF-04 Hosting: immutable evolution Dry harness implemented; first Hosting lifecycle/provider Dry requested.

- PERF-04 HealthChecks: evolution targets/materializer/contracts implemented; first immutable registered/not-started Dry requested.

- PERF-04 Hosting: Dry and reporter self-test are green; first normalized Hosting evolution Short requested.
