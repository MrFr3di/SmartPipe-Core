param()

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$scripts = @(
    (Join-Path $repoRoot 'eng/perf/validate-perf-lab.ps1'),
    (Join-Path $repoRoot 'eng/perf/prepare-target.ps1')
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

Write-Output 'PERF_SCRIPT_CONTRACT_TESTS_OK scripts=2'
