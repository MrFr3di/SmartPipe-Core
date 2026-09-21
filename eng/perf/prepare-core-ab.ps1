param(
    [string]$CandidateSha = '61ceef6bf69aef0a4f79b25384352d238979200f',

    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [switch]$RunDry
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-ExitCode {
    param([string]$Step)

    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE."
    }
}

function Invoke-DotNet {
    param(
        [string[]]$Arguments,
        [string]$Step
    )

    & dotnet @Arguments
    Assert-ExitCode $Step
}

function Get-NuGetContentHash {
    param([string]$PackagePath)

    $previousLanguage = $env:DOTNET_CLI_UI_LANGUAGE
    $env:DOTNET_CLI_UI_LANGUAGE = 'en-US'

    try {
        $output = @(& dotnet nuget verify $PackagePath --verbosity normal 2>&1)
        $hashLine = $output |
            Where-Object { [string]$_ -match '^\s*Content hash:\s*(?<hash>\S+)\s*$' } |
            Select-Object -First 1

        if ($null -eq $hashLine) {
            throw "dotnet nuget verify did not report a content hash for '$PackagePath'. Output: $($output -join [Environment]::NewLine)"
        }

        [void]([string]$hashLine -match '^\s*Content hash:\s*(?<hash>\S+)\s*$')
        if ([string]::IsNullOrWhiteSpace($Matches.hash)) {
            throw "Unable to parse NuGet content hash for '$PackagePath'."
        }

        return $Matches.hash
    }
    finally {
        if ($null -eq $previousLanguage) {
            Remove-Item Env:DOTNET_CLI_UI_LANGUAGE -ErrorAction SilentlyContinue
        }
        else {
            $env:DOTNET_CLI_UI_LANGUAGE = $previousLanguage
        }
    }
}

function New-NuGetConfig {
    param(
        [string]$Path,
        [string]$LocalFeed
    )

    $escapedFeed = [Security.SecurityElement]::Escape($LocalFeed)
    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="smartpipe-target" value="$escapedFeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="smartpipe-target">
      <package pattern="SmartPipe.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@

    Set-Content -LiteralPath $Path -Value $xml -Encoding utf8
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

function Assert-RestoredPackageSource {
    param(
        [string]$PackageId,
        [string]$PackageVersion,
        [string]$ExpectedFeed
    )

    if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        throw 'NUGET_PACKAGES must be set so package source provenance can be verified.'
    }

    $packageDirectoryName = $PackageId.ToLowerInvariant()
    $versionDirectoryName = $PackageVersion.ToLowerInvariant()
    $packageRoot = Join-Path $env:NUGET_PACKAGES "$packageDirectoryName/$versionDirectoryName"
    $metadataPath = Join-Path $packageRoot '.nupkg.metadata'

    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        throw "NuGet metadata is missing for restored package '$PackageId/$PackageVersion'."
    }

    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json -Depth 16
    $source = [string]$metadata.source
    if ([string]::IsNullOrWhiteSpace($source)) {
        throw "NuGet metadata does not contain a source for '$PackageId/$PackageVersion'."
    }

    $expectedPath = [IO.Path]::GetFullPath($ExpectedFeed)
    $parsedUri = $null

    if ([Uri]::TryCreate($source, [UriKind]::Absolute, [ref]$parsedUri) -and $parsedUri.IsFile) {
        $actualPath = [IO.Path]::GetFullPath($parsedUri.LocalPath)
    }
    elseif ([IO.Path]::IsPathFullyQualified($source)) {
        $actualPath = [IO.Path]::GetFullPath($source)
    }
    else {
        throw "Package '$PackageId/$PackageVersion' came from unexpected source '$source'; expected local target feed '$expectedPath'."
    }

    $expectedPath = $expectedPath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $actualPath = $actualPath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $comparison = if ([OperatingSystem]::IsWindows()) {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }

    if (-not [string]::Equals($actualPath, $expectedPath, $comparison)) {
        throw "Package '$PackageId/$PackageVersion' restored from '$actualPath'; expected local target feed '$expectedPath'."
    }

    return $source
}

