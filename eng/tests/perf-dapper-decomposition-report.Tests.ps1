param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$methods = @('RawSingle', 'PipelineSingle', 'RawHundredRows', 'PipelineHundredRows')

function Write-SyntheticResult {
    param(
        [string]$Root,
        [string]$Slot,
        [hashtable]$Means,
        [hashtable]$Allocations
    )

    $results = Join-Path $Root "$Slot/results"
    New-Item -ItemType Directory -Path $results -Force | Out-Null

    $benchmarks = foreach ($method in $methods) {
        [ordered]@{
            DisplayInfo = "Synthetic Dapper decomposition $method"
            Namespace = 'SmartPipe.Perf.DapperDecomposition'
            Type = 'DapperDecompositionBenchmarks'
            Method = $method
            MethodTitle = $method
            Parameters = ''
            FullName = "Synthetic Dapper decomposition $method"
            Statistics = [ordered]@{
                N = 3
                Mean = [double]$Means[$method]
                Median = [double]$Means[$method]
                StandardDeviation = 5.0
            }
            Memory = [ordered]@{
                BytesAllocatedPerOperation = [double]$Allocations[$method]
            }
        }
    }

    [ordered]@{
        Title = 'Synthetic Dapper decomposition'
        Benchmarks = @($benchmarks)
    } | ConvertTo-Json -Depth 32 |
        Set-Content -LiteralPath (Join-Path $results 'Synthetic-report-full-compressed.json') -Encoding utf8
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$report = Join-Path $repoRoot 'eng/perf/report-dapper-decomposition-v220.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("smartpipe-dapper-decomp-report-" + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    [ordered]@{
        schemaVersion = 1
        runId = 'synthetic-dapper-decomposition'
        scenario = 'dapper-decomposition'
        scenarioClass = 'v220-only'
        comparisonPolicy = 'within-version-raw-vs-pipeline'
        authoritativeTiming = $false
        candidateSha = '61ceef6bf69aef0a4f79b25384352d238979200f'
        harnessSha = '0000000000000000000000000000000000000000'
    } | ConvertTo-Json -Depth 16 |
        Set-Content -LiteralPath (Join-Path $tempRoot 'run-manifest.json') -Encoding utf8

    $means1 = @{
        RawSingle = 1000.0
        PipelineSingle = 2000.0
        RawHundredRows = 10000.0
        PipelineHundredRows = 15000.0
    }
    $alloc1 = @{
        RawSingle = 100.0
        PipelineSingle = 1100.0
        RawHundredRows = 1000.0
        PipelineHundredRows = 3000.0
    }
    Write-SyntheticResult -Root $tempRoot -Slot '01-v220' -Means $means1 -Allocations $alloc1

    $means2 = @{
        RawSingle = 1100.0
        PipelineSingle = 2200.0
        RawHundredRows = 11000.0
        PipelineHundredRows = 16000.0
    }
    $alloc2 = @{
        RawSingle = 100.0
        PipelineSingle = 1100.0
        RawHundredRows = 1000.0
        PipelineHundredRows = 3000.0
    }
    Write-SyntheticResult -Root $tempRoot -Slot '02-v220' -Means $means2 -Allocations $alloc2

    $output = @(& $report -RunRoot $tempRoot)
    Assert-True ($output -contains 'PERF_DAPPER_DECOMPOSITION_REPORT_OK run=synthetic-dapper-decomposition workloads=2') 'Dapper decomposition report did not complete.'

    $normalized = Get-Content -LiteralPath (Join-Path $tempRoot 'normalized-results.json') -Raw | ConvertFrom-Json -Depth 64

    Assert-True ([string]$normalized.scenarioClass -ceq 'v220-only') 'Dapper decomposition must remain v220-only.'
    Assert-True ([string]$normalized.comparisonPolicy -ceq 'within-version-raw-vs-pipeline') 'Dapper decomposition policy drifted.'
    Assert-True (@($normalized.records).Count -eq 8) 'Expected eight raw Dapper decomposition records.'
    Assert-True (@($normalized.overhead).Count -eq 2) 'Expected single and hundred overhead summaries.'

    $single = @($normalized.overhead | Where-Object name -eq 'single')[0]
    $hundred = @($normalized.overhead | Where-Object name -eq 'hundred')[0]

    Assert-True ([Math]::Abs([double]$single.rawMeanNs - 1050.0) -lt 0.001) 'Single raw center is incorrect.'
    Assert-True ([Math]::Abs([double]$single.pipelineMeanNs - 2100.0) -lt 0.001) 'Single pipeline center is incorrect.'
    Assert-True ([Math]::Abs([double]$single.pipelineOverRawRatio - 2.0) -lt 0.001) 'Single Pipeline/Raw ratio is incorrect.'
    Assert-True ([Math]::Abs([double]$single.timeDeltaNs - 1050.0) -lt 0.001) 'Single time delta is incorrect.'
    Assert-True ([Math]::Abs([double]$single.allocationDeltaBytes - 1000.0) -lt 0.001) 'Single allocation delta is incorrect.'

    Assert-True ([Math]::Abs([double]$hundred.rawMeanNs - 10500.0) -lt 0.001) 'Hundred raw center is incorrect.'
    Assert-True ([Math]::Abs([double]$hundred.pipelineMeanNs - 15500.0) -lt 0.001) 'Hundred pipeline center is incorrect.'
    Assert-True ([Math]::Abs([double]$hundred.timeDeltaNs - 5000.0) -lt 0.001) 'Hundred time delta is incorrect.'
    Assert-True ([Math]::Abs([double]$hundred.allocationDeltaBytes - 2000.0) -lt 0.001) 'Hundred allocation delta is incorrect.'

    $expectedIncrementalTime = (5000.0 - 1050.0) / 99.0
    $expectedIncrementalAllocation = (2000.0 - 1000.0) / 99.0
    Assert-True ([Math]::Abs([double]$normalized.descriptiveModel.incrementalPipelineTimePerAdditionalRowNs - $expectedIncrementalTime) -lt 0.001) 'Incremental time model is incorrect.'
    Assert-True ([Math]::Abs([double]$normalized.descriptiveModel.incrementalPipelineAllocationPerAdditionalRowBytes - $expectedIncrementalAllocation) -lt 0.001) 'Incremental allocation model is incorrect.'

    $markdown = Get-Content -LiteralPath (Join-Path $tempRoot 'comparison.md') -Raw
    Assert-True ($markdown.Contains('v2.2-only within-version decomposition')) 'Markdown must explain within-version semantics.'
    Assert-True ($markdown.Contains('2.000x')) 'Markdown must render the single ratio.'
    Assert-True ($markdown.Contains('descriptive only')) 'Markdown must warn that the two-point model is descriptive.'

    Write-Output 'PERF_DAPPER_DECOMPOSITION_REPORT_TESTS_OK workloads=2'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
