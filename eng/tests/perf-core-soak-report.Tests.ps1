param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Write-TargetEvidence {
    param(
        [string]$Root,
        [string]$DirectoryName,
        [string]$Profile,
        [double]$ManagedSlopePerSecond,
        [double]$HeapSlopePerSecond,
        [double]$WorkingSlopePerSecond,
        [double]$HandleSlopePerSecond
    )

    $directory = Join-Path $Root $DirectoryName
    New-Item -ItemType Directory -Path $directory -Force | Out-Null

    $lines = [Collections.Generic.List[string]]::new()

    foreach ($elapsed in @(0.0, 10.0, 20.0, 30.0, 40.0)) {
        $snapshot = [ordered]@{
            Kind = 'snapshot'
            SchemaVersion = 1
            Profile = $Profile
            ElapsedSeconds = $elapsed
            CompletedRuns = [long](100 + $elapsed)
            Errors = 0
            ActiveRuns = 0
            CreatedComponents = [long](300 + $elapsed * 3)
            DisposedComponents = [long](300 + $elapsed * 3)
            ManagedMemoryBytes = [long](1000 + $ManagedSlopePerSecond * $elapsed)
            GcHeapSizeBytes = [long](2000 + $HeapSlopePerSecond * $elapsed)
            GcFragmentedBytes = 100
            TotalAllocatedBytes = [long](5000 + 1000 * $elapsed)
            Gen0Collections = [int]($elapsed / 10)
            Gen1Collections = [int]($elapsed / 20)
            Gen2Collections = [int]($elapsed / 40)
            WorkingSetBytes = [long](3000 + $WorkingSlopePerSecond * $elapsed)
            CpuTimeMilliseconds = 100 + $elapsed
            ThreadPoolThreadCount = 4
            ThreadPoolPendingWorkItems = 0
            HandleOrFdCount = [int](20 + $HandleSlopePerSecond * $elapsed)
        }

        $lines.Add(($snapshot | ConvertTo-Json -Compress -Depth 16))
    }

    $final = [ordered]@{
        Kind = 'final'
        SchemaVersion = 1
        Profile = $Profile
        DurationSeconds = 40.0
        SnapshotIntervalSeconds = 10.0
        ItemsPerRun = 250
        ConcurrentRunsPerBatch = 16
        CompletedRuns = 140
        Errors = 0
        ActiveRuns = 0
        CreatedComponents = 420
        DisposedComponents = 420
        SnapshotCount = 5
        LifecycleInvariantPassed = $true
    }

    $lines.Add(($final | ConvertTo-Json -Compress -Depth 16))
    $lines | Set-Content -LiteralPath (Join-Path $directory 'stdout.jsonl') -Encoding utf8
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$report = Join-Path $repoRoot 'eng/perf/report-core-soak.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("smartpipe-perf-soak-report-" + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    [ordered]@{
        schemaVersion = 1
        runId = 'synthetic-core-soak-test'
        scenario = 'soak-leak'
        scenarioClass = 'evolution'
        comparisonPolicy = 'side-by-side-no-cross-version-ratio'
        profile = 'verify'
        authoritativeTiming = $false
        baselineSha = '8e79902d22de714f493582946f7c260462b0895e'
        candidateSha = '61ceef6bf69aef0a4f79b25384352d238979200f'
        harnessSha = '0000000000000000000000000000000000000000'
    } | ConvertTo-Json -Depth 16 |
        Set-Content -LiteralPath (Join-Path $tempRoot 'run-manifest.json') -Encoding utf8

    Write-TargetEvidence -Root $tempRoot -DirectoryName '01-v212' -Profile 'verify' -ManagedSlopePerSecond 10 -HeapSlopePerSecond 20 -WorkingSlopePerSecond 30 -HandleSlopePerSecond 0.1
    Write-TargetEvidence -Root $tempRoot -DirectoryName '02-v220' -Profile 'verify' -ManagedSlopePerSecond 5 -HeapSlopePerSecond 0 -WorkingSlopePerSecond -10 -HandleSlopePerSecond 0

    $output = @(& $report -RunRoot $tempRoot)
    Assert-True ($output -contains 'PERF_CORE_SOAK_REPORT_OK run=synthetic-core-soak-test targets=2 profile=verify') 'Core soak report did not complete successfully.'

    $normalizedPath = Join-Path $tempRoot 'normalized-results.json'
    $markdownPath = Join-Path $tempRoot 'comparison.md'

    Assert-True (Test-Path -LiteralPath $normalizedPath -PathType Leaf) 'Core soak normalized-results.json was not created.'
    Assert-True (Test-Path -LiteralPath $markdownPath -PathType Leaf) 'Core soak comparison.md was not created.'

    $normalized = Get-Content -LiteralPath $normalizedPath -Raw | ConvertFrom-Json -Depth 64
    Assert-True ([string]$normalized.scenarioClass -ceq 'evolution') 'Core soak class must remain evolution.'
    Assert-True ([string]$normalized.comparisonPolicy -ceq 'side-by-side-no-cross-version-ratio') 'Core soak comparison policy drifted.'
    Assert-True (@($normalized.targets).Count -eq 2) 'Core soak must contain two target summaries.'

    $baseline = @($normalized.targets | Where-Object target -eq 'v212')[0]
    $candidate = @($normalized.targets | Where-Object target -eq 'v220')[0]

    Assert-True ([Math]::Abs([double]$baseline.managedMemorySlopeBytesPerMinute - 600.0) -lt 0.001) 'Baseline managed-memory slope is incorrect.'
    Assert-True ([Math]::Abs([double]$baseline.gcHeapSlopeBytesPerMinute - 1200.0) -lt 0.001) 'Baseline GC-heap slope is incorrect.'
    Assert-True ([Math]::Abs([double]$baseline.workingSetSlopeBytesPerMinute - 1800.0) -lt 0.001) 'Baseline working-set slope is incorrect.'
    Assert-True ([Math]::Abs([double]$baseline.handleOrFdSlopePerMinute - 6.0) -lt 0.001) 'Baseline handle/fd slope is incorrect.'

    Assert-True ([Math]::Abs([double]$candidate.managedMemorySlopeBytesPerMinute - 300.0) -lt 0.001) 'Candidate managed-memory slope is incorrect.'
    Assert-True ([Math]::Abs([double]$candidate.gcHeapSlopeBytesPerMinute) -lt 0.001) 'Candidate GC-heap slope is incorrect.'
    Assert-True ([Math]::Abs([double]$candidate.workingSetSlopeBytesPerMinute + 600.0) -lt 0.001) 'Candidate working-set slope is incorrect.'

    Assert-True ($null -eq $baseline.PSObject.Properties['timeDeltaPercent']) 'Core soak output must not contain a timing percentage.'
    Assert-True ($null -eq $baseline.PSObject.Properties['memoryDeltaPercent']) 'Core soak output must not contain a memory-regression percentage.'

    $markdown = Get-Content -LiteralPath $markdownPath -Raw
    Assert-True ($markdown.Contains('Memory slopes are evidence-only')) 'Core soak Markdown must state evidence-only memory policy.'
    Assert-True ($markdown.Contains('Validation-only profile')) 'Core soak verify report must label short slopes as validation-only.'
    Assert-True (-not $markdown.Contains('Time delta')) 'Core soak Markdown must not render a timing delta.'

    Write-Output 'PERF_CORE_SOAK_REPORT_TESTS_OK targets=2'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
