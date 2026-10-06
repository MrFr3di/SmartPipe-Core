# SP220-07 benchmark snapshot

This is an informational local snapshot for the Channels, Composite, Filter,
and Logger leaf APIs. It is not a release threshold. The worktree has no prior
comparable benchmark baseline, so these measurements establish directional
context only and do not support a regression claim.

Run context:

- Baseline `HEAD`: `6604355e168d9e7d404a585f30f38490d5b05730` (benchmark files were uncommitted).
- Working directory: `C:\Reposit\SmartPipe.Core\.work\wt-07\benchmarks\SmartPipe.Benchmarks`.
- OS/CPU: Windows 11 `10.0.26200.9168`, 12th Gen Intel Core i3-12100F, 4 physical/8 logical cores.
- .NET: SDK `10.0.302`, runtime `.NET 10.0.11`; BenchmarkDotNet `0.15.8`.
- The exact PowerShell command below was run from the working directory above:

```powershell
dotnet restore .\SmartPipe.Benchmarks.csproj --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build .\SmartPipe.Benchmarks.csproj -c Release --no-restore --warnaserror -v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run -c Release --no-build -- --filter '*Sp22007*' --iterationCount 20 --warmupCount 5 --launchCount 1 --invocationCount 1 --unrollFactor 1 --exporters json --artifacts 'BenchmarkDotNet.Artifacts\sp22007-final'
```

`Program.cs` supplies the single `InProcessNoEmitToolchain` job. The command
does not add `--job Dry`; therefore each of the seven filtered benchmarks ran
with one launch, five warmups, and twenty measured iterations (one invocation
and unroll factor). The MemoryDiagnoser was enabled. Generated JSON was read
from `BenchmarkDotNet.Artifacts\sp22007-final\results\` before cleanup.

The setup is deterministic: Channels merges three completed 128-item readers
with bounded capacity 64 and also exercises the legacy pair overload; Composite
uses two initialized add-one stages and requires value `42`; Filter uses
canonical token-aware accepted and filtered predicates and throws if the
expected result state is not returned; Logger compares the legacy raw
constructor with the safe `PayloadMode.None` path using an enabled no-op logger.

| Area / benchmark | Median | Mean | Approx. mean throughput | Allocated |
| --- | ---: | ---: | ---: | ---: |
| Channels `MergeMany_ThreeReaders_Bounded` | 239.700 us | 245.447 us | 4.07 Kops/s | 5,680 B/op |
| Channels `MergePair_Unbounded` | 165.800 us | 182.700 us | 5.47 Kops/s | 9,976 B/op |
| Composite `Transform_TwoStages` | 5.250 us | 5.185 us | 193.05 Kops/s | 520 B/op |
| Filter `Transform_TokenAware_Accepted` | 2.600 us | 2.628 us | 380.52 Kops/s | 288 B/op |
| Filter `Transform_TokenAware_Filtered` | 2.900 us | 2.995 us | 333.89 Kops/s | 0 B/op |
| Logger `Write_LegacyRaw` | 3.400 us | 3.474 us | 287.85 Kops/s | 808 B/op |
| Logger `Write_SafeDefault` | 1.700 us | 1.947 us | 513.61 Kops/s | 0 B/op |

The measured shape is directional: the safe logger path is faster and
allocation-free relative to the legacy raw path; the filtered predicate is
slightly slower than the accepted predicate while allocating nothing; and the
bounded three-reader merge is slower than the two-reader unbounded merge for
the larger deterministic workload. Several short scenarios produced the
BenchmarkDotNet `MinIterationTime` advisory and isolated outlier removal; no
benchmark failed. There is no hard 3% shared-runner gate.
