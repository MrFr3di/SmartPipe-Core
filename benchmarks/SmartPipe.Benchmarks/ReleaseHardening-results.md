# Release hardening benchmark snapshot (2.2.0)

Informational local snapshot for the runtime hot-path changes in the 2.2.0
release hardening work. It is not a release threshold. Each comparison runs the
previous algorithm and the new one side by side in the same process, so the
ratio is the meaningful number; absolute times on a shared 4-core VM are noisy
(see the Error column of the raw reports).

Run context:

- OS/CPU: Ubuntu 24.04, Intel Xeon 2.10GHz, 4 logical cores.
- .NET: SDK `10.0.303`, runtime `.NET 10.0.11`; BenchmarkDotNet `0.15.8`.
- Job: `ShortRun` (3 warmups, 3 iterations), in-process toolchain, MemoryDiagnoser.

```sh
dotnet build benchmarks/SmartPipe.Benchmarks/SmartPipe.Benchmarks.csproj -c Release
cd benchmarks/SmartPipe.Benchmarks
dotnet run -c Release --no-build -- --filter '*ReleaseHardening*' --job short --inProcess
```

## Lineage append (`ReleaseHardeningLineageBenchmarks`)

Builds one lineage chain of `Depth` stages. Baseline is the previous
copy-per-stage `Concat(...).ToArray()` path.

| Depth | Copy per stage | `LineageTrail` | Time ratio | Allocated (old → new) |
| ---: | ---: | ---: | ---: | ---: |
| 4 | 260 ns | 74 ns | 0.28 | 416 B → 216 B |
| 16 | 1,347 ns | 329 ns | 0.24 | 2,672 B → 904 B |
| 64 | 7,751 ns | 1,251 ns | 0.16 | 23,216 B → 3,320 B |

The gap widens with depth, as expected for quadratic versus amortized linear
total work. `LineageTrail` still allocates one view object per append.

## Strict CSV record encoding (`ReleaseHardeningCsvEncodingBenchmarks`)

Per record, measured over 64 records per invocation. Baseline is the previous
`ToStringAndReset` plus exact-length `byte[]` path; both use the same bounded
record writer.

| Record shape | String + array | Pooled chunk encoding | Time ratio | Allocated per record (old → new) |
| --- | ---: | ---: | ---: | ---: |
| ASCII, 40 chars | 90 ns | 42 ns | 0.47 | 168 B → 0 B |
| Cyrillic, ~400 chars | 679 ns | 373 ns | 0.55 | 1,504 B → 0 B |
| Emoji surrogate pairs, ~8,000 chars | 19,611 ns | 10,711 ns | 0.55 | 28,064 B → 0 B |

The first run of this benchmark showed the new path still allocating
`2 × length + 24` bytes per record: the bounded writer inherited
`TextWriter.Write(string)`, which copies the string into a new `char[]`. The
writer now overrides it, and the table above is the rerun. The pooled byte
buffer grows to the largest record seen and is kept for the sink's lifetime,
so a sink that once wrote a large record retains that buffer until disposal.

## Circuit breaker window (`ReleaseHardeningCircuitBreakerBenchmarks`)

One `RecordFailure` plus one `RecordSuccess` on a closed breaker whose sliding
window already holds `WindowSamples` samples.

| Window samples | Median | Allocated |
| ---: | ---: | ---: |
| 1,000 | 289 ns | 48 B |
| 100,000 | 499 ns | 48 B |

A 100× larger window costs well under 2× more per call; the difference is
within the run's noise. The previous implementation counted failures by
scanning the whole window under the lock on every failure, so its cost grew
linearly with the number of samples (100,000 iterations per failure in the
second row). No old-code baseline is included for this row.

## Pipeline metric tag (`ReleaseHardeningMetricsBenchmarks`)

`RecordProcessed` (one counter plus one histogram measurement) without and
with the `smartpipe.pipeline_id` tag.

| Listener | Untagged | Pipeline tag | Ratio | Allocated |
| --- | ---: | ---: | ---: | ---: |
| none | 69 ns | 64 ns | 0.93 | 0 B |
| `MeterListener` enabled | 73 ns | 77 ns | 1.05 | 0 B |

The single-tag overload does not allocate, and the cost difference is within
noise. OpenTelemetry SDK aggregation and export costs are outside this
measurement.
