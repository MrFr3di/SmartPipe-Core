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

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$prepare = Join-Path $repoRoot 'eng/perf/prepare-core-ab.ps1'
& $prepare -CandidateSha $CandidateSha

$baselineProject = Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Benchmarks.V212/SmartPipe.Perf.Benchmarks.V212.csproj'
$candidateProject = Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Benchmarks.V220/SmartPipe.Perf.Benchmarks.V220.csproj'
$sharedSource = Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Benchmarks.Shared/CorePipelineAbBenchmarks.cs'
$sharedSourceSha = (Get-FileHash -LiteralPath $sharedSource -Algorithm SHA256).Hash.ToLowerInvariant()
$harnessSha = (& git -C $repoRoot rev-parse HEAD).Trim()
Assert-ExitCode 'Resolve harness SHA'

$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + $harnessSha.Substring(0, 12)
$runRoot = Join-Path $repoRoot "artifacts/perf/runs/core-ab/$runId"
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
        'run', '--project', $project,
        '--configuration', 'Release',
        '--no-build', '--',
        '--job', $Job,
        '--filter', '*CorePipelineAbBenchmarks*',
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
        throw "Core A/B slot $slot ($target) failed with exit code $exitCode."
    }
}

$environment = [ordered]@{
    osDescription = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    frameworkDescription = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
    processorCount = [Environment]::ProcessorCount
    runnerOs = $env:RUNNER_OS
    runnerArch = $env:RUNNER_ARCH
    runnerImage = $env:ImageOS
    runnerImageVersion = $env:ImageVersion
    ci = $env:CI
}

$manifest = [ordered]@{
    schemaVersion = 1
    runId = $runId
    scenario = 'core-runtime'
    scenarioClass = 'strict-ab'
    job = $Job
    authoritativeTiming = $false
    timingPolicy = 'github-hosted-informational-unless-run-on-controlled-machine'
    baselineSha = '8e79902d22de714f493582946f7c260462b0895e'
    candidateSha = $CandidateSha
    harnessSha = $harnessSha
    sharedSourceSha256 = $sharedSourceSha
    order = @($executions)
    environment = $environment
}

$manifest | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath (Join-Path $runRoot 'run-manifest.json') -Encoding utf8
(& dotnet --info) | Set-Content -LiteralPath (Join-Path $runRoot 'dotnet-info.txt') -Encoding utf8

Write-Output "PERF_CORE_AB_RUN_OK runId=$runId job=$Job order=v212,v220,v220,v212"
