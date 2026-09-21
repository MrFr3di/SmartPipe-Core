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
    (Join-Path $repoRoot 'eng/perf/prepare-extensions-strict-ab.ps1')
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

Write-Output 'PERF_SCRIPT_CONTRACT_TESTS_OK scripts=19'
