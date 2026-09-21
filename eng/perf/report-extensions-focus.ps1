param(
    [Parameter(Mandatory = $true)]
    [string]$RunRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$expectedMethods = @(
    'ZeroChildren',
    'OneChild',
    'ThreeChildren'
)

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

function Get-DeltaPercent {
    param(
        [double]$Baseline,
        [double]$Candidate
    )

    if ($Baseline -eq 0) {
        return $null
    }

    return ($Candidate - $Baseline) / $Baseline * 100.0
}

$resolvedRunRoot = (Resolve-Path -LiteralPath $RunRoot).Path
$runManifestPath = Join-Path $resolvedRunRoot 'run-manifest.json'

if (-not (Test-Path -LiteralPath $runManifestPath -PathType Leaf)) {
    throw "Run manifest is missing: $runManifestPath"
}

$runManifest = Get-Content -LiteralPath $runManifestPath -Raw | ConvertFrom-Json -Depth 64

if ([string]$runManifest.scenarioClass -cne 'strict-ab') {
    throw "SP220-07 report expects scenarioClass=strict-ab, got '$($runManifest.scenarioClass)'."
}

$records = [Collections.Generic.List[object]]::new()
$slotDirectories = Get-ChildItem -LiteralPath $resolvedRunRoot -Directory |
    Where-Object { $_.Name -match '^(?<slot>\d{2})-(?<target>v212|v220)$' } |
    Sort-Object Name

if ($slotDirectories.Count -ne 4) {
    throw "SP220-07 report requires exactly four A-B-B-A slot directories, got $($slotDirectories.Count)."
}

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
    $benchmarks = @($document.Benchmarks)

    foreach ($method in $expectedMethods) {
        $benchmark = $benchmarks |
            Where-Object { [string]$_.Method -ceq $method } |
            Select-Object -First 1

        if ($null -eq $benchmark) {
            throw "Slot $slot ($target) is missing '$method'."
        }

        $statisticsProperty = $benchmark.PSObject.Properties['Statistics']
        if ($null -eq $statisticsProperty -or $null -eq $statisticsProperty.Value) {
            throw "Slot $slot ($target) method '$method' has no statistics."
        }

        $memoryProperty = $benchmark.PSObject.Properties['Memory']
        $allocated = if ($null -ne $memoryProperty -and $null -ne $memoryProperty.Value) {
            [double]$memoryProperty.Value.BytesAllocatedPerOperation
        }
        else {
            0.0
        }

        $records.Add([ordered]@{
            slot = $slot
            target = $target
            method = $method
            sampleCount = [int]$benchmark.Statistics.N
            meanNs = [double]$benchmark.Statistics.Mean
            medianNs = [double]$benchmark.Statistics.Median
            standardDeviationNs = [double]$benchmark.Statistics.StandardDeviation
            allocatedBytes = $allocated
            resultFile = [IO.Path]::GetRelativePath($resolvedRunRoot, $resultFile.FullName).Replace('\', '/')
        })
    }
}

if ($records.Count -ne ($expectedMethods.Count * 4)) {
    throw "Expected $($expectedMethods.Count * 4) normalized records, got $($records.Count)."
}

$comparisons = [Collections.Generic.List[object]]::new()

foreach ($method in $expectedMethods) {
    $group = @($records | Where-Object { $_.method -ceq $method })
    $baseline = @($group | Where-Object { $_.target -ceq 'v212' } | Sort-Object slot)
    $candidate = @($group | Where-Object { $_.target -ceq 'v220' } | Sort-Object slot)

    if ($baseline.Count -ne 2 -or $candidate.Count -ne 2) {
        throw "Strict A/B method '$method' requires exactly two v212 and two v220 observations."
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
        method = $method
        baselineMeanNs = $baselineMeanCenter
        candidateMeanNs = $candidateMeanCenter
        timeDeltaPercent = Get-DeltaPercent -Baseline $baselineMeanCenter -Candidate $candidateMeanCenter
        baselineAllocatedBytes = $baselineAllocationCenter
        candidateAllocatedBytes = $candidateAllocationCenter
        allocationDeltaPercent = Get-DeltaPercent -Baseline $baselineAllocationCenter -Candidate $candidateAllocationCenter
        baselineRepeatDriftPercent = Get-RepeatDriftPercent -Values $baselineMeans -Center $baselineMeanCenter
        candidateRepeatDriftPercent = Get-RepeatDriftPercent -Values $candidateMeans -Center $candidateMeanCenter
    })
}


$zero = @($comparisons | Where-Object method -eq 'ZeroChildren')[0]
$one = @($comparisons | Where-Object method -eq 'OneChild')[0]
$three = @($comparisons | Where-Object method -eq 'ThreeChildren')[0]

$baselineFirstChildNs = [double]$one.baselineMeanNs - [double]$zero.baselineMeanNs
$candidateFirstChildNs = [double]$one.candidateMeanNs - [double]$zero.candidateMeanNs
$baselineAdditionalChildNs = ([double]$three.baselineMeanNs - [double]$one.baselineMeanNs) / 2.0
$candidateAdditionalChildNs = ([double]$three.candidateMeanNs - [double]$one.candidateMeanNs) / 2.0

