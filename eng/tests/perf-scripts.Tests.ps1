param()

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$scripts = @(
    (Join-Path $repoRoot 'eng/perf/validate-perf-lab.ps1'),
    (Join-Path $repoRoot 'eng/perf/prepare-target.ps1'),
    (Join-Path $repoRoot 'eng/perf/prepare-core-ab.ps1'),
    (Join-Path $repoRoot 'eng/perf/run-core-ab.ps1'),
    (Join-Path $repoRoot 'eng/perf/report-core-ab.ps1'),
    (Join-Path $repoRoot 'eng/perf/run-core-stress.ps1'),
    (Join-Path $repoRoot 'eng/perf/prepare-di-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/run-di-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/report-di-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/prepare-hosting-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/prepare-healthchecks-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/run-hosting-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/report-hosting-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/run-healthchecks-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/report-healthchecks-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/prepare-opentelemetry-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/run-opentelemetry-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/report-opentelemetry-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/prepare-extensions-strict-ab.ps1'),
    (Join-Path $repoRoot 'eng/perf/run-extensions-strict-ab.ps1'),
    (Join-Path $repoRoot 'eng/perf/report-extensions-strict-ab.ps1'),
    (Join-Path $repoRoot 'eng/perf/prepare-extensions-focus.ps1'),
    (Join-Path $repoRoot 'eng/perf/prepare-json-strict-ab.ps1'),
    (Join-Path $repoRoot 'eng/perf/run-json-strict-ab.ps1'),
    (Join-Path $repoRoot 'eng/perf/report-json-strict-ab.ps1'),
    (Join-Path $repoRoot 'eng/perf/prepare-csv-strict-ab.ps1'),
    (Join-Path $repoRoot 'eng/perf/run-csv-strict-ab.ps1'),
    (Join-Path $repoRoot 'eng/perf/report-csv-strict-ab.ps1'),
    (Join-Path $repoRoot 'eng/perf/prepare-dapper-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/run-dapper-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/report-dapper-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/prepare-dapper-decomposition-v220.ps1'),
    (Join-Path $repoRoot 'eng/perf/run-dapper-decomposition-v220.ps1'),
    (Join-Path $repoRoot 'eng/perf/report-dapper-decomposition-v220.ps1'),
    (Join-Path $repoRoot 'eng/perf/prepare-entity-framework-core-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/run-entity-framework-core-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/report-entity-framework-core-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/prepare-definition-model-v220.ps1'),
    (Join-Path $repoRoot 'eng/perf/run-definition-model-v220.ps1'),
    (Join-Path $repoRoot 'eng/perf/report-definition-model-v220.ps1'),
    (Join-Path $repoRoot 'eng/perf/run-core-soak.ps1'),
    (Join-Path $repoRoot 'eng/perf/report-core-soak.ps1'),
    (Join-Path $repoRoot 'eng/perf/report-package-footprint.ps1')
)

foreach ($script in $scripts) {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        $script,
        [ref]$tokens,
        [ref]$errors)

    Assert-True ($errors.Count -eq 0) "PowerShell parse errors in '$script': $($errors -join '; ')"
}

$materializerPath = Join-Path $repoRoot 'eng/perf/prepare-target.ps1'
$materializer = Get-Content -LiteralPath $materializerPath -Raw

Assert-True ($materializer -match 'worktree\s+add\s+--detach') 'Candidate must be materialized in a detached git worktree.'
Assert-True ($materializer -match "'--locked-mode'") 'Target restores must use locked mode.'
Assert-True ($materializer -match 'verify-baseline') 'Baseline must be verified through RepositoryChecks.'
Assert-True ($materializer -match 'verify-package-graph') 'Candidate package graph must be verified.'
Assert-True ($materializer -match 'verify-package-metadata') 'Candidate package metadata must be verified.'
Assert-True ($materializer.Contains("'--repo-root', `$worktreeRoot")) 'Candidate pack and verification commands must bind to the detached worktree repository root.'
Assert-True ($materializer.Contains('$candidateManifest = Join-Path $workPackagesDir ''manifest.json''')) 'Pack manifest must live directly inside the detached worktree package output directory.'
Assert-True ($materializer -match 'Get-FileHash.+SHA256') 'Target package provenance must include SHA-256 hashes.'
Assert-True ($materializer -match '61ceef6bf69aef0a4f79b25384352d238979200f') 'Materializer default candidate SHA must remain pinned.'
Assert-True ($materializer -notmatch 'perflab\.') 'Candidate package version must remain the repository product version.'
Assert-True ($materializer -notmatch '(?m)git\s+-C\s+\$repoRoot\s+(checkout|reset)') 'Materializer must not checkout/reset the harness working tree.'

$coreAb = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/prepare-core-ab.ps1') -Raw
Assert-True ($coreAb -match "'--use-lock-file'") 'Core A/B restore must generate a lock file.'
Assert-True ($coreAb -match "'--locked-mode'") 'Core A/B restore must verify the generated lock in locked mode.'
Assert-True ($coreAb -match 'contentHash') 'Core A/B restore must verify NuGet package content hash.'
Assert-True ($coreAb -match 'dotnet nuget verify') 'Core A/B must compute the package content hash with NuGet itself.'
Assert-True ($coreAb -match 'Assert-RestoredPackageSource') 'Core A/B must verify the actual restored package source.'
Assert-True ($coreAb -match "'--no-http-cache'") 'Core A/B restore must bypass the HTTP cache.'
Assert-True ($coreAb -notmatch 'SHA512\]::HashData') 'Core A/B must not substitute a raw file SHA-512 for NuGet contentHash.'
Assert-True ($coreAb -match 'packageSourceMapping') 'Core A/B restore must use NuGet Package Source Mapping.'
Assert-True ($coreAb -match 'SmartPipe\.\*') 'SmartPipe packages must be mapped to the verified local target feed.'
Assert-True ($coreAb -match 'sharedSourceSha256') 'Core A/B provenance must record the shared workload source hash.'

$runner = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/run-core-ab.ps1') -Raw
Assert-True ($runner -match "target = 'v212'[\s\S]*target = 'v220'[\s\S]*target = 'v220'[\s\S]*target = 'v212'") 'Core A/B order must remain counter-balanced A-B-B-A.'
Assert-True ($runner -match "scenarioClass = 'strict-ab'") 'Core A/B run manifest must classify the scenario as strict-ab.'
Assert-True ($runner.Contains('authoritativeTiming = $false')) 'GitHub-compatible Core A/B timing must default to non-authoritative.'

$stressRunner = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/run-core-stress.ps1') -Raw
Assert-True ($stressRunner -match "Profile 'parallel32'") 'Core stress must run the parallel32 profile.'
Assert-True ($stressRunner -match "Profile 'sequential1000'") 'Core stress must run the sequential1000 profile.'
Assert-True ($stressRunner -match "Profile 'cancel32'") 'Core stress must run the deterministic cancel32 profile.'
Assert-True ($stressRunner -match "Profile 'sourcefailure1000'") 'Core stress must run the deterministic sourcefailure1000 profile.'
Assert-True ($stressRunner -match 'TerminalRuns') 'Core stress runner must validate terminal run counts.'
Assert-True ($stressRunner -match 'DisposedComponents') 'Core stress runner must validate component disposal counts.'
Assert-True ($stressRunner -match 'LifecycleInvariantPassed') 'Core stress runner must fail closed on lifecycle invariant failure.'
Assert-True ($stressRunner -match "scenarioClass = 'strict-ab'") 'Core stress must classify its comparison as strict-ab.'
Assert-True ($stressRunner.Contains('authoritativeTiming = $false')) 'Hosted stress elapsed time must remain non-authoritative.'
Assert-True ($stressRunner -match 'contentHash differs from verified Core A/B provenance') 'Stress restore must bind to verified Core A/B package content.'
Assert-True (($stressRunner | Select-String -Pattern 'Tee-Object -FilePath \$restoreLog.*Out-Host' -AllMatches).Matches.Count -ge 2) 'Stress target preparation must consume restore output so the function returns only its typed descriptor.'
Assert-True ($stressRunner -match 'Tee-Object -FilePath \$buildLog \| Out-Host') 'Stress target preparation must consume build output so the function returns only its typed descriptor.'
$stressSource = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Stress.Shared/Program.cs') -Raw
Assert-True ($stressSource -notmatch 'Task\.Delay') 'Deterministic Core stress must not rely on Task.Delay.'
Assert-True ($stressSource -match 'ExpectedChecksum') 'Deterministic Core stress must enforce a checksum oracle.'
Assert-True ($stressSource -match 'ExpectedItems') 'Deterministic Core stress must enforce an item-count oracle.'
Assert-True ($stressSource -match '"cancel32"') 'Deterministic Core stress source must implement cancel32.'
Assert-True ($stressSource -match '"sourcefailure1000"') 'Deterministic Core stress source must implement sourcefailure1000.'
Assert-True ($stressSource -match 'PipelineRunState\.Cancelled') 'Cancellation stress must verify Cancelled terminal state.'
Assert-True ($stressSource -match 'PipelineRunState\.Faulted') 'Failure stress must verify Faulted terminal state.'
Assert-True ($stressSource -match 'ExpectedDisposedComponents') 'Lifecycle stress must enforce disposal-count invariants.'
Assert-True ($stressSource -match 'WaitAsync\(TimeSpan\.FromSeconds\(10\)\)') 'Cancellation stress must use bounded coordination waits.'
Assert-True ($stressSource -notmatch 'Thread\.Sleep') 'Deterministic Core stress must not use Thread.Sleep.'

$soakRunner = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/run-core-soak.ps1') -Raw
Assert-True ($soakRunner -match "ValidateSet\('verify', '30m', '60m', '120m'\)") 'Core soak runner must expose verify/30m/60m/120m profiles.'
Assert-True ($soakRunner -match 'contentHash differs from verified Core A/B provenance') 'Core soak restore must bind to verified Core A/B package content.'
Assert-True ($soakRunner -match "scenario = 'soak-leak'") 'Core soak manifest must use the canonical soak-leak scenario.'
Assert-True ($soakRunner -match "scenarioClass = 'evolution'") 'Core soak must remain evolution-classified.'
Assert-True ($soakRunner -match "comparisonPolicy = 'side-by-side-no-cross-version-ratio'") 'Core soak must forbid cross-version ratio reporting.'
Assert-True ($soakRunner.Contains('authoritativeTiming = $false')) 'Hosted soak timing must remain non-authoritative.'
Assert-True ($soakRunner -match 'ActiveRuns') 'Core soak runner must gate on final active runs.'
Assert-True ($soakRunner -match 'CreatedComponents') 'Core soak runner must record created resources.'
Assert-True ($soakRunner -match 'DisposedComponents') 'Core soak runner must gate on exact resource disposal.'
Assert-True ($soakRunner -match 'LifecycleInvariantPassed') 'Core soak runner must fail closed on lifecycle invariant failure.'
Assert-True ($soakRunner -match 'stdout\.jsonl') 'Core soak runner must retain raw JSONL snapshots.'

