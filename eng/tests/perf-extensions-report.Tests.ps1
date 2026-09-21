param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$methods = @(
    'FilterSyncPass',
    'ConditionalFalsePassThrough',
    'ConditionalTrueTransform',
    'CompositeThreeTransforms',
    'ValidationValid',
    'CompressionBrotli1KiB',
    'LoggerSinkLegacyDisabled',
    'ChannelMergeTwoReaders'
)

function Write-SyntheticResult {
    param(
        [string]$Root,
        [string]$Slot,
        [double]$Mean,
        [double]$Allocated
    )

    $results = Join-Path $Root "$Slot/results"
    New-Item -ItemType Directory -Path $results -Force | Out-Null

    $benchmarks = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $methods.Count; $index++) {
        $scale = $index + 1
        $benchmarks.Add([ordered]@{
            DisplayInfo = "Synthetic SP220-07 $($methods[$index])"
            Namespace = 'SmartPipe.Perf.ExtensionsStrictAb'
            Type = 'ExtensionsStrictAbBenchmarks'
            Method = $methods[$index]
            MethodTitle = $methods[$index]
            Parameters = ''
            FullName = "Synthetic SP220-07 $($methods[$index])"
            HardwareIntrinsics = 'synthetic'
            Statistics = [ordered]@{
                N = 3
                Mean = ($Mean * $scale)
                Median = ($Mean * $scale)
                StandardDeviation = 5.0
            }
            Memory = [ordered]@{
                BytesAllocatedPerOperation = ($Allocated * $scale)
            }
        })
    }

    $document = [ordered]@{
        Title = 'Synthetic SP220-07'
        HostEnvironmentInfo = [ordered]@{}
        Benchmarks = @($benchmarks)
    }

    $path = Join-Path $results 'Synthetic-report-full-compressed.json'
    $document | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $path -Encoding utf8
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$report = Join-Path $repoRoot 'eng/perf/report-extensions-strict-ab.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("smartpipe-perf-sp22007-report-" + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    $manifest = [ordered]@{
        schemaVersion = 1
        runId = 'synthetic-sp22007-report-test'
        scenario = 'channels-transforms-logging-dataannotations'
        scenarioClass = 'strict-ab'
        comparisonPolicy = 'strict-cross-version-ratio'
        authoritativeTiming = $false
        baselineSha = '8e79902d22de714f493582946f7c260462b0895e'
        candidateSha = '61ceef6bf69aef0a4f79b25384352d238979200f'
        harnessSha = '0000000000000000000000000000000000000000'
    }

    $manifest |
        ConvertTo-Json -Depth 16 |
        Set-Content -LiteralPath (Join-Path $tempRoot 'run-manifest.json') -Encoding utf8

    Write-SyntheticResult -Root $tempRoot -Slot '01-v212' -Mean 1000.0 -Allocated 100.0
    Write-SyntheticResult -Root $tempRoot -Slot '02-v220' -Mean 1100.0 -Allocated 105.0
    Write-SyntheticResult -Root $tempRoot -Slot '03-v220' -Mean 1200.0 -Allocated 107.0
    Write-SyntheticResult -Root $tempRoot -Slot '04-v212' -Mean 900.0 -Allocated 102.0

    $output = @(& $report -RunRoot $tempRoot)
    Assert-True ($output -contains 'PERF_EXTENSIONS_STRICT_AB_REPORT_OK run=synthetic-sp22007-report-test comparisons=8') 'SP220-07 report did not complete successfully.'

    $normalizedPath = Join-Path $tempRoot 'normalized-results.json'
    $markdownPath = Join-Path $tempRoot 'comparison.md'
    Assert-True (Test-Path -LiteralPath $normalizedPath -PathType Leaf) 'SP220-07 normalized-results.json was not created.'
    Assert-True (Test-Path -LiteralPath $markdownPath -PathType Leaf) 'SP220-07 comparison.md was not created.'

    $normalized = Get-Content -LiteralPath $normalizedPath -Raw | ConvertFrom-Json -Depth 64
    Assert-True ([string]$normalized.scenarioClass -ceq 'strict-ab') 'SP220-07 normalized class must remain strict-ab.'
    Assert-True ([string]$normalized.comparisonPolicy -ceq 'strict-cross-version-ratio') 'SP220-07 comparison policy drifted.'
    Assert-True (@($normalized.records).Count -eq 32) 'Expected 32 normalized SP220-07 raw records.'
    Assert-True (@($normalized.comparisons).Count -eq 8) 'Expected eight SP220-07 method comparisons.'

    $comparison = @($normalized.comparisons | Where-Object method -eq 'FilterSyncPass')[0]
    Assert-True ($null -ne $comparison) 'FilterSyncPass comparison is missing.'
    Assert-True ([Math]::Abs([double]$comparison.baselineMeanNs - 950.0) -lt 0.001) 'SP220-07 baseline center is incorrect.'
    Assert-True ([Math]::Abs([double]$comparison.candidateMeanNs - 1150.0) -lt 0.001) 'SP220-07 candidate center is incorrect.'
    Assert-True ([Math]::Abs([double]$comparison.timeDeltaPercent - 21.0526315789) -lt 0.001) 'SP220-07 timing delta is incorrect.'
    Assert-True ([Math]::Abs([double]$comparison.baselineAllocatedBytes - 101.0) -lt 0.001) 'SP220-07 baseline allocation center is incorrect.'
    Assert-True ([Math]::Abs([double]$comparison.candidateAllocatedBytes - 106.0) -lt 0.001) 'SP220-07 candidate allocation center is incorrect.'
    Assert-True ([Math]::Abs([double]$comparison.allocationDeltaPercent - 4.9504950495) -lt 0.001) 'SP220-07 allocation delta is incorrect.'
    Assert-True ([Math]::Abs([double]$comparison.baselineRepeatDriftPercent - 10.5263157895) -lt 0.001) 'SP220-07 baseline repeat drift is incorrect.'
    Assert-True ([Math]::Abs([double]$comparison.candidateRepeatDriftPercent - 8.6956521739) -lt 0.001) 'SP220-07 candidate repeat drift is incorrect.'

    $markdown = Get-Content -LiteralPath $markdownPath -Raw
    Assert-True ($markdown.Contains('+21.05%')) 'SP220-07 Markdown must render strict timing delta.'
    Assert-True ($markdown.Contains('+4.95%')) 'SP220-07 Markdown must render strict allocation delta.'
    Assert-True ($markdown.Contains('GitHub-hosted timing remains informational')) 'SP220-07 Markdown must preserve timing authority warning.'

    Write-Output 'PERF_EXTENSIONS_REPORT_TESTS_OK comparisons=8'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
