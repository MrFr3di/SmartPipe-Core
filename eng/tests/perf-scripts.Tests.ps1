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
    (Join-Path $repoRoot 'eng/perf/prepare-json-strict-ab.ps1'),
    (Join-Path $repoRoot 'eng/perf/run-json-strict-ab.ps1'),
    (Join-Path $repoRoot 'eng/perf/report-json-strict-ab.ps1'),
    (Join-Path $repoRoot 'eng/perf/prepare-csv-strict-ab.ps1'),
    (Join-Path $repoRoot 'eng/perf/run-csv-strict-ab.ps1'),
    (Join-Path $repoRoot 'eng/perf/report-csv-strict-ab.ps1'),
    (Join-Path $repoRoot 'eng/perf/prepare-dapper-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/run-dapper-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/report-dapper-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/prepare-entity-framework-core-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/run-entity-framework-core-evolution.ps1'),
    (Join-Path $repoRoot 'eng/perf/report-entity-framework-core-evolution.ps1')
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
Assert-True ($stressRunner -match "scenarioClass = 'strict-ab'") 'Core stress must classify its comparison as strict-ab.'
Assert-True ($stressRunner.Contains('authoritativeTiming = $false')) 'Hosted stress elapsed time must remain non-authoritative.'
Assert-True ($stressRunner -match 'contentHash differs from verified Core A/B provenance') 'Stress restore must bind to verified Core A/B package content.'
Assert-True (($stressRunner | Select-String -Pattern 'Tee-Object -FilePath \$restoreLog.*Out-Host' -AllMatches).Matches.Count -ge 2) 'Stress target preparation must consume restore output so the function returns only its typed descriptor.'
Assert-True ($stressRunner -match 'Tee-Object -FilePath \$buildLog \| Out-Host') 'Stress target preparation must consume build output so the function returns only its typed descriptor.'
$stressSource = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Stress.Shared/Program.cs') -Raw
Assert-True ($stressSource -notmatch 'Task\.Delay') 'Deterministic Core stress must not rely on Task.Delay.'
Assert-True ($stressSource -match 'ExpectedChecksum') 'Deterministic Core stress must enforce a checksum oracle.'
Assert-True ($stressSource -match 'ExpectedItems') 'Deterministic Core stress must enforce an item-count oracle.'

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

Write-Output 'PERF_SCRIPT_CONTRACT_TESTS_OK scripts=33'