$soakSource = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Soak.Shared/Program.cs') -Raw
Assert-True ($soakSource -match 'ConcurrentRunsPerBatch = 16') 'Core soak must exercise concurrent run churn.'
Assert-True ($soakSource -match 'TimeSpan\.FromMinutes\(30\)') 'Core soak must include the 30-minute profile.'
Assert-True ($soakSource -match 'TimeSpan\.FromMinutes\(60\)') 'Core soak must include the 60-minute profile.'
Assert-True ($soakSource -match 'TimeSpan\.FromMinutes\(120\)') 'Core soak must include the 120-minute profile.'
Assert-True ($soakSource -match 'GC\.GetTotalMemory') 'Core soak snapshots must collect managed memory.'
Assert-True ($soakSource -match 'GC\.GetGCMemoryInfo') 'Core soak snapshots must collect GC heap information.'
Assert-True ($soakSource -match 'GC\.GetTotalAllocatedBytes') 'Core soak snapshots must collect allocation totals.'
Assert-True ($soakSource -match 'ThreadPool\.PendingWorkItemCount') 'Core soak snapshots must collect ThreadPool queue depth.'
Assert-True ($soakSource -match '/proc/self/fd') 'Core soak must collect Linux fd count where available.'
Assert-True ($soakSource -match 'created == disposed') 'Core soak hard gate must require exact component cleanup.'

$soakReport = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/report-core-soak.ps1') -Raw
Assert-True ($soakReport -match 'Get-SlopePerMinute') 'Core soak report must calculate trend slopes.'
Assert-True ($soakReport -match 'Select-Object -Skip \$skip') 'Core soak trend must exclude the warmup prefix.'
Assert-True ($soakReport -match 'managedMemorySlopeBytesPerMinute') 'Core soak report must expose managed-memory slope.'
Assert-True ($soakReport -match 'gcHeapSlopeBytesPerMinute') 'Core soak report must expose GC-heap slope.'
Assert-True ($soakReport -match 'workingSetSlopeBytesPerMinute') 'Core soak report must expose working-set slope.'
Assert-True ($soakReport -notmatch 'timeDeltaPercent') 'Core soak report must not emit timing regression percentages.'
Assert-True ($soakReport -notmatch 'memoryDeltaPercent') 'Core soak report must not emit uncalibrated memory regression percentages.'

$packageFootprint = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/report-package-footprint.ps1') -Raw
Assert-True ($packageFootprint -match '\[IO\.Compression\.ZipFile\]::OpenRead') 'Package footprint must inspect the actual nupkg archive.'
Assert-True ($packageFootprint -match "\*\.nuspec") 'Package footprint must derive dependencies from nuspec metadata.'
Assert-True ($packageFootprint -match "scenario = 'package-infrastructure'") 'Package footprint must use the package-infrastructure scenario.'
Assert-True ($packageFootprint -match "scenarioClass = 'evolution'") 'Package footprint must remain evolution-classified.'
Assert-True ($packageFootprint -match "comparisonPolicy = 'package-closure-engineering-metrics'") 'Package footprint must remain engineering-metrics evidence.'
Assert-True ($packageFootprint.Contains('authoritativeTiming = $false')) 'Package footprint must not claim authoritative timing.'
Assert-True ($packageFootprint -match "baseline = 'SmartPipe.Extensions'") 'Split capability comparisons must use the 2.1.2 broad Extensions package as baseline.'
Assert-True ($packageFootprint -match "candidate = 'SmartPipe.Extensions.DependencyInjection'") 'Package footprint must include the DI split package.'
Assert-True ($packageFootprint -match "candidate = 'SmartPipe.Extensions.EntityFrameworkCore'") 'Package footprint must include the EF Core split package.'
Assert-True ($packageFootprint -match "candidate = 'SmartPipe.Extensions.Csv'") 'Package footprint must include the CSV split package.'
Assert-True ($packageFootprint -notmatch 'SmartPipe\.Extensions\.Mapster') 'SP220-12 Mapster must remain excluded from the SP220-01..11 footprint report.'
Assert-True ($packageFootprint -match 'These are package-closure engineering metrics, not runtime performance claims') 'Package footprint report must forbid runtime-speed interpretation.'
Assert-True ($packageFootprint -match 'compressedBytes') 'Package footprint must report compressed package bytes.'
Assert-True ($packageFootprint -match 'uncompressedBytes') 'Package footprint must report uncompressed package bytes.'
Assert-True ($packageFootprint -match 'externalDependencyCount') 'Package footprint must report external dependency closure.'
Assert-True ($packageFootprint -match '\$compressedBytes \+= \[long\]\$package\.compressedBytes') 'Package footprint totals must use explicit typed aggregation.'
Assert-True ($packageFootprint -notmatch 'Measure-Object -Property compressedBytes') 'Package footprint must not rely on PowerShell property adapters for ordered-dictionary aggregation.'



$di = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/prepare-di-evolution.ps1') -Raw
Assert-True ($di -match "scenarioClass = 'evolution'") 'DI comparison must remain classified as evolution.'
Assert-True ($di -match 'SmartPipe\.Extensions') 'DI baseline must use SmartPipe.Extensions.'
Assert-True ($di -match 'SmartPipe\.Extensions\.DependencyInjection') 'DI candidate must use SmartPipe.Extensions.DependencyInjection.'
Assert-True ($di -match 'packageSourceMapping') 'DI restore must use NuGet Package Source Mapping.'
Assert-True ($di -match 'SmartPipe\.\*') 'All SmartPipe packages must be mapped to the local target feed.'
Assert-True ($di -match 'dotnet nuget verify') 'DI provenance must use NuGet-native package content hashes.'
Assert-True ($di.Contains("Where-Object { `$_.Name -like 'SmartPipe.*' }")) 'DI provenance must verify every resolved SmartPipe package.'
Assert-True ($di -match "'--locked-mode'") 'DI restore must verify the generated lock in locked mode.'
Assert-True ($di -match "'--no-http-cache'") 'DI restore must bypass the NuGet HTTP cache.'
Assert-True ($di -match 'adapterSourceSha256') 'DI provenance must record target-specific adapter source hashes.'
$diBenchmarkSource = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.DependencyInjection.Shared/DependencyInjectionEvolutionBenchmarks.cs') -Raw
Assert-True ($diBenchmarkSource -match 'SetupAsync') 'DI evolution benchmark must run an async correctness precheck.'
Assert-True ($diBenchmarkSource -match 'StartCompleteDisposeRunAsync') 'DI correctness precheck must exercise a complete run lifecycle.'
Assert-True ($diBenchmarkSource -match 'descriptorCount <= 0') 'DI correctness precheck must validate provider registration output.'
Assert-True ($diBenchmarkSource -match 'ResolveFactory\(\) is null') 'DI correctness precheck must validate factory resolution.'
Assert-True ($diBenchmarkSource -notmatch 'public sealed class DependencyInjectionEvolutionBenchmarks') 'BenchmarkDotNet DI evolution type must remain unsealed.'
$diScaleSource = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.DependencyInjection.V220/DependencyInjectionV220ScaleBenchmarks.cs') -Raw
Assert-True ($diScaleSource -match 'BenchmarkCategory\("V220Only"') 'DI scale benchmark must remain v2.2-only characterization.'
Assert-True ($diScaleSource -match '\[Params\(1, 32, 256\)\]') 'DI scale benchmark must cover 1, 32, and 256 keys.'
Assert-True ($diScaleSource -match 'RegisterAndBuildProviderManyKeys') 'DI scale benchmark must cover multi-key registration/provider build.'
Assert-True ($diScaleSource -match 'ResolveLastKeyedFactory') 'DI scale benchmark must cover keyed factory lookup.'
Assert-True ($diScaleSource -notmatch 'public sealed class DependencyInjectionV220ScaleBenchmarks') 'BenchmarkDotNet DI scale type must remain unsealed.'
Assert-True ($di -match "scenarioClass = 'v220-only'") 'DI scale provenance must remain v220-only.'
Assert-True ($di -match 'v220-scale-provenance\.json') 'DI scale source/provenance must be recorded.'
Assert-True (($di | Select-String -Pattern 'Assert-BenchmarkResult' -AllMatches).Matches.Count -ge 3) 'DI Dry must require BenchmarkDotNet result artifacts for both evolution targets and v2.2 scale.'
Assert-True ($di -match 'produced no statistics') 'DI Dry must reject BenchmarkDotNet JSON records without statistics.'
Assert-True ($di -match 'produced no measurements') 'DI Dry must reject BenchmarkDotNet JSON records without measurements.'
Assert-True (($di | Select-String -Pattern "'--exporters'[\s\S]{0,80}'json'" -AllMatches).Matches.Count -ge 2) 'DI Dry and v2.2 scale Dry must explicitly export BenchmarkDotNet JSON evidence.'

$diRunner = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/run-di-evolution.ps1') -Raw
Assert-True ($diRunner -match "target = 'v212'[\s\S]*target = 'v220'[\s\S]*target = 'v220'[\s\S]*target = 'v212'") 'DI evolution order must remain counter-balanced A-B-B-A.'
Assert-True ($diRunner -match "scenarioClass = 'evolution'") 'DI runner must classify the scenario as evolution.'
Assert-True ($diRunner -match "comparisonPolicy = 'side-by-side-no-cross-version-ratio'") 'DI runner must forbid cross-version ratio reporting.'
Assert-True ($diRunner.Contains('authoritativeTiming = $false')) 'Hosted DI timing must remain non-authoritative.'
Assert-True ($diRunner -match 'Assert-BenchmarkResult') 'DI Short must fail when BenchmarkDotNet produces no result artifact.'
Assert-True ($diRunner -match 'produced no statistics') 'DI Short must reject BenchmarkDotNet JSON records without statistics.'
Assert-True ($diRunner -match 'produced no measurements') 'DI Short must reject BenchmarkDotNet JSON records without measurements.'

