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
    (Join-Path $repoRoot 'eng/perf/run-core-ab.ps1')
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
Assert-True ($materializer -match 'Get-FileHash.+SHA256') 'Target package provenance must include SHA-256 hashes.'
Assert-True ($materializer -match '61ceef6bf69aef0a4f79b25384352d238979200f') 'Materializer default candidate SHA must remain pinned.'
Assert-True ($materializer -notmatch '(?m)git\s+-C\s+\$repoRoot\s+(checkout|reset)') 'Materializer must not checkout/reset the harness working tree.'

$coreAb = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/prepare-core-ab.ps1') -Raw
Assert-True ($coreAb -match "'--use-lock-file'") 'Core A/B restore must generate a lock file.'
Assert-True ($coreAb -match "'--locked-mode'") 'Core A/B restore must verify the generated lock in locked mode.'
Assert-True ($coreAb -match 'contentHash') 'Core A/B restore must verify NuGet package content hash.'
Assert-True ($coreAb -match 'sharedSourceSha256') 'Core A/B provenance must record the shared workload source hash.'

$runner = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/perf/run-core-ab.ps1') -Raw
Assert-True ($runner -match "target = 'v212'[\s\S]*target = 'v220'[\s\S]*target = 'v220'[\s\S]*target = 'v212'") 'Core A/B order must remain counter-balanced A-B-B-A.'
Assert-True ($runner -match "scenarioClass = 'strict-ab'") 'Core A/B run manifest must classify the scenario as strict-ab.'
Assert-True ($runner -match "authoritativeTiming = \$false") 'GitHub-compatible Core A/B timing must default to non-authoritative.'

Write-Output 'PERF_SCRIPT_CONTRACT_TESTS_OK scripts=4'
