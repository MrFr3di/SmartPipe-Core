param(
    [Parameter(Mandatory = $true)]
    [string]$RunRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$expectedMethods = @('RawSingle', 'PipelineSingle', 'RawHundredRows', 'PipelineHundredRows')

function Get-Median {
    param([double[]]$Values)

    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) {
        throw 'Median requires at least one value.'
    }

    $middle = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return [double]$sorted[$middle]
    }

    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function Get-RepeatDriftPercent {
    param([double[]]$Values, [double]$Center)

    if ($Values.Count -lt 2 -or $Center -eq 0) {
        return $null
    }

    return [Math]::Abs($Values[1] - $Values[0]) / $Center * 100.0
}

$resolvedRunRoot = (Resolve-Path -LiteralPath $RunRoot).Path
$manifest = Get-Content -LiteralPath (Join-Path $resolvedRunRoot 'run-manifest.json') -Raw | ConvertFrom-Json -Depth 64

if ([string]$manifest.scenarioClass -cne 'v220-only') {
    throw "Dapper decomposition report expects scenarioClass=v220-only."
}

$records = [Collections.Generic.List[object]]::new()
$slots = Get-ChildItem -LiteralPath $resolvedRunRoot -Directory |
    Where-Object { $_.Name -match '^(?<slot>\d{2})-v220$' } |
    Sort-Object Name

if ($slots.Count -ne 2) {
    throw "Dapper decomposition report requires exactly two repeated v220 slots, got $($slots.Count)."
}