$diReport = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/report-di-evolution.ps1') -Raw
Assert-True ($diReport -match 'side-by-side-no-cross-version-ratio') 'DI report must preserve evolution comparison policy.'
Assert-True ($diReport -notmatch 'timeDeltaPercent') 'DI report must not emit a strict A/B timing percentage.'
Assert-True ($diReport -notmatch 'allocationDeltaPercent') 'DI report must not emit a strict A/B allocation percentage.'
Assert-True ($diReport -match 'baselineRepeatDriftPercent') 'DI report must expose baseline repeat drift.'
Assert-True ($diReport -match 'candidateRepeatDriftPercent') 'DI report must expose candidate repeat drift.'

$hosting = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/prepare-hosting-evolution.ps1') -Raw
Assert-True ($hosting -match "scenarioClass = 'evolution'") 'Hosting comparison must remain classified as evolution.'
Assert-True ($hosting -match 'SmartPipe\.Extensions\.Hosting') 'Hosting candidate must use SmartPipe.Extensions.Hosting.'
Assert-True ($hosting -match 'packageSourceMapping') 'Hosting restore must use NuGet Package Source Mapping.'
Assert-True ($hosting -match 'SmartPipe\.\*') 'Hosting SmartPipe packages must map to the local target feed.'
Assert-True ($hosting -match 'dotnet nuget verify') 'Hosting provenance must use NuGet-native content hashes.'
Assert-True ($hosting -match "'--locked-mode'") 'Hosting restore must verify the generated lock in locked mode.'
Assert-True ($hosting -match "'--exporters'[\s\S]{0,80}'json'") 'Hosting Dry must explicitly export BenchmarkDotNet JSON.'
Assert-True ($hosting -match 'Assert-BenchmarkResult') 'Hosting Dry must fail when BenchmarkDotNet produces no JSON result.'
Assert-True ($hosting -match 'produced no statistics') 'Hosting Dry must reject BenchmarkDotNet JSON records without statistics.'
Assert-True ($hosting -match 'produced no measurements') 'Hosting Dry must reject BenchmarkDotNet JSON records without measurements.'
$hostingSource = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Hosting.Shared/HostingEvolutionBenchmarks.cs') -Raw
Assert-True ($hostingSource -match 'BenchmarkCategory\("Evolution", "Hosting"\)') 'Hosting benchmark must remain evolution-classified.'
Assert-True ($hostingSource -match 'SetupAsync') 'Hosting benchmark must run a correctness precheck.'
Assert-True ($hostingSource -match 'StartStopFreshAsync') 'Hosting correctness precheck must exercise start/stop lifecycle.'
Assert-True ($hostingSource -notmatch 'public sealed class HostingEvolutionBenchmarks') 'BenchmarkDotNet Hosting type must remain unsealed.'

$health = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/prepare-healthchecks-evolution.ps1') -Raw
Assert-True ($health -match "scenarioClass = 'evolution'") 'HealthChecks comparison must remain classified as evolution.'
Assert-True ($health -match 'SmartPipe\.Extensions\.HealthChecks') 'HealthChecks candidate must use SmartPipe.Extensions.HealthChecks.'
Assert-True ($health -match 'packageSourceMapping') 'HealthChecks restore must use NuGet Package Source Mapping.'
Assert-True ($health -match 'SmartPipe\.\*') 'HealthChecks SmartPipe packages must map to the local target feed.'
Assert-True ($health -match 'dotnet nuget verify') 'HealthChecks provenance must use NuGet-native content hashes.'
Assert-True ($health -match "'--locked-mode'") 'HealthChecks restore must verify the generated lock in locked mode.'
Assert-True ($health -match "'--exporters'[\s\S]{0,80}'json'") 'HealthChecks Dry must explicitly export BenchmarkDotNet JSON.'
Assert-True ($health -match 'Assert-BenchmarkResult') 'HealthChecks Dry must fail when BenchmarkDotNet produces no JSON result.'
Assert-True ($health -match 'produced no statistics') 'HealthChecks Dry must reject BenchmarkDotNet JSON records without statistics.'
Assert-True ($health -match 'produced no measurements') 'HealthChecks Dry must reject BenchmarkDotNet JSON records without measurements.'
Assert-True ($health -match 'zero measured operations') 'HealthChecks Dry must reject zero-operation BenchmarkDotNet records.'
Assert-True ($health -match 'PERF_HEALTHCHECKS_EVOLUTION_READY') 'HealthChecks materializer must expose the correct completion marker.'
$healthSource = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.HealthChecks.Shared/HealthChecksEvolutionBenchmarks.cs') -Raw
Assert-True ($healthSource -match 'BenchmarkCategory\("Evolution", "HealthChecks"\)') 'HealthChecks benchmark must remain evolution-classified.'
Assert-True ($healthSource -match 'HealthStatus\.Degraded') 'HealthChecks correctness precheck must validate the registered/not-started Degraded outcome.'
Assert-True ($healthSource -match 'CheckRegisteredPipelineAsync') 'HealthChecks correctness precheck must evaluate through HealthCheckService.'
Assert-True ($healthSource -notmatch 'public sealed class HealthChecksEvolutionBenchmarks') 'BenchmarkDotNet HealthChecks type must remain unsealed.'

$otel = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/prepare-opentelemetry-evolution.ps1') -Raw
Assert-True ($otel -match "scenarioClass = 'evolution'") 'OpenTelemetry comparison must remain classified as evolution.'
Assert-True ($otel -match "PrimaryPackageId 'SmartPipe.Core'") 'OpenTelemetry baseline must bind to SmartPipe.Core 2.1.2.'
Assert-True ($otel -match 'SmartPipe\.Extensions\.OpenTelemetry') 'OpenTelemetry candidate must use SmartPipe.Extensions.OpenTelemetry.'
Assert-True ($otel -match 'packageSourceMapping') 'OpenTelemetry restore must use NuGet Package Source Mapping.'
Assert-True ($otel -match 'SmartPipe\.\*') 'OpenTelemetry SmartPipe packages must map to the local target feed.'
Assert-True ($otel -match 'dotnet nuget verify') 'OpenTelemetry provenance must use NuGet-native content hashes.'
Assert-True ($otel -match "'--locked-mode'") 'OpenTelemetry restore must verify the generated lock in locked mode.'
Assert-True ($otel -match "'--exporters'[\s\S]{0,80}'json'") 'OpenTelemetry Dry must explicitly export BenchmarkDotNet JSON.'
Assert-True ($otel -match 'Assert-BenchmarkResult') 'OpenTelemetry Dry must fail when BenchmarkDotNet produces no JSON result.'
Assert-True ($otel -match 'produced no statistics') 'OpenTelemetry Dry must reject BenchmarkDotNet JSON records without statistics.'
Assert-True ($otel -match 'produced no measurements') 'OpenTelemetry Dry must reject BenchmarkDotNet JSON records without measurements.'
Assert-True ($otel -match 'zero measured operations') 'OpenTelemetry Dry must reject zero-operation BenchmarkDotNet records.'
Assert-True ($otel -match 'PERF_OPENTELEMETRY_EVOLUTION_READY') 'OpenTelemetry materializer must expose the correct completion marker.'
$otelSource = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.OpenTelemetry.Shared/OpenTelemetryEvolutionBenchmarks.cs') -Raw
Assert-True ($otelSource -match 'BenchmarkCategory\("Evolution", "OpenTelemetry"\)') 'OpenTelemetry benchmark must remain evolution-classified.'
Assert-True ($otelSource -match 'ValidateRegistration') 'OpenTelemetry benchmark must run a telemetry-registration correctness oracle.'
Assert-True ($otelSource -match 'BuildResolveDisposeProviders') 'OpenTelemetry benchmark must cover provider construction and resolution.'
Assert-True ($otelSource -notmatch 'public sealed class OpenTelemetryEvolutionBenchmarks') 'BenchmarkDotNet OpenTelemetry type must remain unsealed.'
$otelV220 = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.OpenTelemetry.V220/OpenTelemetryTarget.cs') -Raw
Assert-True ($otelV220 -match 'AddSmartPipeInstrumentation') 'OpenTelemetry candidate must exercise the SmartPipe instrumentation helper.'
Assert-True ($otelV220 -match 'WithMetrics') 'OpenTelemetry candidate benchmark app must own MeterProvider composition.'
Assert-True ($otelV220 -match 'WithTracing') 'OpenTelemetry candidate benchmark app must own TracerProvider composition.'
Assert-True ($otelV220 -match 'ReferenceEquals') 'OpenTelemetry candidate precheck must enforce exact builder identity.'
Assert-True ($otelV220 -match 'countAfterFirstRegistration') 'OpenTelemetry candidate precheck must enforce idempotent registration.'
Assert-True ($otelV220 -match 'AddInMemoryExporter') 'OpenTelemetry correctness oracle must verify exported telemetry.'
Assert-True ($otelV220 -match 'counter.Enabled') 'OpenTelemetry correctness oracle must verify metric listener activation.'
Assert-True ($otelV220 -match 'activitySource.HasListeners') 'OpenTelemetry correctness oracle must verify trace listener activation.'

$otelRunner = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/run-opentelemetry-evolution.ps1') -Raw
Assert-True ($otelRunner -match "target = 'v212'[\s\S]*target = 'v220'[\s\S]*target = 'v220'[\s\S]*target = 'v212'") 'OpenTelemetry evolution order must remain counter-balanced A-B-B-A.'
Assert-True ($otelRunner -match "scenarioClass = 'evolution'") 'OpenTelemetry runner must classify the scenario as evolution.'
Assert-True ($otelRunner -match "comparisonPolicy = 'side-by-side-no-cross-version-ratio'") 'OpenTelemetry runner must forbid cross-version ratio reporting.'
Assert-True ($otelRunner.Contains('authoritativeTiming = $false')) 'Hosted OpenTelemetry timing must remain non-authoritative.'
Assert-True ($otelRunner -match 'Assert-BenchmarkResult') 'OpenTelemetry runner must reject missing BenchmarkDotNet JSON evidence.'
Assert-True ($otelRunner -match 'produced no statistics') 'OpenTelemetry Short must reject BenchmarkDotNet JSON records without statistics.'
Assert-True ($otelRunner -match 'produced no measurements') 'OpenTelemetry Short must reject BenchmarkDotNet JSON records without measurements.'

$otelReport = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/report-opentelemetry-evolution.ps1') -Raw
Assert-True ($otelReport -match 'side-by-side-no-cross-version-ratio') 'OpenTelemetry report must preserve evolution comparison policy.'
Assert-True ($otelReport -notmatch 'timeDeltaPercent') 'OpenTelemetry report must not emit a strict A/B timing percentage.'
Assert-True ($otelReport -notmatch 'allocationDeltaPercent') 'OpenTelemetry report must not emit a strict A/B allocation percentage.'
Assert-True ($otelReport -match 'Group-Object \{ \[string\]\$_\.method \}') 'OpenTelemetry reporter must group normalized records by method value.'
Assert-True ($otelReport -match 'baselineRepeatDriftPercent') 'OpenTelemetry report must expose baseline repeat drift.'
Assert-True ($otelReport -match 'candidateRepeatDriftPercent') 'OpenTelemetry report must expose candidate repeat drift.'



