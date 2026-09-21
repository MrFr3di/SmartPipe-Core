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
        Title = 'Synthetic DI'
        HostEnvironmentInfo = [ordered]@{}
        Benchmarks = @(
            [ordered]@{
                DisplayInfo = 'Synthetic DI ResolveFactory'
                Namespace = 'SmartPipe.Perf.DependencyInjection'
                Type = 'DependencyInjectionEvolutionBenchmarks'
                Method = 'ResolveFactory'
                MethodTitle = 'ResolveFactory'
                Parameters = ''
                FullName = 'Synthetic DI ResolveFactory'
                HardwareIntrinsics = 'synthetic'
                Statistics = [ordered]@{
                    N = 3
                    Mean = $Mean
                    Median = $Mean
                    StandardDeviation = 5.0
                }
                Memory = [ordered]@{
                    BytesAllocatedPerOperation = $Allocated
                }
            },
            [ordered]@{
                DisplayInfo = 'Synthetic DI RegisterAndBuildProvider'
                Namespace = 'SmartPipe.Perf.DependencyInjection'
                Type = 'DependencyInjectionEvolutionBenchmarks'
                Method = 'RegisterAndBuildProvider'
                MethodTitle = 'RegisterAndBuildProvider'
                Parameters = ''
                FullName = 'Synthetic DI RegisterAndBuildProvider'
                HardwareIntrinsics = 'synthetic'
                Statistics = [ordered]@{
                    N = 3
                    Mean = ($Mean * 2.0)
                    Median = ($Mean * 2.0)
                    StandardDeviation = 10.0
                }
                Memory = [ordered]@{
                    BytesAllocatedPerOperation = ($Allocated * 2.0)
                }
            }
        )
    }

    $path = Join-Path $results 'Synthetic-report-full-compressed.json'
    $document | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $path -Encoding utf8
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$report = Join-Path $repoRoot 'eng/perf/report-di-evolution.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("smartpipe-perf-di-report-" + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    $manifest = [ordered]@{
        schemaVersion = 1
        runId = 'synthetic-di-report-test'
        scenario = 'dependency-injection'
        scenarioClass = 'evolution'
        comparisonPolicy = 'side-by-side-no-cross-version-ratio'
        authoritativeTiming = $false
        baselineSha = '8e79902d22de714f493582946f7c260462b0895e'
        candidateSha = '61ceef6bf69aef0a4f79b25384352d238979200f'
        harnessSha = '0000000000000000000000000000000000000000'
    }

    $manifest | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $tempRoot 'run-manifest.json') -Encoding utf8

    Write-SyntheticResult -Root $tempRoot -Slot '01-v212' -Mean 1000.0 -Allocated 100.0
    Write-SyntheticResult -Root $tempRoot -Slot '02-v220' -Mean 1500.0 -Allocated 120.0
    Write-SyntheticResult -Root $tempRoot -Slot '03-v220' -Mean 1700.0 -Allocated 124.0
    Write-SyntheticResult -Root $tempRoot -Slot '04-v212' -Mean 900.0 -Allocated 102.0

    $output = @(& $report -RunRoot $tempRoot)
    Assert-True ($output -contains 'PERF_DI_REPORT_OK run=synthetic-di-report-test comparisons=2') 'DI report did not complete successfully.'

    $normalizedPath = Join-Path $tempRoot 'normalized-results.json'
    $markdownPath = Join-Path $tempRoot 'comparison.md'
    Assert-True (Test-Path -LiteralPath $normalizedPath -PathType Leaf) 'DI normalized-results.json was not created.'
    Assert-True (Test-Path -LiteralPath $markdownPath -PathType Leaf) 'DI comparison.md was not created.'

    $normalized = Get-Content -LiteralPath $normalizedPath -Raw | ConvertFrom-Json -Depth 64
    Assert-True ([string]$normalized.scenarioClass -ceq 'evolution') 'DI normalized class must remain evolution.'
    Assert-True ([string]$normalized.comparisonPolicy -ceq 'side-by-side-no-cross-version-ratio') 'DI comparison policy drifted.'
    Assert-True (@($normalized.records).Count -eq 8) 'Expected eight normalized DI raw records.'
    Assert-True (@($normalized.sideBySide).Count -eq 2) 'Expected two distinct DI side-by-side method groups.'

    $methods = @($normalized.sideBySide | ForEach-Object { [string]$_.method } | Sort-Object)
    Assert-True ($methods.Count -eq 2) 'Expected exactly two DI method groups.'
    Assert-True ($methods[0] -ceq 'RegisterAndBuildProvider') 'RegisterAndBuildProvider comparison is missing.'
    Assert-True ($methods[1] -ceq 'ResolveFactory') 'ResolveFactory comparison is missing.'

    $comparison = @($normalized.sideBySide | Where-Object method -eq 'ResolveFactory')[0]
    Assert-True ($null -ne $comparison) 'ResolveFactory comparison is missing.'
    Assert-True ([Math]::Abs([double]$comparison.baselineMeanNs - 950.0) -lt 0.001) 'DI baseline center is incorrect.'
    Assert-True ([Math]::Abs([double]$comparison.candidateMeanNs - 1600.0) -lt 0.001) 'DI candidate center is incorrect.'
    Assert-True ($null -eq $comparison.PSObject.Properties['timeDeltaPercent']) 'DI evolution output must not contain timeDeltaPercent.'
    Assert-True ($null -eq $comparison.PSObject.Properties['allocationDeltaPercent']) 'DI evolution output must not contain allocationDeltaPercent.'

    $markdown = Get-Content -LiteralPath $markdownPath -Raw
    Assert-True ($markdown.Contains('Cross-version percentage deltas are intentionally omitted.')) 'DI Markdown must explain omitted cross-version deltas.'
    Assert-True (-not $markdown.Contains('Time Δ')) 'DI Markdown must not render a strict timing delta column.'

    Write-Output 'PERF_DI_REPORT_TESTS_OK comparisons=2'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
