param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$methods = @(
    'SourceGeneratedSmallRoundTrip',
    'OptionsSmallRoundTrip',
    'SourceGeneratedMediumRoundTrip',
    'OptionsMediumRoundTrip'
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
            DisplayInfo = "Synthetic SP220-08 JSON $($methods[$index])"
            Namespace = 'SmartPipe.Perf.JsonStrictAb'
            Type = 'JsonStrictAbBenchmarks'
            Method = $methods[$index]
            MethodTitle = $methods[$index]
            Parameters = ''
            FullName = "Synthetic SP220-08 JSON $($methods[$index])"
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
        Title = 'Synthetic SP220-08 JSON'
        HostEnvironmentInfo = [ordered]@{}
        Benchmarks = @($benchmarks)
    }

    $path = Join-Path $results 'Synthetic-report-full-compressed.json'
    $document | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $path -Encoding utf8
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$report = Join-Path $repoRoot 'eng/perf/report-json-strict-ab.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("smartpipe-perf-json-report-" + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    $manifest = [ordered]@{
        schemaVersion = 1
        runId = 'synthetic-json-report-test'
        scenario = 'json'
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
    Assert-True ($output -contains 'PERF_JSON_STRICT_AB_REPORT_OK run=synthetic-json-report-test comparisons=4') 'JSON report did not complete successfully.'

    $normalized = Get-Content -LiteralPath (Join-Path $tempRoot 'normalized-results.json') -Raw | ConvertFrom-Json -Depth 64
    Assert-True ([string]$normalized.scenarioClass -ceq 'strict-ab') 'JSON normalized class must remain strict-ab.'
    Assert-True ([string]$normalized.comparisonPolicy -ceq 'strict-cross-version-ratio') 'JSON comparison policy drifted.'
    Assert-True (@($normalized.records).Count -eq 16) 'Expected 16 normalized JSON raw records.'
    Assert-True (@($normalized.comparisons).Count -eq 4) 'Expected four JSON method comparisons.'

    $comparison = @($normalized.comparisons | Where-Object method -eq 'SourceGeneratedSmallRoundTrip')[0]
    Assert-True ([Math]::Abs([double]$comparison.baselineMeanNs - 950.0) -lt 0.001) 'JSON baseline center is incorrect.'
    Assert-True ([Math]::Abs([double]$comparison.candidateMeanNs - 1150.0) -lt 0.001) 'JSON candidate center is incorrect.'
    Assert-True ([Math]::Abs([double]$comparison.timeDeltaPercent - 21.0526315789) -lt 0.001) 'JSON timing delta is incorrect.'
    Assert-True ([Math]::Abs([double]$comparison.allocationDeltaPercent - 4.9504950495) -lt 0.001) 'JSON allocation delta is incorrect.'

    $markdown = Get-Content -LiteralPath (Join-Path $tempRoot 'comparison.md') -Raw
    Assert-True ($markdown.Contains('+21.05%')) 'JSON Markdown must render strict timing delta.'
    Assert-True ($markdown.Contains('+4.95%')) 'JSON Markdown must render strict allocation delta.'
    Assert-True ($markdown.Contains('GitHub-hosted timing remains informational')) 'JSON Markdown must preserve timing authority warning.'

    Write-Output 'PERF_JSON_REPORT_TESTS_OK comparisons=4'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