$healthV212Project = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.HealthChecks.V212/SmartPipe.Perf.HealthChecks.V212.csproj') -Raw
$healthV220Project = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.HealthChecks.V220/SmartPipe.Perf.HealthChecks.V220.csproj') -Raw
Assert-True ($healthV212Project -match 'Microsoft.Extensions.DependencyInjection" Version="10\.0\.11"') 'HealthChecks baseline app must pin the full DI runtime to 10.0.11.'
Assert-True ($healthV220Project -match 'Microsoft.Extensions.DependencyInjection" Version="10\.0\.11"') 'HealthChecks candidate app must pin the full DI runtime to 10.0.11.'
Assert-True ($healthV212Project -match 'Microsoft.Extensions.Logging" Version="10\.0\.11"') 'HealthChecks baseline app must pin logging to 10.0.11.'
Assert-True ($healthV220Project -match 'Microsoft.Extensions.Logging" Version="10\.0\.11"') 'HealthChecks candidate app must pin logging to 10.0.11.'

$otelV212Project = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.OpenTelemetry.V212/SmartPipe.Perf.OpenTelemetry.V212.csproj') -Raw
$otelV220Project = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.OpenTelemetry.V220/SmartPipe.Perf.OpenTelemetry.V220.csproj') -Raw
Assert-True ($otelV212Project -match 'Microsoft.Extensions.DependencyInjection" Version="10\.0\.11"') 'OpenTelemetry baseline app must pin the full DI runtime to 10.0.11.'
Assert-True ($otelV220Project -match 'Microsoft.Extensions.DependencyInjection" Version="10\.0\.11"') 'OpenTelemetry candidate app must pin the full DI runtime to 10.0.11.'
Assert-True ($otelV212Project -match 'Microsoft.Extensions.Logging" Version="10\.0\.11"') 'OpenTelemetry baseline app must pin logging to 10.0.11.'
Assert-True ($otelV220Project -match 'Microsoft.Extensions.Logging" Version="10\.0\.11"') 'OpenTelemetry candidate app must pin logging to 10.0.11.'

$extensions = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/prepare-extensions-strict-ab.ps1') -Raw
Assert-True ($extensions -match "scenarioClass = 'strict-ab'") 'SP220-07 comparison must remain classified as strict-ab.'
Assert-True ($extensions -match "scenario = 'channels-transforms-logging-dataannotations'") 'SP220-07 scenario id must remain canonical.'
Assert-True ($extensions -match "PrimaryPackageId 'SmartPipe.Extensions'") 'SP220-07 baseline must bind to SmartPipe.Extensions 2.1.2.'
Assert-True ($extensions -match "PrimaryPackageId 'SmartPipe.Extensions.Transforms'") 'SP220-07 candidate must bind to the split transform package.'
Assert-True ($extensions -match 'packageSourceMapping') 'SP220-07 restore must use NuGet Package Source Mapping.'
Assert-True ($extensions -match 'dotnet nuget verify') 'SP220-07 provenance must use NuGet-native content hashes.'
Assert-True ($extensions -match "'--locked-mode'") 'SP220-07 restore must verify the generated lock in locked mode.'
Assert-True ($extensions -match "'--no-http-cache'") 'SP220-07 restore must bypass the NuGet HTTP cache.'
Assert-True ($extensions -match 'Assert-BenchmarkResult') 'SP220-07 Dry must reject missing BenchmarkDotNet JSON evidence.'
Assert-True ($extensions -match 'produced no statistics') 'SP220-07 Dry must reject BenchmarkDotNet records without statistics.'
Assert-True ($extensions -match 'produced no measurements') 'SP220-07 Dry must reject BenchmarkDotNet records without measurements.'
Assert-True ($extensions -match 'PERF_EXTENSIONS_STRICT_AB_READY') 'SP220-07 materializer must expose the canonical completion marker.'

$extensionsSource = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.ExtensionsStrictAb.Shared/ExtensionsStrictAbBenchmarks.cs') -Raw
Assert-True ($extensionsSource -match 'BenchmarkCategory\("Comparative", "StrictAB", "SP220-07"\)') 'SP220-07 benchmark must remain strict-ab classified.'
Assert-True ($extensionsSource -match 'DecompressBrotli') 'SP220-07 compression oracle must verify decompressed payload bytes.'
Assert-True ($extensionsSource -match 'ExpectedChannelChecksum') 'SP220-07 channel oracle must verify deterministic count/checksum semantics.'
Assert-True ($extensionsSource -match 'LoggerSinkLegacyDisabled') 'SP220-07 must benchmark only the legacy-compatible LoggerSink path in strict A/B.'
Assert-True ($extensionsSource -match 'ConditionalFalsePassThrough') 'SP220-07 must cover conditional pass-through semantics.'
Assert-True ($extensionsSource -match 'CompositeThreeTransforms') 'SP220-07 must cover initialized composite success-path semantics.'

$extensionsV212Project = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.ExtensionsStrictAb.V212/SmartPipe.Perf.ExtensionsStrictAb.V212.csproj') -Raw
$extensionsV220Project = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.ExtensionsStrictAb.V220/SmartPipe.Perf.ExtensionsStrictAb.V220.csproj') -Raw
Assert-True ($extensionsV212Project -match 'SmartPipe.Extensions" Version="2\.1\.2"') 'SP220-07 baseline project must reference SmartPipe.Extensions 2.1.2.'
Assert-True ($extensionsV220Project -match 'SmartPipe.Extensions.Channels" Version="2\.2\.0"') 'SP220-07 candidate must reference SmartPipe.Extensions.Channels 2.2.0.'
Assert-True ($extensionsV220Project -match 'SmartPipe.Extensions.Transforms" Version="2\.2\.0"') 'SP220-07 candidate must reference SmartPipe.Extensions.Transforms 2.2.0.'
Assert-True ($extensionsV220Project -match 'SmartPipe.Extensions.DataAnnotations" Version="2\.2\.0"') 'SP220-07 candidate must reference SmartPipe.Extensions.DataAnnotations 2.2.0.'
Assert-True ($extensionsV220Project -match 'SmartPipe.Extensions.Logging" Version="2\.2\.0"') 'SP220-07 candidate must reference SmartPipe.Extensions.Logging 2.2.0.'
Assert-True ($extensionsV212Project -match 'Microsoft.Extensions.Logging.Abstractions" Version="10\.0\.11"') 'SP220-07 baseline must pin Logging.Abstractions 10.0.11.'
Assert-True ($extensionsV220Project -match 'Microsoft.Extensions.Logging.Abstractions" Version="10\.0\.11"') 'SP220-07 candidate must pin Logging.Abstractions 10.0.11.'

$extensionsRunner = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/run-extensions-strict-ab.ps1') -Raw
Assert-True ($extensionsRunner -match "target = 'v212'[\s\S]*target = 'v220'[\s\S]*target = 'v220'[\s\S]*target = 'v212'") 'SP220-07 strict A/B order must remain counter-balanced A-B-B-A.'
Assert-True ($extensionsRunner -match "scenarioClass = 'strict-ab'") 'SP220-07 runner must remain strict-ab.'
Assert-True ($extensionsRunner -match "comparisonPolicy = 'strict-cross-version-ratio'") 'SP220-07 runner must preserve strict ratio policy.'
Assert-True ($extensionsRunner.Contains('authoritativeTiming = $false')) 'Hosted SP220-07 timing must remain non-authoritative.'
Assert-True ($extensionsRunner -match 'Assert-BenchmarkResult') 'SP220-07 Short must reject missing BenchmarkDotNet evidence.'
Assert-True ($extensionsRunner -match 'produced no statistics') 'SP220-07 Short must reject results without statistics.'
Assert-True ($extensionsRunner -match 'produced no measurements') 'SP220-07 Short must reject results without measurements.'
Assert-True ($extensionsRunner -match 'zero measured operations') 'SP220-07 Short must reject zero-operation results.'

$extensionsReport = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/report-extensions-strict-ab.ps1') -Raw
Assert-True ($extensionsReport -match 'strict-cross-version-ratio') 'SP220-07 report must preserve strict ratio policy.'
Assert-True ($extensionsReport -match 'timeDeltaPercent') 'SP220-07 report must emit strict timing deltas.'
Assert-True ($extensionsReport -match 'allocationDeltaPercent') 'SP220-07 report must emit strict allocation deltas.'
Assert-True ($extensionsReport -match 'baselineRepeatDriftPercent') 'SP220-07 report must expose baseline repeat drift.'
Assert-True ($extensionsReport -match 'candidateRepeatDriftPercent') 'SP220-07 report must expose candidate repeat drift.'
Assert-True ($extensionsReport -match 'records.Count -ne \(\$expectedMethods.Count \* 4\)') 'SP220-07 report must reject incomplete A-B-B-A evidence.'

$extensionsFocus = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/prepare-extensions-focus.ps1') -Raw
Assert-True ($extensionsFocus -match "scenarioClass = 'strict-ab'") 'SP220-07 focused Composite comparison must remain strict-ab.'
Assert-True ($extensionsFocus -match "scenario = 'extensions-focus'") 'SP220-07 focused scenario id must remain isolated from the main strict workload.'
Assert-True ($extensionsFocus -match 'ZeroChildren') 'Composite focus must measure zero-child fixed overhead.'
Assert-True ($extensionsFocus -match 'OneChild') 'Composite focus must measure one-child overhead.'
Assert-True ($extensionsFocus -match 'ThreeChildren') 'Composite focus must retain the three-child comparison point.'
Assert-True ($extensionsFocus -match 'CompositeFocusBenchmarks') 'Composite focus Dry must target only the focused Composite class.'
Assert-True ($extensionsFocus -match "PrimaryPackageId 'SmartPipe.Extensions'") 'Composite focus baseline must bind to SmartPipe.Extensions 2.1.2.'
Assert-True ($extensionsFocus -match "PrimaryPackageId 'SmartPipe.Extensions.Transforms'") 'Composite focus candidate must bind to SmartPipe.Extensions.Transforms 2.2.0.'
Assert-True ($extensionsFocus -match 'PERF_EXTENSIONS_FOCUS_READY') 'Composite focus materializer must expose its completion marker.'

