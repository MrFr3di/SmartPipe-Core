Set-StrictMode -Version Latest

$script:SmartPipeRunnerDefaultRoot = 'C:\SmartPipe-Runner'
$script:SmartPipeRunnerRepository = 'MrFr3di/SmartPipe-Core'
$script:SmartPipeRunnerLabel = 'smartpipe-cleanup-v1'

function Get-SmartPipeFullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'A path is required.'
    }

    try {
        $fullPath = [IO.Path]::GetFullPath($Path)
    }
    catch {
        throw "Invalid path: $Path"
    }

    if ($fullPath.Length -gt 3) {
        return $fullPath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    }

    return $fullPath
}

function Test-SmartPipeSamePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Left,

        [Parameter(Mandatory = $true)]
        [string] $Right
    )

    return [string]::Equals(
        (Get-SmartPipeFullPath -Path $Left),
        (Get-SmartPipeFullPath -Path $Right),
        [StringComparison]::OrdinalIgnoreCase)
}

function Test-SmartPipeContainedPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Boundary,

        [switch] $AllowBoundary
    )

    $candidate = Get-SmartPipeFullPath -Path $Path
    $boundaryPath = Get-SmartPipeFullPath -Path $Boundary
    if ($AllowBoundary -and (Test-SmartPipeSamePath -Left $candidate -Right $boundaryPath)) {
        return $true
    }

    $prefix = "$boundaryPath$([IO.Path]::DirectorySeparatorChar)"
    return $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-SmartPipeNoReparsePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Boundary
    )

    $candidate = Get-SmartPipeFullPath -Path $Path
    $boundaryPath = Get-SmartPipeFullPath -Path $Boundary
    if (-not (Test-SmartPipeContainedPath -Path $candidate -Boundary $boundaryPath -AllowBoundary)) {
        throw "Path is outside the approved boundary: $candidate"
    }

    $current = $candidate
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse point is not an approved cleanup target: $current"
            }
        }

        if (Test-SmartPipeSamePath -Left $current -Right $boundaryPath) {
            break
        }

        $parent = Split-Path -Path $current -Parent
        if ([string]::IsNullOrWhiteSpace($parent) -or (Test-SmartPipeSamePath -Left $parent -Right $current)) {
            throw "Could not prove path containment: $candidate"
        }

        $current = Get-SmartPipeFullPath -Path $parent
        if (-not (Test-SmartPipeContainedPath -Path $current -Boundary $boundaryPath -AllowBoundary)) {
            throw "Path escaped the approved boundary: $candidate"
        }
    }

    if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
        return
    }

    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($candidate)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($child in Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse point is not an approved cleanup target: $($child.FullName)"
            }

            if ($child.PSIsContainer) {
                $pending.Push($child.FullName)
            }
        }
    }
}

function Assert-SmartPipeCleanupTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Boundary,

        [switch] $AllowBoundary
    )

    $candidate = Get-SmartPipeFullPath -Path $Path
    $boundaryPath = Get-SmartPipeFullPath -Path $Boundary
    if (Test-SmartPipeSamePath -Left $candidate -Right $boundaryPath) {
        throw "Cleanup target is the approved boundary itself: $candidate"
    }
    if (-not (Test-SmartPipeContainedPath -Path $candidate -Boundary $boundaryPath -AllowBoundary:$AllowBoundary)) {
        throw "Cleanup target is outside the approved boundary: $candidate"
    }

    $runnerLeaf = Split-Path -Path $candidate -Leaf
    if ($runnerLeaf -in @('_tool', '_work', 'bin', 'Runner', 'externals')) {
        throw "Cleanup target is too broad or protected: $candidate"
    }

    $runnerRoot = Get-SmartPipeFullPath -Path $script:SmartPipeRunnerDefaultRoot
    if (Test-SmartPipeSamePath -Left $candidate -Right $runnerRoot) {
        throw 'The dedicated runner root is never a cleanup target.'
    }

    Assert-SmartPipeNoReparsePath -Path $candidate -Boundary $Boundary
    return $candidate
}

function Remove-SmartPipeCleanupTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Boundary,

        [switch] $AllowBoundary
    )

    $candidate = Assert-SmartPipeCleanupTarget -Path $Path -Boundary $Boundary -AllowBoundary:$AllowBoundary
    if (-not (Test-Path -LiteralPath $candidate)) {
        return $false
    }

    if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
        throw "Cleanup target is not a directory: $candidate"
    }

    Remove-Item -LiteralPath $candidate -Recurse -Force -ErrorAction Stop
    return $true
}

