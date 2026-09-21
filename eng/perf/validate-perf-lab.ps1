param(
    [ValidateSet('smoke', 'benchmark-smoke', 'stress', 'soak')]
    [string]$Mode = 'smoke',

    [string]$CandidateSha = ''
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Sha {
    param([string]$Value, [string]$Name)
    Assert-True (-not [string]::IsNullOrWhiteSpace($Value)) "$Name must be present."
    Assert-True ($Value -cmatch '^[0-9a-f]{40}$') "$Name must be exactly 40 lowercase hexadecimal characters."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$targetsPath = Join-Path $repoRoot 'perf/manifests/targets.json'
$scenariosPath = Join-Path $repoRoot 'perf/manifests/scenarios.json'
$thresholdsPath = Join-Path $repoRoot 'perf/manifests/thresholds.json'

foreach ($path in @($targetsPath, $scenariosPath, $thresholdsPath)) {
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Required manifest is missing: $path"
}
foreach ($schema in @('targets.schema.json', 'scenarios.schema.json', 'thresholds.schema.json')) {
    Assert-True (Test-Path -LiteralPath (Join-Path $repoRoot "perf/manifests/$schema") -PathType Leaf) "perf/manifests/$schema is missing."
}

$targets = Get-Content -LiteralPath $targetsPath -Raw | ConvertFrom-Json -Depth 32
$scenarios = Get-Content -LiteralPath $scenariosPath -Raw | ConvertFrom-Json -Depth 32
$thresholds = Get-Content -LiteralPath $thresholdsPath -Raw | ConvertFrom-Json -Depth 32

Assert-True ($targets.schemaVersion -eq 1) 'Unsupported targets manifest schemaVersion.'
Assert-True ($scenarios.schemaVersion -eq 1) 'Unsupported scenarios manifest schemaVersion.'
Assert-True ($thresholds.schemaVersion -eq 1) 'Unsupported thresholds manifest schemaVersion.'

Assert-True ($targets.baseline.version -ceq '2.1.2') 'Baseline version must remain 2.1.2.'
Assert-True ($targets.baseline.gitSha -ceq '8e79902d22de714f493582946f7c260462b0895e') 'Baseline Git SHA drifted from the published 2.1.2 baseline.'
Assert-True ($targets.baseline.manifest -ceq 'eng/baselines/2.1.2/manifest.json') 'Baseline manifest path drifted.'
Assert-True (Test-Path -LiteralPath (Join-Path $repoRoot $targets.baseline.manifest) -PathType Leaf) 'Tracked 2.1.2 baseline manifest is missing.'

Assert-True ($targets.candidate.id -ceq 'smartpipe-2.2.0-sp220-01-11') 'Candidate id must describe SP220-01 through SP220-11.'
Assert-True ($targets.candidate.version -ceq '2.2.0') 'Candidate version must remain 2.2.0.'
Assert-True ($targets.candidate.status -ceq 'pinned') 'Candidate must be pinned.'
Assert-Sha ([string]$targets.candidate.gitSha) 'candidate.gitSha'

$expectedEpics = 1..11 | ForEach-Object { 'SP220-{0:D2}' -f $_ }
$includedEpics = @($targets.candidate.includedEpics)
Assert-True ($includedEpics.Count -eq 11) 'candidate.includedEpics must contain exactly SP220-01 through SP220-11.'
for ($index = 0; $index -lt $expectedEpics.Count; $index++) {
    Assert-True ($includedEpics[$index] -ceq $expectedEpics[$index]) 'candidate.includedEpics is not the canonical SP220-01 through SP220-11 sequence.'
}
Assert-True (@($targets.candidate.excludedEpics).Count -eq 1 -and $targets.candidate.excludedEpics[0] -ceq 'SP220-12') 'SP220-12 must remain explicitly excluded from this lab scope.'

$allowedClasses = @('strict-ab', 'evolution', 'v220-only')
$scenarioIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($group in @($scenarios.groups)) {
    Assert-True (-not [string]::IsNullOrWhiteSpace($group.id)) 'Scenario group id must be non-empty.'
    Assert-True ($scenarioIds.Add([string]$group.id)) "Duplicate scenario group id '$($group.id)'."
    Assert-True ($allowedClasses -ccontains [string]$group.defaultClass) "Scenario '$($group.id)' has an invalid defaultClass."
    Assert-True ([int]$group.priority -ge 1 -and [int]$group.priority -le 3) "Scenario '$($group.id)' priority must be 1..3."
    foreach ($epic in @($group.epics)) {
        Assert-True ($expectedEpics -ccontains [string]$epic) "Scenario '$($group.id)' references out-of-scope epic '$epic'."
    }
}

Assert-True ($thresholds.phase -ceq 'evidence-only') 'Bootstrap regression policy must remain evidence-only.'
Assert-True ([bool]$thresholds.hardGates.correctness) 'Correctness must remain a hard gate.'
Assert-True ([bool]$thresholds.hardGates.resourceLifecycle) 'Resource lifecycle must remain a hard gate.'
Assert-True ([bool]$thresholds.hardGates.boundedMemoryContract) 'Bounded-memory contracts must remain hard gates.'
Assert-True (-not [bool]$thresholds.hardGates.wallClockRegression) 'Wall-clock regression cannot become a hard gate during evidence-only phase.'
Assert-True (-not [bool]$thresholds.hardGates.throughputRegression) 'Throughput regression cannot become a hard gate during evidence-only phase.'
Assert-True ($thresholds.policy.githubHostedTiming -ceq 'informational-only') 'GitHub-hosted timing must remain informational-only.'

if (-not [string]::IsNullOrWhiteSpace($CandidateSha)) {
    Assert-Sha $CandidateSha 'candidate-sha'
    Assert-True ($CandidateSha -ceq $targets.candidate.gitSha) 'Explicit candidate-sha must match the pinned candidate SHA.'
}

Write-Output "PERF_LAB_CONTRACT_OK mode=$Mode groups=$($scenarios.groups.Count) baseline=$($targets.baseline.gitSha) candidate=$($targets.candidate.gitSha)"
