# Circuit sampling 2.2.1 measurements

Exact expiry removes an expired prefix under one monitor while retaining the
inclusive boundary, EWMA and half-open semantics. Opt-in Bucketed sampling
bounds sample storage independently of rate. The measurements justify that
memory option; they also show a small single-thread cost relative to batched
Exact, so Exact remains the default.

## Actual breaker measurements

The [raw exports](results/runtime-io-2.2.1/README.md) and
[metadata](results/runtime-io-2.2.1/metadata.json) use the same host and .NET
settings as the runtime matrix. The merged-F breaker is also the immediate O2
parent implementation: its production breaker sources did not change in I1,
I2 or O1. Benchmark-only files are identical between baseline and candidate.
The private constructor refactor changes setup only, not the timed methods.

Steady operations acquire a permit, record success/failure and dispose it.
Workers have fixed 4,096-operation batches, with 1/8/32 threads and 1,000/100,000
live samples. A deterministic monotonic clock keeps window populations
comparable; measured execution time is wall time. Idle expiry is a separate
operation after all samples expire. The first two columns below are Exact.

| Operation / live samples / threads | F Exact mean | New Exact mean | Bucketed mean |
|---|---:|---:|---:|
| SteadyExpiry / 1,000 / 1 | 67.67 ns | 54.12 ns | 58.66 ns |
| SteadyExpiry / 100,000 / 1 | 68.99 ns | 57.69 ns | 59.89 ns |
| SteadyExpiry / 100,000 / 8 | 358.13 ns | 235.79 ns | 142.03 ns |
| SteadyExpiry / 100,000 / 32 | 388.82 ns | 258.24 ns | 151.68 ns |
| ExpireIdleBurst / 100,000 / 1 | 2,530,700.87 ns | 976,413.45 ns | 2,663.30 ns |


Steady operations still allocate 88 B in all modes; sampling does not remove
that allocation. The export does not identify its allocation site. Idle cleanup allocates zero bytes. Bucketed
has a few nanoseconds of single-thread cost relative to new Exact in this run,
while contended steady operations and large idle bursts improve. Its oldest
partially overlapping bucket can retain samples for less than one additional
bucket width. That is temporal uncertainty, not a failure-ratio guarantee;
a concentrated failure burst can change a threshold decision substantially.

## Retained storage

The standalone diagnostic measures forced-GC managed-heap deltas with the clock,
breaker and sampling strategy kept alive. It includes array/object headers;
it is neither process RSS nor total pipeline memory. This diagnostic runs
outside BenchmarkDotNet timed operations and outside production. The single
`ReadRetainedHeap` helper has a documented S1215 exception because full
collection is required for this measurement; no production rule is suppressed.

| Live samples | F Exact after fill / idle | Candidate Exact after fill / idle | Bucketed after fill / idle |
|---:|---:|---:|---:|
| 1,000 | 16,832 / 16,832 B | 16,896 / 16,896 B | 1,912 / 1,912 B |
| 100,000 | 2,097,456 / 2,097,456 B | 2,097,496 / 2,097,496 B | 1,912 / 1,912 B |
| 1,000,000 | 16,777,520 / 16,777,520 B | 16,777,560 / 16,777,560 B | 1,912 / 1,912 B |

Exact batching retains queue capacity after idle. Its extra strategy object adds
a constant 40–64 B per breaker here, not a per-sample/item allocation. With 64
buckets, the resource oracle proves 65 slots / 1,560 B of backing arrays,
independent of sample rate. The isolated prototype replay also retains 1,560 B
before/after idle at 1k/100k/1M samples; its 30-second window represents
approximately 33/3,333/33,333 samples/sec. Prototype timings and raw fill rates
are separate from actual-breaker evidence and are not substituted for it.

## Individual-operation latency and contention

The instrumented report measures each permit/record/dispose operation, with
10,000 operations per worker and three repetitions. It reports operation p99
and completed operations/sec, rather than batch p99. Process monitor contention
counts include worker-coordination monitors and cannot be attributed solely to
the breaker lock. The same instrumentation runs on baseline/new Exact/Bucketed.

| Live samples / threads | F Exact p99 range | New Exact p99 range | Bucketed p99 range |
|---|---:|---:|---:|
| 1,000 / 1 | 1,600–2,400 ns | 1,600–2,300 ns | 1,900–2,200 ns |
| 1,000 / 8 | 3,000–4,100 ns | 2,100–3,800 ns | 2,600–3,200 ns |
| 1,000 / 32 | 1,900–2,800 ns | 900–3,100 ns | 2,200–3,200 ns |
| 100,000 / 1 | 200–600 ns | 200–500 ns | 200–300 ns |
| 100,000 / 8 | 2,700–2,800 ns | 600–1,000 ns | 2,500–2,900 ns |
| 100,000 / 32 | 2,300–4,500 ns | 1,900–2,800 ns | 1,100–2,400 ns |


The exact oracle preserves boundary, large expired prefixes, synthetic EWMA
samples and reentrant-clock behavior (7 cases). The bounded-window oracle and
resource suite covers 9 cases; public opt-in tests cover 11, including oldest
bucket threshold divergence, reset, snapshot validation and stale half-open
generations. Runtime ownership/cancellation suites run alongside them. New
settings belong to 2.2.1 under the release owner's explicit versioning exception;
existing defaults and constructor signatures remain unchanged.
