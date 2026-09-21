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
    (Join-Path $repoRoot 'eng/perf/run-core-stress.ps1')
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

$report = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/report-core-ab.ps1') -Raw
Assert-True ($report -match 'baselineRepeatDriftPercent') 'Core A/B report must expose baseline repeat drift.'
Assert-True ($report -match 'candidateRepeatDriftPercent') 'Core A/B report must expose candidate repeat drift.'
Assert-True ($report -match 'allocationDeltaPercent') 'Core A/B report must preserve allocation deltas.'
Assert-True ($report -match 'authoritativeTiming') 'Core A/B report must preserve timing authority metadata.'
Assert-True ($report -match 'full-compressed\.json') 'Core A/B report must normalize BenchmarkDotNet raw JSON.'

Write-Output 'PERF_SCRIPT_CONTRACT_TESTS_OK scripts=6'