$compositeDecomposition = [ordered]@{
    baselineFixedZeroChildNs = [double]$zero.baselineMeanNs
    candidateFixedZeroChildNs = [double]$zero.candidateMeanNs
    fixedDeltaNs = [double]$zero.candidateMeanNs - [double]$zero.baselineMeanNs
    fixedDeltaPercent = Get-DeltaPercent -Baseline ([double]$zero.baselineMeanNs) -Candidate ([double]$zero.candidateMeanNs)
    baselineFirstChildIncrementNs = $baselineFirstChildNs
    candidateFirstChildIncrementNs = $candidateFirstChildNs
    firstChildIncrementDeltaNs = $candidateFirstChildNs - $baselineFirstChildNs
    firstChildIncrementDeltaPercent = Get-DeltaPercent -Baseline $baselineFirstChildNs -Candidate $candidateFirstChildNs
    baselineAdditionalChildIncrementNs = $baselineAdditionalChildNs
    candidateAdditionalChildIncrementNs = $candidateAdditionalChildNs
    additionalChildIncrementDeltaNs = $candidateAdditionalChildNs - $baselineAdditionalChildNs
    additionalChildIncrementDeltaPercent = Get-DeltaPercent -Baseline $baselineAdditionalChildNs -Candidate $candidateAdditionalChildNs
}

$normalized = [ordered]@{
    schemaVersion = 1
    runId = [string]$runManifest.runId
    scenario = [string]$runManifest.scenario
    scenarioClass = [string]$runManifest.scenarioClass
    comparisonPolicy = 'strict-cross-version-ratio'
    authoritativeTiming = [bool]$runManifest.authoritativeTiming
    baselineSha = [string]$runManifest.baselineSha
    candidateSha = [string]$runManifest.candidateSha
    harnessSha = [string]$runManifest.harnessSha
    records = @($records)
    comparisons = @($comparisons)
    decomposition = $compositeDecomposition
}

$normalizedPath = Join-Path $resolvedRunRoot 'normalized-results.json'
$normalized |
    ConvertTo-Json -Depth 64 |
    Set-Content -LiteralPath $normalizedPath -Encoding utf8

$culture = [Globalization.CultureInfo]::InvariantCulture
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# SP220-07 focused Composite strict A/B comparison')
$lines.Add('')
$lines.Add("Run: $($runManifest.runId)")
$lines.Add('')
$lines.Add("Timing authority: $([bool]$runManifest.authoritativeTiming). GitHub-hosted timing remains informational even though the workload itself is strict A/B.")
$lines.Add('')
$lines.Add('| Method | 2.1.2 Mean ns | 2.2.0 Mean ns | Time delta | 2.1.2 KiB/op | 2.2.0 KiB/op | Allocation delta | Drift 2.1.2 | Drift 2.2.0 |')
$lines.Add('| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |')

foreach ($comparison in $comparisons) {
    $timeDelta = if ($null -eq $comparison.timeDeltaPercent) {
        'n/a'
    }
    else {
        $comparison.timeDeltaPercent.ToString('+0.00;-0.00;0.00', $culture) + '%'
    }

    $allocationDelta = if ($null -eq $comparison.allocationDeltaPercent) {
        'n/a'
    }
    else {
        $comparison.allocationDeltaPercent.ToString('+0.00;-0.00;0.00', $culture) + '%'
    }

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

    $baselineMean = $comparison.baselineMeanNs.ToString('F2', $culture)
    $candidateMean = $comparison.candidateMeanNs.ToString('F2', $culture)
    $baselineKiB = ($comparison.baselineAllocatedBytes / 1024.0).ToString('F3', $culture)
    $candidateKiB = ($comparison.candidateAllocatedBytes / 1024.0).ToString('F3', $culture)

    $lines.Add("| $($comparison.method) | $baselineMean | $candidateMean | $timeDelta | $baselineKiB | $candidateKiB | $allocationDelta | $baselineDrift | $candidateDrift |")
}


$lines.Add('')
$lines.Add('## Composite cost decomposition')
$lines.Add('')
$lines.Add('| Component | 2.1.2 ns | 2.2.0 ns | Delta |')
$lines.Add('| --- | ---: | ---: | ---: |')
$lines.Add("| Fixed zero-child wrapper | $($compositeDecomposition.baselineFixedZeroChildNs.ToString('F2', $culture)) | $($compositeDecomposition.candidateFixedZeroChildNs.ToString('F2', $culture)) | $($compositeDecomposition.fixedDeltaPercent.ToString('+0.00;-0.00;0.00', $culture))% |")
$lines.Add("| First child incremental | $($compositeDecomposition.baselineFirstChildIncrementNs.ToString('F2', $culture)) | $($compositeDecomposition.candidateFirstChildIncrementNs.ToString('F2', $culture)) | $($compositeDecomposition.firstChildIncrementDeltaPercent.ToString('+0.00;-0.00;0.00', $culture))% |")
$lines.Add("| Additional child incremental | $($compositeDecomposition.baselineAdditionalChildIncrementNs.ToString('F2', $culture)) | $($compositeDecomposition.candidateAdditionalChildIncrementNs.ToString('F2', $culture)) | $($compositeDecomposition.additionalChildIncrementDeltaPercent.ToString('+0.00;-0.00;0.00', $culture))% |")
$lines.Add('')
$lines.Add('The incremental values are descriptive finite differences of the measured 0/1/3-child workloads; they are not independent benchmarks.')

$reportPath = Join-Path $resolvedRunRoot 'comparison.md'
$lines | Set-Content -LiteralPath $reportPath -Encoding utf8

Write-Output "PERF_EXTENSIONS_FOCUS_REPORT_OK run=$($runManifest.runId) comparisons=$($comparisons.Count)"
