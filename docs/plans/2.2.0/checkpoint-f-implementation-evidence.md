# Checkpoint F: review implementation evidence

PR: [#111](https://github.com/MrFr3di/SmartPipe-Core/pull/111), target `release/2.2.0`.
Implementation baseline: `36afb7943d13367a77c3f04a04e55f38f49c4006`.
Local verification: 2026-09-30, SDK 10.0.303 / runtime 10.0.11, Linux x64.

## Implemented behavior

- HTTP arrays accept leading comments when the frozen serializer metadata permits Skip. Original comment bytes reach System.Text.Json for grammar validation; the array-root requirement remains enforced.
- HTTP array/NDJSON byte limits count from the body's captured initial stream position. File readers retain prefix accounting and rewind behavior.
- COPY source/sink reject multiplexing at descriptor composition. Npgsql 10.0.3's COPY binding on a saturated multiplexed pool can wait without respecting cancellation; callers use a separate non-multiplexing data source.
- NDJSON valid records have one owned output copy; oversized records expose an empty marker. HTTP Throw fails on a known oversized nonblank record without waiting for LF. Skip and file/dead-letter consumers retain draining behavior.
- PostgreSQL channel duplicate detection uses an ordinal HashSet after complete blank validation. Ordering, defensive copy, case sensitivity and exception precedence remain unchanged.
- Package consumers use two bounded workers by default and a NativeAOT limit of one, preserving manifest result order. Failure cancels and drains workers before rethrowing the initiating exception. `--max-parallelism 1` supports serial execution.
- PostgreSQL consumers download the package producer's immutable artifact ID from the same workflow run. The validator checks schema/mode, graph inventory/order, exact version, safe paths, symlinks, SHA-256, nuspec identity and extra archives before consumption or publishing credentials.
- Release publication depends on PostgreSQL 18.6/17.11 validation and seven PostgreSQL consumers using that artifact. PR integration lanes keep their existing names and run independently. Windows JSON uses a targeted restore. The baseline lane retains a full solution locked restore because its lock-file suite verifies repository-wide project.assets.json evidence.

## Verification

| Check | Result |
|---|---|
| Full solution Release build, `--no-restore -warnaserror` | 0 warnings, 0 errors |
| `dotnet format SmartPipe.Core.slnx --verify-no-changes --no-restore` | Exit 0 |
| Repository checks `verify --profile sp220-05` | All four checks passed |
| HTTP / HTTP.Json / Json test projects | 39 / 38 / 255 passed |
| Polly test project | 53 passed |
| PostgreSQL Unit namespace | 167 passed |
| RepositoryChecks: CommandLineParser / ConsumerScenarioRunner / ConsumerScenarioScheduler / LockFilePolicy classes | 54 / 39 / 4 / 5 passed |
| Workflow contracts, including RED mutation checks | Passed |
| PowerShell package artifact fixtures | Passed: valid, missing/tampered archive, wrong version, path escape/absolute path, duplicate ID, missing graph package, extra archive, wrong nuspec version with updated hash, symlink |
| BenchmarkDotNet Dry smoke | All 15 framing cases executed |
| Independent source review | No blocking findings |

The changed RepositoryChecks classes passed; the entire RepositoryChecks project was not rerun. An earlier full-project attempt on the preceding revision encountered a read-only user-profile fixture and did not complete. PostgreSQL server tests and actual package consumers are assigned to CI. Local suites do not establish their success.

RED evidence: leading-comment/position-budget/early-Throw HTTP tests, oversized marker JSON test and COPY factory tests failed on the preceding behavior and passed after the changes. Workflow contracts rejected missing artifact gates; artifact fixtures rejected a missing validator. A source-linked runner harness with the preceding serial runner observed one ordinary process where two were required; the new runner observed two, then zero active processes after cancellation. The harness uses the hosting category's first two ordinary scenarios; NativeAOT scenarios are deliberately serialized.

## NDJSON measurements

Raw data: [before](evidence/checkpoint-f/ndjson-before.csv), [after](evidence/checkpoint-f/ndjson-after.csv).
Both use the same source-linked framer harness, fixed in-memory input prepared outside measurement, three warmups, then 100/20/8 repetitions for small/1 MiB/16 MiB inputs. Each repetition rewinds and drains the iterator, consuming Bytes and TooLarge in a checksum. Allocation uses `GC.GetTotalAllocatedBytes(true)`; Gen2 uses collection counts. This is a controlled local comparison, not a statistically stable throughput benchmark.

| 16 MiB input | Before allocated B/op | After allocated B/op | Before / after MiB/s |
|---|---:|---:|---:|
| Plain, at limit | 67,109,919 | 50,332,586 | 142.79 / 109.85 |
| BOM + trim, at limit | 83,887,042 | 50,332,586 | 108.85 / 111.64 |
| Oversized (limit = size − 1) | 67,109,802 | 33,555,229 | 141.57 / 114.08 |

The normal path removes one 16 MiB output copy; the oversized path removes two. The growable MemoryStream buffer remains, so oversized framing still allocates about 32 MiB. Oversized checksums change intentionally because Bytes is now empty. Throughput results are mixed and include slower samples; no throughput improvement or peak-RAM guarantee is claimed.

The committed MemoryDiagnoser benchmark is runnable with:

```sh
dotnet run --project benchmarks/SmartPipe.Benchmarks -c Release -- --filter '*JsonFramingBenchmarks*'
```

The local `--job Dry` smoke executed all 15 cases and reported approximately 48 MiB/op for plain/BOM-trim 16 MiB and 32 MiB/op for oversized records. Its single cold-start sample is evidence that the benchmark runs, not a full before/after timing series.

## Channel validation measurements

Raw data: [before](evidence/checkpoint-f/channels-before.csv), [after](evidence/checkpoint-f/channels-after.csv).
The same source-linked `PostgreSqlChannelSet.Create` harness composes preallocated ordinal names, with unique and duplicate-at-end inputs; no PostgreSQL/network I/O. It warms up 16 times, selects a bounded repetition count from a probe, then reports the median of seven samples and thread-local allocated bytes. The preceding source is from the baseline commit.

| Unique channels | Before ns/op | After ns/op | Before / after allocated B/op |
|---|---:|---:|---:|
| 1 | 96.1 | 196.5 | 112 / 288 |
| 10 | 483.7 | 683.0 | 184 / 944 |
| 100 | 31,026.0 | 3,553.0 | 904 / 8,280 |
| 1,000 | 3,395,640.7 | 30,131.1 | 8,104 / 81,256 |
| 10,000 | 471,492,781.5 | 440,246.9 | 80,104 / 753,152 |

Large inputs demonstrate the expected scaling change. Typical small inputs are slower and allocate more; HashSet adds temporary memory, especially as it grows. These are composition measurements, not LISTEN throughput results.

## Consumer scheduling and CI measurements

An eight-scenario delay-only scheduler harness measured 804.1 ms with one worker and 399.2 ms with two, using a 100 ms asynchronous delay per scenario. This demonstrates worker overlap only; it does not predict compilation speed, NativeAOT memory use or CI wall time.

One prior-candidate baseline is available: [CI run 36676656439](https://github.com/MrFr3di/SmartPipe-Core/actions/runs/36676656439), head `36afb79`. Job durations were build/test/pack 1,404 s, PostgreSQL 18.6 202 s, PostgreSQL 17.11 54 s, Windows JSON 82 s, Windows baseline 66 s. This is insufficient for the plan's three comparable before/after runs. Artifact reuse removes the second solution build/pack, but consumer dependency waiting can increase the critical path; no runner-minute or total-time improvement is claimed yet.

## Remaining evidence

- Attach the new exact-head CI result, build commit, artifact ID/hash manifest and seven consumer results to PR #111 when available. A successful prior-head run does not validate these changes.
- Obtain three comparable CI runs and stable BenchmarkDotNet before/after runs for performance conclusions beyond the measured allocation/scaling changes.
- Confirm required-check configuration before merge. No branch protection changes were made.
- SP220-18 final release validation remains unchecked: the release tag's exact artifacts and publication authorization are separate gates.

## First candidate CI feedback

[Run 36706227929](https://github.com/MrFr3di/SmartPipe-Core/actions/runs/36706227929), head `65f3e82`, passed both PostgreSQL integration versions and Windows JSON. Windows baseline ran 625 tests: 624 passed; `RepositoryLockFiles_AreCompleteAndReconciled` failed because the narrowed restore omitted assets evidence for unrelated projects. The follow-up restores the full solution in that lane and adds a rejecting workflow mutation. Targeted JSON restore and PostgreSQL artifact reuse remain. These first-candidate results do not replace the follow-up's exact-head CI evidence.
