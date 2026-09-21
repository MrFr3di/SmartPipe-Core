param(
    [Parameter(Mandatory = $true)]
    [string]$RunRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-SlopePerMinute {
    param(
        [object[]]$Snapshots,
        [string]$Property
    )

    if ($Snapshots.Count -lt 2) {
        return $null
    }

    $skip = [int][Math]::Floor($Snapshots.Count * 0.2)
    $samples = @($Snapshots | Select-Object -Skip $skip)

    if ($samples.Count -lt 2) {
        $samples = $Snapshots
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

    $handleSlope = $null
    if ([int]$first.HandleOrFdCount -ge 0 -and [int]$last.HandleOrFdCount -ge 0) {
        $handleSlope = Get-SlopePerMinute -Snapshots $snapshots -Property 'HandleOrFdCount'
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
        managedMemoryStartBytes = [long]$first.ManagedMemoryBytes
        managedMemoryEndBytes = [long]$last.ManagedMemoryBytes
        managedMemorySlopeBytesPerMinute = Get-SlopePerMinute -Snapshots $snapshots -Property 'ManagedMemoryBytes'
        gcHeapStartBytes = [long]$first.GcHeapSizeBytes
        gcHeapEndBytes = [long]$last.GcHeapSizeBytes
        gcHeapSlopeBytesPerMinute = Get-SlopePerMinute -Snapshots $snapshots -Property 'GcHeapSizeBytes'
        gcFragmentedStartBytes = [long]$first.GcFragmentedBytes
        gcFragmentedEndBytes = [long]$last.GcFragmentedBytes
        gcFragmentedSlopeBytesPerMinute = Get-SlopePerMinute -Snapshots $snapshots -Property 'GcFragmentedBytes'
        workingSetStartBytes = [long]$first.WorkingSetBytes
        workingSetEndBytes = [long]$last.WorkingSetBytes
        workingSetSlopeBytesPerMinute = Get-SlopePerMinute -Snapshots $snapshots -Property 'WorkingSetBytes'
        totalAllocatedDeltaBytes = [long]$last.TotalAllocatedBytes - [long]$first.TotalAllocatedBytes
        gen0Delta = [int]$last.Gen0Collections - [int]$first.Gen0Collections
        gen1Delta = [int]$last.Gen1Collections - [int]$first.Gen1Collections
        gen2Delta = [int]$last.Gen2Collections - [int]$first.Gen2Collections
        threadPoolThreadCountStart = [int]$first.ThreadPoolThreadCount
        threadPoolThreadCountEnd = [int]$last.ThreadPoolThreadCount
        threadPoolPendingWorkItemsFinal = [long]$last.ThreadPoolPendingWorkItems
        hardGatePassed = (
            [long]$final.Errors -eq 0 -and
            [long]$final.ActiveRuns -eq 0 -and
            [long]$final.CreatedComponents -eq [long]$final.DisposedComponents -and
            [bool]$final.LifecycleInvariantPassed -and
            [long]$last.ThreadPoolPendingWorkItems -eq 0)
        automaticLeakVerdict = 'not-issued'
        handleOrFdStart = [int]$first.HandleOrFdCount
        handleOrFdEnd = [int]$last.HandleOrFdCount
        handleOrFdSlopePerMinute = $handleSlope
        stdout = [IO.Path]::GetRelativePath($resolvedRunRoot, $stdoutPath).Replace('\', '/')
    })
}

if ($targetSummaries.Count -ne 2) {
    throw "Core soak report requires exactly two target summaries, got $($targetSummaries.Count)."
}

$normalized = [ordered]@{
    schemaVersion = 1
    runId = [string]$manifest.runId
    scenario = [string]$manifest.scenario
    scenarioClass = [string]$manifest.scenarioClass
    comparisonPolicy = 'side-by-side-no-cross-version-ratio'
    profile = [string]$manifest.profile
    authoritativeTiming = [bool]$manifest.authoritativeTiming
    trendPolicy = 'evidence-only-no-automatic-leak-verdict'
    trendWarmupFraction = 0.2
    baselineSha = [string]$manifest.baselineSha
    candidateSha = [string]$manifest.candidateSha
    harnessSha = [string]$manifest.harnessSha
    targets = @($targetSummaries)
}

$normalized |
    ConvertTo-Json -Depth 64 |
    Set-Content -LiteralPath (Join-Path $resolvedRunRoot 'normalized-results.json') -Encoding utf8

$culture = [Globalization.CultureInfo]::InvariantCulture
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Core soak / leak evidence')
$lines.Add('')
$lines.Add("Run: $($manifest.runId)")
$lines.Add('')
$lines.Add("Profile: $($manifest.profile)")
$lines.Add('')
if ([string]$manifest.profile -ceq 'verify') {
    $lines.Add('Validation-only profile: verify checks orchestration, correctness and final cleanup. Its short memory slopes are not leak evidence.')
    $lines.Add('')
}
$lines.Add('This is lifecycle/trend evidence. No cross-version speed or memory-regression percentage is emitted. Memory, fragmentation, working-set and handle/fd slopes are evidence-only; this report never issues an automatic leak/no-leak verdict.')
$lines.Add('')
$lines.Add('| Target | Runs | Hard gate | Active final | Created / disposed | Managed slope MiB/min | GC heap slope MiB/min | Fragmentation slope MiB/min | Working-set slope MiB/min | Gen2 delta | TP pending final | Handle/fd start->end |')
$lines.Add('| --- | ---: | :---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |')

foreach ($summary in $targetSummaries) {
    $managed = if ($null -eq $summary.managedMemorySlopeBytesPerMinute) {
        'n/a'
    }
    else {
        ([double]$summary.managedMemorySlopeBytesPerMinute / 1MB).ToString('F3', $culture)
    }

    $heap = if ($null -eq $summary.gcHeapSlopeBytesPerMinute) {
        'n/a'
    }
    else {
        ([double]$summary.gcHeapSlopeBytesPerMinute / 1MB).ToString('F3', $culture)
    }

    $fragmentation = if ($null -eq $summary.gcFragmentedSlopeBytesPerMinute) {
        'n/a'
    }
    else {
        ([double]$summary.gcFragmentedSlopeBytesPerMinute / 1MB).ToString('F3', $culture)
    }

    $working = if ($null -eq $summary.workingSetSlopeBytesPerMinute) {
        'n/a'
    }
    else {
        ([double]$summary.workingSetSlopeBytesPerMinute / 1MB).ToString('F3', $culture)
    }

    $handles = "$($summary.handleOrFdStart)->$($summary.handleOrFdEnd)"
    $hardGate = if ([bool]$summary.hardGatePassed) { 'PASS' } else { 'FAIL' }

    $lines.Add("| $($summary.target) | $($summary.completedRuns) | $hardGate | $($summary.activeRunsFinal) | $($summary.createdComponents) / $($summary.disposedComponents) | $managed | $heap | $fragmentation | $working | $($summary.gen2Delta) | $($summary.threadPoolPendingWorkItemsFinal) | $handles |")
}

$lines |
    Set-Content -LiteralPath (Join-Path $resolvedRunRoot 'comparison.md') -Encoding utf8

Write-Output "PERF_CORE_SOAK_REPORT_OK run=$($manifest.runId) targets=$($targetSummaries.Count) profile=$($manifest.profile)"
