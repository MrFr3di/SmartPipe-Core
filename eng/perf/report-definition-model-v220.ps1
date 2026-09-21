param(
    [Parameter(Mandatory = $true)]
    [string]$RunRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$expectedMethods = @(
    'Build_ZeroStage',
    'Build_OneStage',
    'Build_TenStages',
    'BuildAndStart_ZeroStage',
    'BuildAndStart_OneStage',
    'BuildAndStart_TenStages',
    'StartAndComplete_ZeroStage',
    'StartAndComplete_OneStage',
    'StartAndComplete_TenStages',
    'LegacyBuilder_StartAndComplete_OneStage'
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
    param([double[]]$Values, [double]$Center)

    if ($Values.Count -lt 2 -or $Center -eq 0) {
        return $null
    }

    return [Math]::Abs($Values[1] - $Values[0]) / $Center * 100.0
}

function Get-Ratio {
    param([double]$Numerator, [double]$Denominator)

    if ($Denominator -eq 0) {
        return $null
    }

    return $Numerator / $Denominator
}

$resolvedRunRoot = (Resolve-Path -LiteralPath $RunRoot).Path
$manifestPath = Join-Path $resolvedRunRoot 'run-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Run manifest is missing: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 64

if ([string]$manifest.scenarioClass -cne 'v220-only') {
    throw "Definition-model report expects scenarioClass=v220-only, got '$($manifest.scenarioClass)'."
}

$slotDirectories = @(
    Get-ChildItem -LiteralPath $resolvedRunRoot -Directory |
        Where-Object { $_.Name -match '^(?<slot>\d{2})-v220$' } |
        Sort-Object Name
)

if ($slotDirectories.Count -ne 2) {
    throw "Definition-model report requires exactly two v2.2 repeat directories, got $($slotDirectories.Count)."
}

$records = [Collections.Generic.List[object]]::new()

foreach ($slotDirectory in $slotDirectories) {
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

        if ($null -eq $benchmark) {
            throw "Definition-model slot $slot is missing '$method'."
        }

        $statistics = $benchmark.PSObject.Properties['Statistics']
        if ($null -eq $statistics -or $null -eq $statistics.Value) {
            throw "Definition-model slot $slot method '$method' has no statistics."
        }

        $memory = $benchmark.PSObject.Properties['Memory']
        $allocated = if ($null -ne $memory -and $null -ne $memory.Value) {
            [double]$memory.Value.BytesAllocatedPerOperation
        }
        else {
            0.0
        }

        $records.Add([ordered]@{
            slot = $slot
            target = 'v220'
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

if ($records.Count -ne ($expectedMethods.Count * 2)) {
    throw "Expected $($expectedMethods.Count * 2) definition-model records, got $($records.Count)."
}

$summaries = [Collections.Generic.List[object]]::new()

foreach ($method in $expectedMethods) {
    $group = @($records | Where-Object { $_.method -ceq $method } | Sort-Object slot)
    if ($group.Count -ne 2) {
        throw "Definition-model method '$method' requires exactly two repeat observations."
    }

    $means = [double[]]@($group | ForEach-Object { [double]$_.meanNs })
    $allocations = [double[]]@($group | ForEach-Object { [double]$_.allocatedBytes })
    $center = Get-Median $means

    $summaries.Add([ordered]@{
        method = $method
        meanNs = $center
        allocatedBytes = Get-Median $allocations
        repeatDriftPercent = Get-RepeatDriftPercent -Values $means -Center $center
    })
}

function Get-Summary {
    param([string]$Method)

    $value = $summaries |
        Where-Object { $_.method -ceq $Method } |
        Select-Object -First 1

    if ($null -eq $value) {
        throw "Definition-model summary '$Method' is missing."
    }

    return $value
}

$buildZero = Get-Summary 'Build_ZeroStage'
$buildOne = Get-Summary 'Build_OneStage'
$buildTen = Get-Summary 'Build_TenStages'
$buildAndStartZero = Get-Summary 'BuildAndStart_ZeroStage'
$buildAndStartOne = Get-Summary 'BuildAndStart_OneStage'
$buildAndStartTen = Get-Summary 'BuildAndStart_TenStages'
$startZero = Get-Summary 'StartAndComplete_ZeroStage'
$startOne = Get-Summary 'StartAndComplete_OneStage'
$startTen = Get-Summary 'StartAndComplete_TenStages'

$scaling = [ordered]@{
    buildOneVsZeroTimeRatio = Get-Ratio -Numerator $buildOne.meanNs -Denominator $buildZero.meanNs
    buildTenVsZeroTimeRatio = Get-Ratio -Numerator $buildTen.meanNs -Denominator $buildZero.meanNs
    buildTenVsZeroAllocationRatio = Get-Ratio -Numerator $buildTen.allocatedBytes -Denominator $buildZero.allocatedBytes
    buildAndStartOneVsZeroTimeRatio = Get-Ratio -Numerator $buildAndStartOne.meanNs -Denominator $buildAndStartZero.meanNs
    buildAndStartTenVsZeroTimeRatio = Get-Ratio -Numerator $buildAndStartTen.meanNs -Denominator $buildAndStartZero.meanNs
    buildAndStartTenVsZeroAllocationRatio = Get-Ratio -Numerator $buildAndStartTen.allocatedBytes -Denominator $buildAndStartZero.allocatedBytes
    startOneVsZeroTimeRatio = Get-Ratio -Numerator $startOne.meanNs -Denominator $startZero.meanNs
    startTenVsZeroTimeRatio = Get-Ratio -Numerator $startTen.meanNs -Denominator $startZero.meanNs
    startTenVsZeroAllocationRatio = Get-Ratio -Numerator $startTen.allocatedBytes -Denominator $startZero.allocatedBytes
}

$normalized = [ordered]@{
    schemaVersion = 1
    runId = [string]$manifest.runId
    scenario = [string]$manifest.scenario
    scenarioClass = [string]$manifest.scenarioClass
    comparisonPolicy = 'absolute-and-intraversion-scaling'
    authoritativeTiming = [bool]$manifest.authoritativeTiming
    candidateSha = [string]$manifest.candidateSha
    harnessSha = [string]$manifest.harnessSha
    records = @($records)
    summaries = @($summaries)
    scaling = $scaling
}

$normalized |
    ConvertTo-Json -Depth 64 |
    Set-Content -LiteralPath (Join-Path $resolvedRunRoot 'normalized-results.json') -Encoding utf8

$culture = [Globalization.CultureInfo]::InvariantCulture
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# SmartPipe 2.2.0 definition-model characterization')
$lines.Add('')
$lines.Add("Run: $($manifest.runId)")
$lines.Add('')
$lines.Add('This is a v2.2-only characterization. There is no 2.1.2 baseline and no cross-version regression percentage. Timing on GitHub-hosted runners is informational.')
$lines.Add('')
$lines.Add('| Method | Mean ns | KiB/op | Repeat drift |')
$lines.Add('| --- | ---: | ---: | ---: |')

foreach ($summary in $summaries) {
    $drift = if ($null -eq $summary.repeatDriftPercent) {
        'n/a'
    }
    else {
        $summary.repeatDriftPercent.ToString('F2', $culture) + '%'
    }

    $mean = $summary.meanNs.ToString('F2', $culture)
    $kib = ($summary.allocatedBytes / 1024.0).ToString('F3', $culture)
    $lines.Add("| $($summary.method) | $mean | $kib | $drift |")
}

$lines.Add('')
$lines.Add('## Intra-version scaling')
$lines.Add('')
foreach ($property in $scaling.GetEnumerator()) {
    $value = if ($null -eq $property.Value) {
        'n/a'
    }
    else {
        ([double]$property.Value).ToString('F3', $culture) + 'x'
    }
    $lines.Add("- $($property.Key): $value")
}

$lines |
    Set-Content -LiteralPath (Join-Path $resolvedRunRoot 'comparison.md') -Encoding utf8

Write-Output "PERF_DEFINITION_MODEL_V220_REPORT_OK run=$($manifest.runId) methods=$($summaries.Count)"