function Prepare-BenchmarkTarget {
    param(
        [string]$TargetId,
        [string]$ProjectPath,
        [string]$TargetRoot,
        [string]$ExpectedPackageVersion
    )

    $packagesDir = Join-Path $TargetRoot 'packages'
    $targetManifestPath = Join-Path $TargetRoot 'target.json'
    if (-not (Test-Path -LiteralPath $targetManifestPath -PathType Leaf)) {
        throw "Target manifest is missing: $targetManifestPath"
    }

    $targetManifest = Get-Content -LiteralPath $targetManifestPath -Raw | ConvertFrom-Json -Depth 64
    $package = Get-ChildItem -LiteralPath $packagesDir -File -Filter 'SmartPipe.Core*.nupkg' |
        Where-Object { $_.Name -notlike '*.symbols.nupkg' -and $_.Name -notlike '*.snupkg' } |
        Where-Object { $_.Name -ceq "SmartPipe.Core.$ExpectedPackageVersion.nupkg" } |
        Select-Object -First 1

    if ($null -eq $package) {
        throw "Expected SmartPipe.Core.$ExpectedPackageVersion.nupkg was not found in '$packagesDir'."
    }

    $runRoot = Join-Path $artifactsRoot $TargetId
    Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

    $nugetConfig = Join-Path $runRoot 'nuget.config'
    $lockFile = Join-Path $runRoot 'packages.lock.json'
    $restoreLog = Join-Path $runRoot 'restore.txt'
    $buildLog = Join-Path $runRoot 'build.txt'

    New-NuGetConfig -Path $nugetConfig -LocalFeed $packagesDir

    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        $cachedSmartPipeCore = Join-Path $env:NUGET_PACKAGES ("smartpipe.core/" + $ExpectedPackageVersion.ToLowerInvariant())
        Remove-Item -LiteralPath $cachedSmartPipeCore -Recurse -Force -ErrorAction SilentlyContinue
    }

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
    Assert-ExitCode "Generate $TargetId lock file"

    $entry = Get-SmartPipeCoreLockEntry -LockPath $lockFile
    if ([string]$entry.resolved -cne $ExpectedPackageVersion) {
        throw "$TargetId restored SmartPipe.Core '$($entry.resolved)' instead of '$ExpectedPackageVersion'."
    }

    $expectedHash = Get-NuGetContentHash -PackagePath $package.FullName
    if ([string]$entry.contentHash -cne $expectedHash) {
        throw "$TargetId SmartPipe.Core lock contentHash does not match the NuGet-native content hash of the verified local package."
    }

    $restoredSource = Assert-RestoredPackageSource -PackageId 'SmartPipe.Core' -PackageVersion $ExpectedPackageVersion -ExpectedFeed $packagesDir

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
    Assert-ExitCode "Locked restore $TargetId"

    $buildArgs = @(
        'build', $ProjectPath,
        '--configuration', $Configuration,
        '--no-restore',
        '-warnaserror'
    )

    & dotnet @buildArgs 2>&1 | Tee-Object -FilePath $buildLog
    Assert-ExitCode "Build $TargetId comparative benchmark"

    $metadata = [ordered]@{
        schemaVersion = 1
        targetId = $TargetId
        productSha = [string]$targetManifest.productSha
        expectedPackageVersion = $ExpectedPackageVersion
        restoredSource = $restoredSource
        packagePath = [IO.Path]::GetRelativePath($repoRoot, $package.FullName).Replace('\', '/')
        packageSha256 = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        nugetContentHash = $expectedHash
        lockContentHash = [string]$entry.contentHash
        lockFile = [IO.Path]::GetRelativePath($repoRoot, $lockFile).Replace('\', '/')
        lockFileSha256 = (Get-FileHash -LiteralPath $lockFile -Algorithm SHA256).Hash.ToLowerInvariant()
        sharedSourceSha256 = $sharedSourceSha
        harnessSha = $harnessSha
    }

    $metadata | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath (Join-Path $runRoot 'build-provenance.json') -Encoding utf8

    if ($RunDry) {
        $bdnArtifacts = Join-Path $runRoot 'BenchmarkDotNet.Artifacts'
        Invoke-DotNet -Arguments @(
            'run', '--project', $ProjectPath,
            '--configuration', $Configuration,
            '--no-build', '--',
            '--job', 'Dry',
            '--filter', '*CorePipelineAbBenchmarks*',
            '--artifacts', $bdnArtifacts
        ) -Step "BenchmarkDotNet Dry $TargetId"
    }

    Write-Output "PERF_AB_TARGET_READY target=$TargetId project=$ProjectPath"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$prepareTargets = Join-Path $repoRoot 'eng/perf/prepare-target.ps1'
$targetsManifest = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/manifests/targets.json') -Raw | ConvertFrom-Json -Depth 64
$pinnedCandidate = [string]$targetsManifest.candidate.gitSha

if ($CandidateSha -cne $pinnedCandidate) {
    throw "CandidateSha '$CandidateSha' does not match pinned candidate '$pinnedCandidate'."
}

$shortSha = $CandidateSha.Substring(0, 12)
$baselineRoot = Join-Path $repoRoot 'artifacts/perf/targets/2.1.2'
$candidateRoot = Join-Path $repoRoot "artifacts/perf/targets/candidate-$shortSha"

if (-not (Test-Path -LiteralPath (Join-Path $baselineRoot 'target.json')) -or
    -not (Test-Path -LiteralPath (Join-Path $candidateRoot 'target.json'))) {
    & $prepareTargets -Target all -CandidateSha $CandidateSha -Configuration $Configuration
}

$harnessSha = (& git -C $repoRoot rev-parse HEAD).Trim()
Assert-ExitCode 'Resolve harness SHA'

$sharedSource = Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Benchmarks.Shared/CorePipelineAbBenchmarks.cs'
$sharedSourceSha = (Get-FileHash -LiteralPath $sharedSource -Algorithm SHA256).Hash.ToLowerInvariant()
$artifactsRoot = Join-Path $repoRoot 'artifacts/perf/core-ab'
New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

$baselineProject = Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Benchmarks.V212/SmartPipe.Perf.Benchmarks.V212.csproj'
$candidateProject = Join-Path $repoRoot 'perf/src/SmartPipe.Perf.Benchmarks.V220/SmartPipe.Perf.Benchmarks.V220.csproj'

Prepare-BenchmarkTarget -TargetId 'v212' -ProjectPath $baselineProject -TargetRoot $baselineRoot -ExpectedPackageVersion '2.1.2'
Prepare-BenchmarkTarget -TargetId 'v220' -ProjectPath $candidateProject -TargetRoot $candidateRoot -ExpectedPackageVersion '2.2.0'

(& dotnet --info) | Set-Content -LiteralPath (Join-Path $artifactsRoot 'dotnet-info.txt') -Encoding utf8

Write-Output "PERF_CORE_AB_READY baseline=2.1.2 candidate=$CandidateSha sourceSha256=$sharedSourceSha"
