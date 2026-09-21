param(
    [ValidateSet('smoke', 'benchmark-smoke', 'stress', 'soak')]
    [string]$Mode = 'smoke',

    [string]$CandidateSha = ''
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Assert-ShaOrNull {
    param($Value, [string]$Name)
    if ($null -eq $Value) {
        return
    }

    Assert-True ($Value -is [string]) "$Name must be a string or null."
    Assert-True ($Value -cmatch '^[0-9a-f]{40}$') "$Name must be exactly 40 lowercase hexadecimal characters."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$targetsPath = Join-Path $repoRoot 'perf/manifests/targets.json'
$scenariosPath = Join-Path $repoRoot 'perf/manifests/scenarios.json'

Assert-True (Test-Path -LiteralPath $targetsPath -PathType Leaf) 'perf/manifests/targets.json is missing.'
Assert-True (Test-Path -LiteralPath $scenariosPath -PathType Leaf) 'perf/manifests/scenarios.json is missing.'

$targets = Get-Content -LiteralPath $targetsPath -Raw | ConvertFrom-Json -Depth 32
$scenarios = Get-Content -LiteralPath $scenariosPath -Raw | ConvertFrom-Json -Depth 32

Assert-True ($targets.schemaVersion -eq 1) 'Unsupported targets manifest schemaVersion.'
Assert-True ($scenarios.schemaVersion -eq 1) 'Unsupported scenarios manifest schemaVersion.'

Assert-True ($targets.baseline.version -ceq '2.1.2') 'Baseline version must remain 2.1.2.'
Assert-True ($targets.baseline.gitSha -ceq '8e79902d22de714f493582946f7c260462b0895e') 'Baseline Git SHA drifted from the published 2.1.2 baseline.'
Assert-True ($targets.baseline.manifest -ceq 'eng/baselines/2.1.2/manifest.json') 'Baseline manifest path drifted.'
Assert-True (Test-Path -LiteralPath (Join-Path $repoRoot $targets.baseline.manifest) -PathType Leaf) 'Tracked 2.1.2 baseline manifest is missing.'

Assert-ShaOrNull $targets.integrationAtLabBootstrap.gitSha 'integrationAtLabBootstrap.gitSha'
Assert-ShaOrNull $targets.fullCandidate.gitSha 'fullCandidate.gitSha'

$expectedEpics = 1..12 | ForEach-Object { 'SP220-{0:D2}' -f $_ }
$requiredEpics = @($targets.fullCandidate.requiredEpics)
Assert-True ($requiredEpics.Count -eq 12) 'fullCandidate.requiredEpics must contain exactly SP220-01 through SP220-12.'
for ($index = 0; $index -lt $expectedEpics.Count; $index++) {
    Assert-True ($requiredEpics[$index] -ceq $expectedEpics[$index]) 'fullCandidate.requiredEpics is not the canonical SP220-01 through SP220-12 sequence.'
}

if ($null -eq $targets.fullCandidate.gitSha) {
    Assert-True ($targets.fullCandidate.status -ceq 'blocked-on-sp220-12') 'An unpinned full candidate must remain blocked-on-sp220-12.'
}
else {
    Assert-True ($targets.fullCandidate.status -ceq 'pinned') 'A full candidate SHA may be present only when status is pinned.'
}

$allowedClasses = @('strict-ab', 'evolution', 'v220-only')
$scenarioIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($group in @($scenarios.groups)) {
    Assert-True (-not [string]::IsNullOrWhiteSpace($group.id)) 'Scenario group id must be non-empty.'
    Assert-True ($scenarioIds.Add([string]$group.id)) "Duplicate scenario group id '$($group.id)'."
    Assert-True ($allowedClasses -ccontains [string]$group.defaultClass) "Scenario '$($group.id)' has an invalid defaultClass."
    Assert-True ([int]$group.priority -ge 1 -and [int]$group.priority -le 3) "Scenario '$($group.id)' priority must be 1..3."
    foreach ($epic in @($group.epics)) {
        Assert-True ($expectedEpics -ccontains [string]$epic) "Scenario '$($group.id)' references unknown epic '$epic'."
    }
}

if (-not [string]::IsNullOrWhiteSpace($CandidateSha)) {
    Assert-True ($CandidateSha -cmatch '^[0-9a-f]{40}$') 'candidate-sha must be exactly 40 lowercase hexadecimal characters.'
}

if ($Mode -in @('stress', 'soak')) {
    $hasExplicitCandidate = -not [string]::IsNullOrWhiteSpace($CandidateSha)
    $hasPinnedFullCandidate = $null -ne $targets.fullCandidate.gitSha
    Assert-True ($hasExplicitCandidate -or $hasPinnedFullCandidate) "$Mode requires an explicit candidate SHA until the full SP220-01 through SP220-12 candidate is pinned."
}

$fullCandidateLabel = if ($null -eq $targets.fullCandidate.gitSha) { 'UNPINNED' } else { $targets.fullCandidate.gitSha }
Write-Output "PERF_LAB_CONTRACT_OK mode=$Mode groups=$($scenarios.groups.Count) baseline=$($targets.baseline.gitSha) fullCandidate=$fullCandidateLabel"
