param(
    [string]$CandidateSha = '61ceef6bf69aef0a4f79b25384352d238979200f',

    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [switch]$RunDry
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$expectedMethods = @(
    'RawSingle',
    'PipelineSingle',
    'CompiledPipelineSingle',
    'RawHundredRows',
    'PipelineHundredRows',
    'CompiledPipelineHundredRows'
)

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

        $memory = $benchmark.PSObject.Properties['Memory']
        if ($null -ne $memory -and
            $null -ne $memory.Value -and
            [long]$memory.Value.TotalOperations -le 0) {
            throw "$Step reported zero measured operations for '$method'."
        }
    }
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
            throw "dotnet nuget verify did not report a content hash for '$PackagePath'."
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

function New-TargetNuGetConfig {
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

function Clear-SmartPipeGlobalPackages {
    if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        throw 'NUGET_PACKAGES must be set for efcore-decomposition provenance verification.'
    }

    if (-not (Test-Path -LiteralPath $env:NUGET_PACKAGES -PathType Container)) {
        return
    }

    Get-ChildItem -LiteralPath $env:NUGET_PACKAGES -Directory |
        Where-Object { $_.Name -like 'smartpipe.*' } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

function Get-Net10DependencyGroup {
    param([string]$LockPath)

    $lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json -Depth 64
    $framework = $lock.dependencies.PSObject.Properties |
        Where-Object { $_.Name -like 'net10.0*' } |
        Select-Object -First 1

    if ($null -eq $framework) {
        throw "No net10.0 dependency group found in '$LockPath'."
    }

    return $framework.Value
}

function Resolve-LocalPackage {
    param(
        [string]$PackagesDir,
        [string]$PackageId,
        [string]$Version
    )

    $expectedName = "$PackageId.$Version.nupkg"
    $package = Get-ChildItem -LiteralPath $PackagesDir -File -Filter '*.nupkg' |
        Where-Object { $_.Name -notlike '*.symbols.nupkg' } |
        Where-Object { $_.Name -ieq $expectedName } |
        Select-Object -First 1

    if ($null -eq $package) {
        throw "Expected local package '$expectedName' was not found in '$PackagesDir'."
    }

    return $package
}

function Get-RestoredSource {
    param(
        [string]$PackageId,
        [string]$Version,
        [string]$ExpectedFeed
    )

    $packageRoot = Join-Path $env:NUGET_PACKAGES (
        $PackageId.ToLowerInvariant() + '/' + $Version.ToLowerInvariant())
    $metadataPath = Join-Path $packageRoot '.nupkg.metadata'

    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        throw "NuGet metadata is missing for '$PackageId/$Version'."
    }

    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json -Depth 16
    $source = [string]$metadata.source

    if ([string]::IsNullOrWhiteSpace($source)) {
        throw "NuGet metadata source is missing for '$PackageId/$Version'."
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
        throw "Package '$PackageId/$Version' came from unexpected source '$source'."
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
        throw "Package '$PackageId/$Version' restored from '$actualPath'; expected '$expectedPath'."
    }

    return $source
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$targetsManifest = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/manifests/targets.json') -Raw | ConvertFrom-Json -Depth 64
$pinnedCandidate = [string]$targetsManifest.candidate.gitSha

if ($CandidateSha -cne $pinnedCandidate) {
    throw "CandidateSha '$CandidateSha' does not match pinned candidate '$pinnedCandidate'."
}

$prepareTargets = Join-Path $repoRoot 'eng/perf/prepare-target.ps1'
$shortSha = $CandidateSha.Substring(0, 12)
$candidateRoot = Join-Path $repoRoot "artifacts/perf/targets/candidate-$shortSha"
$targetManifestPath = Join-Path $candidateRoot 'target.json'

if (-not (Test-Path -LiteralPath $targetManifestPath -PathType Leaf)) {
    & $prepareTargets -Target candidate -CandidateSha $CandidateSha -Configuration $Configuration
}

$targetManifest = Get-Content -LiteralPath $targetManifestPath -Raw | ConvertFrom-Json -Depth 64
$packagesDir = Join-Path $candidateRoot 'packages'
$project = Join-Path $repoRoot 'perf/src/SmartPipe.Perf.EntityFrameworkCore.Decomposition.V220/SmartPipe.Perf.EntityFrameworkCore.Decomposition.V220.csproj'
$source = Join-Path $repoRoot 'perf/src/SmartPipe.Perf.EntityFrameworkCore.Decomposition.V220/EfCoreDecompositionBenchmarks.cs'
$artifactsRoot = Join-Path $repoRoot 'artifacts/perf/efcore-decomposition/v220'

Remove-Item -LiteralPath $artifactsRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

$nugetConfig = Join-Path $artifactsRoot 'nuget.config'
$lockFile = Join-Path $artifactsRoot 'packages.lock.json'
$restoreLog = Join-Path $artifactsRoot 'restore.txt'
$buildLog = Join-Path $artifactsRoot 'build.txt'

New-TargetNuGetConfig -Path $nugetConfig -LocalFeed $packagesDir
Clear-SmartPipeGlobalPackages

$restoreArgs = @(
    'restore',
    $project,
    '--configfile',
    $nugetConfig,
    '--use-lock-file',
    '--lock-file-path',
    $lockFile,
    '--force-evaluate',
    '--no-http-cache',
    '-p:RestorePackagesWithLockFile=true',
    '-p:DisableImplicitLibraryPacksFolder=true',
    '--verbosity',
    'minimal'
)

& dotnet @restoreArgs 2>&1 | Tee-Object -FilePath $restoreLog | Out-Host
Assert-ExitCode 'Generate efcore-decomposition lock file'

$dependencyGroup = Get-Net10DependencyGroup -LockPath $lockFile
$smartPipeEntries = @(
    $dependencyGroup.PSObject.Properties |
        Where-Object { $_.Name -like 'SmartPipe.*' } |
        Sort-Object Name
)

$primaryEntry = $smartPipeEntries |
    Where-Object { $_.Name -ceq 'SmartPipe.Extensions.EntityFrameworkCore' } |
    Select-Object -First 1

if ($null -eq $primaryEntry -or [string]$primaryEntry.Value.resolved -cne '2.2.0') {
    throw 'EF Core decomposition target must resolve SmartPipe.Extensions.EntityFrameworkCore 2.2.0 from the candidate feed.'
}

$packageProvenance = [Collections.Generic.List[object]]::new()

foreach ($entry in $smartPipeEntries) {
    $packageId = [string]$entry.Name
    $packageVersion = [string]$entry.Value.resolved
    $package = Resolve-LocalPackage -PackagesDir $packagesDir -PackageId $packageId -Version $packageVersion
    $nugetContentHash = Get-NuGetContentHash -PackagePath $package.FullName

    if ([string]$entry.Value.contentHash -cne $nugetContentHash) {
        throw "EF Core decomposition lock contentHash mismatch for '$packageId/$packageVersion'."
    }

    $restoredSource = Get-RestoredSource -PackageId $packageId -Version $packageVersion -ExpectedFeed $packagesDir

    $packageProvenance.Add([ordered]@{
        id = $packageId
        version = $packageVersion
        source = $restoredSource
        packagePath = [IO.Path]::GetRelativePath($repoRoot, $package.FullName).Replace('\', '/')
        packageSha256 = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        nugetContentHash = $nugetContentHash
    })
}

$lockedArgs = @(
    'restore',
    $project,
    '--configfile',
    $nugetConfig,
    '--locked-mode',
    '--no-http-cache',
    '--lock-file-path',
    $lockFile,
    '-p:RestorePackagesWithLockFile=true',
    '-p:DisableImplicitLibraryPacksFolder=true',
    '--verbosity',
    'minimal'
)

& dotnet @lockedArgs 2>&1 | Tee-Object -FilePath $restoreLog -Append | Out-Host
Assert-ExitCode 'Locked efcore-decomposition restore'

$buildArgs = @(
    'build',
    $project,
    '--configuration',
    $Configuration,
    '--no-restore',
    '-warnaserror'
)

& dotnet @buildArgs 2>&1 | Tee-Object -FilePath $buildLog | Out-Host
Assert-ExitCode 'Build efcore-decomposition v2.2-only target'

$harnessSha = (& git -C $repoRoot rev-parse HEAD).Trim()
Assert-ExitCode 'Resolve harness SHA'

[ordered]@{
    schemaVersion = 1
    scenario = 'efcore-decomposition'
    scenarioClass = 'v220-only'
    targetId = 'v220'
    productSha = [string]$targetManifest.productSha
    primaryPackageId = 'SmartPipe.Extensions.EntityFrameworkCore'
    primaryPackageVersion = '2.2.0'
    packages = @($packageProvenance)
    lockFile = [IO.Path]::GetRelativePath($repoRoot, $lockFile).Replace('\', '/')
    lockFileSha256 = (Get-FileHash -LiteralPath $lockFile -Algorithm SHA256).Hash.ToLowerInvariant()
    benchmarkSourceSha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    targetProjectSha256 = (Get-FileHash -LiteralPath $project -Algorithm SHA256).Hash.ToLowerInvariant()
    harnessSha = $harnessSha
} | ConvertTo-Json -Depth 64 |
    Set-Content -LiteralPath (Join-Path $artifactsRoot 'build-provenance.json') -Encoding utf8

if ($RunDry) {
    $bdnArtifacts = Join-Path $artifactsRoot 'BenchmarkDotNet.Artifacts'
    $benchmarkArgs = @(
        'run',
        '--project',
        $project,
        '--configuration',
        $Configuration,
        '--no-build',
        '--',
        '--job',
        'Dry',
        '--filter',
        '*EfCoreDecompositionBenchmarks*',
        '--artifacts',
        $bdnArtifacts,
        '--exporters',
        'json',
        '--stopOnFirstError'
    )

    & dotnet @benchmarkArgs
    Assert-ExitCode 'BenchmarkDotNet efcore-decomposition Dry'
    Assert-BenchmarkResult -Artifacts $bdnArtifacts -Step 'BenchmarkDotNet efcore-decomposition Dry'
}

(& dotnet --info) |
    Set-Content -LiteralPath (Join-Path $artifactsRoot 'dotnet-info.txt') -Encoding utf8

Write-Output "PERF_EFCORE_DECOMPOSITION_V220_READY candidate=$CandidateSha methods=$($expectedMethods.Count)"
