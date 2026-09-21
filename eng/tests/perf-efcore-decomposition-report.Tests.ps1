param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$methods = @(
    'RawSingle',
    'PipelineSingle',
    'RawCompiledSingle',
    'CompiledPipelineSingle',
    'RawHundredRows',
    'PipelineHundredRows',
    'RawCompiledHundredRows',
    'CompiledPipelineHundredRows'
)

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
            DisplayInfo = "Synthetic EF decomposition $method"
            Namespace = 'SmartPipe.Perf.EfCoreDecomposition'
            Type = 'EfCoreDecompositionBenchmarks'
            Method = $method
            MethodTitle = $method
            Parameters = ''
            FullName = "Synthetic EF decomposition $method"
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
        Title = 'Synthetic EF decomposition'
        Benchmarks = @($benchmarks)
    } | ConvertTo-Json -Depth 32 |
        Set-Content -LiteralPath (Join-Path $results 'Synthetic-report-full-compressed.json') -Encoding utf8
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$report = Join-Path $repoRoot 'eng/perf/report-efcore-decomposition-v220.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("smartpipe-efcore-decomp-report-" + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    [ordered]@{
        schemaVersion = 1
        runId = 'synthetic-efcore-decomposition'
        scenario = 'efcore-decomposition'
        scenarioClass = 'v220-only'
        comparisonPolicy = 'paired-raw-vs-pipeline-normal-and-compiled'
        authoritativeTiming = $false
        candidateSha = '61ceef6bf69aef0a4f79b25384352d238979200f'
        harnessSha = '0000000000000000000000000000000000000000'
    } | ConvertTo-Json -Depth 16 |
        Set-Content -LiteralPath (Join-Path $tempRoot 'run-manifest.json') -Encoding utf8

    $means1 = @{
        RawSingle = 1000.0
        PipelineSingle = 2000.0
        RawCompiledSingle = 800.0
        CompiledPipelineSingle = 1800.0
        RawHundredRows = 10000.0
        PipelineHundredRows = 15000.0
        RawCompiledHundredRows = 9000.0
        CompiledPipelineHundredRows = 13000.0
    }
    $alloc1 = @{
        RawSingle = 100.0
        PipelineSingle = 1100.0
        RawCompiledSingle = 80.0
        CompiledPipelineSingle = 880.0
        RawHundredRows = 1000.0
        PipelineHundredRows = 3000.0
        RawCompiledHundredRows = 900.0
        CompiledPipelineHundredRows = 2400.0
    }
    Write-SyntheticResult -Root $tempRoot -Slot '01-v220' -Means $means1 -Allocations $alloc1

    $means2 = @{
        RawSingle = 1100.0
        PipelineSingle = 2200.0
        RawCompiledSingle = 900.0
        CompiledPipelineSingle = 1900.0
        RawHundredRows = 11000.0
        PipelineHundredRows = 16000.0
        RawCompiledHundredRows = 10000.0
        CompiledPipelineHundredRows = 14000.0
    }
    $alloc2 = @{
        RawSingle = 100.0
        PipelineSingle = 1100.0
        RawCompiledSingle = 80.0
        CompiledPipelineSingle = 880.0
        RawHundredRows = 1000.0
        PipelineHundredRows = 3000.0
        RawCompiledHundredRows = 900.0
        CompiledPipelineHundredRows = 2400.0
    }
    Write-SyntheticResult -Root $tempRoot -Slot '02-v220' -Means $means2 -Allocations $alloc2

    $output = @(& $report -RunRoot $tempRoot)
    Assert-True ($output -contains 'PERF_EFCORE_DECOMPOSITION_REPORT_OK run=synthetic-efcore-decomposition workloads=2 pairedBaselines=2') 'EF decomposition report did not complete.'

    $normalized = Get-Content -LiteralPath (Join-Path $tempRoot 'normalized-results.json') -Raw | ConvertFrom-Json -Depth 64

    Assert-True ([int]$normalized.schemaVersion -eq 2) 'EF decomposition normalized schema must be version 2.'
    Assert-True ([string]$normalized.scenarioClass -ceq 'v220-only') 'EF decomposition must remain v220-only.'
    Assert-True ([string]$normalized.comparisonPolicy -ceq 'paired-raw-vs-pipeline-normal-and-compiled') 'EF decomposition policy drifted.'
    Assert-True (@($normalized.records).Count -eq 16) 'Expected sixteen raw EF decomposition records.'
    Assert-True (@($normalized.comparisons).Count -eq 2) 'Expected single and hundred EF comparisons.'

    $single = @($normalized.comparisons | Where-Object name -eq 'single')[0]
    $hundred = @($normalized.comparisons | Where-Object name -eq 'hundred')[0]

    Assert-True ([Math]::Abs([double]$single.rawMeanNs - 1050.0) -lt 0.001) 'Single raw center is incorrect.'
    Assert-True ([Math]::Abs([double]$single.normalPipelineMeanNs - 2100.0) -lt 0.001) 'Single normal center is incorrect.'
    Assert-True ([Math]::Abs([double]$single.rawCompiledMeanNs - 850.0) -lt 0.001) 'Single raw compiled center is incorrect.'
    Assert-True ([Math]::Abs([double]$single.compiledPipelineMeanNs - 1850.0) -lt 0.001) 'Single compiled pipeline center is incorrect.'
    Assert-True ([Math]::Abs([double]$single.normalOverRawRatio - 2.0) -lt 0.001) 'Single Normal/Raw ratio is incorrect.'
    Assert-True ([Math]::Abs([double]$single.compiledOverRawCompiledRatio - (1850.0 / 850.0)) -lt 0.001) 'Single Compiled/RawCompiled ratio is incorrect.'
    Assert-True ([Math]::Abs([double]$single.compiledTimeDeltaNs - 1000.0) -lt 0.001) 'Single compiled SmartPipe time overhead is incorrect.'
    Assert-True ([Math]::Abs([double]$single.normalAllocationDeltaBytes - 1000.0) -lt 0.001) 'Single normal allocation overhead is incorrect.'
    Assert-True ([Math]::Abs([double]$single.compiledAllocationDeltaBytes - 800.0) -lt 0.001) 'Single compiled allocation overhead is incorrect.'

    Assert-True ([Math]::Abs([double]$hundred.rawMeanNs - 10500.0) -lt 0.001) 'Hundred raw center is incorrect.'
    Assert-True ([Math]::Abs([double]$hundred.normalPipelineMeanNs - 15500.0) -lt 0.001) 'Hundred normal center is incorrect.'
    Assert-True ([Math]::Abs([double]$hundred.rawCompiledMeanNs - 9500.0) -lt 0.001) 'Hundred raw compiled center is incorrect.'
    Assert-True ([Math]::Abs([double]$hundred.compiledPipelineMeanNs - 13500.0) -lt 0.001) 'Hundred compiled pipeline center is incorrect.'

    $expectedNormalIncremental = (5000.0 - 1050.0) / 99.0
    $expectedCompiledIncremental = (4000.0 - 1000.0) / 99.0
    Assert-True ([Math]::Abs([double]$normalized.descriptiveModel.normalIncrementalTimePerAdditionalRowNs - $expectedNormalIncremental) -lt 0.001) 'Normal incremental time model is incorrect.'
    Assert-True ([Math]::Abs([double]$normalized.descriptiveModel.compiledIncrementalTimePerAdditionalRowNs - $expectedCompiledIncremental) -lt 0.001) 'Compiled incremental time model is incorrect.'

    $markdown = Get-Content -LiteralPath (Join-Path $tempRoot 'comparison.md') -Raw
    Assert-True ($markdown.Contains('CompiledQuerySource is paired with raw EF.CompileAsyncQuery')) 'Markdown must explain the paired compiled baseline.'
    Assert-True ($markdown.Contains('descriptive only')) 'Markdown must warn about the two-point model.'

    Write-Output 'PERF_EFCORE_DECOMPOSITION_REPORT_TESTS_OK workloads=2 pairedBaselines=2'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
