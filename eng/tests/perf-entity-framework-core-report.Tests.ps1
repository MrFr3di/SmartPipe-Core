param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition,[string]$Message)
    if(-not $Condition){ throw $Message }
}

$methods=@('ReadHundredRows','ReadSingleFiltered')

function Write-SyntheticResult {
    param([string]$Root,[string]$Slot,[double]$Mean,[double]$Allocated)

    $results=Join-Path $Root "$Slot/results"
    New-Item -ItemType Directory -Path $results -Force | Out-Null

    $benchmarks=[Collections.Generic.List[object]]::new()
    for($index=0;$index -lt $methods.Count;$index++){
        $scale=$index+1
        $benchmarks.Add([ordered]@{
            DisplayInfo="Synthetic Entity Framework Core $($methods[$index])"
            Namespace='SmartPipe.Perf.Entity Framework Core'
            Type='Entity Framework CoreEvolutionBenchmarks'
            Method=$methods[$index]
            MethodTitle=$methods[$index]
            Parameters=''
            FullName="Synthetic Entity Framework Core $($methods[$index])"
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
        Title='Synthetic Entity Framework Core'
        HostEnvironmentInfo=[ordered]@{}
        Benchmarks=@($benchmarks)
    } | ConvertTo-Json -Depth 32 |
        Set-Content -LiteralPath (Join-Path $results 'Synthetic-report-full-compressed.json') -Encoding utf8
}

$repoRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$report=Join-Path $repoRoot 'eng/perf/report-entity-framework-core-evolution.ps1'
$tempRoot=Join-Path ([IO.Path]::GetTempPath()) ("smartpipe-perf-entity-framework-core-report-"+[Guid]::NewGuid().ToString('N'))

try{
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    [ordered]@{
        schemaVersion=1
        runId='synthetic-entity-framework-core-report-test'
        scenario='entity-framework-core'
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
    Assert-True ($output -contains 'PERF_EFCORE_REPORT_OK run=synthetic-entity-framework-core-report-test comparisons=2') 'Entity Framework Core report did not complete successfully.'

    $normalized=Get-Content -LiteralPath (Join-Path $tempRoot 'normalized-results.json') -Raw | ConvertFrom-Json -Depth 64
    Assert-True ([string]$normalized.scenarioClass -ceq 'evolution') 'Entity Framework Core normalized class must remain evolution.'
    Assert-True ([string]$normalized.comparisonPolicy -ceq 'side-by-side-no-cross-version-ratio') 'Entity Framework Core comparison policy drifted.'
    Assert-True (@($normalized.records).Count -eq 8) 'Expected eight normalized Entity Framework Core raw records.'
    Assert-True (@($normalized.sideBySide).Count -eq 2) 'Expected two Entity Framework Core side-by-side comparisons.'

    $all=@($normalized.sideBySide | Where-Object method -eq 'ReadHundredRows')[0]
    Assert-True ([Math]::Abs([double]$all.baselineMeanNs-950.0) -lt 0.001) 'Entity Framework Core baseline center is incorrect.'
    Assert-True ([Math]::Abs([double]$all.candidateMeanNs-1600.0) -lt 0.001) 'Entity Framework Core candidate center is incorrect.'
    Assert-True ($null -eq $all.PSObject.Properties['timeDeltaPercent']) 'Entity Framework Core evolution output must not contain timeDeltaPercent.'
    Assert-True ($null -eq $all.PSObject.Properties['allocationDeltaPercent']) 'Entity Framework Core evolution output must not contain allocationDeltaPercent.'

    $markdown=Get-Content -LiteralPath (Join-Path $tempRoot 'comparison.md') -Raw
    Assert-True ($markdown.Contains('Cross-version percentage deltas are intentionally omitted.')) 'Entity Framework Core Markdown must explain omitted deltas.'
    Assert-True (-not $markdown.Contains('Time Δ')) 'Entity Framework Core Markdown must not render strict timing delta.'

    Write-Output 'PERF_EFCORE_REPORT_TESTS_OK comparisons=2'
}
finally{
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
