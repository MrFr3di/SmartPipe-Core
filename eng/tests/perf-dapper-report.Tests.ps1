param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition,[string]$Message)
    if(-not $Condition){ throw $Message }
}

$methods=@('ReadHundredRows','ReadSingleParameterized')

function Write-SyntheticResult {
    param([string]$Root,[string]$Slot,[double]$Mean,[double]$Allocated)

    $results=Join-Path $Root "$Slot/results"
    New-Item -ItemType Directory -Path $results -Force | Out-Null

    $benchmarks=[Collections.Generic.List[object]]::new()
    for($index=0;$index -lt $methods.Count;$index++){
        $scale=$index+1
        $benchmarks.Add([ordered]@{
            DisplayInfo="Synthetic Dapper $($methods[$index])"
            Namespace='SmartPipe.Perf.Dapper'
            Type='DapperEvolutionBenchmarks'
            Method=$methods[$index]
            MethodTitle=$methods[$index]
            Parameters=''
            FullName="Synthetic Dapper $($methods[$index])"
            HardwareIntrinsics='synthetic'
            Statistics=[ordered]@{
                N=3
                Mean=($Mean*$scale)
                Median=($Mean*$scale)
                StandardDeviation=5.0
            }
            Memory=[ordered]@{
                BytesAllocatedPerOperation=($Allocated*$scale)
            }
        })
    }

    [ordered]@{
        Title='Synthetic Dapper'
        HostEnvironmentInfo=[ordered]@{}
        Benchmarks=@($benchmarks)
    } | ConvertTo-Json -Depth 32 |
        Set-Content -LiteralPath (Join-Path $results 'Synthetic-report-full-compressed.json') -Encoding utf8
}

$repoRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$report=Join-Path $repoRoot 'eng/perf/report-dapper-evolution.ps1'
$tempRoot=Join-Path ([IO.Path]::GetTempPath()) ("smartpipe-perf-dapper-report-"+[Guid]::NewGuid().ToString('N'))

try{
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    [ordered]@{
        schemaVersion=1
        runId='synthetic-dapper-report-test'
        scenario='dapper'
        scenarioClass='evolution'
        comparisonPolicy='side-by-side-no-cross-version-ratio'
        authoritativeTiming=$false
        baselineSha='8e79902d22de714f493582946f7c260462b0895e'
        candidateSha='61ceef6bf69aef0a4f79b25384352d238979200f'
        harnessSha='0000000000000000000000000000000000000000'
    } | ConvertTo-Json -Depth 16 |
        Set-Content -LiteralPath (Join-Path $tempRoot 'run-manifest.json') -Encoding utf8

    Write-SyntheticResult -Root $tempRoot -Slot '01-v212' -Mean 1000.0 -Allocated 100.0
    Write-SyntheticResult -Root $tempRoot -Slot '02-v220' -Mean 1500.0 -Allocated 120.0
    Write-SyntheticResult -Root $tempRoot -Slot '03-v220' -Mean 1700.0 -Allocated 124.0
    Write-SyntheticResult -Root $tempRoot -Slot '04-v212' -Mean 900.0 -Allocated 102.0

    $output=@(& $report -RunRoot $tempRoot)
    Assert-True ($output -contains 'PERF_DAPPER_REPORT_OK run=synthetic-dapper-report-test comparisons=2') 'Dapper report did not complete successfully.'

    $normalized=Get-Content -LiteralPath (Join-Path $tempRoot 'normalized-results.json') -Raw | ConvertFrom-Json -Depth 64
    Assert-True ([string]$normalized.scenarioClass -ceq 'evolution') 'Dapper normalized class must remain evolution.'
    Assert-True ([string]$normalized.comparisonPolicy -ceq 'side-by-side-no-cross-version-ratio') 'Dapper comparison policy drifted.'
    Assert-True (@($normalized.records).Count -eq 8) 'Expected eight normalized Dapper raw records.'
    Assert-True (@($normalized.sideBySide).Count -eq 2) 'Expected two Dapper side-by-side comparisons.'

    $all=@($normalized.sideBySide | Where-Object method -eq 'ReadHundredRows')[0]
    Assert-True ([Math]::Abs([double]$all.baselineMeanNs-950.0) -lt 0.001) 'Dapper baseline center is incorrect.'
    Assert-True ([Math]::Abs([double]$all.candidateMeanNs-1600.0) -lt 0.001) 'Dapper candidate center is incorrect.'
    Assert-True ($null -eq $all.PSObject.Properties['timeDeltaPercent']) 'Dapper evolution output must not contain timeDeltaPercent.'
    Assert-True ($null -eq $all.PSObject.Properties['allocationDeltaPercent']) 'Dapper evolution output must not contain allocationDeltaPercent.'

    $markdown=Get-Content -LiteralPath (Join-Path $tempRoot 'comparison.md') -Raw
    Assert-True ($markdown.Contains('Cross-version percentage deltas are intentionally omitted.')) 'Dapper Markdown must explain omitted deltas.'
    Assert-True (-not $markdown.Contains('Time Δ')) 'Dapper Markdown must not render strict timing delta.'

    Write-Output 'PERF_DAPPER_REPORT_TESTS_OK comparisons=2'
}
finally{
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
