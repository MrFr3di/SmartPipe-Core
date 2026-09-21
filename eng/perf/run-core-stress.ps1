param(
    [string]$CandidateSha = '61ceef6bf69aef0a4f79b25384352d238979200f',

    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [int]$ItemsPerRun = 1000
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-ExitCode {
    param([string]$Step)

    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE."
    }
}

function Get-SmartPipeCoreLockEntry {
    param([string]$LockPath)

    $lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json -Depth 64
    $framework = $lock.dependencies.PSObject.Properties |
        Where-Object { $_.Name -like 'net10.0*' } |
        Select-Object -First 1

    if ($null -eq $framework) {
        throw "No net10.0 dependency group found in '$LockPath'."
    }

    $entry = $framework.Value.PSObject.Properties['SmartPipe.Core']
    if ($null -eq $entry) {
        throw "SmartPipe.Core is missing from '$LockPath'."
    }

    return $entry.Value
}

function Prepare-StressTarget {
    param(
        [string]$TargetId,
        [string]$ProjectPath,
        [string]$ExpectedVersion
    )

    $coreAbRoot = Join-Path $repoRoot "artifacts/perf/core-ab/$TargetId"
    $provenancePath = Join-Path $coreAbRoot 'build-provenance.json'
    $nugetConfig = Join-Path $coreAbRoot 'nuget.config'

    if (-not (Test-Path -LiteralPath $provenancePath -PathType Leaf)) {
        throw "Core A/B provenance is missing for '$TargetId'."
    }
    if (-not (Test-Path -LiteralPath $nugetConfig -PathType Leaf)) {
        throw "Core A/B NuGet config is missing for '$TargetId'."
    }

    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json -Depth 32
    if ([string]$provenance.expectedPackageVersion -cne $ExpectedVersion) {
        throw "$TargetId provenance package version '$($provenance.expectedPackageVersion)' does not match '$ExpectedVersion'."
    }

    $targetRoot = Join-Path $stressRoot $TargetId
    Remove-Item -LiteralPath $targetRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null

    $lockFile = Join-Path $targetRoot 'packages.lock.json'
    $restoreLog = Join-Path $targetRoot 'restore.txt'
    $buildLog = Join-Path $targetRoot 'build.txt'

    $restoreArgs = @(
        'restore', $ProjectPath,
        '--configfile', $nugetConfig,
        '--use-lock-file',
        '--lock-file-path', $lockFile,
        '--force-evaluate',
        '--no-http-cache',
        '-p:RestorePackagesWithLockFile=true',
        '-p:DisableImplicitLibraryPacksFolder=true',
        '--verbosity', 'minimal'
    )

    & dotnet @restoreArgs 2>&1 | Tee-Object -FilePath $restoreLog
    Assert-ExitCode "Generate stress lock for $TargetId"

    $entry = Get-SmartPipeCoreLockEntry -LockPath $lockFile
    if ([string]$entry.resolved -cne $ExpectedVersion) {
        throw "$TargetId stress restore resolved SmartPipe.Core '$($entry.resolved)' instead of '$ExpectedVersion'."
    }
    if ([string]$entry.contentHash -cne [string]$provenance.nugetContentHash) {
        throw "$TargetId stress restore contentHash differs from verified Core A/B provenance."
    }

    $lockedArgs = @(
        'restore', $ProjectPath,
        '--configfile', $nugetConfig,
        '--locked-mode',
        '--no-http-cache',
        '--lock-file-path', $lockFile,
        '-p:RestorePackagesWithLockFile=true',
        '-p:DisableImplicitLibraryPacksFolder=true',
        '--verbosity', 'minimal'
    )

    & dotnet @lockedArgs 2>&1 | Tee-Object -FilePath $restoreLog -Append
    Assert-ExitCode "Locked stress restore for $TargetId"

    $buildArgs = @(
        'build', $ProjectPath,
        '--configuration', $Configuration,
        '--no-restore',
        '-warnaserror'
    )

    & dotnet @buildArgs 2>&1 | Tee-Object -FilePath $buildLog
    Assert-ExitCode "Build stress target $TargetId"

    return [ordered]@{
        targetId = $TargetId
        project = $ProjectPath
        expectedVersion = $ExpectedVersion
        lockFile = $lockFile
        lockFileSha256 = (Get-FileHash -LiteralPath $lockFile -Algorithm SHA256).Hash.ToLowerInvariant()
        packageContentHash = [string]$entry.contentHash
        productSha = [string]$provenance.productSha
    }
}