function Assert-SmartPipeRepository {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string] $Repository
    )

    if (-not [string]::Equals($Repository, $script:SmartPipeRunnerRepository, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected repository '$Repository'."
    }
}

function Resolve-SmartPipeRunnerName {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [string] $RequestedName = ''
    )

    $configPath = Join-Path $Root '.runner'
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "Runner configuration is missing: $configPath"
    }

    try {
        $config = Get-Content -LiteralPath $configPath -Raw -ErrorAction Stop | ConvertFrom-Json
        $agentNameProperty = @($config.PSObject.Properties | Where-Object { $_.Name -eq 'agentName' })
        if ($agentNameProperty.Count -ne 1 -or $null -eq $agentNameProperty[0].Value -or
            $agentNameProperty[0].Value -is [Array]) {
            throw 'agentName is missing or ambiguous.'
        }
        $configuredName = [string]$agentNameProperty[0].Value
    }
    catch {
        throw "Runner configuration is invalid: $configPath"
    }

    if ([string]::IsNullOrWhiteSpace($configuredName)) {
        throw "Runner configuration has no unambiguous agentName: $configPath"
    }
    if (-not [string]::IsNullOrWhiteSpace($RequestedName) -and
        -not [string]::Equals($RequestedName, $configuredName, [StringComparison]::Ordinal)) {
        throw "Requested runner name '$RequestedName' does not match .runner agentName '$configuredName'."
    }

    return $configuredName
}

function Assert-SmartPipeWorkspaceRepository {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Workspace
    )

    $gitPath = Join-Path $Workspace '.git'
    if (-not (Test-Path -LiteralPath $gitPath)) {
        throw "Workspace repository metadata is missing: $Workspace"
    }

    $configPath = if (Test-Path -LiteralPath $gitPath -PathType Container) {
        Join-Path $gitPath 'config'
    }
    else {
        $gitPath
    }

    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "Workspace repository configuration is missing: $Workspace"
    }

    $global:LASTEXITCODE = 0
    $gitOutput = & git -C $Workspace remote get-url origin 2>&1
    $gitExitCode = $global:LASTEXITCODE
    if ($gitExitCode -eq 0) {
        $urls = @($gitOutput | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ -ne '' })
        if ($urls.Count -ne 1) {
            throw "Workspace origin remote is ambiguous: $Workspace"
        }

        Assert-SmartPipeCanonicalRemote -Url $urls[0] -Workspace $Workspace
        return
    }

    # Test fixtures and worktrees without a usable git executable use the
    # strict INI fallback. Comments never participate in URL selection.
    $section = ''
    $originUrls = [Collections.Generic.List[string]]::new()
    foreach ($line in (Get-Content -LiteralPath $configPath -ErrorAction Stop)) {
        $text = ([string]$line).Trim()
        if ($text -eq '' -or $text.StartsWith('#') -or $text.StartsWith(';')) {
            continue
        }

        if ($text -match '^\[remote\s+"([^"]+)"\]$') {
            $section = $Matches[1]
            continue
        }

        if ($text -match '^(?<key>[A-Za-z][A-Za-z0-9-]*)\s*=\s*(?<value>\S+)$') {
            if ($section -eq 'origin' -and $Matches.key -eq 'url') {
                [void]$originUrls.Add($Matches.value)
            }
            elseif ($section -eq 'origin' -and $Matches.key -notin @('fetch', 'pushurl', 'mirror', 'tagopt')) {
                throw "Unsupported origin configuration entry: $Workspace"
            }
            continue
        }

        throw "Invalid git remote configuration: $Workspace"
    }

    if ($originUrls.Count -ne 1) {
        throw "Workspace origin remote is missing or ambiguous: $Workspace"
    }

    Assert-SmartPipeCanonicalRemote -Url $originUrls[0] -Workspace $Workspace
}

function Assert-SmartPipeCanonicalRemote {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Url,

        [Parameter(Mandatory = $true)]
        [string] $Workspace
    )

    $normalized = $Url.Trim()
    if ($normalized -match '^(?i:https://github\.com/MrFr3di/SmartPipe-Core(?:\.git)?|git@github\.com:MrFr3di/SmartPipe-Core(?:\.git)?|ssh://git@github\.com/MrFr3di/SmartPipe-Core(?:\.git)?)$') {
        return
    }

    throw "Workspace origin remote is not MrFr3di/SmartPipe-Core: $Workspace"
}

