param(
    [ValidateSet('verify', '30m', '60m', '120m')]
    [string]$Profile = 'verify',
    [string]$CandidateSha = '61ceef6bf69aef0a4f79b25384352d238979200f',
    [ValidateSet('Release')]
    [string]$Configuration = 'Release'
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

function Prepare-SoakTarget {
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

    $targetRoot = Join-Path $soakRoot $TargetId
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

    & dotnet @restoreArgs 2>&1 | Tee-Object -FilePath $restoreLog | Out-Host
    Assert-ExitCode "Generate soak lock for $TargetId"

    $entry = Get-SmartPipeCoreLockEntry -LockPath $lockFile
    if ([string]$entry.resolved -cne $ExpectedVersion) {
        throw "$TargetId soak restore resolved SmartPipe.Core '$($entry.resolved)' instead of '$ExpectedVersion'."
    }
    if ([string]$entry.contentHash -cne [string]$provenance.nugetContentHash) {
        throw "$TargetId soak restore contentHash differs from verified Core A/B provenance."
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

    & dotnet @lockedArgs 2>&1 | Tee-Object -FilePath $restoreLog -Append | Out-Host
    Assert-ExitCode "Locked soak restore for $TargetId"

    $buildArgs = @(
        'build', $ProjectPath,
        '--configuration', $Configuration,
        '--no-restore',
        '-warnaserror'
    )

    & dotnet @buildArgs 2>&1 | Tee-Object -FilePath $buildLog | Out-Host
    Assert-ExitCode "Build soak target $TargetId"

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

function Invoke-SoakTarget {
    param(
        [System.Collections.IDictionary]$Target,
        [int]$Slot
    )

    $targetId = [string]$Target.targetId
    $artifactDir = Join-Path $runRoot ("{0:D2}-{1}" -f $Slot, $targetId)
    New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null

    $stdoutPath = Join-Path $artifactDir 'stdout.jsonl'
    $started = [DateTimeOffset]::UtcNow
    $arguments = @(
        'run',
        '--project', [string]$Target.project,
        '--configuration', $Configuration,
        '--no-build', '--',
        '--profile', $Profile,
        '--items-per-run', '250'
    )

    $output = @(& dotnet @arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output | Set-Content -LiteralPath $stdoutPath -Encoding utf8
    $finished = [DateTimeOffset]::UtcNow

    if ($exitCode -ne 0) {
        throw "Soak profile '$Profile' for '$targetId' failed with exit code $exitCode."
    }

    $jsonObjects = [Collections.Generic.List[object]]::new()
    foreach ($line in $output) {
        $text = [string]$line
        if ([string]::IsNullOrWhiteSpace($text) -or -not $text.TrimStart().StartsWith('{')) {
            continue
        }

        try {
            $jsonObjects.Add(($text | ConvertFrom-Json -Depth 32))
        }
        catch {
            throw "Soak profile '$Profile' for '$targetId' emitted invalid JSON: $text"
        }
    }

    $final = $jsonObjects |
        Where-Object { [string]$_.Kind -ceq 'final' } |
        Select-Object -Last 1

    if ($null -eq $final) {
        throw "Soak profile '$Profile' for '$targetId' produced no final result."
    }

    $snapshots = @($jsonObjects | Where-Object { [string]$_.Kind -ceq 'snapshot' })
    if ($snapshots.Count -lt 2) {
        throw "Soak profile '$Profile' for '$targetId' produced fewer than two snapshots."
    }

    if ([long]$final.CompletedRuns -le 0) {
        throw "Soak profile '$Profile' for '$targetId' completed no runs."
    }
    if ([long]$final.Errors -ne 0) {
        throw "Soak profile '$Profile' for '$targetId' reported $($final.Errors) errors."
    }
    if ([long]$final.ActiveRuns -ne 0) {
        throw "Soak profile '$Profile' for '$targetId' ended with $($final.ActiveRuns) active runs."
    }
    if ([long]$final.CreatedComponents -ne [long]$final.DisposedComponents) {
        throw "Soak profile '$Profile' for '$targetId' leaked components: created=$($final.CreatedComponents), disposed=$($final.DisposedComponents)."
    }
    if (-not [bool]$final.LifecycleInvariantPassed) {
        throw "Soak profile '$Profile' for '$targetId' failed its lifecycle invariant."
    }

    return [ordered]@{
        slot = $Slot
        target = $targetId
        profile = $Profile
        startedUtc = $started.ToString('O')
        finishedUtc = $finished.ToString('O')
        durationSeconds = [double]$final.DurationSeconds
        completedRuns = [long]$final.CompletedRuns
        errors = [long]$final.Errors
        activeRuns = [long]$final.ActiveRuns
        createdComponents = [long]$final.CreatedComponents
        disposedComponents = [long]$final.DisposedComponents
        snapshotCount = [int]$final.SnapshotCount
        lifecycleInvariantPassed = [bool]$final.LifecycleInvariantPassed
        artifacts = [IO.Path]::GetRelativePath($repoRoot, $artifactDir).Replace('\', '/')
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$prepareCoreAb = Join-Path $repoRoot 'eng/perf/prepare-core-ab.ps1'
$report = Join-Path $repoRoot 'eng/perf/report-core-soak.ps1'

& $prepareCoreAb -CandidateSha $CandidateSha -Configuration $Configuration

$harnessSha = (& git -C $repoRoot rev-parse HEAD).Trim()
Assert-ExitCode 'Resolve harness SHA'

$sharedSource = Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Soak.Shared/Program.cs'
$sharedSourceSha = (Get-FileHash -LiteralPath $sharedSource -Algorithm SHA256).Hash.ToLowerInvariant()
$soakRoot = Join-Path $repoRoot 'artifacts/perf/core-soak'
New-Item -ItemType Directory -Path $soakRoot -Force | Out-Null

$v212 = Prepare-SoakTarget -TargetId 'v212' -ProjectPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Soak.V212/SmartPipe.Perf.Soak.V212.csproj') -ExpectedVersion '2.1.2'
$v220 = Prepare-SoakTarget -TargetId 'v220' -ProjectPath (Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Soak.V220/SmartPipe.Perf.Soak.V220.csproj') -ExpectedVersion '2.2.0'

$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + $harnessSha.Substring(0, 12)
$runRoot = Join-Path $repoRoot "artifacts/perf/runs/core-soak/$runId"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

$executions = [Collections.Generic.List[object]]::new()
$executions.Add((Invoke-SoakTarget -Target $v212 -Slot 1))
$executions.Add((Invoke-SoakTarget -Target $v220 -Slot 2))

$manifest = [ordered]@{
    schemaVersion = 1
    runId = $runId
    scenario = 'soak-leak'
    scenarioClass = 'evolution'
    comparisonPolicy = 'side-by-side-no-cross-version-ratio'
    profile = $Profile
    authoritativeTiming = $false
    timingPolicy = 'lifecycle-and-trend-evidence; hosted timing is informational'
    baselineSha = '8e79902d22de714f493582946f7c260462b0895e'
    candidateSha = $CandidateSha
    harnessSha = $harnessSha
    sharedSourceSha256 = $sharedSourceSha
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

Write-Output "PERF_CORE_SOAK_OK runId=$runId profile=$Profile order=v212,v220"
