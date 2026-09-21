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
        Title = 'Synthetic HealthChecks'
        HostEnvironmentInfo = [ordered]@{}
        Benchmarks = @(
            [ordered]@{
                DisplayInfo = 'Synthetic HealthChecks ResolveHealthCheckService'
                Namespace = 'SmartPipe.Perf.HealthChecks'
                Type = 'HealthChecksEvolutionBenchmarks'
                Method = 'ResolveHealthCheckService'
                MethodTitle = 'ResolveHealthCheckService'
                Parameters = ''
                FullName = 'Synthetic HealthChecks ResolveHealthCheckService'
                HardwareIntrinsics = 'synthetic'
                Statistics = [ordered]@{
                    N = 3
                    Mean = $Mean
                    Median = $Mean
                    StandardDeviation = 5.0
                }
                Measurements = @(
                    [ordered]@{
                        IterationMode = 'Workload'
                        IterationStage = 'Actual'
                        Operations = 1
                        Nanoseconds = $Mean
                    }
                )
                Memory = [ordered]@{
                    BytesAllocatedPerOperation = $Allocated
                }
            },
            [ordered]@{
                DisplayInfo = 'Synthetic HealthChecks CheckRegisteredPipeline'
                Namespace = 'SmartPipe.Perf.HealthChecks'
                Type = 'HealthChecksEvolutionBenchmarks'
                Method = 'CheckRegisteredPipeline'
                MethodTitle = 'CheckRegisteredPipeline'
                Parameters = ''
                FullName = 'Synthetic HealthChecks CheckRegisteredPipeline'
                HardwareIntrinsics = 'synthetic'
                Statistics = [ordered]@{
                    N = 3
                    Mean = ($Mean * 2.0)
                    Median = ($Mean * 2.0)
                    StandardDeviation = 10.0
                }
                Measurements = @(
                    [ordered]@{
                        IterationMode = 'Workload'
                        IterationStage = 'Actual'
                        Operations = 1
                        Nanoseconds = ($Mean * 2.0)
                    }
                )
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
$report = Join-Path $repoRoot 'eng/perf/report-healthchecks-evolution.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("smartpipe-perf-healthchecks-report-" + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    $manifest = [ordered]@{
        schemaVersion = 1
        runId = 'synthetic-healthchecks-report-test'
        scenario = 'health-checks'
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
    Assert-True ($output -contains 'PERF_HEALTHCHECKS_REPORT_OK run=synthetic-healthchecks-report-test comparisons=2') 'HealthChecks report did not complete successfully.'

    $normalizedPath = Join-Path $tempRoot 'normalized-results.json'
    $markdownPath = Join-Path $tempRoot 'comparison.md'
    Assert-True (Test-Path -LiteralPath $normalizedPath -PathType Leaf) 'HealthChecks normalized-results.json was not created.'
    Assert-True (Test-Path -LiteralPath $markdownPath -PathType Leaf) 'HealthChecks comparison.md was not created.'

    $normalized = Get-Content -LiteralPath $normalizedPath -Raw | ConvertFrom-Json -Depth 64
    Assert-True ([string]$normalized.scenarioClass -ceq 'evolution') 'HealthChecks normalized class must remain evolution.'
    Assert-True ([string]$normalized.comparisonPolicy -ceq 'side-by-side-no-cross-version-ratio') 'HealthChecks comparison policy drifted.'
    Assert-True (@($normalized.records).Count -eq 8) 'Expected eight normalized HealthChecks raw records.'
    Assert-True (@($normalized.sideBySide).Count -eq 2) 'Expected two distinct HealthChecks side-by-side method groups.'

    $methods = @($normalized.sideBySide | ForEach-Object { [string]$_.method } | Sort-Object)
    Assert-True ($methods.Count -eq 2) 'Expected exactly two HealthChecks method groups.'
    Assert-True ($methods[0] -ceq 'CheckRegisteredPipeline') 'CheckRegisteredPipeline comparison is missing.'
    Assert-True ($methods[1] -ceq 'ResolveHealthCheckService') 'ResolveHealthCheckService comparison is missing.'

    $comparison = @($normalized.sideBySide | Where-Object method -eq 'ResolveHealthCheckService')[0]
    Assert-True ($null -ne $comparison) 'ResolveHealthCheckService comparison is missing.'
    Assert-True ([Math]::Abs([double]$comparison.baselineMeanNs - 950.0) -lt 0.001) 'HealthChecks baseline center is incorrect.'
    Assert-True ([Math]::Abs([double]$comparison.candidateMeanNs - 1600.0) -lt 0.001) 'HealthChecks candidate center is incorrect.'
    Assert-True ($null -eq $comparison.PSObject.Properties['timeDeltaPercent']) 'HealthChecks evolution output must not contain timeDeltaPercent.'
    Assert-True ($null -eq $comparison.PSObject.Properties['allocationDeltaPercent']) 'HealthChecks evolution output must not contain allocationDeltaPercent.'

    $markdown = Get-Content -LiteralPath $markdownPath -Raw
    Assert-True ($markdown.Contains('Cross-version percentage deltas are intentionally omitted.')) 'HealthChecks Markdown must explain omitted cross-version deltas.'
    Assert-True (-not $markdown.Contains('Time Δ')) 'HealthChecks Markdown must not render a strict timing delta column.'


    $failedResult = Join-Path $tempRoot '02-v220/results/Synthetic-report-full-compressed.json'
    $failedDocument = Get-Content -LiteralPath $failedResult -Raw | ConvertFrom-Json -Depth 64
    $failedDocument.Benchmarks[0].Statistics = $null
    $failedDocument | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $failedResult -Encoding utf8

    $failed = $false
    try {
        & $report -RunRoot $tempRoot | Out-Null
    }
    catch {
        $failed = $true
        Assert-True ($_.Exception.Message.Contains("Statistics is missing")) 'HealthChecks reporter must diagnose missing Statistics explicitly.'
    }

    Assert-True $failed 'HealthChecks reporter must reject incomplete BenchmarkDotNet evidence.'

    Write-Output 'PERF_HEALTHCHECKS_REPORT_TESTS_OK comparisons=2 incompleteEvidenceGuard=ok'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
