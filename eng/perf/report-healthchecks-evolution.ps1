param(
    [Parameter(Mandatory = $true)]
    [string]$RunRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Median {
    param([double[]]$Values)

    if ($Values.Count -eq 0) {
        throw 'Median requires at least one value.'
    }

    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)

    if (($sorted.Count % 2) -eq 1) {
        return [double]$sorted[$middle]
    }

    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function Get-RepeatDriftPercent {
    param(
        [double[]]$Values,
        [double]$Center
    )

    if ($Values.Count -lt 2 -or $Center -eq 0) {
        return $null
    }

    return [Math]::Abs($Values[1] - $Values[0]) / $Center * 100.0
}

$resolvedRunRoot = (Resolve-Path -LiteralPath $RunRoot).Path
$runManifestPath = Join-Path $resolvedRunRoot 'run-manifest.json'

if (-not (Test-Path -LiteralPath $runManifestPath -PathType Leaf)) {
    throw "Run manifest is missing: $runManifestPath"
}

$runManifest = Get-Content -LiteralPath $runManifestPath -Raw | ConvertFrom-Json -Depth 64
if ([string]$runManifest.scenarioClass -cne 'evolution') {
    throw "HealthChecks report expects scenarioClass=evolution, got '$($runManifest.scenarioClass)'."
}

$records = [Collections.Generic.List[object]]::new()
$slotDirectories = Get-ChildItem -LiteralPath $resolvedRunRoot -Directory |
    Where-Object { $_.Name -match '^(?<slot>\d{2})-(?<target>v212|v220)$' } |
    Sort-Object Name

foreach ($slotDirectory in $slotDirectories) {
    [void]($slotDirectory.Name -match '^(?<slot>\d{2})-(?<target>v212|v220)$')
    $slot = [int]$Matches.slot
    $target = [string]$Matches.target

    $resultFile = Get-ChildItem -LiteralPath $slotDirectory.FullName -File -Recurse -Filter '*-report-full-compressed.json' |
        Select-Object -First 1

    if ($null -eq $resultFile) {
        throw "BenchmarkDotNet JSON result is missing in '$($slotDirectory.FullName)'."
    }

    $document = Get-Content -LiteralPath $resultFile.FullName -Raw | ConvertFrom-Json -Depth 100
    foreach ($benchmark in @($document.Benchmarks)) {
        $records.Add([ordered]@{
            slot = $slot
            target = $target
            method = [string]$benchmark.Method
            sampleCount = [int]$benchmark.Statistics.N
            meanNs = [double]$benchmark.Statistics.Mean
            medianNs = [double]$benchmark.Statistics.Median
            standardDeviationNs = [double]$benchmark.Statistics.StandardDeviation
            allocatedBytes = [double]$benchmark.Memory.BytesAllocatedPerOperation
            resultFile = [IO.Path]::GetRelativePath($resolvedRunRoot, $resultFile.FullName).Replace('\', '/')
        })
    }
}

if ($records.Count -eq 0) {
    throw 'No BenchmarkDotNet HealthChecks records were found.'
}

$comparisons = [Collections.Generic.List[object]]::new()
$groups = $records | Group-Object { [string]$_.method }

foreach ($group in $groups) {
    $baseline = @($group.Group | Where-Object { $_.target -ceq 'v212' } | Sort-Object slot)
    $candidate = @($group.Group | Where-Object { $_.target -ceq 'v220' } | Sort-Object slot)

    if ($baseline.Count -ne 2 -or $candidate.Count -ne 2) {
        throw "Evolution group '$($group.Name)' requires exactly two v212 and two v220 observations."
    }

    $baselineMeans = [double[]]@($baseline | ForEach-Object { [double]$_.meanNs })
    $candidateMeans = [double[]]@($candidate | ForEach-Object { [double]$_.meanNs })
    $baselineAllocations = [double[]]@($baseline | ForEach-Object { [double]$_.allocatedBytes })
    $candidateAllocations = [double[]]@($candidate | ForEach-Object { [double]$_.allocatedBytes })

    $baselineMeanCenter = Get-Median $baselineMeans
    $candidateMeanCenter = Get-Median $candidateMeans

    $comparisons.Add([ordered]@{
        method = [string]$group.Name
        baselineMeanNs = $baselineMeanCenter
        candidateMeanNs = $candidateMeanCenter
        baselineAllocatedBytes = Get-Median $baselineAllocations
        candidateAllocatedBytes = Get-Median $candidateAllocations
        baselineRepeatDriftPercent = Get-RepeatDriftPercent -Values $baselineMeans -Center $baselineMeanCenter
        candidateRepeatDriftPercent = Get-RepeatDriftPercent -Values $candidateMeans -Center $candidateMeanCenter
    })
}

$comparisons = @($comparisons | Sort-Object method)

$normalized = [ordered]@{
    schemaVersion = 1
    runId = [string]$runManifest.runId
    scenario = [string]$runManifest.scenario
    scenarioClass = [string]$runManifest.scenarioClass
    comparisonPolicy = 'side-by-side-no-cross-version-ratio'
    authoritativeTiming = [bool]$runManifest.authoritativeTiming
    baselineSha = [string]$runManifest.baselineSha
    candidateSha = [string]$runManifest.candidateSha
    harnessSha = [string]$runManifest.harnessSha
    records = @($records)
    sideBySide = $comparisons
}

$normalizedPath = Join-Path $resolvedRunRoot 'normalized-results.json'
$normalized | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $normalizedPath -Encoding utf8

$culture = [Globalization.CultureInfo]::InvariantCulture
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# HealthChecks evolution comparison')
$lines.Add('')
$lines.Add("Run: $($runManifest.runId)")
$lines.Add('')
$lines.Add('This is an evolution comparison, not a strict A/B regression test. SmartPipe 2.2.0 uses keyed readiness over the canonical registry and bounded run-observation model, while SmartPipe 2.1.2 uses a generic-pair run health monitor. Both adapters evaluate one registered but not-started pipeline as Degraded. Cross-version percentage deltas are intentionally omitted.')
$lines.Add('')
$lines.Add('| Method | 2.1.2 Mean us | 2.2.0 Mean us | 2.1.2 KiB/op | 2.2.0 KiB/op | Repeat drift 2.1.2 | Repeat drift 2.2.0 |')
$lines.Add('| --- | ---: | ---: | ---: | ---: | ---: | ---: |')

foreach ($comparison in $comparisons) {
    $baselineDrift = if ($null -eq $comparison.baselineRepeatDriftPercent) {
        'n/a'
    }
    else {
        $comparison.baselineRepeatDriftPercent.ToString('F2', $culture) + '%'
    }

    $candidateDrift = if ($null -eq $comparison.candidateRepeatDriftPercent) {
        'n/a'
    }
    else {
        $comparison.candidateRepeatDriftPercent.ToString('F2', $culture) + '%'
    }

    $baselineMeanUs = ($comparison.baselineMeanNs / 1000.0).ToString('F3', $culture)
    $candidateMeanUs = ($comparison.candidateMeanNs / 1000.0).ToString('F3', $culture)
    $baselineKiB = ($comparison.baselineAllocatedBytes / 1024.0).ToString('F2', $culture)
    $candidateKiB = ($comparison.candidateAllocatedBytes / 1024.0).ToString('F2', $culture)

    $lines.Add("| $($comparison.method) | $baselineMeanUs | $candidateMeanUs | $baselineKiB | $candidateKiB | $baselineDrift | $candidateDrift |")
}

$reportPath = Join-Path $resolvedRunRoot 'comparison.md'
$lines | Set-Content -LiteralPath $reportPath -Encoding utf8

Write-Output "PERF_HEALTHCHECKS_REPORT_OK run=$($runManifest.runId) comparisons=$($comparisons.Count)"