$extensionsFocusSource = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.ExtensionsFocus.Shared/CompositeFocusBenchmarks.cs') -Raw
Assert-True ($extensionsFocusSource -match 'BenchmarkCategory\("Comparative", "StrictAB", "SP220-07-Focus"\)') 'Composite focus benchmark must remain strict A/B.'
Assert-True ($extensionsFocusSource -match 'new CompositeTransform<int>\(\)') 'Composite focus must include a zero-child composite.'
Assert-True ($extensionsFocusSource -match 'ZeroChildren') 'Composite focus must keep the zero-child benchmark.'
Assert-True ($extensionsFocusSource -match 'OneChild') 'Composite focus must keep the one-child benchmark.'
Assert-True ($extensionsFocusSource -match 'ThreeChildren') 'Composite focus must keep the three-child benchmark.'

$channelFocusSource = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.ExtensionsFocus.V220/ChannelMergeFocusBenchmarks.cs') -Raw
Assert-True ($channelFocusSource -match 'BenchmarkCategory\("V220Only", "SP220-07-Focus", "ChannelMerge"\)') 'Channel focus must remain candidate-only characterization.'
Assert-True ($channelFocusSource -match 'CompatibilityTwoReader') 'Channel focus must retain the compatibility overload.'
Assert-True ($channelFocusSource -match 'MergeManyTwoReader') 'Channel focus must retain the generalized two-reader path.'


$json = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/prepare-json-strict-ab.ps1') -Raw
Assert-True ($json -match "scenarioClass = 'strict-ab'") 'SP220-08 JSON comparison must remain strict-ab.'
Assert-True ($json -match "scenario = 'json'") 'SP220-08 JSON scenario id must remain canonical.'
Assert-True (($json | Select-String -Pattern "PrimaryPackageId 'SmartPipe.Extensions.Json'" -AllMatches).Matches.Count -eq 2) 'Both JSON targets must bind to SmartPipe.Extensions.Json.'
Assert-True ($json -match "ExpectedPrimaryVersion '2.1.2'") 'JSON baseline must bind to version 2.1.2.'
Assert-True ($json -match "ExpectedPrimaryVersion '2.2.0'") 'JSON candidate must bind to version 2.2.0.'
Assert-True ($json -match 'packageSourceMapping') 'JSON restore must use NuGet Package Source Mapping.'
Assert-True ($json -match 'dotnet nuget verify') 'JSON provenance must use NuGet-native content hashes.'
Assert-True ($json -match "'--locked-mode'") 'JSON restore must verify the generated lock in locked mode.'
Assert-True ($json -match "'--no-http-cache'") 'JSON restore must bypass the NuGet HTTP cache.'
Assert-True ($json -match 'Assert-BenchmarkResult') 'JSON Dry must reject missing BenchmarkDotNet JSON evidence.'
Assert-True ($json -match 'SourceGeneratedSmallRoundTrip') 'JSON Dry must require source-generated small round-trip evidence.'
Assert-True ($json -match 'OptionsSmallRoundTrip') 'JSON Dry must require options small round-trip evidence.'
Assert-True ($json -match 'SourceGeneratedMediumRoundTrip') 'JSON Dry must require source-generated medium round-trip evidence.'
Assert-True ($json -match 'OptionsMediumRoundTrip') 'JSON Dry must require options medium round-trip evidence.'

$jsonSource = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.JsonStrictAb.Shared/JsonStrictAbBenchmarks.cs') -Raw
Assert-True ($jsonSource -match 'BenchmarkCategory\("Comparative", "StrictAB", "SP220-08"\)') 'SP220-08 JSON benchmark must remain strict-ab classified.'
Assert-True ($jsonSource -match 'JsonSerializable\(typeof\(SmallPayload\)\)') 'JSON benchmark must use source-generated metadata for small payloads.'
Assert-True ($jsonSource -match 'JsonSerializable\(typeof\(MediumPayload\)\)') 'JSON benchmark must use source-generated metadata for medium payloads.'
Assert-True ($jsonSource -match 'AssertSmall') 'JSON benchmark must preserve a small-payload correctness oracle.'
Assert-True ($jsonSource -match 'AssertMedium') 'JSON benchmark must preserve a medium-payload correctness oracle.'

$jsonV212Project = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.JsonStrictAb.V212/SmartPipe.Perf.JsonStrictAb.V212.csproj') -Raw
$jsonV220Project = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.JsonStrictAb.V220/SmartPipe.Perf.JsonStrictAb.V220.csproj') -Raw
Assert-True ($jsonV212Project -match 'SmartPipe.Extensions.Json" Version="2\.1\.2"') 'JSON baseline project must reference SmartPipe.Extensions.Json 2.1.2.'
Assert-True ($jsonV220Project -match 'SmartPipe.Extensions.Json" Version="2\.2\.0"') 'JSON candidate project must reference SmartPipe.Extensions.Json 2.2.0.'

$jsonRunner = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/run-json-strict-ab.ps1') -Raw
Assert-True ($jsonRunner -match "target = 'v212'[\s\S]*target = 'v220'[\s\S]*target = 'v220'[\s\S]*target = 'v212'") 'JSON strict A/B order must remain counter-balanced A-B-B-A.'
Assert-True ($jsonRunner -match "scenarioClass = 'strict-ab'") 'JSON runner must remain strict-ab.'
Assert-True ($jsonRunner -match "comparisonPolicy = 'strict-cross-version-ratio'") 'JSON runner must preserve strict ratio policy.'
Assert-True ($jsonRunner.Contains('authoritativeTiming = $false')) 'Hosted JSON timing must remain non-authoritative.'
Assert-True ($jsonRunner -match 'Assert-BenchmarkResult') 'JSON Short must reject missing BenchmarkDotNet evidence.'
Assert-True ($jsonRunner -match 'produced no statistics') 'JSON Short must reject results without statistics.'
Assert-True ($jsonRunner -match 'produced no measurements') 'JSON Short must reject results without measurements.'

$jsonReport = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/report-json-strict-ab.ps1') -Raw
Assert-True ($jsonReport -match 'strict-cross-version-ratio') 'JSON report must preserve strict ratio policy.'
Assert-True ($jsonReport -match 'timeDeltaPercent') 'JSON report must emit strict timing deltas.'
Assert-True ($jsonReport -match 'allocationDeltaPercent') 'JSON report must emit strict allocation deltas.'
Assert-True ($jsonReport -match 'baselineRepeatDriftPercent') 'JSON report must expose baseline repeat drift.'
Assert-True ($jsonReport -match 'candidateRepeatDriftPercent') 'JSON report must expose candidate repeat drift.'
Assert-True ($jsonReport -match 'records.Count -ne \(\$expectedMethods.Count \* 4\)') 'JSON report must reject incomplete A-B-B-A evidence.'

$csv = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/prepare-csv-strict-ab.ps1') -Raw
Assert-True ($csv -match "scenarioClass = 'strict-ab'") 'SP220-09 CSV comparison must remain strict-ab.'
Assert-True ($csv -match "scenario = 'csv'") 'SP220-09 CSV scenario id must remain canonical.'
Assert-True ($csv -match "PrimaryPackageId 'SmartPipe.Extensions'") 'CSV baseline must bind to SmartPipe.Extensions 2.1.2.'
Assert-True ($csv -match "PrimaryPackageId 'SmartPipe.Extensions.Csv'") 'CSV candidate must bind to SmartPipe.Extensions.Csv 2.2.0.'
Assert-True ($csv -match "ExpectedPrimaryVersion '2.1.2'") 'CSV baseline version must remain 2.1.2.'
Assert-True ($csv -match "ExpectedPrimaryVersion '2.2.0'") 'CSV candidate version must remain 2.2.0.'
Assert-True ($csv -match 'packageSourceMapping') 'CSV restore must use NuGet Package Source Mapping.'
Assert-True ($csv -match 'dotnet nuget verify') 'CSV provenance must use NuGet-native content hashes.'
Assert-True ($csv -match "'--locked-mode'") 'CSV restore must verify the generated lock in locked mode.'
Assert-True ($csv -match "'--no-http-cache'") 'CSV restore must bypass the NuGet HTTP cache.'
Assert-True ($csv -match 'Assert-BenchmarkResult') 'CSV Dry must reject missing BenchmarkDotNet JSON evidence.'
Assert-True ($csv -match 'SmallRoundTrip') 'CSV Dry must require small round-trip evidence.'
Assert-True ($csv -match 'MediumRoundTrip') 'CSV Dry must require medium round-trip evidence.'

$csvSource = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.CsvStrictAb.Shared/CsvStrictAbBenchmarks.cs') -Raw
Assert-True ($csvSource -match 'BenchmarkCategory\("Comparative", "StrictAB", "SP220-09"\)') 'SP220-09 CSV benchmark must remain strict-ab classified.'
Assert-True ($csvSource -match 'AssertSmall') 'CSV benchmark must preserve the small-record correctness oracle.'
Assert-True ($csvSource -match 'AssertMedium') 'CSV benchmark must preserve the medium-record correctness oracle.'
Assert-True ($csvSource -notmatch 'CsvFileSource|CsvFileSink') 'CSV strict timing must remain CPU-only and exclude filesystem IO.'

$csvV212Project = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.CsvStrictAb.V212/SmartPipe.Perf.CsvStrictAb.V212.csproj') -Raw
$csvV220Project = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.CsvStrictAb.V220/SmartPipe.Perf.CsvStrictAb.V220.csproj') -Raw
Assert-True ($csvV212Project -match 'SmartPipe.Extensions" Version="2\.1\.2"') 'CSV baseline project must reference SmartPipe.Extensions 2.1.2.'
Assert-True ($csvV220Project -match 'SmartPipe.Extensions.Csv" Version="2\.2\.0"') 'CSV candidate project must reference SmartPipe.Extensions.Csv 2.2.0.'

$csvRunner = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/run-csv-strict-ab.ps1') -Raw
Assert-True ($csvRunner -match "target = 'v212'[\s\S]*target = 'v220'[\s\S]*target = 'v220'[\s\S]*target = 'v212'") 'CSV strict A/B order must remain counter-balanced A-B-B-A.'
Assert-True ($csvRunner -match "scenarioClass = 'strict-ab'") 'CSV runner must remain strict-ab.'
Assert-True ($csvRunner -match "comparisonPolicy = 'strict-cross-version-ratio'") 'CSV runner must preserve strict ratio policy.'
Assert-True ($csvRunner.Contains('authoritativeTiming = $false')) 'Hosted CSV timing must remain non-authoritative.'
Assert-True ($csvRunner -match 'Assert-BenchmarkResult') 'CSV Short must reject missing BenchmarkDotNet evidence.'
Assert-True ($csvRunner -match 'produced no statistics') 'CSV Short must reject results without statistics.'
Assert-True ($csvRunner -match 'produced no measurements') 'CSV Short must reject results without measurements.'