function Invoke-StressProfile {
    param(
        [hashtable]$Target,
        [string]$Profile,
        [int]$Slot
    )

    $targetId = [string]$Target.targetId
    $artifactDir = Join-Path $runRoot ("{0:D2}-{1}-{2}" -f $Slot, $Profile, $targetId)
    New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null

    $stdoutPath = Join-Path $artifactDir 'stdout.jsonl'
    $started = [DateTimeOffset]::UtcNow
    $arguments = @(
        'run',
        '--project', [string]$Target.project,
        '--configuration', $Configuration,
        '--no-build', '--',
        '--profile', $Profile,
        '--items-per-run', $ItemsPerRun
    )

    $output = @(& dotnet @arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output | Set-Content -LiteralPath $stdoutPath -Encoding utf8
    $finished = [DateTimeOffset]::UtcNow

    if ($exitCode -ne 0) {
        throw "Stress profile '$Profile' for '$targetId' failed with exit code $exitCode."
    }

    $jsonLine = $output |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        Select-Object -Last 1

    if ($null -eq $jsonLine) {
        throw "Stress profile '$Profile' for '$targetId' produced no result JSON."
    }

    try {
        $result = [string]$jsonLine | ConvertFrom-Json -Depth 32
    }
    catch {
        throw "Stress profile '$Profile' for '$targetId' did not end with valid result JSON: $jsonLine"
    }

    if ([long]$result.Errors -ne 0) {
        throw "Stress profile '$Profile' for '$targetId' reported $($result.Errors) errors."
    }
    if ([long]$result.CompletedItems -ne [long]$result.ExpectedItems) {
        throw "Stress profile '$Profile' for '$targetId' completed $($result.CompletedItems) of $($result.ExpectedItems) expected items."
    }
    if ([long]$result.Checksum -ne [long]$result.ExpectedChecksum) {
        throw "Stress profile '$Profile' for '$targetId' checksum mismatch."
    }

    return [ordered]@{
        slot = $Slot
        target = $targetId
        profile = $Profile
        startedUtc = $started.ToString('O')
        finishedUtc = $finished.ToString('O')
        elapsedMilliseconds = [double]$result.ElapsedMilliseconds
        completedItems = [long]$result.CompletedItems
        expectedItems = [long]$result.ExpectedItems
        errors = [long]$result.Errors
        processWorkingSetBytes = [long]$result.ProcessWorkingSetBytes
        threadPoolThreadCount = [int]$result.ThreadPoolThreadCount
        threadPoolPendingWorkItems = [long]$result.ThreadPoolPendingWorkItems
        artifacts = [IO.Path]::GetRelativePath($repoRoot, $artifactDir).Replace('\', '/')
    }
}

if ($ItemsPerRun -le 0) {
    throw 'ItemsPerRun must be positive.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$prepareCoreAb = Join-Path $repoRoot 'eng/perf/prepare-core-ab.ps1'
& $prepareCoreAb -CandidateSha $CandidateSha -Configuration $Configuration

$harnessSha = (& git -C $repoRoot rev-parse HEAD).Trim()
Assert-ExitCode 'Resolve harness SHA'

$sharedStressSource = Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Stress.Shared/Program.cs'
$sharedStressSourceSha = (Get-FileHash -LiteralPath $sharedStressSource -Algorithm SHA256).Hash.ToLowerInvariant()
$stressRoot = Join-Path $repoRoot 'artifacts/perf/core-stress'
New-Item -ItemType Directory -Path $stressRoot -Force | Out-Null

$v212 = Prepare-StressTarget -TargetId 'v212' -ProjectPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Stress.V212/SmartPipe.Perf.Stress.V212.csproj') -ExpectedVersion '2.1.2'
$v220 = Prepare-StressTarget -TargetId 'v220' -ProjectPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Stress.V220/SmartPipe.Perf.Stress.V220.csproj') -ExpectedVersion '2.2.0'

$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + $harnessSha.Substring(0, 12)
$runRoot = Join-Path $repoRoot "artifacts/perf/runs/core-stress/$runId"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

$executions = [Collections.Generic.List[object]]::new()
$executions.Add((Invoke-StressProfile -Target $v212 -Profile 'parallel32' -Slot 1))
$executions.Add((Invoke-StressProfile -Target $v220 -Profile 'parallel32' -Slot 2))
$executions.Add((Invoke-StressProfile -Target $v220 -Profile 'sequential1000' -Slot 3))
$executions.Add((Invoke-StressProfile -Target $v212 -Profile 'sequential1000' -Slot 4))

$manifest = [ordered]@{
    schemaVersion = 1
    runId = $runId
    scenario = 'core-runtime-stress'
    scenarioClass = 'strict-ab'
    authoritativeTiming = $false
    timingPolicy = 'correctness-and-resource-evidence; hosted elapsed time is informational'
    baselineSha = '8e79902d22de714f493582946f7c260462b0895e'
    candidateSha = $CandidateSha
    harnessSha = $harnessSha
    sharedStressSourceSha256 = $sharedStressSourceSha
    itemsPerRun = $ItemsPerRun
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

$manifest | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath (Join-Path $runRoot 'run-manifest.json') -Encoding utf8
(& dotnet --info) | Set-Content -LiteralPath (Join-Path $runRoot 'dotnet-info.txt') -Encoding utf8

Write-Output "PERF_CORE_STRESS_OK runId=$runId order=parallel32:v212,v220;sequential1000:v220,v212"
