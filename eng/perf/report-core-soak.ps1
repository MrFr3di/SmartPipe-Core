param(
    [Parameter(Mandatory = $true)]
    [string]$RunRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Median {
    param([double[]]$Values)

    if ($Values.Count -eq 0) {
        return $null
    }

    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)

    if (($sorted.Count % 2) -eq 1) {
        return [double]$sorted[$middle]
    }

    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function Get-PostWarmupSamples {
    param([object[]]$Snapshots)

    if ($Snapshots.Count -lt 2) {
        return @($Snapshots)
    }

    $skip = [int][Math]::Floor($Snapshots.Count * 0.2)
    $samples = @($Snapshots | Select-Object -Skip $skip)

    if ($samples.Count -lt 2) {
        return @($Snapshots)
    }

    return $samples
}

function Get-OlsSlopePerMinute {
    param(
        [object[]]$Snapshots,
        [string]$Property
    )

    $samples = @(Get-PostWarmupSamples -Snapshots $Snapshots)
    if ($samples.Count -lt 2) {
        return $null
    }

    $xs = [double[]]@($samples | ForEach-Object { [double]$_.ElapsedSeconds })
    $ys = [double[]]@($samples | ForEach-Object { [double]$_.$Property })

    $meanX = ($xs | Measure-Object -Average).Average
    $meanY = ($ys | Measure-Object -Average).Average

    [double]$numerator = 0
    [double]$denominator = 0

    for ($index = 0; $index -lt $xs.Count; $index++) {
        $dx = $xs[$index] - $meanX
        $numerator += $dx * ($ys[$index] - $meanY)
        $denominator += $dx * $dx
    }

    if ($denominator -eq 0) {
        return $null
    }

    return ($numerator / $denominator) * 60.0
}

function Get-TheilSenSlopePerMinute {
    param(
        [object[]]$Snapshots,
        [string]$Property
    )

    $samples = @(Get-PostWarmupSamples -Snapshots $Snapshots)
    if ($samples.Count -lt 2) {
        return $null
    }

    $slopes = [Collections.Generic.List[double]]::new()

    for ($left = 0; $left -lt ($samples.Count - 1); $left++) {
        $x1 = [double]$samples[$left].ElapsedSeconds
        $y1 = [double]$samples[$left].$Property

        for ($right = $left + 1; $right -lt $samples.Count; $right++) {
            $x2 = [double]$samples[$right].ElapsedSeconds
            $dx = $x2 - $x1

            if ($dx -le 0) {
                continue
            }

            $y2 = [double]$samples[$right].$Property
            $slopes.Add((($y2 - $y1) / $dx) * 60.0)
        }
    }

    if ($slopes.Count -eq 0) {
        return $null
    }

    return Get-Median -Values ([double[]]$slopes.ToArray())
}

function Get-WindowStats {
    param(
        [object[]]$Snapshots,
        [string]$Property
    )

    $samples = @(Get-PostWarmupSamples -Snapshots $Snapshots)
    if ($samples.Count -eq 0) {
        return $null
    }

    $windowSize = [Math]::Max(1, [int][Math]::Floor($samples.Count * 0.25))
    $early = @($samples | Select-Object -First $windowSize)
    $late = @($samples | Select-Object -Last $windowSize)

    $earlyValues = [double[]]@($early | ForEach-Object { [double]$_.$Property })
    $lateValues = [double[]]@($late | ForEach-Object { [double]$_.$Property })

    $startMedian = Get-Median -Values $earlyValues
    $endMedian = Get-Median -Values $lateValues

    return [ordered]@{
        windowSize = $windowSize
        startMedian = $startMedian
        endMedian = $endMedian
        delta = if ($null -eq $startMedian -or $null -eq $endMedian) { $null } else { $endMedian - $startMedian }
    }
}

function Format-MiBPerMinute {
    param([object]$Value)

    if ($null -eq $Value) {
        return 'n/a'
    }

    return ([double]$Value / 1MB).ToString('F3', [Globalization.CultureInfo]::InvariantCulture)
}

function Format-MiB {
    param([object]$Value)

    if ($null -eq $Value) {
        return 'n/a'
    }

    return ([double]$Value / 1MB).ToString('F3', [Globalization.CultureInfo]::InvariantCulture)
}

$resolvedRunRoot = (Resolve-Path -LiteralPath $RunRoot).Path
$manifestPath = Join-Path $resolvedRunRoot 'run-manifest.json'

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Run manifest is missing: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 64

if ([string]$manifest.scenarioClass -cne 'evolution') {
    throw "Core soak report expects scenarioClass=evolution, got '$($manifest.scenarioClass)'."
}

$targetSummaries = [Collections.Generic.List[object]]::new()

$slotDirectories = @(
    Get-ChildItem -LiteralPath $resolvedRunRoot -Directory |
        Where-Object { $_.Name -match '^(?<slot>\d{2})-(?<target>v212|v220)$' } |
        Sort-Object Name
)

foreach ($slotDirectory in $slotDirectories) {
    [void]($slotDirectory.Name -match '^(?<slot>\d{2})-(?<target>v212|v220)$')
    $target = [string]$Matches.target
    $stdoutPath = Join-Path $slotDirectory.FullName 'stdout.jsonl'

    if (-not (Test-Path -LiteralPath $stdoutPath -PathType Leaf)) {
        throw "Core soak stdout is missing for '$target'."
    }

    $objects = [Collections.Generic.List[object]]::new()

    foreach ($line in Get-Content -LiteralPath $stdoutPath) {
        $text = [string]$line
        if ([string]::IsNullOrWhiteSpace($text) -or -not $text.TrimStart().StartsWith('{')) {
            continue
        }

        $objects.Add(($text | ConvertFrom-Json -Depth 32))
    }

    $snapshots = @($objects | Where-Object { [string]$_.Kind -ceq 'snapshot' })
    $final = $objects |
        Where-Object { [string]$_.Kind -ceq 'final' } |
        Select-Object -Last 1

    if ($snapshots.Count -lt 2 -or $null -eq $final) {
        throw "Core soak evidence is incomplete for '$target'."
    }

    $first = $snapshots[0]
    $last = $snapshots[-1]
    $postWarmup = @(Get-PostWarmupSamples -Snapshots $snapshots)
    $trendFirst = $postWarmup[0]
    $trendLast = $postWarmup[-1]

    $managedWindow = Get-WindowStats -Snapshots $snapshots -Property 'ManagedMemoryBytes'
    $heapWindow = Get-WindowStats -Snapshots $snapshots -Property 'GcHeapSizeBytes'
    $fragmentedWindow = Get-WindowStats -Snapshots $snapshots -Property 'GcFragmentedBytes'
    $workingSetWindow = Get-WindowStats -Snapshots $snapshots -Property 'WorkingSetBytes'

    $validHandleSnapshots = @(
        $snapshots | Where-Object { [int]$_.HandleOrFdCount -ge 0 }
    )
    $handleOlsSlope = $null
    $handleRobustSlope = $null
    $handleWindow = $null

    if ($validHandleSnapshots.Count -ge 2) {
        $handleOlsSlope = Get-OlsSlopePerMinute -Snapshots $validHandleSnapshots -Property 'HandleOrFdCount'
        $handleRobustSlope = Get-TheilSenSlopePerMinute -Snapshots $validHandleSnapshots -Property 'HandleOrFdCount'
        $handleWindow = Get-WindowStats -Snapshots $validHandleSnapshots -Property 'HandleOrFdCount'
    }

    $completedRunsPostWarmup = [long]$trendLast.CompletedRuns - [long]$trendFirst.CompletedRuns
    $allocatedPostWarmup = [long]$trendLast.TotalAllocatedBytes - [long]$trendFirst.TotalAllocatedBytes
    $allocatedPerCompletedRun = if ($completedRunsPostWarmup -gt 0) {
        [double]$allocatedPostWarmup / [double]$completedRunsPostWarmup
    }
    else {
        $null
    }

    $finalPending = if ($null -ne $final.PSObject.Properties['ThreadPoolPendingWorkItems']) {
        [long]$final.ThreadPoolPendingWorkItems
    }
    else {
        [long]$last.ThreadPoolPendingWorkItems
    }

    $finalHandles = if ($null -ne $final.PSObject.Properties['HandleOrFdCount']) {
        [int]$final.HandleOrFdCount
    }
    else {
        [int]$last.HandleOrFdCount
    }

    $targetSummaries.Add([ordered]@{
        target = $target
        profile = [string]$final.Profile
        completedRuns = [long]$final.CompletedRuns
        errors = [long]$final.Errors
        activeRunsFinal = [long]$final.ActiveRuns
        createdComponents = [long]$final.CreatedComponents
        disposedComponents = [long]$final.DisposedComponents
        lifecycleInvariantPassed = [bool]$final.LifecycleInvariantPassed

        snapshotCount = $snapshots.Count
        postWarmupSnapshotCount = $postWarmup.Count
        warmupFractionDiscarded = 0.2

        managedMemoryStartBytes = [long]$first.ManagedMemoryBytes
        managedMemoryEndBytes = [long]$last.ManagedMemoryBytes
        managedMemorySlopeBytesPerMinute = Get-OlsSlopePerMinute -Snapshots $snapshots -Property 'ManagedMemoryBytes'
        managedMemoryRobustSlopeBytesPerMinute = Get-TheilSenSlopePerMinute -Snapshots $snapshots -Property 'ManagedMemoryBytes'
        managedMemoryWindowStartMedianBytes = $managedWindow.startMedian
        managedMemoryWindowEndMedianBytes = $managedWindow.endMedian
        managedMemoryWindowDeltaBytes = $managedWindow.delta

        gcHeapStartBytes = [long]$first.GcHeapSizeBytes
        gcHeapEndBytes = [long]$last.GcHeapSizeBytes
        gcHeapSlopeBytesPerMinute = Get-OlsSlopePerMinute -Snapshots $snapshots -Property 'GcHeapSizeBytes'
        gcHeapRobustSlopeBytesPerMinute = Get-TheilSenSlopePerMinute -Snapshots $snapshots -Property 'GcHeapSizeBytes'
        gcHeapWindowStartMedianBytes = $heapWindow.startMedian
        gcHeapWindowEndMedianBytes = $heapWindow.endMedian
        gcHeapWindowDeltaBytes = $heapWindow.delta

        gcFragmentedStartBytes = [long]$first.GcFragmentedBytes
        gcFragmentedEndBytes = [long]$last.GcFragmentedBytes
        gcFragmentedRobustSlopeBytesPerMinute = Get-TheilSenSlopePerMinute -Snapshots $snapshots -Property 'GcFragmentedBytes'
        gcFragmentedWindowDeltaBytes = $fragmentedWindow.delta

        workingSetStartBytes = [long]$first.WorkingSetBytes
        workingSetEndBytes = [long]$last.WorkingSetBytes
        workingSetSlopeBytesPerMinute = Get-OlsSlopePerMinute -Snapshots $snapshots -Property 'WorkingSetBytes'
        workingSetRobustSlopeBytesPerMinute = Get-TheilSenSlopePerMinute -Snapshots $snapshots -Property 'WorkingSetBytes'
        workingSetWindowStartMedianBytes = $workingSetWindow.startMedian
        workingSetWindowEndMedianBytes = $workingSetWindow.endMedian
        workingSetWindowDeltaBytes = $workingSetWindow.delta

        totalAllocatedDeltaBytes = [long]$last.TotalAllocatedBytes - [long]$first.TotalAllocatedBytes
        postWarmupAllocatedDeltaBytes = $allocatedPostWarmup
        postWarmupCompletedRunsDelta = $completedRunsPostWarmup
        allocatedBytesPerCompletedRunPostWarmup = $allocatedPerCompletedRun

        gen0Delta = [int]$last.Gen0Collections - [int]$first.Gen0Collections
        gen1Delta = [int]$last.Gen1Collections - [int]$first.Gen1Collections
        gen2Delta = [int]$last.Gen2Collections - [int]$first.Gen2Collections

        threadPoolThreadCountStart = [int]$first.ThreadPoolThreadCount
        threadPoolThreadCountEnd = [int]$last.ThreadPoolThreadCount
        threadPoolPendingWorkItemsFinal = $finalPending

        handleOrFdStart = [int]$first.HandleOrFdCount
        handleOrFdEnd = [int]$last.HandleOrFdCount
        handleOrFdFinal = $finalHandles
        handleOrFdSlopePerMinute = $handleOlsSlope
        handleOrFdRobustSlopePerMinute = $handleRobustSlope
        handleOrFdWindowDelta = if ($null -eq $handleWindow) { $null } else { $handleWindow.delta }

        stdout = [IO.Path]::GetRelativePath($resolvedRunRoot, $stdoutPath).Replace('\', '/')
    })
}

if ($targetSummaries.Count -ne 2) {
    throw "Core soak report requires exactly two target summaries, got $($targetSummaries.Count)."
}

$normalized = [ordered]@{
    schemaVersion = 2
    runId = [string]$manifest.runId
    scenario = [string]$manifest.scenario
    scenarioClass = [string]$manifest.scenarioClass
    comparisonPolicy = 'side-by-side-no-cross-version-ratio'
    profile = [string]$manifest.profile
    authoritativeTiming = [bool]$manifest.authoritativeTiming
    baselineSha = [string]$manifest.baselineSha
    candidateSha = [string]$manifest.candidateSha
    harnessSha = [string]$manifest.harnessSha
    trendPolicy = [ordered]@{
        warmupFractionDiscarded = 0.2
        olsSlope = 'compatibility diagnostic'
        robustSlope = 'Theil-Sen median pairwise slope over post-warmup snapshots'
        windowDelta = 'last-quartile median minus first-quartile median within post-warmup snapshots'
        memoryVerdict = 'evidence-only; no automatic leak classification'
    }
    targets = @($targetSummaries)
}

$normalized |
    ConvertTo-Json -Depth 64 |
    Set-Content -LiteralPath (Join-Path $resolvedRunRoot 'normalized-results.json') -Encoding utf8

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Core soak / leak evidence')
$lines.Add('')
$lines.Add("Run: $($manifest.runId)")
$lines.Add('')
$lines.Add("Profile: $($manifest.profile)")
$lines.Add('')

if ([string]$manifest.profile -ceq 'verify') {
    $lines.Add('Validation-only profile: verify checks orchestration, correctness and final cleanup. Its short memory trends are not leak evidence.')
    $lines.Add('')
}

$lines.Add('Lifecycle/disposal/pending-work invariants are hard gates. Memory trends remain evidence-only: the report discards the first 20% warm-up, retains OLS for compatibility, adds a robust Theil-Sen slope, and compares early/late post-warmup window medians. No cross-version memory-regression percentage or automatic leak verdict is emitted.')
$lines.Add('')
$lines.Add('| Target | Runs | Active final | Created / disposed | Managed robust MiB/min | Managed window Δ MiB | Heap robust MiB/min | Heap window Δ MiB | WS robust MiB/min | WS window Δ MiB | B/run post-warmup | Gen2 Δ | TP pending final | Handle/fd final |')
$lines.Add('| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |')

foreach ($summary in $targetSummaries) {
    $managedRobust = Format-MiBPerMinute -Value $summary.managedMemoryRobustSlopeBytesPerMinute
    $managedWindowDelta = Format-MiB -Value $summary.managedMemoryWindowDeltaBytes
    $heapRobust = Format-MiBPerMinute -Value $summary.gcHeapRobustSlopeBytesPerMinute
    $heapWindowDelta = Format-MiB -Value $summary.gcHeapWindowDeltaBytes
    $workingRobust = Format-MiBPerMinute -Value $summary.workingSetRobustSlopeBytesPerMinute
    $workingWindowDelta = Format-MiB -Value $summary.workingSetWindowDeltaBytes
    $bytesPerRun = if ($null -eq $summary.allocatedBytesPerCompletedRunPostWarmup) {
        'n/a'
    }
    else {
        ([double]$summary.allocatedBytesPerCompletedRunPostWarmup).ToString('F1', [Globalization.CultureInfo]::InvariantCulture)
    }

    $lines.Add("| $($summary.target) | $($summary.completedRuns) | $($summary.activeRunsFinal) | $($summary.createdComponents) / $($summary.disposedComponents) | $managedRobust | $managedWindowDelta | $heapRobust | $heapWindowDelta | $workingRobust | $workingWindowDelta | $bytesPerRun | $($summary.gen2Delta) | $($summary.threadPoolPendingWorkItemsFinal) | $($summary.handleOrFdFinal) |")
}

$lines.Add('')
$lines.Add('OLS slopes remain in normalized-results.json for compatibility and diagnostics. Prefer the robust slope plus early/late window delta when interpreting GC-shaped memory series.')

$lines |
    Set-Content -LiteralPath (Join-Path $resolvedRunRoot 'comparison.md') -Encoding utf8

Write-Output "PERF_CORE_SOAK_REPORT_OK run=$($manifest.runId) targets=$($targetSummaries.Count) profile=$($manifest.profile) schema=2"
