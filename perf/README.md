# SmartPipe comparative performance lab

This directory is the experiment surface for the SmartPipe 2.1.2 → 2.2.0 comparison.

The lab is deliberately separate from product implementation. Its source revision is the **harness revision**; the products under test are immutable package sets recorded in `perf/manifests/targets.json`.

## Current state

- published baseline: SmartPipe 2.1.2, Git SHA `8e79902d22de714f493582946f7c260462b0895e`;
- current accepted Checkpoint E integration head at lab bootstrap: `61ceef6bf69aef0a4f79b25384352d238979200f` (SP220-01…11);
- full SP220-01…12 candidate: intentionally unset until SP220-12 is accepted.

Until the full candidate is pinned, the lab may run smoke and exploratory checks, but it must not publish a result labelled “2.1.2 vs full 2.2.0”.

## Evidence classes

- `strict-ab`: equivalent semantics on 2.1.2 and 2.2.0; ratios are allowed.
- `evolution`: same user goal but materially different API/runtime contract; side-by-side evidence only.
- `v220-only`: new 2.2.0 capability; no synthetic 2.1.2 ratio.

## CI policy

Pushes that only evolve the lab run a lightweight smoke workflow. Expensive benchmark/stress/soak runs are explicit `workflow_dispatch` operations. GitHub-hosted timings are informational, never authoritative release gates.

NuGet dependencies use the built-in `actions/setup-dotnet` cache keyed from lock files. Product packages, `bin/obj`, BenchmarkDotNet output and stress/soak output are never dependency-cached.

Raw CI artifacts use short retention. Curated, reviewed summaries may be committed under `docs/performance/reports/` later; raw traces and dumps are not committed.

See `docs/performance/2.2.0-comparative-lab-plan.md` for the complete methodology.