function Get-SmartPipeListenerProcesses {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [string] $FixturePath = ''
    )

    if (-not [string]::IsNullOrWhiteSpace($FixturePath)) {
        if (-not (Test-Path -LiteralPath $FixturePath -PathType Leaf)) {
            return @()
        }

        $text = (Get-Content -LiteralPath $FixturePath -Raw -ErrorAction Stop).Trim()
        $count = 0
        if (-not [int]::TryParse($text, [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$count) -or $count -lt 0) {
            throw "Invalid listener fixture state: $FixturePath"
        }

        $fixtureListeners = [Collections.Generic.List[object]]::new()
        for ($index = 1; $index -le $count; $index++) {
            [void]$fixtureListeners.Add([pscustomobject]@{
                ProcessId = 0
                Name = 'Runner.Listener.fixture'
                CommandLine = $Root
            })
        }
        return @($fixtureListeners)
    }

    try {
        $escapedRoot = [Regex]::Escape((Get-SmartPipeFullPath -Path $Root))
        return @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop | Where-Object {
            $_.Name -in @('Runner.Listener.exe', 'Runner.Listener') -and
            $_.CommandLine -match $escapedRoot
        })
    }
    catch {
        if ($IsWindows) {
            throw "Unable to inspect listener processes for $Root."
        }
        return @()
    }
}

function Stop-SmartPipeListenerProcesses {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [string] $FixturePath = '',

        [int] $TimeoutSeconds = 20
    )

    if ($TimeoutSeconds -lt 1) {
        throw 'Listener stop timeout must be positive.'
    }

    $listeners = @(Get-SmartPipeListenerProcesses -Root $Root -FixturePath $FixturePath)
    if (-not [string]::IsNullOrWhiteSpace($FixturePath)) {
        Set-Content -LiteralPath $FixturePath -Value '0' -NoNewline
        return
    }

    foreach ($listener in $listeners) {
        if ([int]$listener.ProcessId -gt 0) {
            Stop-Process -Id $listener.ProcessId -Force -ErrorAction Stop
        }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (@(Get-SmartPipeListenerProcesses -Root $Root -FixturePath $FixturePath).Count -gt 0) {
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "Runner listener did not stop within $TimeoutSeconds seconds: $Root"
        }
        Start-Sleep -Seconds 1
    }
}

function Start-SmartPipeRunner {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [string] $FixturePath = ''
    )

    $runCommand = Join-Path $Root 'run.cmd'
    if (-not (Test-Path -LiteralPath $runCommand -PathType Leaf)) {
        throw "Runner command is missing: $runCommand"
    }

    Start-Process -FilePath $runCommand -WorkingDirectory $Root -WindowStyle Hidden | Out-Null
    if (-not [string]::IsNullOrWhiteSpace($FixturePath)) {
        Set-Content -LiteralPath $FixturePath -Value '1' -NoNewline
    }
}

function Get-SmartPipeRemoteRunner {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Repository,

        [Parameter(Mandatory = $true)]
        [string] $RunnerName,

        [string] $GhPath = 'gh'
    )

    $global:LASTEXITCODE = 0
    $json = & $GhPath api "repos/$Repository/actions/runners?per_page=100" 2>&1
    $ghExitCode = $global:LASTEXITCODE
    if ($ghExitCode -ne 0) {
        throw "Unable to query GitHub runner state: $($json -join ' ')"
    }

    $response = ($json -join [Environment]::NewLine) | ConvertFrom-Json
    $runners = @($response.runners | Where-Object { $_.name -eq $RunnerName })
    if ($runners.Count -ne 1) {
        throw "Expected exactly one GitHub runner named '$RunnerName'."
    }

    return ,$runners[0]
}

function Get-SmartPipeRunnerLabelNames {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Runner
    )

    $names = [Collections.Generic.List[string]]::new()
    foreach ($label in @($Runner.labels)) {
        if ($label -is [string]) {
            $name = [string]$label
        }
        else {
            $nameProperty = $label.PSObject.Properties['name']
            $name = if ($null -ne $nameProperty) { [string]$nameProperty.Value } else { '' }
        }
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            [void]$names.Add($name)
        }
    }
    return $names.ToArray()
}