foreach ($slotDirectory in $slots) {
    [void]($slotDirectory.Name -match '^(?<slot>\d{2})-v220$')
    $slot = [int]$Matches.slot

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

        if ($null -eq $benchmark -or $null -eq $benchmark.Statistics) {
            throw "Slot $slot is missing complete '$method' statistics."
        }

        $allocated = if ($null -ne $benchmark.Memory) {
            [double]$benchmark.Memory.BytesAllocatedPerOperation
        }
        else {
            0.0
        }

        $records.Add([ordered]@{
            slot = $slot
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

$summary = [ordered]@{}
foreach ($method in $expectedMethods) {
    $rows = @($records | Where-Object { $_.method -ceq $method } | Sort-Object slot)
    if ($rows.Count -ne 2) {
        throw "Method '$method' requires two repeated observations."
    }

    $means = [double[]]@($rows | ForEach-Object { [double]$_.meanNs })
    $allocations = [double[]]@($rows | ForEach-Object { [double]$_.allocatedBytes })
    $center = Get-Median $means

    $summary[$method] = [ordered]@{
        meanNs = $center
        allocatedBytes = Get-Median $allocations
        repeatDriftPercent = Get-RepeatDriftPercent -Values $means -Center $center
    }
}

function New-Overhead {
    param(
        [string]$Name,
        [string]$RawMethod,
        [string]$PipelineMethod,
        [int]$Rows
    )

    $raw = $summary[$RawMethod]
    $pipeline = $summary[$PipelineMethod]
    $timeDeltaNs = [double]$pipeline.meanNs - [double]$raw.meanNs
    $allocationDeltaBytes = [double]$pipeline.allocatedBytes - [double]$raw.allocatedBytes

    return [ordered]@{
        name = $Name
        rows = $Rows
        rawMeanNs = [double]$raw.meanNs
        pipelineMeanNs = [double]$pipeline.meanNs
        pipelineOverRawRatio = if ([double]$raw.meanNs -eq 0) { $null } else { [double]$pipeline.meanNs / [double]$raw.meanNs }
        timeDeltaNs = $timeDeltaNs
        rawAllocatedBytes = [double]$raw.allocatedBytes
        pipelineAllocatedBytes = [double]$pipeline.allocatedBytes
        allocationDeltaBytes = $allocationDeltaBytes
        rawRepeatDriftPercent = $raw.repeatDriftPercent
        pipelineRepeatDriftPercent = $pipeline.repeatDriftPercent
    }
}

$single = New-Overhead -Name 'single' -RawMethod 'RawSingle' -PipelineMethod 'PipelineSingle' -Rows 1
$hundred = New-Overhead -Name 'hundred' -RawMethod 'RawHundredRows' -PipelineMethod 'PipelineHundredRows' -Rows 100

$incrementalPerAdditionalRowNs =
    ([double]$hundred.timeDeltaNs - [double]$single.timeDeltaNs) / 99.0
$incrementalAllocationPerAdditionalRowBytes =
    ([double]$hundred.allocationDeltaBytes - [double]$single.allocationDeltaBytes) / 99.0

$normalized = [ordered]@{
    schemaVersion = 1
    runId = [string]$manifest.runId
    scenario = 'dapper-decomposition'
    scenarioClass = 'v220-only'
    comparisonPolicy = 'within-version-raw-vs-pipeline'
    authoritativeTiming = [bool]$manifest.authoritativeTiming
    candidateSha = [string]$manifest.candidateSha
    harnessSha = [string]$manifest.harnessSha
    records = @($records)
    methodSummary = $summary
    overhead = @($single, $hundred)
    descriptiveModel = [ordered]@{
        incrementalPipelineTimePerAdditionalRowNs = $incrementalPerAdditionalRowNs
        incrementalPipelineAllocationPerAdditionalRowBytes = $incrementalAllocationPerAdditionalRowBytes
        note = 'Descriptive two-point model only; hosted timing is non-authoritative.'
    }
}

$normalized |
    ConvertTo-Json -Depth 64 |
    Set-Content -LiteralPath (Join-Path $resolvedRunRoot 'normalized-results.json') -Encoding utf8

$culture = [Globalization.CultureInfo]::InvariantCulture
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Dapper 2.2.0 lifecycle decomposition')
$lines.Add('')
$lines.Add("Run: $($manifest.runId)")
$lines.Add('')
$lines.Add('This is a v2.2-only within-version decomposition. Raw and Pipeline methods use the same SQLite fixture, SQL, Dapper ExecuteReaderAsync path, and row mapper. The ratio isolates the additional SmartPipe activation/lifecycle/envelope/result layers. Hosted timing remains informational.')
$lines.Add('')
$lines.Add('| Workload | Raw mean us | Pipeline mean us | Pipeline / Raw | Extra us | Extra KiB/op | Raw drift | Pipeline drift |')
$lines.Add('| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |')

foreach ($row in @($single, $hundred)) {
    $rawUs = ([double]$row.rawMeanNs / 1000.0).ToString('F3', $culture)
    $pipelineUs = ([double]$row.pipelineMeanNs / 1000.0).ToString('F3', $culture)
    $ratio = if ($null -eq $row.pipelineOverRawRatio) { 'n/a' } else { ([double]$row.pipelineOverRawRatio).ToString('F3', $culture) + 'x' }
    $extraUs = ([double]$row.timeDeltaNs / 1000.0).ToString('F3', $culture)
    $extraKiB = ([double]$row.allocationDeltaBytes / 1024.0).ToString('F3', $culture)
    $rawDrift = if ($null -eq $row.rawRepeatDriftPercent) { 'n/a' } else { ([double]$row.rawRepeatDriftPercent).ToString('F2', $culture) + '%' }
    $pipelineDrift = if ($null -eq $row.pipelineRepeatDriftPercent) { 'n/a' } else { ([double]$row.pipelineRepeatDriftPercent).ToString('F2', $culture) + '%' }

    $lines.Add("| $($row.name) | $rawUs | $pipelineUs | $ratio | $extraUs | $extraKiB | $rawDrift | $pipelineDrift |")
}

$lines.Add('')
$lines.Add("Descriptive incremental overhead per additional row: $(([double]$incrementalPerAdditionalRowNs).ToString('F2', $culture)) ns and $(([double]$incrementalAllocationPerAdditionalRowBytes).ToString('F2', $culture)) bytes.")
$lines.Add('')
$lines.Add('The two-point incremental model is descriptive only; it is not a regression threshold and must not be extrapolated outside the measured 1-row and 100-row workloads.')

$lines | Set-Content -LiteralPath (Join-Path $resolvedRunRoot 'comparison.md') -Encoding utf8

Write-Output "PERF_DAPPER_DECOMPOSITION_REPORT_OK run=$($manifest.runId) workloads=2"
