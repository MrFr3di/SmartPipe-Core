param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Write-SyntheticResult {
    param(
        [string]$Root,
        [string]$Slot,
        [double]$Mean,
        [double]$Allocated
    )

    $results = Join-Path $Root "$Slot/results"
    New-Item -ItemType Directory -Path $results -Force | Out-Null

    $document = [ordered]@{
        Title = 'Synthetic'
        HostEnvironmentInfo = [ordered]@{}
        Benchmarks = @(
            [ordered]@{
                DisplayInfo = 'Synthetic'
                Namespace = 'SmartPipe.Perf.Benchmarks'
                Type = 'CorePipelineAbBenchmarks'
                Method = 'SourceTransformSink'
                MethodTitle = 'SourceTransformSink'
                Parameters = 'ItemCount=1000&MaxConcurrency=1'
                FullName = 'Synthetic'
                HardwareIntrinsics = 'synthetic'
                Statistics = [ordered]@{
                    N = 3
                    Mean = $Mean
                    Median = $Mean
                    StandardDeviation = 10.0
                }
                Memory = [ordered]@{
                    BytesAllocatedPerOperation = $Allocated
                }
            }
        )
    }

    $path = Join-Path $results 'Synthetic-report-full-compressed.json'
    $document | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $path -Encoding utf8
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$report = Join-Path $repoRoot 'eng/perf/report-core-ab.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("smartpipe-perf-report-" + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    $manifest = [ordered]@{
        schemaVersion = 1
        runId = 'synthetic-report-test'
        scenario = 'core-runtime'
        scenarioClass = 'strict-ab'
        authoritativeTiming = $false
        baselineSha = '8e79902d22de714f493582946f7c260462b0895e'
        candidateSha = '61ceef6bf69aef0a4f79b25384352d238979200f'
        harnessSha = '0000000000000000000000000000000000000000'
    }
    $manifest | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $tempRoot 'run-manifest.json') -Encoding utf8

    Write-SyntheticResult -Root $tempRoot -Slot '01-v212' -Mean 1000.0 -Allocated 100.0
    Write-SyntheticResult -Root $tempRoot -Slot '02-v220' -Mean 1100.0 -Allocated 105.0
    Write-SyntheticResult -Root $tempRoot -Slot '03-v220' -Mean 1200.0 -Allocated 107.0
    Write-SyntheticResult -Root $tempRoot -Slot '04-v212' -Mean 900.0 -Allocated 102.0

    $output = @(& $report -RunRoot $tempRoot)
    Assert-True ($output -contains 'PERF_CORE_AB_REPORT_OK run=synthetic-report-test comparisons=1') 'Report did not complete successfully.'

    $normalizedPath = Join-Path $tempRoot 'normalized-results.json'
    $markdownPath = Join-Path $tempRoot 'comparison.md'
    Assert-True (Test-Path -LiteralPath $normalizedPath -PathType Leaf) 'normalized-results.json was not created.'
    Assert-True (Test-Path -LiteralPath $markdownPath -PathType Leaf) 'comparison.md was not created.'

    $normalized = Get-Content -LiteralPath $normalizedPath -Raw | ConvertFrom-Json -Depth 64
    Assert-True (@($normalized.records).Count -eq 4) 'Expected four normalized raw records.'
    Assert-True (@($normalized.comparisons).Count -eq 1) 'Expected one normalized comparison.'

    $comparison = @($normalized.comparisons)[0]
    Assert-True ([int]$comparison.itemCount -eq 1000) 'ItemCount normalization failed.'
    Assert-True ([int]$comparison.maxConcurrency -eq 1) 'MaxConcurrency normalization failed.'
    Assert-True ([Math]::Abs([double]$comparison.baselineMeanNs - 950.0) -lt 0.001) 'Baseline center is incorrect.'
    Assert-True ([Math]::Abs([double]$comparison.candidateMeanNs - 1150.0) -lt 0.001) 'Candidate center is incorrect.'
    Assert-True ([Math]::Abs([double]$comparison.timeDeltaPercent - 21.0526315789) -lt 0.0001) 'Timing delta is incorrect.'
    Assert-True ([Math]::Abs([double]$comparison.baselineAllocatedBytes - 101.0) -lt 0.001) 'Baseline allocation center is incorrect.'
    Assert-True ([Math]::Abs([double]$comparison.candidateAllocatedBytes - 106.0) -lt 0.001) 'Candidate allocation center is incorrect.'
    Assert-True (-not [bool]$normalized.authoritativeTiming) 'Synthetic hosted result must remain non-authoritative.'

    $markdown = Get-Content -LiteralPath $markdownPath -Raw
    Assert-True ($markdown.Contains('+21.05%')) 'Markdown timing delta formatting is incorrect.'
    Assert-True ($markdown.Contains('10.53%')) 'Baseline repeat drift formatting is incorrect.'
    Assert-True ($markdown.Contains('8.70%')) 'Candidate repeat drift formatting is incorrect.'

    Write-Output 'PERF_REPORT_TESTS_OK comparisons=1'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