$csvReport = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/report-csv-strict-ab.ps1') -Raw
Assert-True ($csvReport -match 'strict-cross-version-ratio') 'CSV report must preserve strict ratio policy.'
Assert-True ($csvReport -match 'timeDeltaPercent') 'CSV report must emit strict timing deltas.'
Assert-True ($csvReport -match 'allocationDeltaPercent') 'CSV report must emit strict allocation deltas.'
Assert-True ($csvReport -match 'baselineRepeatDriftPercent') 'CSV report must expose baseline repeat drift.'
Assert-True ($csvReport -match 'candidateRepeatDriftPercent') 'CSV report must expose candidate repeat drift.'
Assert-True ($csvReport -match 'records.Count -ne \(\$expectedMethods.Count \* 4\)') 'CSV report must reject incomplete A-B-B-A evidence.'

$dapper = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/prepare-dapper-evolution.ps1') -Raw
Assert-True ($dapper -match "scenarioClass = 'evolution'") 'SP220-10 Dapper comparison must remain evolution-classified.'
Assert-True ($dapper -match "scenario = 'dapper'") 'SP220-10 Dapper scenario id must remain canonical.'
Assert-True ($dapper -match "PrimaryPackageId 'SmartPipe.Extensions'") 'Dapper baseline must bind to SmartPipe.Extensions 2.1.2.'
Assert-True ($dapper -match "PrimaryPackageId 'SmartPipe.Extensions.Dapper'") 'Dapper candidate must bind to SmartPipe.Extensions.Dapper 2.2.0.'
Assert-True ($dapper -match 'packageSourceMapping') 'Dapper restore must use NuGet Package Source Mapping.'
Assert-True ($dapper -match 'dotnet nuget verify') 'Dapper provenance must use NuGet-native content hashes.'
Assert-True ($dapper -match "'--locked-mode'") 'Dapper restore must verify the generated lock in locked mode.'
Assert-True ($dapper -match "'--no-http-cache'") 'Dapper restore must bypass the NuGet HTTP cache.'
Assert-True ($dapper -match 'Assert-BenchmarkResult') 'Dapper Dry must reject missing BenchmarkDotNet JSON evidence.'
Assert-True ($dapper -match 'ReadHundredRows') 'Dapper Dry must require the 100-row workload.'
Assert-True ($dapper -match 'ReadSingleParameterized') 'Dapper Dry must require the parameterized workload.'
Assert-True ($dapper -match 'DapperEvolutionTarget\.cs') 'Dapper materializer must hash the target-specific evolution adapters.'

$dapperSource = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Dapper.Shared/DapperEvolutionBenchmarks.cs') -Raw
Assert-True ($dapperSource -match 'BenchmarkCategory\("Evolution", "Dapper"\)') 'Dapper benchmark must remain evolution-classified.'
Assert-True ($dapperSource -match 'SqliteOpenMode\.Memory') 'Dapper benchmark must use an in-memory SQLite database.'
Assert-True ($dapperSource -match 'SqliteCacheMode\.Shared') 'Dapper benchmark must use a named shared in-memory SQLite database.'
Assert-True ($dapperSource -match 'Pooling = false') 'Dapper benchmark must disable SQLite pooling for deterministic per-run connections.'
Assert-True ($dapperSource -match '5050') 'Dapper 100-row correctness oracle must verify the deterministic checksum.'
Assert-True ($dapperSource -match 'checksum=42') 'Dapper parameterized correctness oracle must verify the selected row.'
Assert-True ($dapperSource -match 'public readonly record struct QueryObservation') 'Dapper benchmark observation type must remain public for BenchmarkDotNet public methods.'


$dapperV212 = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Dapper.V212/DapperEvolutionTarget.cs') -Raw
$dapperV220 = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Dapper.V220/DapperEvolutionTarget.cs') -Raw
Assert-True ($dapperV212 -match 'DapperSelector') 'Dapper baseline must exercise the legacy selector path.'
Assert-True ($dapperV212 -match 'leaveOpen: false') 'Dapper baseline must own and dispose each per-run connection.'
Assert-True ($dapperV220 -match 'DapperPipelineComponents\.QuerySource') 'Dapper candidate must exercise the new query-source component.'
Assert-True ($dapperV220 -match 'PipelineDefinitionBuilder') 'Dapper candidate must compose a public pipeline definition.'
Assert-True ($dapperV220 -match 'definition\.StartAsync') 'Dapper candidate must execute through the public Core run lifecycle.'
Assert-True ($dapperV220 -match 'ReadResultsAsync') 'Dapper candidate must consume results through PipelineRun.'

$dapperV212Project = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Dapper.V212/SmartPipe.Perf.Dapper.V212.csproj') -Raw
$dapperV220Project = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Dapper.V220/SmartPipe.Perf.Dapper.V220.csproj') -Raw
Assert-True ($dapperV212Project -match 'SmartPipe.Extensions" Version="2\.1\.2"') 'Dapper baseline project must reference SmartPipe.Extensions 2.1.2.'
Assert-True ($dapperV220Project -match 'SmartPipe.Extensions.Dapper" Version="2\.2\.0"') 'Dapper candidate project must reference SmartPipe.Extensions.Dapper 2.2.0.'
Assert-True ($dapperV212Project -match 'Microsoft.Data.Sqlite" Version="10\.0\.11"') 'Dapper baseline must pin Microsoft.Data.Sqlite 10.0.11.'
Assert-True ($dapperV220Project -match 'Microsoft.Data.Sqlite" Version="10\.0\.11"') 'Dapper candidate must pin Microsoft.Data.Sqlite 10.0.11.'

$dapperRunner = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/run-dapper-evolution.ps1') -Raw
Assert-True ($dapperRunner -match "target = 'v212'[\s\S]*target = 'v220'[\s\S]*target = 'v220'[\s\S]*target = 'v212'") 'Dapper evolution order must remain counter-balanced A-B-B-A.'
Assert-True ($dapperRunner -match "scenarioClass = 'evolution'") 'Dapper runner must remain evolution-classified.'
Assert-True ($dapperRunner -match "comparisonPolicy = 'side-by-side-no-cross-version-ratio'") 'Dapper runner must forbid cross-version ratio reporting.'
Assert-True ($dapperRunner.Contains('authoritativeTiming = $false')) 'Hosted Dapper timing must remain non-authoritative.'
Assert-True ($dapperRunner -match 'PERF_DAPPER_EVOLUTION_RUN_OK') 'Dapper runner must expose the Dapper-specific completion marker.'
Assert-True ($dapperRunner -notmatch 'PERF_OPENTELEMETRY_EVOLUTION_RUN_OK') 'Dapper runner must not retain an OpenTelemetry completion marker.'
Assert-True ($dapperRunner -match 'Assert-BenchmarkResult') 'Dapper Short must reject missing BenchmarkDotNet JSON evidence.'
Assert-True ($dapperRunner -match 'produced no statistics') 'Dapper Short must reject results without statistics.'
Assert-True ($dapperRunner -match 'produced no measurements') 'Dapper Short must reject results without measurements.'

$dapperReport = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/report-dapper-evolution.ps1') -Raw
Assert-True ($dapperReport -match 'side-by-side-no-cross-version-ratio') 'Dapper report must preserve evolution comparison policy.'
Assert-True ($dapperReport -notmatch 'timeDeltaPercent') 'Dapper report must not emit a strict A/B timing percentage.'
Assert-True ($dapperReport -notmatch 'allocationDeltaPercent') 'Dapper report must not emit a strict A/B allocation percentage.'
Assert-True ($dapperReport -match 'baselineRepeatDriftPercent') 'Dapper report must expose baseline repeat drift.'
Assert-True ($dapperReport -match 'candidateRepeatDriftPercent') 'Dapper report must expose candidate repeat drift.'

$dapperDecomp = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/prepare-dapper-decomposition-v220.ps1') -Raw
Assert-True ($dapperDecomp -match "scenario = 'dapper-decomposition'") 'Dapper decomposition scenario id must remain canonical.'
Assert-True ($dapperDecomp -match "scenarioClass = 'v220-only'") 'Dapper decomposition must remain v220-only.'
Assert-True ($dapperDecomp -match 'SmartPipe.Extensions.Dapper 2.2.0') 'Dapper decomposition must bind to SmartPipe.Extensions.Dapper 2.2.0.'
Assert-True ($dapperDecomp -match 'packageSourceMapping') 'Dapper decomposition restore must use NuGet Package Source Mapping.'
Assert-True ($dapperDecomp -match 'dotnet nuget verify') 'Dapper decomposition provenance must use NuGet-native content hashes.'
Assert-True ($dapperDecomp -match "'--locked-mode'") 'Dapper decomposition restore must verify the generated lock in locked mode.'
Assert-True ($dapperDecomp -match 'Assert-BenchmarkResult') 'Dapper decomposition Dry must reject incomplete BenchmarkDotNet evidence.'
Assert-True ($dapperDecomp -match 'RawSingle') 'Dapper decomposition Dry must require RawSingle.'
Assert-True ($dapperDecomp -match 'PipelineSingle') 'Dapper decomposition Dry must require PipelineSingle.'
Assert-True ($dapperDecomp -match 'RawHundredRows') 'Dapper decomposition Dry must require RawHundredRows.'
Assert-True ($dapperDecomp -match 'PipelineHundredRows') 'Dapper decomposition Dry must require PipelineHundredRows.'

$dapperDecompSource = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Dapper.Decomposition.V220/DapperDecompositionBenchmarks.cs') -Raw
Assert-True ($dapperDecompSource -match 'BenchmarkCategory\("V220Only", "Dapper", "Decomposition"\)') 'Dapper decomposition benchmark must remain candidate-only.'
Assert-True ($dapperDecompSource -match 'ExecuteReaderAsync') 'Dapper raw path must use the same Dapper ExecuteReaderAsync primitive as the integration.'
Assert-True ($dapperDecompSource -match 'DapperPipelineComponents\.QuerySource') 'Dapper pipeline path must use the public QuerySource component.'
Assert-True ($dapperDecompSource -match 'MapRow\(reader\)') 'Dapper raw path must use the same row mapper shape.'
Assert-True ($dapperDecompSource -match 'Pooling = false') 'Dapper decomposition must disable SQLite pooling.'
Assert-True ($dapperDecompSource -match 'SqliteOpenMode\.Memory') 'Dapper decomposition must use in-memory SQLite.'
Assert-True ($dapperDecompSource -notmatch 'SmartPipe\.Extensions\.Dapper\.Internal') 'Dapper decomposition must not use internal SmartPipe APIs.'

