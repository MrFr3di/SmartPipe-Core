param(
    [string]$CandidateSha = '61ceef6bf69aef0a4f79b25384352d238979200f'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-PackageInfo {
    param([System.IO.FileInfo]$Package)

    $archive = [IO.Compression.ZipFile]::OpenRead($Package.FullName)

    try {
        $nuspec = $archive.Entries |
            Where-Object { $_.FullName -like '*.nuspec' } |
            Select-Object -First 1

        if ($null -eq $nuspec) {
            throw "Package '$($Package.FullName)' contains no nuspec."
        }

        $reader = [IO.StreamReader]::new($nuspec.Open())
        try {
            [xml]$xml = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $metadata = $xml.package.metadata
        $id = [string]$metadata.id
        $version = [string]$metadata.version

        if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($version)) {
            throw "Package '$($Package.FullName)' has invalid nuspec id/version."
        }

        $dependencies = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

        $namespaceUri = [string]$xml.DocumentElement.NamespaceURI
        if ([string]::IsNullOrWhiteSpace($namespaceUri)) {
            $nodes = @($xml.SelectNodes('//dependency'))
        }
        else {
            $namespaceManager = [Xml.XmlNamespaceManager]::new($xml.NameTable)
            $namespaceManager.AddNamespace('n', $namespaceUri)
            $nodes = @($xml.SelectNodes('//n:dependency', $namespaceManager))
        }

        foreach ($node in $nodes) {
            $dependencyId = [string]$node.id
            if (-not [string]::IsNullOrWhiteSpace($dependencyId)) {
                [void]$dependencies.Add($dependencyId)
            }
        }

        $uncompressedBytes = [long]0
        foreach ($entry in $archive.Entries) {
            $uncompressedBytes += [long]$entry.Length
        }

        return [ordered]@{
            id = $id
            version = $version
            path = $Package.FullName
            compressedBytes = [long]$Package.Length
            uncompressedBytes = $uncompressedBytes
            dependencies = @($dependencies | Sort-Object)
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-PackageMap {
    param([string]$PackagesDirectory)

    if (-not (Test-Path -LiteralPath $PackagesDirectory -PathType Container)) {
        throw "Package directory is missing: $PackagesDirectory"
    }

    $map = @{}

    foreach ($package in Get-ChildItem -LiteralPath $PackagesDirectory -File -Filter '*.nupkg') {
        if ($package.Name -like '*.symbols.nupkg') {
            continue
        }

        $info = Get-PackageInfo -Package $package
        $key = ([string]$info.id).ToLowerInvariant()

        if ($map.ContainsKey($key)) {
            throw "Duplicate package id '$($info.id)' in '$PackagesDirectory'."
        }

        $map[$key] = $info
    }

    if ($map.Count -eq 0) {
        throw "No NuGet packages found in '$PackagesDirectory'."
    }

    return $map
}

function Get-SmartPipeClosure {
    param(
        [hashtable]$Map,
        [string]$RootId
    )

    $rootKey = $RootId.ToLowerInvariant()
    if (-not $Map.ContainsKey($rootKey)) {
        return $null
    }

    $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($RootId)

    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()

        if (-not $visited.Add($current)) {
            continue
        }

        $currentKey = $current.ToLowerInvariant()
        if (-not $Map.ContainsKey($currentKey)) {
            continue
        }

        $package = $Map[$currentKey]

        foreach ($dependency in @($package.dependencies)) {
            if ([string]$dependency -like 'SmartPipe.*') {
                $queue.Enqueue([string]$dependency)
            }
        }
    }

    $packages = [Collections.Generic.List[object]]::new()
    $externalDependencies = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [long]$compressed = 0
    [long]$uncompressed = 0

    foreach ($id in $visited | Sort-Object) {
        $key = $id.ToLowerInvariant()
        if (-not $Map.ContainsKey($key)) {
            continue
        }

        $package = $Map[$key]
        $packages.Add([ordered]@{
            id = [string]$package.id
            version = [string]$package.version
            compressedBytes = [long]$package.compressedBytes
            uncompressedBytes = [long]$package.uncompressedBytes
        })

        $compressed += [long]$package.compressedBytes
        $uncompressed += [long]$package.uncompressedBytes

        foreach ($dependency in @($package.dependencies)) {
            if ([string]$dependency -notlike 'SmartPipe.*') {
                [void]$externalDependencies.Add([string]$dependency)
            }
        }
    }

    return [ordered]@{
        root = $RootId
        packageCount = $packages.Count
        compressedBytes = $compressed
        uncompressedBytes = $uncompressed
        externalDependencyCount = $externalDependencies.Count
        externalDependencies = @($externalDependencies | Sort-Object)
        packages = @($packages)
    }
}

function Get-TargetSummary {
    param(
        [hashtable]$Map,
        [string]$TargetId
    )

    $packages = @($Map.Values | Sort-Object { [string]$_.id })
    [long]$compressedBytes = 0
    [long]$uncompressedBytes = 0

    foreach ($package in $packages) {
        $compressedBytes += [long]$package.compressedBytes
        $uncompressedBytes += [long]$package.uncompressedBytes
    }

    return [ordered]@{
        target = $TargetId
        packageCount = $packages.Count
        compressedBytes = $compressedBytes
        uncompressedBytes = $uncompressedBytes
        packages = @(
            $packages | ForEach-Object {
                [ordered]@{
                    id = [string]$_.id
                    version = [string]$_.version
                    compressedBytes = [long]$_.compressedBytes
                    uncompressedBytes = [long]$_.uncompressedBytes
                    dependencies = @($_.dependencies)
                }
            }
        )
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$prepareTarget = Join-Path $repoRoot 'eng/perf/prepare-target.ps1'
$targetManifest = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/manifests/targets.json') -Raw | ConvertFrom-Json -Depth 64
$pinnedCandidate = [string]$targetManifest.candidate.gitSha

if ($CandidateSha -cne $pinnedCandidate) {
    throw "CandidateSha '$CandidateSha' does not match pinned candidate '$pinnedCandidate'."
}

$shortSha = $CandidateSha.Substring(0, 12)
$baselineRoot = Join-Path $repoRoot 'artifacts/perf/targets/2.1.2'
$candidateRoot = Join-Path $repoRoot "artifacts/perf/targets/candidate-$shortSha"

if (-not (Test-Path -LiteralPath (Join-Path $baselineRoot 'target.json') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $candidateRoot 'target.json') -PathType Leaf)) {
    & $prepareTarget -Target all -CandidateSha $CandidateSha
}

$baselineMap = Get-PackageMap -PackagesDirectory (Join-Path $baselineRoot 'packages')
$candidateMap = Get-PackageMap -PackagesDirectory (Join-Path $candidateRoot 'packages')

$capabilities = @(
    [ordered]@{ id = 'core'; baseline = 'SmartPipe.Core'; candidate = 'SmartPipe.Core' },
    [ordered]@{ id = 'json'; baseline = 'SmartPipe.Extensions.Json'; candidate = 'SmartPipe.Extensions.Json' },
    [ordered]@{ id = 'dependency-injection'; baseline = 'SmartPipe.Extensions'; candidate = 'SmartPipe.Extensions.DependencyInjection' },
    [ordered]@{ id = 'hosting'; baseline = 'SmartPipe.Extensions'; candidate = 'SmartPipe.Extensions.Hosting' },
    [ordered]@{ id = 'health-checks'; baseline = 'SmartPipe.Extensions'; candidate = 'SmartPipe.Extensions.HealthChecks' },
    [ordered]@{ id = 'opentelemetry'; baseline = 'SmartPipe.Extensions'; candidate = 'SmartPipe.Extensions.OpenTelemetry' },
    [ordered]@{ id = 'channels'; baseline = 'SmartPipe.Extensions'; candidate = 'SmartPipe.Extensions.Channels' },
    [ordered]@{ id = 'transforms'; baseline = 'SmartPipe.Extensions'; candidate = 'SmartPipe.Extensions.Transforms' },
    [ordered]@{ id = 'dataannotations'; baseline = 'SmartPipe.Extensions'; candidate = 'SmartPipe.Extensions.DataAnnotations' },
    [ordered]@{ id = 'logging'; baseline = 'SmartPipe.Extensions'; candidate = 'SmartPipe.Extensions.Logging' },
    [ordered]@{ id = 'csv'; baseline = 'SmartPipe.Extensions'; candidate = 'SmartPipe.Extensions.Csv' },
    [ordered]@{ id = 'dapper'; baseline = 'SmartPipe.Extensions'; candidate = 'SmartPipe.Extensions.Dapper' },
    [ordered]@{ id = 'entity-framework-core'; baseline = 'SmartPipe.Extensions'; candidate = 'SmartPipe.Extensions.EntityFrameworkCore' }
)

$comparisons = [Collections.Generic.List[object]]::new()

foreach ($capability in $capabilities) {
    $baselineClosure = Get-SmartPipeClosure -Map $baselineMap -RootId ([string]$capability.baseline)
    $candidateClosure = Get-SmartPipeClosure -Map $candidateMap -RootId ([string]$capability.candidate)

    if ($null -eq $baselineClosure -or $null -eq $candidateClosure) {
        $comparisons.Add([ordered]@{
            capability = [string]$capability.id
            baselineRoot = [string]$capability.baseline
            candidateRoot = [string]$capability.candidate
            status = 'unavailable'
            baseline = $baselineClosure
            candidate = $candidateClosure
        })
        continue
    }

    $comparisons.Add([ordered]@{
        capability = [string]$capability.id
        baselineRoot = [string]$capability.baseline
        candidateRoot = [string]$capability.candidate
        status = 'available'
        baseline = $baselineClosure
        candidate = $candidateClosure
        compressedByteDelta = [long]$candidateClosure.compressedBytes - [long]$baselineClosure.compressedBytes
        uncompressedByteDelta = [long]$candidateClosure.uncompressedBytes - [long]$baselineClosure.uncompressedBytes
        packageCountDelta = [int]$candidateClosure.packageCount - [int]$baselineClosure.packageCount
    })
}

$outputRoot = Join-Path $repoRoot 'artifacts/perf/package-footprint'
Remove-Item -LiteralPath $outputRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$harnessSha = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to resolve harness SHA.'
}

$result = [ordered]@{
    schemaVersion = 1
    scenario = 'package-infrastructure'
    scenarioClass = 'evolution'
    comparisonPolicy = 'package-closure-engineering-metrics'
    authoritativeTiming = $false
    baselineSha = '8e79902d22de714f493582946f7c260462b0895e'
    candidateSha = $CandidateSha
    harnessSha = $harnessSha
    baseline = Get-TargetSummary -Map $baselineMap -TargetId 'v212'
    candidate = Get-TargetSummary -Map $candidateMap -TargetId 'v220'
    capabilityClosures = @($comparisons)
}

$result |
    ConvertTo-Json -Depth 64 |
    Set-Content -LiteralPath (Join-Path $outputRoot 'package-footprint.json') -Encoding utf8

$culture = [Globalization.CultureInfo]::InvariantCulture
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# SmartPipe package / infrastructure footprint')
$lines.Add('')
$lines.Add('These are package-closure engineering metrics, not runtime performance claims.')
$lines.Add('')
$lines.Add("| Target | SmartPipe packages | Compressed MiB | Uncompressed MiB |")
$lines.Add("| --- | ---: | ---: | ---: |")

foreach ($target in @($result.baseline, $result.candidate)) {
    $compressed = ([double]$target.compressedBytes / 1MB).ToString('F3', $culture)
    $uncompressed = ([double]$target.uncompressedBytes / 1MB).ToString('F3', $culture)
    $lines.Add("| $($target.target) | $($target.packageCount) | $compressed | $uncompressed |")
}

$lines.Add('')
$lines.Add('## Representative leaf closures')
$lines.Add('')
$lines.Add('| Capability | 2.1.2 root | 2.2.0 root | Packages 2.1.2→2.2.0 | Compressed KiB 2.1.2→2.2.0 | External dependency IDs 2.1.2→2.2.0 |')
$lines.Add('| --- | --- | --- | ---: | ---: | ---: |')

foreach ($comparison in $comparisons) {
    if ([string]$comparison.status -cne 'available') {
        $lines.Add("| $($comparison.capability) | $($comparison.baselineRoot) | $($comparison.candidateRoot) | unavailable | unavailable | unavailable |")
        continue
    }

    $baselineKiB = ([double]$comparison.baseline.compressedBytes / 1KB).ToString('F1', $culture)
    $candidateKiB = ([double]$comparison.candidate.compressedBytes / 1KB).ToString('F1', $culture)
    $packageCounts = "$($comparison.baseline.packageCount)->$($comparison.candidate.packageCount)"
    $compressed = "$baselineKiB->$candidateKiB"
    $external = "$($comparison.baseline.externalDependencyCount)->$($comparison.candidate.externalDependencyCount)"

    $lines.Add("| $($comparison.capability) | $($comparison.baselineRoot) | $($comparison.candidateRoot) | $packageCounts | $compressed | $external |")
}

$lines |
    Set-Content -LiteralPath (Join-Path $outputRoot 'package-footprint.md') -Encoding utf8

Write-Output "PERF_PACKAGE_FOOTPRINT_OK baselinePackages=$($result.baseline.packageCount) candidatePackages=$($result.candidate.packageCount) capabilities=$($comparisons.Count)"
