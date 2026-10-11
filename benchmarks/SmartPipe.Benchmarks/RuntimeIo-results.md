# Runtime I/O 2.2.1 measurements

Output and observer gates reduce managed allocations when output is suppressed or
no observers were registered. The default path keeps retry-budget and timed
admission state outside its async frame. Completed start, invocation and success
notifications use synchronous `ValueTask` paths; pending operations retain the
original await and error handling.

## Method and provenance

The [raw exports and commands](results/runtime-io-2.2.1/README.md) and
[metadata](results/runtime-io-2.2.1/metadata.json) identify production commits,
harness checksums and the execution host. Merged F is `4e550683`; the immediate
O1 parent is `c7c08ff9`. Benchmark-only overlays leave their production sources
unchanged. All runs use the same Debian 13 / AMD EPYC 9V74 host, SDK 10.0.401,
runtime 10.0.12, concurrent workstation GC and a two-CPU container quota.

The 72 cases combine 1/4/16 stages, 0/1 observers, sink absent/present,
concurrency 1/8/32 and completed/asynchronous transforms. Each invocation
processes 10,000 items, drains output concurrently, awaits Completion/Dispose
and verifies output counts, transformed checksum, sink counts and final state.
BenchmarkDotNet normalizes allocations and execution time per item, with five
warmups, 15 iterations, two launches and a 100 ms target iteration time.
These short iterations give wide timing intervals; allocation results and
elapsed-time claims are assessed separately.

| Stages / observers / sink (sync, concurrency 1) | F B/item | I2 parent B/item | Candidate B/item |
|---|---:|---:|---:|
| 1 / 0 / False | 610 | 611 | 451 |
| 1 / 0 / True | 682 | 682 | 362 |
| 4 / 0 / False | 1331 | 1331 | 691 |
| 4 / 0 / True | 1403 | 1403 | 603 |
| 16 / 0 / False | 4212 | 4212 | 1654 |
| 16 / 0 / True | 4284 | 4284 | 1564 |
| 16 / 1 / False | 4213 | 4213 | 4213 |
| 16 / 1 / True | 4284 | 4284 | 4196 |

For 16 completed stages without observers, with a sink and concurrency one,
F measured 10.805 µs/item, 4,284 B/item and 0.2000 Gen0 collections per 1,000
items; the candidate measured 7.219 µs/item, 1,564 B/item and 0.0000 reported
Gen0 collections in this short diagnostic. The normalized means correspond to
approximately 92.6k versus 138.5k items/sec. Zero observed collections in one
short measurement does not imply zero GC under a sustained workload.

The sink still runs for suppressed success. With observers, their events remain
allocated; suppressed output saves the output record. Asynchronous and parallel
cases also allocate scheduler/awaiter state, whose count varies with scheduling.
The deterministic concurrency-one completed path is the disabled-policy heap
check; there is no new per-item retry/admission object on that path. Values are
normalized whole-run allocations rounded to B/item; the one-byte F/I2 variation
also includes reader/awaiter scheduling and is not evidence of a new object per
envelope.

## Timing regression found during validation

The first candidate introduced extra async helper frames while removing Sonar
duplication. A longer paired run (500 ms target, 15 iterations, three launches)
confirmed a regression with 16 completed stages, one observer, a sink and
concurrency one: 10.521 ± 0.244 µs/item on F versus 11.573 ± 0.258 µs/item.
The first success-only fast path was insufficient (11.317 µs). The start,
invocation and success correction measured **10.548 ± 0.273 µs/item**, overlapping
F and only 0.26% above its mean. Subsequent item-latency validation also moved a successful default initial
attempt outside the generic async loop frame. Each `ValueTask` is consumed
exactly once; pending/failure/retry paths retain their original timestamps and
exception sequencing.

The other suspected case, one asynchronous stage with an observer/sink at
concurrency 32, measured 2.690 ± 0.173 µs on F and 2.869 ± 0.224 µs after that correction.
Their intervals overlap. The two-CPU quota and thread-pool scheduling make a
small percentage difference in that mean insufficient evidence of a regression.
The [paired exports](results/runtime-io-2.2.1/review/final-hotpath/SmartPipe.Benchmarks.RuntimeIoAllocationBenchmarks-report-full-compressed.json)
retain individual measurements, including the unsuccessful intermediate fixes.

The final 72-case matrix has no >5% mean-time increase with non-overlapping
BenchmarkDotNet confidence intervals against either F or the immediate O1
parent. This is evidence from the sampled host/configurations, not a
workload-independent guarantee. The complete matrix remains archived.

## Individual item latency