$dapperDecompProject = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Dapper.Decomposition.V220/SmartPipe.Perf.Dapper.Decomposition.V220.csproj') -Raw
Assert-True ($dapperDecompProject -match 'Dapper" Version="2\.1\.86"') 'Dapper decomposition must pin Dapper 2.1.86.'
Assert-True ($dapperDecompProject -match 'Microsoft.Data.Sqlite" Version="10\.0\.11"') 'Dapper decomposition must pin Microsoft.Data.Sqlite 10.0.11.'
Assert-True ($dapperDecompProject -match 'SmartPipe.Extensions.Dapper" Version="2\.2\.0"') 'Dapper decomposition must pin SmartPipe.Extensions.Dapper 2.2.0.'

$dapperDecompRunner = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/run-dapper-decomposition-v220.ps1') -Raw
Assert-True ($dapperDecompRunner -match "scenarioClass = 'v220-only'") 'Dapper decomposition runner must remain v220-only.'
Assert-True ($dapperDecompRunner -match "comparisonPolicy = 'within-version-raw-vs-pipeline'") 'Dapper decomposition runner must preserve within-version policy.'
Assert-True ($dapperDecompRunner.Contains('authoritativeTiming = $false')) 'Hosted Dapper decomposition timing must remain non-authoritative.'
Assert-True ($dapperDecompRunner -match 'foreach \(\$slot in 1\.\.2\)') 'Dapper decomposition must repeat twice on the same runner.'

$dapperDecompReport = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/report-dapper-decomposition-v220.ps1') -Raw
Assert-True ($dapperDecompReport -match 'pipelineOverRawRatio') 'Dapper decomposition report must expose Pipeline/Raw ratio.'
Assert-True ($dapperDecompReport -match 'allocationDeltaBytes') 'Dapper decomposition report must expose allocation overhead.'
Assert-True ($dapperDecompReport -match 'incrementalPipelineTimePerAdditionalRowNs') 'Dapper decomposition report must expose the descriptive per-row model.'
Assert-True ($dapperDecompReport -match 'Descriptive two-point model only') 'Dapper decomposition report must label the two-point model as descriptive.'


$efCore = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/prepare-entity-framework-core-evolution.ps1') -Raw
Assert-True ($efCore -match "scenarioClass = 'evolution'") 'SP220-11 EF Core comparison must remain evolution-classified.'
Assert-True ($efCore -match "scenario = 'entity-framework-core'") 'SP220-11 EF Core scenario id must remain canonical.'
Assert-True ($efCore -match "PrimaryPackageId 'SmartPipe.Extensions'") 'EF Core baseline must bind to SmartPipe.Extensions 2.1.2.'
Assert-True ($efCore -match "PrimaryPackageId 'SmartPipe.Extensions.EntityFrameworkCore'") 'EF Core candidate must bind to SmartPipe.Extensions.EntityFrameworkCore 2.2.0.'
Assert-True ($efCore -match 'packageSourceMapping') 'EF Core restore must use NuGet Package Source Mapping.'
Assert-True ($efCore -match 'dotnet nuget verify') 'EF Core provenance must use NuGet-native content hashes.'
Assert-True ($efCore -match "'--locked-mode'") 'EF Core restore must verify the generated lock in locked mode.'
Assert-True ($efCore -match "'--no-http-cache'") 'EF Core restore must bypass the NuGet HTTP cache.'
Assert-True ($efCore -match 'Assert-BenchmarkResult') 'EF Core Dry must reject missing BenchmarkDotNet JSON evidence.'
Assert-True ($efCore -match 'ReadHundredRows') 'EF Core Dry must require the 100-row workload.'
Assert-True ($efCore -match 'ReadSingleFiltered') 'EF Core Dry must require the filtered single-row workload.'
Assert-True ($efCore -match 'EntityFrameworkCoreEvolutionTarget\.cs') 'EF Core materializer must hash target-specific adapters.'

$efCoreSource = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.EntityFrameworkCore.Shared/EntityFrameworkCoreEvolutionBenchmarks.cs') -Raw
Assert-True ($efCoreSource -match 'BenchmarkCategory\("Evolution", "EntityFrameworkCore"\)') 'EF Core benchmark must remain evolution-classified.'
Assert-True ($efCoreSource -match 'SqliteConnection') 'EF Core benchmark must use SQLite rather than the EF InMemory provider.'
Assert-True ($efCoreSource -match 'Data Source=:memory:') 'EF Core benchmark must use an in-memory SQLite database.'
Assert-True ($efCoreSource -match 'connection\.OpenAsync') 'EF Core benchmark must keep the SQLite in-memory connection explicitly open.'
Assert-True ($efCoreSource -match 'UseSqlite\(_connection\)') 'EF Core contexts must use the shared SQLite connection.'
Assert-True ($efCoreSource -match 'EnsureCreatedAsync') 'EF Core benchmark must create the relational schema before measurement.'
Assert-True ($efCoreSource -notmatch 'UseInMemoryDatabase') 'EF Core benchmark must not use the discouraged EF InMemory provider.'
Assert-True ($efCoreSource -match '5050') 'EF Core 100-row correctness oracle must verify the deterministic checksum.'
Assert-True ($efCoreSource -match 'checksum=42') 'EF Core filtered correctness oracle must verify the selected row.'
Assert-True ($efCoreSource -match 'public readonly record struct EfQueryObservation') 'EF Core benchmark observation type must remain public for BenchmarkDotNet public methods.'

$efCoreV212 = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.EntityFrameworkCore.V212/EntityFrameworkCoreEvolutionTarget.cs') -Raw
$efCoreV220 = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.EntityFrameworkCore.V220/EntityFrameworkCoreEvolutionTarget.cs') -Raw
Assert-True ($efCoreV212 -match 'EfCoreSelector') 'EF Core baseline must exercise the legacy selector path.'
Assert-True ($efCoreV212 -match 'WithTracking\(false\)') 'EF Core baseline must use no-tracking query semantics.'
Assert-True ($efCoreV220 -match 'EfCorePipelineComponents\.QuerySource') 'EF Core candidate must exercise the new query-source component.'
Assert-True ($efCoreV220 -match 'EfCoreQueryTrackingMode\.NoTracking') 'EF Core candidate must use no-tracking query semantics.'
Assert-True ($efCoreV220 -match 'definition\.StartAsync') 'EF Core candidate must execute through the public Core run lifecycle.'
Assert-True ($efCoreV220 -match 'ReadResultsAsync') 'EF Core candidate must consume results through PipelineRun.'

$efCoreV212Project = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.EntityFrameworkCore.V212/SmartPipe.Perf.EntityFrameworkCore.V212.csproj') -Raw
$efCoreV220Project = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.EntityFrameworkCore.V220/SmartPipe.Perf.EntityFrameworkCore.V220.csproj') -Raw
Assert-True ($efCoreV212Project -match 'SmartPipe.Extensions" Version="2\.1\.2"') 'EF Core baseline project must reference SmartPipe.Extensions 2.1.2.'
Assert-True ($efCoreV220Project -match 'SmartPipe.Extensions.EntityFrameworkCore" Version="2\.2\.0"') 'EF Core candidate project must reference SmartPipe.Extensions.EntityFrameworkCore 2.2.0.'
Assert-True ($efCoreV212Project -match 'Microsoft.EntityFrameworkCore.Sqlite" Version="10\.0\.11"') 'EF Core baseline must pin the SQLite provider to 10.0.11.'
Assert-True ($efCoreV220Project -match 'Microsoft.EntityFrameworkCore.Sqlite" Version="10\.0\.11"') 'EF Core candidate must pin the SQLite provider to 10.0.11.'
Assert-True ($efCoreV212Project -notmatch 'Microsoft.EntityFrameworkCore.InMemory') 'EF Core baseline must not reference the InMemory provider.'
Assert-True ($efCoreV220Project -notmatch 'Microsoft.EntityFrameworkCore.InMemory') 'EF Core candidate must not reference the InMemory provider.'

$efCoreRunner = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/run-entity-framework-core-evolution.ps1') -Raw
Assert-True ($efCoreRunner -match "target = 'v212'[\s\S]*target = 'v220'[\s\S]*target = 'v220'[\s\S]*target = 'v212'") 'EF Core evolution order must remain counter-balanced A-B-B-A.'
Assert-True ($efCoreRunner -match "scenarioClass = 'evolution'") 'EF Core runner must remain evolution-classified.'
Assert-True ($efCoreRunner -match "comparisonPolicy = 'side-by-side-no-cross-version-ratio'") 'EF Core runner must forbid cross-version ratio reporting.'
Assert-True ($efCoreRunner.Contains('authoritativeTiming = $false')) 'Hosted EF Core timing must remain non-authoritative.'
Assert-True ($efCoreRunner -match 'PERF_EFCORE_EVOLUTION_RUN_OK') 'EF Core runner must expose the EF-specific completion marker.'
Assert-True ($efCoreRunner -match 'Assert-BenchmarkResult') 'EF Core Short must reject missing BenchmarkDotNet JSON evidence.'
Assert-True ($efCoreRunner -match 'produced no statistics') 'EF Core Short must reject results without statistics.'
Assert-True ($efCoreRunner -match 'produced no measurements') 'EF Core Short must reject results without measurements.'

$efCoreReport = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/report-entity-framework-core-evolution.ps1') -Raw
Assert-True ($efCoreReport -match 'side-by-side-no-cross-version-ratio') 'EF Core report must preserve evolution comparison policy.'
Assert-True ($efCoreReport -notmatch 'timeDeltaPercent') 'EF Core report must not emit a strict A/B timing percentage.'
Assert-True ($efCoreReport -notmatch 'allocationDeltaPercent') 'EF Core report must not emit a strict A/B allocation percentage.'
Assert-True ($efCoreReport -match 'baselineRepeatDriftPercent') 'EF Core report must expose baseline repeat drift.'
Assert-True ($efCoreReport -match 'candidateRepeatDriftPercent') 'EF Core report must expose candidate repeat drift.'
Assert-True ($efCoreReport -match 'PERF_EFCORE_REPORT_OK') 'EF Core reporter must expose the EF-specific completion marker.'