function Add-SmartPipeRunnerLabel {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Repository,

        [Parameter(Mandatory = $true)]
        [object] $Runner,

        [string] $GhPath = 'gh'
    )

    $runnerId = [string]$Runner.id
    if ([string]::IsNullOrWhiteSpace($runnerId) -or $runnerId -notmatch '^[0-9]+$') {
        throw 'GitHub runner id is missing or invalid; refusing label mutation.'
    }

    $before = @(Get-SmartPipeRunnerLabelNames -Runner $Runner)
    $global:LASTEXITCODE = 0
    $json = & $GhPath api --method POST "repos/$Repository/actions/runners/$runnerId/labels" -f "labels[]=$script:SmartPipeRunnerLabel" 2>&1
    $ghExitCode = $global:LASTEXITCODE
    if ($ghExitCode -ne 0) {
        throw "Unable to add runner label '$script:SmartPipeRunnerLabel'. Existing labels were not intentionally removed."
    }

    try {
        $postResponse = ($json -join [Environment]::NewLine) | ConvertFrom-Json
        $postLabels = @(Get-SmartPipeRunnerLabelNames -Runner $postResponse)
    }
    catch {
        throw "GitHub runner label response was invalid: $($json -join ' '). Recovery: existing labels were not intentionally removed; inspect the runner before retrying."
    }
    if ($script:SmartPipeRunnerLabel -notin $postLabels) {
        throw "GitHub did not confirm runner label '$script:SmartPipeRunnerLabel' in the mutation response."
    }

    $afterRunner = Get-SmartPipeRemoteRunner -Repository $Repository -RunnerName ([string]$Runner.name) -GhPath $GhPath
    $after = @(Get-SmartPipeRunnerLabelNames -Runner $afterRunner)
    if ($script:SmartPipeRunnerLabel -notin $after) {
        throw "GitHub did not confirm runner label '$script:SmartPipeRunnerLabel'."
    }
    foreach ($label in $before) {
        if ($label -notin $after) {
            throw "Adding runner label removed existing label '$label'; refusing to continue."
        }
    }
}

function Remove-SmartPipeRunnerLabel {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Repository,

        [Parameter(Mandatory = $true)]
        [object] $Runner,

        [string] $GhPath = 'gh'
    )

    $runnerId = [string]$Runner.id
    if ([string]::IsNullOrWhiteSpace($runnerId) -or $runnerId -notmatch '^[0-9]+$') {
        throw 'GitHub runner id is missing or invalid; refusing label mutation.'
    }

    $before = @(Get-SmartPipeRunnerLabelNames -Runner $Runner)
    if ($script:SmartPipeRunnerLabel -in $before) {
        $global:LASTEXITCODE = 0
        $null = & $GhPath api --method DELETE "repos/$Repository/actions/runners/$runnerId/labels/$script:SmartPipeRunnerLabel" 2>&1
        $ghExitCode = $global:LASTEXITCODE
        if ($ghExitCode -ne 0) {
            throw "Unable to remove runner label '$script:SmartPipeRunnerLabel'."
        }
    }

    $afterRunner = Get-SmartPipeRemoteRunner -Repository $Repository -RunnerName ([string]$Runner.name) -GhPath $GhPath
    $after = @(Get-SmartPipeRunnerLabelNames -Runner $afterRunner)
    if ($script:SmartPipeRunnerLabel -in $after) {
        throw "GitHub still reports runner label '$script:SmartPipeRunnerLabel' after removal."
    }
    foreach ($label in ($before | Where-Object { $_ -ne $script:SmartPipeRunnerLabel })) {
        if ($label -notin $after) {
            throw "Removing runner label removed unrelated label '$label'; refusing to continue."
        }
    }
}

function Assert-SmartPipeActionsRunsIdle {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Repository,

        [string] $GhPath = 'gh'
    )

    foreach ($status in @('queued', 'in_progress')) {
        $global:LASTEXITCODE = 0
        $json = & $GhPath api "repos/$Repository/actions/runs?status=$status&per_page=100" 2>&1
        $ghExitCode = $global:LASTEXITCODE
        if ($ghExitCode -ne 0) {
            throw "Unable to query $status GitHub Actions runs: $($json -join ' ')"
        }

        $response = ($json -join [Environment]::NewLine) | ConvertFrom-Json
        if (@($response.workflow_runs).Count -gt 0) {
            throw "GitHub Actions has $status runs; refusing runner mutation."
        }
    }
}

function Assert-SmartPipeRemoteRunnerIdle {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Repository,

        [Parameter(Mandatory = $true)]
        [string] $RunnerName,

        [string] $GhPath = 'gh'
    )

    $runner = Get-SmartPipeRemoteRunner -Repository $Repository -RunnerName $RunnerName -GhPath $GhPath
    if ($runner.busy -eq $true) {
        throw "Runner '$RunnerName' is busy."
    }
    return ,$runner
}

