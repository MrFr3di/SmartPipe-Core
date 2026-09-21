param(
    [Parameter(Mandatory = $true)]
    [string]$RunRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$expectedMethods = @(
    'RawSingle',
    'PipelineSingle',
    'RawCompiledSingle',
    'CompiledPipelineSingle',
    'RawHundredRows',
    'PipelineHundredRows',
    'RawCompiledHundredRows',
    'CompiledPipelineHundredRows'
)

function Get-Median {
    param([double[]]$Values)
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { throw 'Median requires at least one value.' }
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) { return [double]$sorted[$middle] }
    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function Get-RepeatDriftPercent {
    param([double[]]$Values,[double]$Center)
    if ($Values.Count -lt 2 -or $Center -eq 0) { return $null }
    return [Math]::Abs($Values[1] - $Values[0]) / $Center * 100.0
}

$resolvedRunRoot = (Resolve-Path -LiteralPath $RunRoot).Path
$manifestPath = Join-Path $resolvedRunRoot 'run-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Run manifest is missing: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 64
if ([string]$manifest.scenarioClass -cne 'v220-only') {
    throw 'EF Core decomposition report expects scenarioClass=v220-only.'
}
if ([string]$manifest.comparisonPolicy -cne 'within-version-raw-vs-normal-vs-compiled') {
    throw "Unexpected EF Core decomposition comparison policy '$($manifest.comparisonPolicy)'."
}

$records = [Collections.Generic.List[object]]::new()
$slots = @(
    Get-ChildItem -LiteralPath $resolvedRunRoot -Directory |
        Where-Object { $_.Name -match '^(?<slot>\d{2})-v220$' } |
        Sort-Object Name
)

if ($slots.Count -ne 2) {
    throw "EF Core decomposition report requires exactly two repeated v220 slots, got $($slots.Count)."
}

foreach ($slotDirectory in $slots) {
    [void]($slotDirectory.Name -match '^(?<slot>\d{2})-v220$')
    $slot = [int]$Matches.slot
    $resultFile = Get-ChildItem -LiteralPath $slotDirectory.FullName -File -Recurse -Filter '*-report-full-compressed.json' | Select-Object -First 1

    if ($null -eq $resultFile) {
        throw "BenchmarkDotNet JSON result is missing in '$($slotDirectory.FullName)'."
    }

    $document = Get-Content -LiteralPath $resultFile.FullName -Raw | ConvertFrom-Json -Depth 100
    $benchmarks = @($document.Benchmarks)

    foreach ($method in $expectedMethods) {
        $benchmark = $benchmarks | Where-Object { [string]$_.Method -ceq $method } | Select-Object -First 1
        if ($null -eq $benchmark -or $null -eq $benchmark.Statistics) {
            throw "Slot $slot is missing complete '$method' statistics."
        }

        $memory = $benchmark.PSObject.Properties['Memory']
        $allocated = if ($null -ne $memory -and $null -ne $memory.Value) {
            [double]$memory.Value.BytesAllocatedPerOperation
        } else { 0.0 }

        $records.Add([ordered]@{
            slot = $slot
            method = $method
            sampleCount = [int]$benchmark.Statistics.N
            meanNs = [double]$benchmark.Statistics.Mean
            medianNs = [double]$benchmark.Statistics.Median
            standardDeviationNs = [double]$benchmark.Statistics.StandardDeviation
            allocatedBytes = $allocated
            resultFile = [IO.Path]::GetRelativePath($resolvedRunRoot,$resultFile.FullName).Replace('\','/')
        })
    }
}

if ($records.Count -ne ($expectedMethods.Count * 2)) {
    throw "Expected $($expectedMethods.Count * 2) normalized EF decomposition records, got $($records.Count)."
}

$summary = [ordered]@{}
foreach ($method in $expectedMethods) {
    $rows = @($records | Where-Object { $_.method -ceq $method } | Sort-Object slot)
    if ($rows.Count -ne 2) { throw "Method '$method' requires two repeated observations." }

    $means = [double[]]@($rows | ForEach-Object { [double]$_.meanNs })
    $allocations = [double[]]@($rows | ForEach-Object { [double]$_.allocatedBytes })
    $center = Get-Median $means

    $summary[$method] = [ordered]@{
        meanNs = $center
        allocatedBytes = Get-Median $allocations
        repeatDriftPercent = Get-RepeatDriftPercent -Values $means -Center $center
    }
}

function New-Comparison {
    param(
        [string]$Name,
        [int]$Rows,
        [string]$RawMethod,
        [string]$NormalMethod,
        [string]$RawCompiledMethod,
        [string]$CompiledMethod
    )

    $raw = $summary[$RawMethod]
    $normal = $summary[$NormalMethod]
    $rawCompiled = $summary[$RawCompiledMethod]
    $compiled = $summary[$CompiledMethod]

    return [ordered]@{
        name = $Name
        rows = $Rows
        rawMeanNs = [double]$raw.meanNs
        normalPipelineMeanNs = [double]$normal.meanNs
        normalOverRawRatio = if ([double]$raw.meanNs -eq 0) { $null } else { [double]$normal.meanNs / [double]$raw.meanNs }
        normalTimeDeltaNs = [double]$normal.meanNs - [double]$raw.meanNs
        rawCompiledMeanNs = [double]$rawCompiled.meanNs
        compiledPipelineMeanNs = [double]$compiled.meanNs
        compiledOverRawCompiledRatio = if ([double]$rawCompiled.meanNs -eq 0) { $null } else { [double]$compiled.meanNs / [double]$rawCompiled.meanNs }
        rawCompiledOverRawRatio = if ([double]$raw.meanNs -eq 0) { $null } else { [double]$rawCompiled.meanNs / [double]$raw.meanNs }
        compiledTimeDeltaNs = [double]$compiled.meanNs - [double]$rawCompiled.meanNs
        rawAllocatedBytes = [double]$raw.allocatedBytes
        normalAllocatedBytes = [double]$normal.allocatedBytes
        normalAllocationDeltaBytes = [double]$normal.allocatedBytes - [double]$raw.allocatedBytes
        rawCompiledAllocatedBytes = [double]$rawCompiled.allocatedBytes
        compiledAllocatedBytes = [double]$compiled.allocatedBytes
        compiledAllocationDeltaBytes = [double]$compiled.allocatedBytes - [double]$rawCompiled.allocatedBytes
        rawRepeatDriftPercent = $raw.repeatDriftPercent
        normalRepeatDriftPercent = $normal.repeatDriftPercent
        rawCompiledRepeatDriftPercent = $rawCompiled.repeatDriftPercent
        compiledRepeatDriftPercent = $compiled.repeatDriftPercent
    }
}

$singleArgs = @{
    Name = 'single'
    Rows = 1
    RawMethod = 'RawSingle'
    NormalMethod = 'PipelineSingle'
    RawCompiledMethod = 'RawCompiledSingle'
    CompiledMethod = 'CompiledPipelineSingle'
}
$hundredArgs = @{
    Name = 'hundred'
    Rows = 100
    RawMethod = 'RawHundredRows'
    NormalMethod = 'PipelineHundredRows'
    RawCompiledMethod = 'RawCompiledHundredRows'
    CompiledMethod = 'CompiledPipelineHundredRows'
}
$single = New-Comparison @singleArgs
$hundred = New-Comparison @hundredArgs

$normalIncrementalNs = ([double]$hundred.normalTimeDeltaNs - [double]$single.normalTimeDeltaNs) / 99.0
$compiledIncrementalNs = ([double]$hundred.compiledTimeDeltaNs - [double]$single.compiledTimeDeltaNs) / 99.0
$normalIncrementalAlloc = ([double]$hundred.normalAllocationDeltaBytes - [double]$single.normalAllocationDeltaBytes) / 99.0
$compiledIncrementalAlloc = ([double]$hundred.compiledAllocationDeltaBytes - [double]$single.compiledAllocationDeltaBytes) / 99.0

$normalized = [ordered]@{
    schemaVersion = 2
    runId = [string]$manifest.runId
    scenario = 'efcore-decomposition'
    scenarioClass = 'v220-only'
    comparisonPolicy = 'within-version-raw-vs-normal-vs-compiled'
    authoritativeTiming = [bool]$manifest.authoritativeTiming
    candidateSha = [string]$manifest.candidateSha
    harnessSha = [string]$manifest.harnessSha
    records = @($records)
    methodSummary = $summary
    comparisons = @($single,$hundred)
    descriptiveModel = [ordered]@{
        normalIncrementalTimePerAdditionalRowNs = $normalIncrementalNs
        compiledIncrementalTimePerAdditionalRowNs = $compiledIncrementalNs
        normalIncrementalAllocationPerAdditionalRowBytes = $normalIncrementalAlloc
        compiledIncrementalAllocationPerAdditionalRowBytes = $compiledIncrementalAlloc
        note = 'Descriptive two-point model only; hosted timing is non-authoritative.'
    }
}

$normalized | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath (Join-Path $resolvedRunRoot 'normalized-results.json') -Encoding utf8

$culture = [Globalization.CultureInfo]::InvariantCulture
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# EF Core 2.2.0 lifecycle decomposition')
$lines.Add('')
$lines.Add("Run: $($manifest.runId)")
$lines.Add('')
$lines.Add('This is a v2.2-only within-version decomposition on one SQLite 10.0.11 fixture. QuerySource is compared with the matching ordinary AsNoTracking query. CompiledQuerySource is compared with the matching EF.CompileAsyncQuery delegate. This pairing isolates SmartPipe pipeline/lifecycle overhead from EF query-compilation effects. Hosted timing remains informational.')
$lines.Add('')
$lines.Add('| Workload | Raw us | QuerySource us | Query/Raw | Raw compiled us | CompiledQuerySource us | Compiled/RawCompiled | RawCompiled/Raw | Query extra KiB | Compiled extra KiB |')
$lines.Add('| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |')

foreach ($row in @($single,$hundred)) {
    $rawUs = ([double]$row.rawMeanNs / 1000.0).ToString('F3',$culture)
    $normalUs = ([double]$row.normalPipelineMeanNs / 1000.0).ToString('F3',$culture)
    $rawCompiledUs = ([double]$row.rawCompiledMeanNs / 1000.0).ToString('F3',$culture)
    $compiledUs = ([double]$row.compiledPipelineMeanNs / 1000.0).ToString('F3',$culture)
    $normalRaw = ([double]$row.normalOverRawRatio).ToString('F3',$culture) + 'x'
    $compiledRaw = ([double]$row.compiledOverRawCompiledRatio).ToString('F3',$culture) + 'x'
    $rawCompiledRaw = ([double]$row.rawCompiledOverRawRatio).ToString('F3',$culture) + 'x'
    $normalKiB = ([double]$row.normalAllocationDeltaBytes / 1024.0).ToString('F3',$culture)
    $compiledKiB = ([double]$row.compiledAllocationDeltaBytes / 1024.0).ToString('F3',$culture)
    $lines.Add("| $($row.name) | $rawUs | $normalUs | $normalRaw | $rawCompiledUs | $compiledUs | $compiledRaw | $rawCompiledRaw | $normalKiB | $compiledKiB |")
}

$lines.Add('')
$lines.Add("QuerySource descriptive incremental overhead per additional row: $(([double]$normalIncrementalNs).ToString('F2',$culture)) ns and $(([double]$normalIncrementalAlloc).ToString('F2',$culture)) bytes.")
$lines.Add("CompiledQuerySource descriptive incremental overhead per additional row relative to RawCompiled: $(([double]$compiledIncrementalNs).ToString('F2',$culture)) ns and $(([double]$compiledIncrementalAlloc).ToString('F2',$culture)) bytes.")
$lines.Add('')
$lines.Add('The two-point models are descriptive only and must not be extrapolated outside the measured one-row and 100-row workloads.')

$lines | Set-Content -LiteralPath (Join-Path $resolvedRunRoot 'comparison.md') -Encoding utf8
Write-Output "PERF_EFCORE_DECOMPOSITION_REPORT_OK run=$($manifest.runId) workloads=2"