$definitionModel = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/prepare-definition-model-v220.ps1') -Raw
Assert-True ($definitionModel -match "scenarioClass = 'v220-only'") 'Definition-model characterization must remain v220-only.'
Assert-True ($definitionModel -match "scenario = 'definition-model'") 'Definition-model scenario id must remain canonical.'
Assert-True ($definitionModel -match "-Target candidate") 'Definition-model materializer must request only the immutable candidate target.'
Assert-True ($definitionModel -match "primaryPackageId = 'SmartPipe.Core'") 'Definition-model provenance must bind to SmartPipe.Core.'
Assert-True ($definitionModel -match "primaryPackageVersion = '2.2.0'") 'Definition-model provenance must bind to SmartPipe.Core 2.2.0.'
Assert-True ($definitionModel -match 'packageSourceMapping') 'Definition-model restore must use NuGet Package Source Mapping.'
Assert-True ($definitionModel -match 'dotnet nuget verify') 'Definition-model provenance must use NuGet-native content hashes.'
Assert-True ($definitionModel -match "'--locked-mode'") 'Definition-model restore must verify the generated lock in locked mode.'
Assert-True ($definitionModel -match "'--no-http-cache'") 'Definition-model restore must bypass the NuGet HTTP cache.'
Assert-True ($definitionModel -match 'Assert-BenchmarkResult') 'Definition-model Dry must reject missing BenchmarkDotNet JSON evidence.'
Assert-True ($definitionModel -match 'Build_ZeroStage') 'Definition-model Dry must require zero-stage build evidence.'
Assert-True ($definitionModel -match 'Build_TenStages') 'Definition-model Dry must require ten-stage build evidence.'
Assert-True ($definitionModel -match 'BuildAndStart_ZeroStage') 'Definition-model Dry must require public cold build-and-start evidence.'
Assert-True ($definitionModel -match 'BuildAndStart_TenStages') 'Definition-model Dry must require public cold ten-stage build-and-start evidence.'
Assert-True ($definitionModel -match 'StartAndComplete_TenStages') 'Definition-model Dry must require ten-stage run evidence.'
Assert-True ($definitionModel -match 'LegacyBuilder_StartAndComplete_OneStage') 'Definition-model Dry must retain the same-version legacy compatibility characterization.'
Assert-True ($definitionModel -match 'PERF_DEFINITION_MODEL_V220_READY') 'Definition-model materializer must expose its candidate-only completion marker.'

$definitionSource = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.DefinitionModel.V220/DefinitionModelBenchmarks.cs') -Raw
Assert-True ($definitionSource -match 'BenchmarkCategory\("V220Only", "DefinitionModel", "SP220-02"\)') 'Definition-model benchmark must remain v220-only classified.'
Assert-True ($definitionSource -notmatch 'GetExecutionPlan') 'Definition-model package benchmark must not depend on internal GetExecutionPlan.'
Assert-True ($definitionSource -match 'BuildAndStart_ZeroStage') 'Definition-model benchmark must characterize public first activation.'
Assert-True ($definitionSource -match 'BuildAndStart_TenStages') 'Definition-model benchmark must characterize public first activation at ten stages.'
Assert-True ($definitionSource -match 'CreateDefinition\(10\)') 'Definition-model benchmark must preserve ten-stage scaling.'
Assert-True ($definitionSource -match 'LegacyBuilder_StartAndComplete_OneStage') 'Definition-model benchmark must preserve the same-version legacy builder characterization.'
Assert-True ($definitionSource -match 'result != 42') 'Definition-model benchmark must preserve output correctness checks.'

$definitionProject = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.DefinitionModel.V220/SmartPipe.Perf.DefinitionModel.V220.csproj') -Raw
Assert-True ($definitionProject -match 'SmartPipe.Core" Version="2\.2\.0"') 'Definition-model project must reference SmartPipe.Core 2.2.0.'
Assert-True ($definitionProject -notmatch '2\.1\.2') 'Definition-model v220-only project must not reference the baseline version.'

$definitionRunner = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/run-definition-model-v220.ps1') -Raw
Assert-True ($definitionRunner -match "scenarioClass = 'v220-only'") 'Definition-model runner must remain v220-only.'
Assert-True ($definitionRunner -match "comparisonPolicy = 'absolute-and-intraversion-scaling'") 'Definition-model runner must preserve candidate-only scaling policy.'
Assert-True ($definitionRunner -match 'foreach \(\$slot in 1\.\.2\)') 'Definition-model runner must execute two independent v2.2 repeats.'
Assert-True ($definitionRunner.Contains('authoritativeTiming = $false')) 'Hosted definition-model timing must remain non-authoritative.'
Assert-True ($definitionRunner -match 'PERF_DEFINITION_MODEL_V220_RUN_OK') 'Definition-model runner must expose its candidate-only completion marker.'
Assert-True ($definitionRunner -match 'Assert-BenchmarkResult') 'Definition-model Short must reject missing BenchmarkDotNet evidence.'
Assert-True ($definitionRunner -notmatch 'v212') 'Definition-model v220-only runner must not introduce a baseline target.'

$definitionReport = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/report-definition-model-v220.ps1') -Raw
Assert-True ($definitionReport -match 'absolute-and-intraversion-scaling') 'Definition-model report must preserve candidate-only scaling policy.'
Assert-True ($definitionReport -notmatch 'timeDeltaPercent') 'Definition-model report must not emit cross-version timing deltas.'
Assert-True ($definitionReport -notmatch 'allocationDeltaPercent') 'Definition-model report must not emit cross-version allocation deltas.'
Assert-True ($definitionReport -match 'repeatDriftPercent') 'Definition-model report must expose repeat drift.'
Assert-True ($definitionReport -match 'buildAndStartTenVsZeroTimeRatio') 'Definition-model report must expose public cold-start scaling.'
Assert-True ($definitionReport -match 'startTenVsZeroTimeRatio') 'Definition-model report must expose warm-start scaling.'
Assert-True ($definitionReport -match 'PERF_DEFINITION_MODEL_V220_REPORT_OK') 'Definition-model reporter must expose its completion marker.'













$hostingRunner = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/run-hosting-evolution.ps1') -Raw
Assert-True ($hostingRunner -match "target = 'v212'[\s\S]*target = 'v220'[\s\S]*target = 'v220'[\s\S]*target = 'v212'") 'Hosting evolution order must remain counter-balanced A-B-B-A.'
Assert-True ($hostingRunner -match "scenarioClass = 'evolution'") 'Hosting runner must classify the scenario as evolution.'
Assert-True ($hostingRunner -match "comparisonPolicy = 'side-by-side-no-cross-version-ratio'") 'Hosting runner must forbid cross-version ratio reporting.'
Assert-True ($hostingRunner.Contains('authoritativeTiming = $false')) 'Hosted Hosting timing must remain non-authoritative.'
Assert-True ($hostingRunner -match 'Assert-BenchmarkResult') 'Hosting runner must reject missing BenchmarkDotNet JSON evidence.'
Assert-True ($hostingRunner -match 'produced no statistics') 'Hosting Short must reject BenchmarkDotNet JSON records without statistics.'
Assert-True ($hostingRunner -match 'produced no measurements') 'Hosting Short must reject BenchmarkDotNet JSON records without measurements.'

$hostingReport = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/report-hosting-evolution.ps1') -Raw
Assert-True ($hostingReport -match 'side-by-side-no-cross-version-ratio') 'Hosting report must preserve evolution comparison policy.'
Assert-True ($hostingReport -notmatch 'timeDeltaPercent') 'Hosting report must not emit a strict A/B timing percentage.'
Assert-True ($hostingReport -notmatch 'allocationDeltaPercent') 'Hosting report must not emit a strict A/B allocation percentage.'
Assert-True ($hostingReport -match 'Group-Object \{ \[string\]\$_\.method \}') 'Hosting reporter must group normalized records by method value.'
Assert-True ($hostingReport -match 'baselineRepeatDriftPercent') 'Hosting report must expose baseline repeat drift.'
Assert-True ($hostingReport -match 'candidateRepeatDriftPercent') 'Hosting report must expose candidate repeat drift.'

$healthRunner = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/run-healthchecks-evolution.ps1') -Raw
Assert-True ($healthRunner -match "target = 'v212'[\s\S]*target = 'v220'[\s\S]*target = 'v220'[\s\S]*target = 'v212'") 'HealthChecks evolution order must remain counter-balanced A-B-B-A.'
Assert-True ($healthRunner -match "scenarioClass = 'evolution'") 'HealthChecks runner must classify the scenario as evolution.'
Assert-True ($healthRunner -match "comparisonPolicy = 'side-by-side-no-cross-version-ratio'") 'HealthChecks runner must forbid cross-version ratio reporting.'
Assert-True ($healthRunner.Contains('authoritativeTiming = $false')) 'Hosted HealthChecks timing must remain non-authoritative.'
Assert-True ($healthRunner -match 'Assert-BenchmarkResult') 'HealthChecks runner must reject missing BenchmarkDotNet JSON evidence.'
Assert-True ($healthRunner -match 'produced no statistics') 'HealthChecks Short must reject BenchmarkDotNet JSON records without statistics.'
Assert-True ($healthRunner -match 'produced no measurements') 'HealthChecks Short must reject BenchmarkDotNet JSON records without measurements.'

$healthReport = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/report-healthchecks-evolution.ps1') -Raw
Assert-True ($healthReport -match 'side-by-side-no-cross-version-ratio') 'HealthChecks report must preserve evolution comparison policy.'
Assert-True ($healthReport -notmatch 'timeDeltaPercent') 'HealthChecks report must not emit a strict A/B timing percentage.'
Assert-True ($healthReport -notmatch 'allocationDeltaPercent') 'HealthChecks report must not emit a strict A/B allocation percentage.'
Assert-True ($healthReport -match 'Group-Object \{ \[string\]\$_\.method \}') 'HealthChecks reporter must group normalized records by method value.'
Assert-True ($healthReport -match 'baselineRepeatDriftPercent') 'HealthChecks report must expose baseline repeat drift.'
Assert-True ($healthReport -match 'candidateRepeatDriftPercent') 'HealthChecks report must expose candidate repeat drift.'

$report = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/report-core-ab.ps1') -Raw
Assert-True ($report -match 'baselineRepeatDriftPercent') 'Core A/B report must expose baseline repeat drift.'
Assert-True ($report -match 'candidateRepeatDriftPercent') 'Core A/B report must expose candidate repeat drift.'
Assert-True ($report -match 'allocationDeltaPercent') 'Core A/B report must preserve allocation deltas.'
Assert-True ($report -match 'authoritativeTiming') 'Core A/B report must preserve timing authority metadata.'
Assert-True ($report -match 'full-compressed\.json') 'Core A/B report must normalize BenchmarkDotNet raw JSON.'

Write-Output 'PERF_SCRIPT_CONTRACT_TESTS_OK scripts=43'
