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
$records = [Collections.Generic.List[object]]::new()

$slotDirectories = Get-ChildItem -LiteralPath $resolvedRunRoot -Directory |
    Where-Object { $_.Name -match '^(?<slot>\d{2})-(?<target>v212|v220)$' } |
    Sort-Object Name

foreach ($slotDirectory in $slotDirectories) {
    [void]($slotDirectory.Name -match '^(?<slot>\d{2})-(?<target>v212|v220)$')
    $slot = [int]$Matches.slot
    $target = [string]$Matches.target

    $resultFile = Get-ChildItem -LiteralPath (Join-Path $slotDirectory.FullName 'results') -File -Filter '*-report-full-compressed.json' |
        Select-Object -First 1

    if ($null -eq $resultFile) {
        throw "BenchmarkDotNet JSON result is missing in '$($slotDirectory.FullName)'."
    }

    $document = Get-Content -LiteralPath $resultFile.FullName -Raw | ConvertFrom-Json -Depth 100
    foreach ($benchmark in @($document.Benchmarks)) {
        $parameters = @{}
        foreach ($pair in ([string]$benchmark.Parameters -split '&')) {
            $parts = $pair -split '=', 2
            if ($parts.Count -ne 2) {
                throw "Unsupported BenchmarkDotNet parameter form '$pair'."
            }
            $parameters[$parts[0]] = $parts[1]
        }

        $records.Add([ordered]@{
            slot = $slot
            target = $target
            method = [string]$benchmark.Method
            itemCount = [int]$parameters.ItemCount
            maxConcurrency = [int]$parameters.MaxConcurrency
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
    throw 'No BenchmarkDotNet records were found.'
}

$comparisons = [Collections.Generic.List[object]]::new()
$groups = $records | Group-Object { "$($_.method)|$($_.itemCount)|$($_.maxConcurrency)" }

foreach ($group in $groups) {
    $baseline = @($group.Group | Where-Object { $_.target -ceq 'v212' } | Sort-Object slot)
    $candidate = @($group.Group | Where-Object { $_.target -ceq 'v220' } | Sort-Object slot)

    if ($baseline.Count -ne 2 -or $candidate.Count -ne 2) {
        throw "Strict A/B group '$($group.Name)' requires exactly two v212 and two v220 observations."
    }

    $baselineMeans = [double[]]@($baseline | ForEach-Object { [double]$_.meanNs })
    $candidateMeans = [double[]]@($candidate | ForEach-Object { [double]$_.meanNs })
    $baselineAllocations = [double[]]@($baseline | ForEach-Object { [double]$_.allocatedBytes })
    $candidateAllocations = [double[]]@($candidate | ForEach-Object { [double]$_.allocatedBytes })

    $baselineMeanCenter = Get-Median $baselineMeans
    $candidateMeanCenter = Get-Median $candidateMeans
    $baselineAllocationCenter = Get-Median $baselineAllocations
    $candidateAllocationCenter = Get-Median $candidateAllocations

    $comparisons.Add([ordered]@{
        method = [string]$baseline[0].method
        itemCount = [int]$baseline[0].itemCount
        maxConcurrency = [int]$baseline[0].maxConcurrency
        baselineMeanNs = $baselineMeanCenter
        candidateMeanNs = $candidateMeanCenter
        timeDeltaPercent = (($candidateMeanCenter / $baselineMeanCenter) - 1.0) * 100.0
        baselineAllocatedBytes = $baselineAllocationCenter
        candidateAllocatedBytes = $candidateAllocationCenter
        allocationDeltaPercent = (($candidateAllocationCenter / $baselineAllocationCenter) - 1.0) * 100.0
        baselineRepeatDriftPercent = Get-RepeatDriftPercent -Values $baselineMeans -Center $baselineMeanCenter
        candidateRepeatDriftPercent = Get-RepeatDriftPercent -Values $candidateMeans -Center $candidateMeanCenter
    })
}

$comparisons = @($comparisons | Sort-Object itemCount, maxConcurrency)
$normalized = [ordered]@{
    schemaVersion = 1
    runId = [string]$runManifest.runId
    scenario = [string]$runManifest.scenario
    scenarioClass = [string]$runManifest.scenarioClass
    authoritativeTiming = [bool]$runManifest.authoritativeTiming
    baselineSha = [string]$runManifest.baselineSha
    candidateSha = [string]$runManifest.candidateSha
    harnessSha = [string]$runManifest.harnessSha
    records = @($records)
    comparisons = $comparisons
}

$normalizedPath = Join-Path $resolvedRunRoot 'normalized-results.json'
$normalized | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $normalizedPath -Encoding utf8

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Core A/B comparison')
$lines.Add('')
$lines.Add("Run: $($runManifest.runId)")
$lines.Add('')
$lines.Add('Positive time delta means the 2.2.0 candidate measured slower in this run. Positive allocation delta means more managed bytes allocated. GitHub-hosted timing is informational unless the run manifest marks it authoritative.')
$lines.Add('')
$lines.Add('| Items | Concurrency | 2.1.2 Mean ms | 2.2.0 Mean ms | Time Δ | 2.1.2 KiB/op | 2.2.0 KiB/op | Alloc Δ | Repeat drift 2.1.2 | Repeat drift 2.2.0 |')
$lines.Add('| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |')

$culture = [Globalization.CultureInfo]::InvariantCulture

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

    $baselineMeanMs = ($comparison.baselineMeanNs / 1000000.0).ToString('F3', $culture)
    $candidateMeanMs = ($comparison.candidateMeanNs / 1000000.0).ToString('F3', $culture)
    $timeDelta = $comparison.timeDeltaPercent.ToString('+0.00;-0.00;0.00', $culture) + '%'
    $baselineKiB = ($comparison.baselineAllocatedBytes / 1024.0).ToString('F2', $culture)
    $candidateKiB = ($comparison.candidateAllocatedBytes / 1024.0).ToString('F2', $culture)
    $allocationDelta = $comparison.allocationDeltaPercent.ToString('+0.00;-0.00;0.00', $culture) + '%'

    $lines.Add("| $($comparison.itemCount) | $($comparison.maxConcurrency) | $baselineMeanMs | $candidateMeanMs | $timeDelta | $baselineKiB | $candidateKiB | $allocationDelta | $baselineDrift | $candidateDrift |")
}

$reportPath = Join-Path $resolvedRunRoot 'comparison.md'
$lines | Set-Content -LiteralPath $reportPath -Encoding utf8

Write-Output "PERF_CORE_AB_REPORT_OK run=$($runManifest.runId) comparisons=$($comparisons.Count)"
