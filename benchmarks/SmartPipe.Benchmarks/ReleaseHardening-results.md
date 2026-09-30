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

Controlled old-versus-new comparison. The baseline, `LegacyClosedStateCircuitBreaker`, is a
benchmark-local copy of the previous closed-state path: `AcquirePermit` pruned the window under
the lock, samples were stamped outside the lock, and every failure counted the window by
enumerating it under the lock. Half-open handling is left out because the breaker never leaves
the closed state here. Both implementations use the same configuration: failure ratio 1.0,
minimum throughput 1, and a sampling duration of `WindowSamples` ticks.

Each operation is one successful and one failed item (permit plus record), advancing a manual
clock one tick per item. With the window prefilled, every new sample expires exactly one old
sample, so the window stays at `WindowSamples` samples with a steady 50% failure ratio. Setup
throws if either breaker would open. The four-thread variants run 256 item pairs per thread per
invocation on a shared breaker; their per-operation time includes starting four threads,
amortized over 1,024 pairs.

Run: `--inProcess --warmupCount 5 --iterationCount 15`, with no other load on the machine.

| Window samples | Variant | Previous | Current | Time ratio | Allocated per pair (old → new) |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1,000 | single thread | 8,803 ns | 260 ns | 0.03 | 1,152 B → 48 B |
| 1,000 | 4 threads | 28,343 ns | 3,107 ns | 0.11 | 1,257 B → 49 B |
| 100,000 | single thread | 2,026,456 ns | 249 ns | 0.0001 | 1,161 B → 48 B |
| 100,000 | 4 threads | 2,848,550 ns | 3,137 ns | 0.001 | 1,284 B → 49 B |

The current cost does not depend on the window size, and the previous cost grows with it. The
previous per-failure enumeration of a `ConcurrentQueue` is also what allocated about 1.1 KB per
pair. Under contention, the current breaker is about 12× slower per pair than single-threaded
because all threads serialize on the window lock; the lock is held only for the enqueue, the
expiry of one sample and an O(1) count. The remaining 48 B per pair is the permit lease objects.

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

## OpenTelemetry SDK (`ReleaseHardeningOpenTelemetryBenchmarks`)

The pipeline-tag measurements above use a bare `MeterListener`. This benchmark measures the same
`RecordProcessed` call (one counter plus one histogram) with the OpenTelemetry SDK 1.17 aggregating
through a `MeterProvider` with the in-memory exporter. The tagged benchmark rotates across
`PipelineCount` recorders with stable ids. Setup verifies the exported cardinality: exactly one
`smartpipe.items.processed` point per id plus the untagged series, otherwise it throws.
`CollectAndExport` measures one forced collection and in-memory export of all SmartPipe
instruments.

Run: `--inProcess --warmupCount 5 --iterationCount 15`, with no other load on the machine.

| Pipeline ids | Untagged record | Tagged record | Time ratio | Collect + export | Allocated |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 122 ns | 207 ns | 1.71 | 834 ns | 0 B per record, 256 B per collect |
| 10 | 117 ns | 204 ns | 1.74 | 1,156 ns | 0 B per record, 256 B per collect |
| 100 | 120 ns | 209 ns | 1.75 | 4,202 ns | 0 B per record, 256 B per collect |

With the SDK aggregating, a tagged measurement costs about 85 ns more than an untagged one, because
the SDK looks up the series for the tag. That extra cost does not grow from 1 to 100 ids. Collection
cost grows with the number of series, as expected; at 100 pipelines one collection takes about 4 µs.
The exporter is in memory, so real exporter serialization and network costs are not included. With
the SDK's default cardinality limit of 2,000 series per instrument, stable per-pipeline ids stay
well below the point where overflow aggregation starts.

