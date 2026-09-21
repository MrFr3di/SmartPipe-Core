param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition,[string]$Message)
    if(-not $Condition){ throw $Message }
}

$methods=@('ZeroChildren','OneChild','ThreeChildren')

function Write-SyntheticResult {
    param(
        [string]$Root,
        [string]$Slot,
        [double]$Mean,
        [double]$Allocated
    )

    $results=Join-Path $Root "$Slot/results"
    New-Item -ItemType Directory -Path $results -Force | Out-Null

    $benchmarks=[Collections.Generic.List[object]]::new()
    for($index=0;$index -lt $methods.Count;$index++){
        $scale=$index+1
        $benchmarks.Add([ordered]@{
            DisplayInfo="Synthetic Composite focus $($methods[$index])"
            Namespace='SmartPipe.Perf.ExtensionsFocus'
            Type='CompositeFocusBenchmarks'
            Method=$methods[$index]
            MethodTitle=$methods[$index]
            Parameters=''
            FullName="Synthetic Composite focus $($methods[$index])"
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
        Title='Synthetic Composite focus'
        HostEnvironmentInfo=[ordered]@{}
        Benchmarks=@($benchmarks)
    } | ConvertTo-Json -Depth 32 |
        Set-Content -LiteralPath (Join-Path $results 'Synthetic-report-full-compressed.json') -Encoding utf8
}

$repoRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$report=Join-Path $repoRoot 'eng/perf/report-extensions-focus.ps1'
$tempRoot=Join-Path ([IO.Path]::GetTempPath()) ("smartpipe-perf-extensions-focus-"+[Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    [ordered]@{
        schemaVersion=1
        runId='synthetic-extensions-focus-report-test'
        scenario='extensions-focus'
        scenarioClass='strict-ab'
        comparisonPolicy='strict-cross-version-ratio'
        authoritativeTiming=$false
        baselineSha='8e79902d22de714f493582946f7c260462b0895e'
        candidateSha='61ceef6bf69aef0a4f79b25384352d238979200f'
        harnessSha='0000000000000000000000000000000000000000'
    } | ConvertTo-Json -Depth 16 |
        Set-Content -LiteralPath (Join-Path $tempRoot 'run-manifest.json') -Encoding utf8

    Write-SyntheticResult -Root $tempRoot -Slot '01-v212' -Mean 100.0 -Allocated 10.0
    Write-SyntheticResult -Root $tempRoot -Slot '02-v220' -Mean 120.0 -Allocated 12.0
    Write-SyntheticResult -Root $tempRoot -Slot '03-v220' -Mean 140.0 -Allocated 14.0
    Write-SyntheticResult -Root $tempRoot -Slot '04-v212' -Mean 80.0 -Allocated 10.0

    $output=@(& $report -RunRoot $tempRoot)
    Assert-True ($output -contains 'PERF_EXTENSIONS_FOCUS_REPORT_OK run=synthetic-extensions-focus-report-test comparisons=3') 'Composite focus report did not complete successfully.'

    $normalized=Get-Content -LiteralPath (Join-Path $tempRoot 'normalized-results.json') -Raw | ConvertFrom-Json -Depth 64
    Assert-True ([string]$normalized.scenarioClass -ceq 'strict-ab') 'Composite focus class must remain strict-ab.'
    Assert-True ([string]$normalized.comparisonPolicy -ceq 'strict-cross-version-ratio') 'Composite focus comparison policy drifted.'
    Assert-True (@($normalized.records).Count -eq 12) 'Expected 12 Composite focus raw records.'
    Assert-True (@($normalized.comparisons).Count -eq 3) 'Expected three Composite focus comparisons.'

    $zero=@($normalized.comparisons | Where-Object method -eq 'ZeroChildren')[0]
    Assert-True ([Math]::Abs([double]$zero.baselineMeanNs-90.0)-lt 0.001) 'Composite focus baseline center is incorrect.'
    Assert-True ([Math]::Abs([double]$zero.candidateMeanNs-130.0)-lt 0.001) 'Composite focus candidate center is incorrect.'
    Assert-True ([Math]::Abs([double]$zero.timeDeltaPercent-44.4444444444)-lt 0.001) 'Composite focus timing delta is incorrect.'

    $markdown=Get-Content -LiteralPath (Join-Path $tempRoot 'comparison.md') -Raw
    Assert-True ($markdown.Contains('+44.44%')) 'Composite focus Markdown must render strict timing delta.'
    Assert-True ($markdown.Contains('GitHub-hosted timing remains informational')) 'Composite focus Markdown must preserve timing authority warning.'

    Write-Output 'PERF_EXTENSIONS_FOCUS_REPORT_TESTS_OK comparisons=3'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
