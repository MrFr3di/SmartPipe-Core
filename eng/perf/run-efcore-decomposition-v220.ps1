param(
    [ValidateSet('Dry', 'Short', 'Medium', 'Default')]
    [string]$Job = 'Short',

    [string]$CandidateSha = '61ceef6bf69aef0a4f79b25384352d238979200f'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-ExitCode {
    param([string]$Step)

    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE."
    }
}

function Assert-BenchmarkResult {
    param(
        [string]$Artifacts,
        [string]$Step
    )

    $result = Get-ChildItem -LiteralPath $Artifacts -File -Recurse -Filter '*-report-full-compressed.json' -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if ($null -eq $result) {
        throw "$Step completed without a BenchmarkDotNet JSON result artifact."
    }

    $document = Get-Content -LiteralPath $result.FullName -Raw | ConvertFrom-Json -Depth 100
    $benchmarks = @($document.Benchmarks)
    $expectedMethods = @(
        'RawSingle',
        'PipelineSingle',
        'CompiledPipelineSingle',
        'RawHundredRows',
        'PipelineHundredRows',
        'CompiledPipelineHundredRows'
    )

    foreach ($method in $expectedMethods) {
        $benchmark = $benchmarks |
            Where-Object { [string]$_.Method -ceq $method } |
            Select-Object -First 1

        if ($null -eq $benchmark) {
            throw "$Step is missing BenchmarkDotNet result '$method'."
        }

        $statistics = $benchmark.PSObject.Properties['Statistics']
        if ($null -eq $statistics -or $null -eq $statistics.Value) {
            throw "$Step produced no statistics for '$method'."
        }

        $measurements = $benchmark.PSObject.Properties['Measurements']
        if ($null -eq $measurements -or @($measurements.Value).Count -eq 0) {
            throw "$Step produced no measurements for '$method'."
        }
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$prepare = Join-Path $repoRoot 'eng/perf/prepare-efcore-decomposition-v220.ps1'
$report = Join-Path $repoRoot 'eng/perf/report-efcore-decomposition-v220.ps1'

& $prepare -CandidateSha $CandidateSha

$project = Join-Path $repoRoot 'perf/src/SmartPipe.Perf.EntityFrameworkCore.Decomposition.V220/SmartPipe.Perf.EntityFrameworkCore.Decomposition.V220.csproj'
$source = Join-Path $repoRoot 'perf/src/SmartPipe.Perf.EntityFrameworkCore.Decomposition.V220/EfCoreDecompositionBenchmarks.cs'

$harnessSha = (& git -C $repoRoot rev-parse HEAD).Trim()
Assert-ExitCode 'Resolve harness SHA'

$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + $harnessSha.Substring(0, 12)
$runRoot = Join-Path $repoRoot "artifacts/perf/runs/efcore-decomposition/$runId"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

$executions = [Collections.Generic.List[object]]::new()

foreach ($slot in 1..2) {
    $artifactDir = Join-Path $runRoot ("{0:D2}-v220" -f $slot)
    New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null

    $started = [DateTimeOffset]::UtcNow
    $arguments = @(
        'run',
        '--project', $project,
        '--configuration', 'Release',
        '--no-build',
        '--',
        '--job', $Job,
        '--filter', '*EfCoreDecompositionBenchmarks*',
        '--artifacts', $artifactDir,
        '--exporters', 'json', 'csv', 'markdown',
        '--allStats',
        '--stopOnFirstError'
    )

    & dotnet @arguments
    $exitCode = $LASTEXITCODE
    $finished = [DateTimeOffset]::UtcNow

    $executions.Add([ordered]@{
        slot = $slot
        target = 'v220'
        startedUtc = $started.ToString('O')
        finishedUtc = $finished.ToString('O')
        elapsedSeconds = [Math]::Round(($finished - $started).TotalSeconds, 3)
        exitCode = $exitCode
        artifacts = [IO.Path]::GetRelativePath($repoRoot, $artifactDir).Replace('\', '/')
    })

    if ($exitCode -ne 0) {
        throw "EF Core decomposition slot $slot failed with exit code $exitCode."
    }

    Assert-BenchmarkResult -Artifacts $artifactDir -Step "EF Core decomposition slot $slot"
}

$manifest = [ordered]@{
    schemaVersion = 1
    runId = $runId
    scenario = 'efcore-decomposition'
    scenarioClass = 'v220-only'
    comparisonPolicy = 'within-version-raw-vs-normal-vs-compiled'
    job = $Job
    authoritativeTiming = $false
    timingPolicy = 'github-hosted-informational-only'
    candidateSha = $CandidateSha
    harnessSha = $harnessSha
    benchmarkSourceSha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    executions = @($executions)
    environment = [ordered]@{
        osDescription = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        frameworkDescription = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        processorCount = [Environment]::ProcessorCount
        runnerOs = $env:RUNNER_OS
        runnerArch = $env:RUNNER_ARCH
        runnerImage = $env:ImageOS
        runnerImageVersion = $env:ImageVersion
    }
}

$manifest |
    ConvertTo-Json -Depth 32 |
    Set-Content -LiteralPath (Join-Path $runRoot 'run-manifest.json') -Encoding utf8

(& dotnet --info) |
    Set-Content -LiteralPath (Join-Path $runRoot 'dotnet-info.txt') -Encoding utf8

& $report -RunRoot $runRoot

Write-Output "PERF_EFCORE_DECOMPOSITION_RUN_OK runId=$runId job=$Job repeats=2"
