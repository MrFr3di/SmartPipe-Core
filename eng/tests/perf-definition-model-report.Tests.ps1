param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$methods = @(
    'Build_ZeroStage',
    'Build_OneStage',
    'Build_TenStages',
    'BuildAndStart_ZeroStage',
    'BuildAndStart_OneStage',
    'BuildAndStart_TenStages',
    'StartAndComplete_ZeroStage',
    'StartAndComplete_OneStage',
    'StartAndComplete_TenStages',
    'LegacyBuilder_StartAndComplete_OneStage'
)

function Write-SyntheticResult {
    param(
        [string]$Root,
        [string]$Slot,
        [double]$BaseMean,
        [double]$BaseAllocated
    )

    $results = Join-Path $Root "$Slot/results"
    New-Item -ItemType Directory -Path $results -Force | Out-Null

    $benchmarks = [Collections.Generic.List[object]]::new()

    for ($index = 0; $index -lt $methods.Count; $index++) {
        $scale = $index + 1

        $benchmarks.Add([ordered]@{
            DisplayInfo = "Synthetic definition-model $($methods[$index])"
            Namespace = 'SmartPipe.Perf.DefinitionModel'
            Type = 'DefinitionModelBenchmarks'
            Method = $methods[$index]
            MethodTitle = $methods[$index]
            Parameters = ''
            FullName = "Synthetic definition-model $($methods[$index])"
            HardwareIntrinsics = 'synthetic'
            Statistics = [ordered]@{
                N = 3
                Mean = ($BaseMean * $scale)
                Median = ($BaseMean * $scale)
                StandardDeviation = 5.0
            }
            Memory = [ordered]@{
                BytesAllocatedPerOperation = ($BaseAllocated * $scale)
            }
        })
    }

    [ordered]@{
        Title = 'Synthetic definition-model'
        HostEnvironmentInfo = [ordered]@{}
        Benchmarks = @($benchmarks)
    } | ConvertTo-Json -Depth 32 |
        Set-Content -LiteralPath (Join-Path $results 'Synthetic-report-full-compressed.json') -Encoding utf8
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$report = Join-Path $repoRoot 'eng/perf/report-definition-model-v220.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("smartpipe-perf-definition-report-" + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    [ordered]@{
        schemaVersion = 1
        runId = 'synthetic-definition-report-test'
        scenario = 'definition-model'
        scenarioClass = 'v220-only'
        comparisonPolicy = 'absolute-and-intraversion-scaling'
        authoritativeTiming = $false
        candidateSha = '61ceef6bf69aef0a4f79b25384352d238979200f'
        harnessSha = '0000000000000000000000000000000000000000'
    } | ConvertTo-Json -Depth 16 |
        Set-Content -LiteralPath (Join-Path $tempRoot 'run-manifest.json') -Encoding utf8

    Write-SyntheticResult -Root $tempRoot -Slot '01-v220' -BaseMean 100.0 -BaseAllocated 10.0
    Write-SyntheticResult -Root $tempRoot -Slot '02-v220' -BaseMean 120.0 -BaseAllocated 12.0

    $output = @(& $report -RunRoot $tempRoot)
    Assert-True ($output -contains 'PERF_DEFINITION_MODEL_V220_REPORT_OK run=synthetic-definition-report-test methods=10') 'Definition-model report did not complete successfully.'

    $normalizedPath = Join-Path $tempRoot 'normalized-results.json'
    $markdownPath = Join-Path $tempRoot 'comparison.md'
    Assert-True (Test-Path -LiteralPath $normalizedPath -PathType Leaf) 'Definition normalized-results.json was not created.'
    Assert-True (Test-Path -LiteralPath $markdownPath -PathType Leaf) 'Definition comparison.md was not created.'

    $normalized = Get-Content -LiteralPath $normalizedPath -Raw | ConvertFrom-Json -Depth 64
    Assert-True ([string]$normalized.scenarioClass -ceq 'v220-only') 'Definition normalized class must remain v220-only.'
    Assert-True ([string]$normalized.comparisonPolicy -ceq 'absolute-and-intraversion-scaling') 'Definition comparison policy drifted.'
    Assert-True (@($normalized.records).Count -eq 20) 'Expected 20 normalized definition records.'
    Assert-True (@($normalized.summaries).Count -eq 10) 'Expected ten definition summaries.'

    $buildZero = @($normalized.summaries | Where-Object method -eq 'Build_ZeroStage')[0]
    Assert-True ([Math]::Abs([double]$buildZero.meanNs - 110.0) -lt 0.001) 'Definition zero-stage center is incorrect.'
    Assert-True ([Math]::Abs([double]$buildZero.allocatedBytes - 11.0) -lt 0.001) 'Definition zero-stage allocation center is incorrect.'
    Assert-True ([Math]::Abs([double]$buildZero.repeatDriftPercent - 18.1818181818) -lt 0.001) 'Definition repeat drift is incorrect.'

    Assert-True ([Math]::Abs([double]$normalized.scaling.buildOneVsZeroTimeRatio - 2.0) -lt 0.001) 'Definition build 1/0 scaling is incorrect.'
    Assert-True ([Math]::Abs([double]$normalized.scaling.buildTenVsZeroTimeRatio - 3.0) -lt 0.001) 'Definition build 10/0 scaling is incorrect.'
    Assert-True ([Math]::Abs([double]$normalized.scaling.buildAndStartOneVsZeroTimeRatio - 1.25) -lt 0.001) 'Definition build-and-start 1/0 scaling is incorrect.'
    Assert-True ([Math]::Abs([double]$normalized.scaling.buildAndStartTenVsZeroTimeRatio - 1.5) -lt 0.001) 'Definition build-and-start 10/0 scaling is incorrect.'
    Assert-True ([Math]::Abs([double]$normalized.scaling.startOneVsZeroTimeRatio - 1.1428571429) -lt 0.001) 'Definition warm start 1/0 scaling is incorrect.'
    Assert-True ([Math]::Abs([double]$normalized.scaling.startTenVsZeroTimeRatio - 1.2857142857) -lt 0.001) 'Definition warm start 10/0 scaling is incorrect.'

    Assert-True ($null -eq $buildZero.PSObject.Properties['timeDeltaPercent']) 'Definition v220-only output must not contain cross-version timing delta.'
    Assert-True ($null -eq $buildZero.PSObject.Properties['allocationDeltaPercent']) 'Definition v220-only output must not contain cross-version allocation delta.'

    $markdown = Get-Content -LiteralPath $markdownPath -Raw
    Assert-True ($markdown.Contains('There is no 2.1.2 baseline')) 'Definition Markdown must state that no baseline exists.'
    Assert-True ($markdown.Contains('buildTenVsZeroTimeRatio')) 'Definition Markdown must render intra-version scaling.'
    Assert-True (-not $markdown.Contains('Time delta')) 'Definition Markdown must not render cross-version delta.'

    Write-Output 'PERF_DEFINITION_REPORT_TESTS_OK methods=11'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
