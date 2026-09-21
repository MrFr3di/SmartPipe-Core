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
        'ReadHundredRows',
        'ReadSingleParameterized'
    )

    foreach ($method in $expectedMethods) {
        $benchmark = $benchmarks |
            Where-Object { [string]$_.Method -ceq $method } |
            Select-Object -First 1

        if ($null -eq $benchmark) {
            throw "$Step is missing BenchmarkDotNet result '$method'."
        }

        $statisticsProperty = $benchmark.PSObject.Properties['Statistics']
        if ($null -eq $statisticsProperty -or $null -eq $statisticsProperty.Value) {
            throw "$Step produced no statistics for '$method'. Treating BenchmarkDotNet exit code 0 as insufficient evidence."
        }

        $measurementsProperty = $benchmark.PSObject.Properties['Measurements']
        if ($null -eq $measurementsProperty -or @($measurementsProperty.Value).Count -eq 0) {
            throw "$Step produced no measurements for '$method'."
        }

        $memoryProperty = $benchmark.PSObject.Properties['Memory']
        if ($null -ne $memoryProperty -and
            $null -ne $memoryProperty.Value -and
            [long]$memoryProperty.Value.TotalOperations -le 0) {
            throw "$Step reported zero measured operations for '$method'."
        }
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$prepare = Join-Path $repoRoot 'eng/perf/prepare-dapper-evolution.ps1'
$report = Join-Path $repoRoot 'eng/perf/report-dapper-evolution.ps1'

& $prepare -CandidateSha $CandidateSha

$baselineProject = Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Dapper.V212/SmartPipe.Perf.Dapper.V212.csproj'
$candidateProject = Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Dapper.V220/SmartPipe.Perf.Dapper.V220.csproj'
$sharedSource = Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Dapper.Shared/DapperEvolutionBenchmarks.cs'
$sharedSourceSha = (Get-FileHash -LiteralPath $sharedSource -Algorithm SHA256).Hash.ToLowerInvariant()

$harnessSha = (& git -C $repoRoot rev-parse HEAD).Trim()
Assert-ExitCode 'Resolve harness SHA'

$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + $harnessSha.Substring(0, 12)
$runRoot = Join-Path $repoRoot "artifacts/perf/runs/dapper/$runId"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

$order = @(
    [ordered]@{ slot = 1; target = 'v212'; project = $baselineProject },
    [ordered]@{ slot = 2; target = 'v220'; project = $candidateProject },
    [ordered]@{ slot = 3; target = 'v220'; project = $candidateProject },
    [ordered]@{ slot = 4; target = 'v212'; project = $baselineProject }
)

$executions = [Collections.Generic.List[object]]::new()

foreach ($entry in $order) {
    $slot = [int]$entry.slot
    $target = [string]$entry.target
    $project = [string]$entry.project
    $artifactDir = Join-Path $runRoot ("{0:D2}-{1}" -f $slot, $target)
    New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null

    $started = [DateTimeOffset]::UtcNow
    $arguments = @(
        'run',
        '--project', $project,
        '--configuration', 'Release',
        '--no-build',
        '--',
        '--job', $Job,
        '--filter', '*DapperEvolutionBenchmarks*',
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
        target = $target
        startedUtc = $started.ToString('O')
        finishedUtc = $finished.ToString('O')
        elapsedSeconds = [Math]::Round(($finished - $started).TotalSeconds, 3)
        exitCode = $exitCode
        artifacts = [IO.Path]::GetRelativePath($repoRoot, $artifactDir).Replace('\', '/')
    })

    if ($exitCode -ne 0) {
        throw "Dapper evolution slot $slot ($target) failed with exit code $exitCode."
    }

    Assert-BenchmarkResult -Artifacts $artifactDir -Step "Dapper evolution slot $slot ($target)"
}

$manifest = [ordered]@{
    schemaVersion = 1
    runId = $runId
    scenario = 'dapper'
    scenarioClass = 'evolution'
    comparisonPolicy = 'side-by-side-no-cross-version-ratio'
    job = $Job
    authoritativeTiming = $false
    timingPolicy = 'github-hosted-informational-only'
    baselineSha = '8e79902d22de714f493582946f7c260462b0895e'
    candidateSha = $CandidateSha
    harnessSha = $harnessSha
    sharedSourceSha256 = $sharedSourceSha
    order = @($executions)
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

Write-Output "PERF_OPENTELEMETRY_EVOLUTION_RUN_OK runId=$runId job=$Job order=v212,v220,v220,v212"
