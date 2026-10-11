# Runtime I/O 2.2.1 raw evidence

The CSV and full compressed BenchmarkDotNet JSON exports retain parameters, individual measurements, allocations and confidence intervals. The final 72-case allocation matrix uses the same harness on merged F, the actual final I2 parent and the candidate. Controls compare that I2 parent with the candidate. Prototype storage JSON reports backing arrays; sampling heap JSON reports forced-GC managed-heap deltas including clock, breaker, strategy and headers. Neither is total pipeline/process memory.

Environment, production commits, settings and harness checksums are in [metadata.json](metadata.json). Baseline/parent checkouts have benchmark-only overlays and unchanged production sources. The final report commit adds evidence and documentation to the measured production tree.

Use [Runtime I/O](../../RuntimeIo-results.md) and [Circuit sampling](../../CircuitBreakerSampling-results.md) for interpretation and acceptance limits. Sampling benchmarks use a deterministic synthetic clock so live-window populations match; measured operation throughput is wall time.

```bash
dotnet build benchmarks/SmartPipe.Benchmarks/SmartPipe.Benchmarks.csproj -c Release -warnaserror
dotnet run -c Release --no-build --project benchmarks/SmartPipe.Benchmarks/SmartPipe.Benchmarks.csproj -- --filter '*RuntimeIoAllocationBenchmarks*' --warmupCount 5 --iterationCount 15 --launchCount 2 --iterationTime 100 --exporters json csv --artifacts artifacts/benchmarks/runtime-io/candidate-accepted/o1
dotnet run -c Release --no-build --project benchmarks/SmartPipe.Benchmarks/SmartPipe.Benchmarks.csproj -- --filter '*CircuitBreakerSamplingBenchmarks*' --warmupCount 5 --iterationCount 15 --launchCount 2 --iterationTime 100 --exporters json csv --artifacts artifacts/benchmarks/runtime-io/candidate/o2
```

Run BenchmarkDotNet from the source checkout directory (`cd` into the baseline/parent before invoking it). `--project` alone does not select its generated ProjectReference. The verified baseline/parent generated projects are archived in `provenance`.

Controls use filters `*RuntimeIoPolicyControlBenchmarks*` and `*RuntimeResilienceControlBenchmarks*` with identical settings. Baseline sampling uses `*CircuitBreakerSamplingBenchmarks*Exact*`; merged F has no Bucketed API.

`--runtime-io-latency PATH` runs all 72 cases with one full warmup per case and three measured repetitions. Each file contains individual-item p99; the two native-clock files per implementation retain six values per case. `--runtime-io-tail-review PATH` repeats four suspicious/representative cases after 100 full warmup runs each, with nine measured repetitions. `--runtime-io-focused-latency PATH` is the earlier single-case counterpart. Runtime timing uses native stopwatch ticks (1 GHz on this host); the older TimeSpan-based files are explicitly preliminary and quantized to 100 ns.

Resilience controls use `--runtime-resilience-diagnostics PATH` (one warmup, three measurements) and `--runtime-resilience-tail-review PATH` (100 warmups per scenario, nine measurements; two processes per implementation in `review/resilience-tail`); sampling diagnostics use `--circuit-sampling-latency`, `--circuit-sampling-bucket-latency`, `--circuit-sampling-diagnostics` and `--circuit-sampling-bucket-diagnostics`, each followed by its output path. Sampling-operation latency has three repetitions and includes process monitor contention, rather than a breaker-only lock counter.

`baseline/o1`, `parent/o1`, `candidate/o1` and `controls` are final acceptance exports. `preacceptance` and `review` preserve earlier regressions, unsuccessful fixes and longer paired repetitions; their commit/harness provenance is separate in metadata. `review/tail` records the final repeated item-latency checks. `sampling` compares the actual public breaker; `prototype` measures isolated windows. [SHA256SUMS](SHA256SUMS) covers every export and metadata file.