function Wait-SmartPipeRunnerReady {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $Repository,

        [Parameter(Mandatory = $true)]
        [string] $RunnerName,

        [string] $GhPath = 'gh',
        [string] $FixturePath = '',
        [int] $TimeoutSeconds = 60
    )

    if ($TimeoutSeconds -lt 1) {
        throw 'Runner readiness timeout must be positive.'
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $listeners = @(Get-SmartPipeListenerProcesses -Root $Root -FixturePath $FixturePath)
        if ($listeners.Count -gt 1) {
            throw "More than one runner listener is tied to $Root."
        }

        $runner = Get-SmartPipeRemoteRunner -Repository $Repository -RunnerName $RunnerName -GhPath $GhPath
        if ($listeners.Count -eq 1 -and [string]$runner.status -eq 'online' -and $runner.busy -eq $false) {
            return
        }

        if ([DateTime]::UtcNow -ge $deadline) {
            throw "Runner '$RunnerName' did not become online and idle with one listener within $TimeoutSeconds seconds."
        }
        Start-Sleep -Seconds 1
    }
}

function Restart-SmartPipeRunner {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $Repository,

        [Parameter(Mandatory = $true)]
        [string] $RunnerName,

        [string] $GhPath = 'gh',
        [string] $FixturePath = '',
        [int] $TimeoutSeconds = 60
    )

    Stop-SmartPipeListenerProcesses -Root $Root -FixturePath $FixturePath
    Start-SmartPipeRunner -Root $Root -FixturePath $FixturePath
    Wait-SmartPipeRunnerReady -Root $Root -Repository $Repository -RunnerName $RunnerName -GhPath $GhPath -FixturePath $FixturePath -TimeoutSeconds $TimeoutSeconds
}

function Get-SmartPipeOwnedEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [string] $EnvironmentPath
    )

    if (Test-Path -LiteralPath $EnvironmentPath -PathType Leaf) {
        $raw = Get-Content -LiteralPath $EnvironmentPath -Raw -ErrorAction Stop
        if ([string]::IsNullOrEmpty($raw)) {
            return ,([Collections.Generic.List[string]]::new())
        }

        $lines = [Collections.Generic.List[string]]::new()
        $rawLines = @($raw -split '\r?\n')
        if ($rawLines.Count -gt 0 -and $rawLines[$rawLines.Count - 1] -eq '') {
            $rawLines = if ($rawLines.Count -eq 1) { @() } else { $rawLines[0..($rawLines.Count - 2)] }
        }
        foreach ($line in $rawLines) {
            [void]$lines.Add([string]$line)
        }
        return ,$lines
    }

    return ,([Collections.Generic.List[string]]::new())
}

function Write-SmartPipeEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [string] $EnvironmentPath,

        [Parameter(Mandatory = $true)]
        [string] $HookPath,

        [Parameter(Mandatory = $true)]
        [string] $DotNetInstallDirectory
    )

    $lines = Get-SmartPipeOwnedEnvironment -EnvironmentPath $EnvironmentPath
    $owned = @{
        'ACTIONS_RUNNER_HOOK_JOB_COMPLETED' = $HookPath
        'DOTNET_INSTALL_DIR' = $DotNetInstallDirectory
    }

    foreach ($key in $owned.Keys) {
        for ($index = $lines.Count - 1; $index -ge 0; $index--) {
            if ($lines[$index] -match "^\s*${key}=") {
                $lines.RemoveAt($index)
            }
        }

        $lines.Add("$key=$($owned[$key])")
    }

    $temporaryPath = "$EnvironmentPath.smartpipe.tmp"
    [IO.File]::WriteAllText($temporaryPath, (($lines -join [Environment]::NewLine) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $EnvironmentPath -Force
}

function Remove-SmartPipeEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [string] $EnvironmentPath
    )

    if (-not (Test-Path -LiteralPath $EnvironmentPath -PathType Leaf)) {
        return
    }

    $lines = Get-SmartPipeOwnedEnvironment -EnvironmentPath $EnvironmentPath
    $ownedKeys = @('ACTIONS_RUNNER_HOOK_JOB_COMPLETED', 'DOTNET_INSTALL_DIR')
    for ($index = $lines.Count - 1; $index -ge 0; $index--) {
        foreach ($key in $ownedKeys) {
            if ($lines[$index] -match "^\s*${key}=") {
                $lines.RemoveAt($index)
                break
            }
        }
    }

    [IO.File]::WriteAllText($EnvironmentPath, (($lines -join [Environment]::NewLine) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
}
