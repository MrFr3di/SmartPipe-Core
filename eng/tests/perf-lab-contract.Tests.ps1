param()

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$validatorSource = Join-Path $repoRoot 'eng/perf/validate-perf-lab.ps1'
$manifestSource = Join-Path $repoRoot 'perf/manifests'
$workflowPath = Join-Path $repoRoot '.github/workflows/perf-lab.yml'

function New-Fixture {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('smartpipe-perf-contract-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path $root 'eng/perf') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'eng/baselines/2.1.2') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'perf/manifests') -Force | Out-Null

    Copy-Item -LiteralPath $validatorSource -Destination (Join-Path $root 'eng/perf/validate-perf-lab.ps1')
    Copy-Item -Path (Join-Path $manifestSource '*') -Destination (Join-Path $root 'perf/manifests')
    Set-Content -LiteralPath (Join-Path $root 'eng/baselines/2.1.2/manifest.json') -Value '{}' -NoNewline

    return $root
}

function Invoke-FixtureValidator {
    param([string]$Root, [string]$Mode = 'smoke', [string]$CandidateSha = '')

    & (Join-Path $Root 'eng/perf/validate-perf-lab.ps1') -Mode $Mode -CandidateSha $CandidateSha | Out-Null
}

function Assert-MutationFails {
    param(
        [scriptblock]$Mutation,
        [string]$Name
    )

    $root = New-Fixture
    try {
        & $Mutation $root
        $failed = $false
        try {
            Invoke-FixtureValidator -Root $root
        }
        catch {
            $failed = $true
        }

        Assert-True $failed "Mutation '$Name' unexpectedly passed validation."
    }
    finally {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Assert-True (Test-Path -LiteralPath $validatorSource -PathType Leaf) 'Perf lab validator is missing.'
Assert-True (Test-Path -LiteralPath $workflowPath -PathType Leaf) 'Perf lab workflow is missing.'

$validRoot = New-Fixture
try {
    Invoke-FixtureValidator -Root $validRoot
}
finally {
    Remove-Item -LiteralPath $validRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Assert-MutationFails -Name 'baseline-sha' -Mutation {
    param($root)
    $path = Join-Path $root 'perf/manifests/targets.json'
    $doc = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 32
    $doc.baseline.gitSha = '0000000000000000000000000000000000000000'
    $doc | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $path
}

Assert-MutationFails -Name 'missing-sp220-12' -Mutation {
    param($root)
    $path = Join-Path $root 'perf/manifests/targets.json'
    $doc = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 32
    $doc.fullCandidate.requiredEpics = @($doc.fullCandidate.requiredEpics | Where-Object { $_ -cne 'SP220-12' })
    $doc | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $path
}

Assert-MutationFails -Name 'duplicate-scenario-id' -Mutation {
    param($root)
    $path = Join-Path $root 'perf/manifests/scenarios.json'
    $doc = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 32
    $doc.groups[1].id = $doc.groups[0].id
    $doc | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $path
}

Assert-MutationFails -Name 'premature-wall-clock-gate' -Mutation {
    param($root)
    $path = Join-Path $root 'perf/manifests/thresholds.json'
    $doc = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 32
    $doc.hardGates.wallClockRegression = $true
    $doc | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $path
}

Assert-MutationFails -Name 'pinned-without-sha' -Mutation {
    param($root)
    $path = Join-Path $root 'perf/manifests/targets.json'
    $doc = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 32
    $doc.fullCandidate.status = 'pinned'
    $doc | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $path
}

$invalidCandidateRoot = New-Fixture
try {
    $failed = $false
    try {
        Invoke-FixtureValidator -Root $invalidCandidateRoot -CandidateSha 'not-a-sha'
    }
    catch {
        $failed = $true
    }
    Assert-True $failed 'Invalid candidate SHA unexpectedly passed validation.'
}
finally {
    Remove-Item -LiteralPath $invalidCandidateRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$workflow = Get-Content -LiteralPath $workflowPath -Raw
Assert-True ($workflow -match '(?m)^permissions:\s*\r?\n\s+contents:\s+read\s*$') 'Workflow must keep contents: read permissions.'
Assert-True ($workflow -match '(?m)^\s*runs-on:\s+ubuntu-24\.04\s*$') 'Workflow must pin the Ubuntu 24.04 image family.'
Assert-True ($workflow -match '(?m)^\s*cache:\s+true\s*$') 'Workflow must enable setup-dotnet NuGet cache.'
Assert-True ($workflow -match "cache-dependency-path:\s*'\*\*/packages\.lock\.json'") 'Workflow must key cache from lock files.'
Assert-True ($workflow -notmatch '(?i)pull_request_target') 'Workflow must not use pull_request_target.'
Assert-True ($workflow -notmatch '(?i)self-hosted') 'Workflow must not use a persistent self-hosted runner.'
Assert-True ($workflow -match '(?m)^\s*retention-days:\s+3\s*$') 'Manual smoke artifact retention must remain short.'

$actionRefs = [regex]::Matches($workflow, 'uses:\s+([^\s@]+)@([^\s#]+)')
Assert-True ($actionRefs.Count -ge 3) 'Expected pinned checkout/setup-dotnet/upload-artifact actions.'
foreach ($match in $actionRefs) {
    $ref = $match.Groups[2].Value
    Assert-True ($ref -cmatch '^[0-9a-f]{40}$') "Action '$($match.Groups[1].Value)' must be pinned to a 40-character commit SHA."
}

Write-Output 'PERF_LAB_CONTRACT_TESTS_OK cases=13'
