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