The separate diagnostic measures source emission to sink completion or output
reader consumption. The 72-case screening diagnostic uses one full warmup and
three repetitions of 10,000 timestamps per case, repeated in two processes.
Timing uses native stopwatch ticks (1 ns resolution on this host), rather than
100 ns-rounded TimeSpan values. These are individual-item percentiles, never
percentiles of BenchmarkDotNet invocation duration.

Screening flagged three cases against F and the one-stage completed sink case
against I2. They were repeated in two independent processes after **100 full
warmup runs per case**, with nine measured repetitions each. A representative
16-stage observer/sink case was included as a fourth control. The table gives
the median of the nine per-run item p99 values in each process, in µs:

| Stages / sink / async (one observer, concurrency 1) | F process 1 / 2 | I2 process 1 / 2 | Candidate process 1 / 2 |
|---|---:|---:|---:|
| 1 / false / true | 8.051 / 10.676 | 8.613 / 11.928 | 14.802 / 9.524 |
| 4 / false / false | 127.068 / 121.690 | 38.161 / 70.048 | 173.326 / 4.822 |
| 1 / true / false | 1.763 / 1.793 | 1.673 / 1.642 | 1.662 / 1.752 |
| 16 / true / false | 54.671 / 30.626 | 32.227 / 24.927 | 29.033 / 22.974 |

The stable one-stage sink case no longer reproduces the screened increase.
Output-reader cases include queueing and scheduling: the same four-stage
candidate varies from 173.326 to 4.822 µs between processes, with individual-run
values spanning 4.671–30,220.535 µs; F spans 5.224–29,741.024 µs. Keeping only a
favorable repetition would conceal that variability. The repeats do not show
a consistent >5% increase outside this noise against F or I2, but they also do
not establish a worst-case latency bound. [All screening and repeated values](results/runtime-io-2.2.1/README.md)
are retained, alongside the earlier single-case native-clock calibration.

## Controls and correctness

Six mixed-stream controls cover EmitAll/EmitFailuresOnly, reliable/best-effort
buffered observers, active telemetry and full lineage, including failures,
filtered outcomes and skip failure policy. Eight resilience controls combine
completed/asynchronous transforms with Unlimited, Budget, TimedWait and
TimedReject. Their raw exports use the same settings and parent harness.

The fixed-fault stream contains 1,000 input envelopes. Initial attempts do not
consume retry tokens. Unlimited performs 200 retries; Budget/TimedWait admit
100. Reject can admit fewer initial attempts and retries. Wait/Reject hold at
most four timed executions. Reject can finish less work, so its elapsed time is not a throughput
improvement claim. These controls measure actual executing attempts; separate
ownership regressions verify that detached attempts retain capacity through
observation and CTS cleanup, and completed attempts release it before backoff.

The asynchronous control results below are normalized per **input envelope**;
all cases receive 1,000 envelopes, but Reject can start much less work. The
p99 values are medians of nine measured runs in each of two processes after
100 full warmups per scenario, in milliseconds:

| Control | I2 B/input; mean µs/input | Candidate B/input; mean µs/input | I2 p99 process 1 / 2 ms | Candidate p99 process 1 / 2 ms |
|---|---:|---:|---:|---:|
| Unlimited | 2972; 3.216 | 2738; 3.374 | 5.123 / 8.079 | 3.465 / 3.024 |
| Budget | 3023; 4.442 | 2814; 3.383 | 3.985 / 4.194 | 4.250 / 4.736 |
| TimedWait | 4959; 7.631 | 4712; 9.425 | 5.325 / 5.905 | 4.176 / 8.163 |
| TimedReject | 659; 2.339 | 479; 2.446 | 1.219 / 1.239 | 1.528 / 1.329 |

These controls expose policy costs and queueing rather than a blanket speedup.
Budget/Wait reduce retries from 200 to 100, Wait serializes timed execution to
four slots, and Reject changes the admitted work. Budget/Reject's short p99
screening differences were repeated with the same 100-warmup/9-run diagnostic
on parent/candidate. Repeated ranges overlap (for example, async Budget:
3.037–6.951 ms on I2 and 4.203–5.636 ms on the candidate); TimedWait reverses its
relative median between processes. The data do not establish a consistently
repeated >5% regression outside this variation, or a guaranteed p99 improvement.
Both the original three-run diagnostics and the longer repetitions remain
archived; applications should measure their own queueing and admitted-work mix.

Core's combined suite passes 1,587 cases. Allocation policy cases cover modern
and legacy policies, default result classification and sink failure; observer
cases cover telemetry and dispatcher lifecycle. CI keeps the existing F gates
and adds independent minimum-count gates for the new features. Public shipped
API snapshots and the 2.2.0 package-validation baseline remain immutable.

The measurements support the allocation changes and the explicit policy
tradeoffs on this host. They do not promise a universal speedup or a bound on
whole-pipeline memory.
