[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$runnerScriptRoot = Join-Path $PSScriptRoot '..\runner'
$jobStartScript = [IO.Path]::GetFullPath((Join-Path $runnerScriptRoot 'job-start-cleanup.ps1'))
$installScript = [IO.Path]::GetFullPath((Join-Path $runnerScriptRoot 'install-runner.ps1'))
$uninstallScript = [IO.Path]::GetFullPath((Join-Path $runnerScriptRoot 'uninstall-runner.ps1'))
$monitorScript = [IO.Path]::GetFullPath((Join-Path $runnerScriptRoot 'monitor-pr.ps1'))

function Assert-RunnerEqual {
    param(
        [Parameter(Mandatory = $true)] $Actual,
        [Parameter(Mandatory = $true)] $Expected,
        [Parameter(Mandatory = $true)] [string] $Message
    )

    if ($Actual -ne $Expected) {
        throw "$Message (actual: '$Actual'; expected: '$Expected')"
    }
}

function Assert-RunnerTrue {
    param(
        [Parameter(Mandatory = $true)] [bool] $Condition,
        [Parameter(Mandatory = $true)] [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-RunnerScript {
    param(
        [Parameter(Mandatory = $true)] [string] $ScriptPath,
        [Parameter(Mandatory = $true)] [string[]] $Arguments,
        [string] $WorkingDirectory = ''
    )

    if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $output = & pwsh -NoProfile -File $ScriptPath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    else {
        $captureId = [Guid]::NewGuid().ToString('N')
        $stdoutPath = Join-Path ([IO.Path]::GetTempPath()) "smartpipe-runner-$captureId.out"
        $stderrPath = Join-Path ([IO.Path]::GetTempPath()) "smartpipe-runner-$captureId.err"
        try {
            $process = Start-Process -FilePath pwsh -ArgumentList (@('-NoProfile', '-File', $ScriptPath) + $Arguments) -WorkingDirectory $WorkingDirectory -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -Wait -PassThru
            $output = @((Get-Content -LiteralPath $stdoutPath -ErrorAction SilentlyContinue), (Get-Content -LiteralPath $stderrPath -ErrorAction SilentlyContinue))
            $exitCode = $process.ExitCode
        }
        finally {
            Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
        }
    }
    [pscustomobject]@{
        ExitCode = $exitCode
        Output = ($output | Out-String).Trim()
    }
}

$fixture = Join-Path ([IO.Path]::GetTempPath()) "smartpipe-runner-contract-$([Guid]::NewGuid().ToString('N'))"
$runnerRoot = Join-Path $fixture 'SmartPipe-Runner'
$workspace = Join-Path $runnerRoot '_work\SmartPipe.Core\SmartPipe.Core'
$tempRoot = Join-Path $runnerRoot '_temp'
$toolRoot = Join-Path $runnerRoot '_tool'
$sibling = Join-Path $runnerRoot '_work\Other.Repo\Other.Repo'

try {
    New-Item -ItemType Directory -Path $workspace, $tempRoot, $toolRoot, $sibling -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $workspace '.git'), (Join-Path $tempRoot 'SmartPipe.Core'), (Join-Path $tempRoot 'CodeQL') -Force | Out-Null
    @'
{"agentName":"SmartPipe-Runner"}
'@ | Set-Content -LiteralPath (Join-Path $runnerRoot '.runner')
    @'
[remote "origin"]
    url = https://github.com/MrFr3di/SmartPipe-Core.git
'@ | Set-Content -LiteralPath (Join-Path $workspace '.git\config')
    'workspace output' | Set-Content -LiteralPath (Join-Path $workspace 'output.txt')
    'tool must survive' | Set-Content -LiteralPath (Join-Path $toolRoot 'preserve.txt')
    'sibling must survive' | Set-Content -LiteralPath (Join-Path $sibling 'preserve.txt')
    'known temp' | Set-Content -LiteralPath (Join-Path $tempRoot 'SmartPipe.Core\cache.txt')
    'known codeql temp' | Set-Content -LiteralPath (Join-Path $tempRoot 'CodeQL\cache.txt')
    'unrelated temp' | Set-Content -LiteralPath (Join-Path $tempRoot 'unrelated.tmp')

    $cleanup = Invoke-RunnerScript -ScriptPath $jobStartScript -WorkingDirectory $workspace -Arguments @(
        '-RunnerRoot', $runnerRoot,
        '-WorkspaceRoot', $workspace,
        '-TempRoot', $tempRoot,
        '-Repository', 'MrFr3di/SmartPipe-Core',
        '-AllowTestRoot'
    )
    Assert-RunnerEqual -Actual $cleanup.ExitCode -Expected 0 -Message "Job-start cleanup must succeed for a valid checkout. $($cleanup.Output)"
    Assert-RunnerTrue -Condition (Test-Path -LiteralPath $workspace -PathType Container) -Message 'The exact workspace directory must be recreated.'
    Assert-RunnerEqual -Actual @(Get-ChildItem -LiteralPath $workspace -Force).Count -Expected 0 -Message 'The recreated workspace must be empty.'
    Assert-RunnerTrue -Condition (-not (Test-Path -LiteralPath (Join-Path $workspace '.git'))) -Message 'The recreated workspace must not retain .git.'
    Assert-RunnerTrue -Condition (-not (Test-Path -LiteralPath (Join-Path $workspace 'output.txt'))) -Message 'The recreated workspace must not retain stale files.'
    Assert-RunnerTrue -Condition (Test-Path -LiteralPath (Join-Path $toolRoot 'preserve.txt')) -Message '_tool must be preserved.'
    Assert-RunnerTrue -Condition (Test-Path -LiteralPath (Join-Path $sibling 'preserve.txt')) -Message 'Sibling repositories must be preserved.'
    Assert-RunnerTrue -Condition (Test-Path -LiteralPath (Join-Path $tempRoot 'unrelated.tmp')) -Message 'Unrelated temp files must be preserved.'
    Assert-RunnerTrue -Condition (-not (Test-Path -LiteralPath (Join-Path $tempRoot 'SmartPipe.Core'))) -Message 'Known SmartPipe temp must be removed.'
    Assert-RunnerTrue -Condition (-not (Test-Path -LiteralPath (Join-Path $tempRoot 'CodeQL'))) -Message 'Known CodeQL temp must be removed.'

    $absentWorkspace = Join-Path $runnerRoot '_work\SmartPipe.Core\absent'
    $absent = Invoke-RunnerScript -ScriptPath $jobStartScript -Arguments @(
        '-RunnerRoot', $runnerRoot,
        '-WorkspaceRoot', $absentWorkspace,
        '-TempRoot', $tempRoot,
        '-Repository', 'MrFr3di/SmartPipe-Core',
        '-AllowTestRoot'
    )
    Assert-RunnerEqual -Actual $absent.ExitCode -Expected 0 -Message 'Absent cleanup targets must be successful.'
    Assert-RunnerTrue -Condition (Test-Path -LiteralPath $absentWorkspace -PathType Container) -Message 'An absent workspace must be recreated.'
    Assert-RunnerEqual -Actual @(Get-ChildItem -LiteralPath $absentWorkspace -Force).Count -Expected 0 -Message 'A recreated absent workspace must be empty.'

    New-Item -ItemType Directory -Path $workspace, (Join-Path $workspace '.git') -Force | Out-Null
    @'
[remote "origin"]
    url = https://github.com/example/other.git
# https://github.com/MrFr3di/SmartPipe-Core.git
[remote "upstream"]
    url = https://github.com/MrFr3di/SmartPipe-Core.git
'@ | Set-Content -LiteralPath (Join-Path $workspace '.git\config')
    $wrongRepo = Invoke-RunnerScript -ScriptPath $jobStartScript -Arguments @(
        '-RunnerRoot', $runnerRoot,
        '-WorkspaceRoot', $workspace,
        '-TempRoot', $tempRoot,
        '-Repository', 'MrFr3di/SmartPipe-Core',
        '-AllowTestRoot'
    )
    Assert-RunnerTrue -Condition ($wrongRepo.ExitCode -ne 0) -Message 'A checkout with a commented or secondary canonical remote must fail closed.'
    Assert-RunnerTrue -Condition (Test-Path -LiteralPath $workspace) -Message 'A rejected checkout must not be deleted.'

    $outside = Join-Path $fixture 'outside'
    New-Item -ItemType Directory -Path $outside -Force | Out-Null
    $outsideResult = Invoke-RunnerScript -ScriptPath $jobStartScript -Arguments @(
        '-RunnerRoot', $runnerRoot,
        '-WorkspaceRoot', $outside,
        '-TempRoot', $tempRoot,
        '-Repository', 'MrFr3di/SmartPipe-Core',
        '-AllowTestRoot'
    )
    Assert-RunnerTrue -Condition ($outsideResult.ExitCode -ne 0) -Message 'A workspace outside the runner root must fail closed.'

    Remove-Item -LiteralPath $workspace -Recurse -Force
    New-Item -ItemType Directory -Path $workspace, (Join-Path $workspace '.git') -Force | Out-Null
    @'
[remote "origin"]
    url = https://github.com/MrFr3di/SmartPipe-Core.git
'@ | Set-Content -LiteralPath (Join-Path $workspace '.git\config')

    $reparseCreated = $false
    try {
        New-Item -ItemType SymbolicLink -Path (Join-Path $workspace 'reparse') -Target $sibling -Force -ErrorAction Stop | Out-Null
        $reparseCreated = $true
    }
    catch {
        Write-Output 'Runner contract: symbolic-link fixture unavailable; reparse refusal remains covered by workflow cleanup contracts.'
    }
    if ($reparseCreated) {
        $reparse = Invoke-RunnerScript -ScriptPath $jobStartScript -Arguments @(
            '-RunnerRoot', $runnerRoot,
            '-WorkspaceRoot', $workspace,
            '-TempRoot', $tempRoot,
            '-Repository', 'MrFr3di/SmartPipe-Core',
            '-AllowTestRoot'
        )
        Assert-RunnerTrue -Condition ($reparse.ExitCode -ne 0) -Message 'A reparse point must fail closed.'
        Assert-RunnerTrue -Condition (Test-Path -LiteralPath $workspace) -Message 'A reparse rejection must preserve the checkout.'
    }

    Remove-Item -LiteralPath $workspace -Recurse -Force
    @'
@echo off
exit /b 0
'@ | Set-Content -LiteralPath (Join-Path $runnerRoot 'run.cmd')
    $listenerFixture = Join-Path $fixture 'listener.count'
    '1' | Set-Content -LiteralPath $listenerFixture -NoNewline
    $runnerGh = Join-Path $fixture 'runner-gh.ps1'
$queuedFlag = Join-Path $fixture 'queued.flag'
$inProgressFlag = Join-Path $fixture 'in-progress.flag'
$offlineFlag = Join-Path $fixture 'offline.flag'
    $labelState = Join-Path $fixture 'runner-labels.json'
    @('self-hosted', 'Windows', 'X64', 'existing-label') | ConvertTo-Json -Compress | Set-Content -LiteralPath $labelState
    @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)
$joined = $Arguments -join ' '
$labels = @((Get-Content -LiteralPath $env:SMARTPIPE_LABEL_STATE -Raw | ConvertFrom-Json))
if ($joined -like '*actions/runners/42/labels/smartpipe-cleanup-v1*') {
    $labels = @($labels | Where-Object { $_ -ne 'smartpipe-cleanup-v1' })
    $labels | ConvertTo-Json -Compress | Set-Content -LiteralPath $env:SMARTPIPE_LABEL_STATE
    $response = @{ labels = @($labels | ForEach-Object { @{ name = $_ } }) }
}
elseif ($joined -like '*actions/runners/42/labels*') {
    if ('smartpipe-cleanup-v1' -notin $labels) { $labels += 'smartpipe-cleanup-v1' }
    Remove-Item -LiteralPath $env:SMARTPIPE_OFFLINE_FLAG -Force -ErrorAction SilentlyContinue
    $labels | ConvertTo-Json -Compress | Set-Content -LiteralPath $env:SMARTPIPE_LABEL_STATE
    $response = @{ labels = @($labels | ForEach-Object { @{ name = $_ } }) }
}
elseif ($joined -like '*actions/runs?status=queued*') {
    if (Test-Path -LiteralPath $env:SMARTPIPE_QUEUED_FLAG) { $response = @{ workflow_runs = @(@{ id = 1 }) } } else { $response = @{ workflow_runs = @() } }
}
elseif ($joined -like '*actions/runs?status=in_progress*') {
    if (Test-Path -LiteralPath $env:SMARTPIPE_IN_PROGRESS_FLAG) { $response = @{ workflow_runs = @(@{ id = 2 }) } } else { $response = @{ workflow_runs = @() } }
}
elseif ($joined -like '*actions/runners?*') {
    $labelObjects = @($labels | ForEach-Object { @{ name = $_ } })
    $runnerStatus = if (Test-Path -LiteralPath $env:SMARTPIPE_OFFLINE_FLAG) { 'offline' } else { 'online' }
    $response = @{ runners = @(@{ id = 42; name = 'SmartPipe-Runner'; status = $runnerStatus; busy = $false; labels = $labelObjects }) }
}
elseif ($null -eq $response) {
    throw "Unexpected fake gh request: $joined"
}
    $response | ConvertTo-Json -Depth 5 -Compress
'@ | Set-Content -LiteralPath $runnerGh
    $env:SMARTPIPE_QUEUED_FLAG = $queuedFlag
    $env:SMARTPIPE_IN_PROGRESS_FLAG = $inProgressFlag
    $env:SMARTPIPE_OFFLINE_FLAG = $offlineFlag
    $env:SMARTPIPE_LABEL_STATE = $labelState
    $environment = Join-Path $runnerRoot '.env'
@'
UNRELATED_ENV=preserve
ACTIONS_RUNNER_HOOK_JOB_COMPLETED=C:\legacy\smartpipe-post-job-cleanup.ps1
'@ | Set-Content -LiteralPath $environment
    New-Item -ItemType Directory -Path (Join-Path $runnerRoot 'hooks') -Force | Out-Null
    'legacy hook' | Set-Content -LiteralPath (Join-Path $runnerRoot 'hooks\smartpipe-post-job-cleanup.ps1')

    New-Item -ItemType File -Path $queuedFlag -Force | Out-Null
    $queuedInstall = Invoke-RunnerScript -ScriptPath $installScript -Arguments @(
        '-RunnerRoot', $runnerRoot,
        '-Repository', 'MrFr3di/SmartPipe-Core',
        '-RunnerName', 'SmartPipe-Runner',
        '-GhPath', $runnerGh,
        '-ListenerFixturePath', $listenerFixture,
        '-AllowTestRoot'
    )
    Assert-RunnerTrue -Condition ($queuedInstall.ExitCode -ne 0) -Message "Installer must refuse queued Actions runs before mutation. $($queuedInstall.Output)"
    Assert-RunnerTrue -Condition (-not (Test-Path -LiteralPath (Join-Path $runnerRoot 'hooks\smartpipe-job-start-cleanup.ps1'))) -Message 'Queued-run refusal must not copy the hook.'
    Remove-Item -LiteralPath $queuedFlag -Force

    New-Item -ItemType File -Path $inProgressFlag -Force | Out-Null
    $inProgressInstall = Invoke-RunnerScript -ScriptPath $installScript -Arguments @(
        '-RunnerRoot', $runnerRoot,
        '-Repository', 'MrFr3di/SmartPipe-Core',
        '-RunnerName', 'SmartPipe-Runner',
        '-GhPath', $runnerGh,
        '-ListenerFixturePath', $listenerFixture,
        '-AllowTestRoot'
    )
    Assert-RunnerTrue -Condition ($inProgressInstall.ExitCode -ne 0) -Message "Installer must refuse in-progress Actions runs before mutation. $($inProgressInstall.Output)"
    Assert-RunnerTrue -Condition (-not (Test-Path -LiteralPath (Join-Path $runnerRoot 'hooks\smartpipe-job-start-cleanup.ps1'))) -Message 'In-progress refusal must not copy the hook.'
    Remove-Item -LiteralPath $inProgressFlag -Force

    New-Item -ItemType File -Path $offlineFlag -Force | Out-Null
    $install = Invoke-RunnerScript -ScriptPath $installScript -Arguments @(
        '-RunnerRoot', $runnerRoot,
        '-Repository', 'MrFr3di/SmartPipe-Core',
        '-GhPath', $runnerGh,
        '-ListenerFixturePath', $listenerFixture,
        '-AllowTestRoot'
    )
    Assert-RunnerEqual -Actual $install.ExitCode -Expected 0 -Message "Installer must accept an idle fixture root and restore one listener. $($install.Output)"
    Assert-RunnerEqual -Actual ((Get-Content -LiteralPath $listenerFixture -Raw).Trim()) -Expected '1' -Message 'Successful installation must leave exactly one listener fixture.'
    $labelsAfterInstall = @((Get-Content -LiteralPath $labelState -Raw | ConvertFrom-Json))
    Assert-RunnerTrue -Condition ('smartpipe-cleanup-v1' -in $labelsAfterInstall) -Message 'Installer must register the cleanup label through GitHub.'
    Assert-RunnerTrue -Condition ('existing-label' -in $labelsAfterInstall) -Message 'Installer must preserve unrelated runner labels.'
    $installAgain = Invoke-RunnerScript -ScriptPath $installScript -Arguments @(
        '-RunnerRoot', $runnerRoot,
        '-Repository', 'MrFr3di/SmartPipe-Core',
        '-RunnerName', 'SmartPipe-Runner',
        '-GhPath', $runnerGh,
        '-ListenerFixturePath', $listenerFixture,
        '-AllowTestRoot'
    )
    Assert-RunnerEqual -Actual $installAgain.ExitCode -Expected 0 -Message 'Installer must be idempotent.'
    $environmentLines = @(Get-Content -LiteralPath $environment)
    Assert-RunnerEqual -Actual @($environmentLines | Where-Object { $_ -match '^ACTIONS_RUNNER_HOOK_JOB_STARTED=' }).Count -Expected 1 -Message 'Job-start hook environment entry must be unique.'
    Assert-RunnerEqual -Actual @($environmentLines | Where-Object { $_ -match '^ACTIONS_RUNNER_HOOK_JOB_COMPLETED=' }).Count -Expected 0 -Message 'Legacy job-completed hook environment entry must be removed.'
    Assert-RunnerEqual -Actual @($environmentLines | Where-Object { $_ -match '^DOTNET_INSTALL_DIR=' }).Count -Expected 1 -Message '.NET install directory entry must be unique.'
    Assert-RunnerEqual -Actual @($environmentLines | Where-Object { $_ -match '^SMARTPIPE_CLEANUP_LABEL=' }).Count -Expected 0 -Message 'Runner labels must not be represented by an environment marker.'
    Assert-RunnerTrue -Condition (@($environmentLines | Where-Object { $_ -eq 'UNRELATED_ENV=preserve' }).Count -eq 1) -Message 'Installer must preserve unrelated environment entries.'
    Assert-RunnerTrue -Condition (Test-Path -LiteralPath (Join-Path $runnerRoot 'hooks\smartpipe-job-start-cleanup.ps1')) -Message 'Installer must copy the job-start hook.'
    Assert-RunnerTrue -Condition (-not (Test-Path -LiteralPath (Join-Path $runnerRoot 'hooks\smartpipe-post-job-cleanup.ps1'))) -Message 'Installer must remove the legacy hook copy.'

    $environmentBeforeAmbiguous = Get-Content -LiteralPath $environment -Raw
    $labelsBeforeAmbiguous = Get-Content -LiteralPath $labelState -Raw
    'unclassified-duplicate' | Set-Content -LiteralPath $listenerFixture -NoNewline
    $ambiguousInstall = Invoke-RunnerScript -ScriptPath $installScript -Arguments @(
        '-RunnerRoot', $runnerRoot,
        '-Repository', 'MrFr3di/SmartPipe-Core',
        '-RunnerName', 'SmartPipe-Runner',
        '-GhPath', $runnerGh,
        '-ListenerFixturePath', $listenerFixture,
        '-AllowTestRoot'
    )
    Assert-RunnerTrue -Condition ($ambiguousInstall.ExitCode -ne 0) -Message "Installer must refuse an unclassified duplicate before mutation. $($ambiguousInstall.Output)"
    Assert-RunnerTrue -Condition ($ambiguousInstall.Output -match '4102') -Message "Unclassified listener diagnostics must report the exact PID. $($ambiguousInstall.Output)"
    Assert-RunnerEqual -Actual ((Get-Content -LiteralPath $listenerFixture -Raw).Trim()) -Expected 'unclassified-duplicate' -Message 'Unclassified duplicate refusal must not stop or rewrite the listener fixture.'
    Assert-RunnerEqual -Actual (Get-Content -LiteralPath $environment -Raw) -Expected $environmentBeforeAmbiguous -Message 'Unclassified duplicate refusal must precede environment mutation.'
    Assert-RunnerEqual -Actual (Get-Content -LiteralPath $labelState -Raw) -Expected $labelsBeforeAmbiguous -Message 'Unclassified duplicate refusal must precede label mutation.'
    '1' | Set-Content -LiteralPath $listenerFixture -NoNewline

    $uninstall = Invoke-RunnerScript -ScriptPath $uninstallScript -Arguments @(
        '-RunnerRoot', $runnerRoot,
        '-Repository', 'MrFr3di/SmartPipe-Core',
        '-GhPath', $runnerGh,
        '-ListenerFixturePath', $listenerFixture,
        '-AllowTestRoot'
    )
    Assert-RunnerEqual -Actual $uninstall.ExitCode -Expected 0 -Message "Uninstaller must succeed and restore one listener. $($uninstall.Output)"
    Assert-RunnerEqual -Actual ((Get-Content -LiteralPath $listenerFixture -Raw).Trim()) -Expected '1' -Message 'Uninstall must leave exactly one listener fixture.'
    $uninstalledLines = @(Get-Content -LiteralPath $environment)
    Assert-RunnerTrue -Condition (@($uninstalledLines | Where-Object { $_ -match '^(ACTIONS_RUNNER_HOOK_JOB_STARTED|ACTIONS_RUNNER_HOOK_JOB_COMPLETED|DOTNET_INSTALL_DIR)=' }).Count -eq 0) -Message 'Uninstaller must remove only owned environment entries.'
    Assert-RunnerTrue -Condition (@($uninstalledLines | Where-Object { $_ -eq 'UNRELATED_ENV=preserve' }).Count -eq 1) -Message 'Uninstaller must preserve unrelated environment entries.'
    Assert-RunnerTrue -Condition (-not (Test-Path -LiteralPath (Join-Path $runnerRoot 'hooks\smartpipe-job-start-cleanup.ps1'))) -Message 'Uninstaller must remove the owned hook copy.'
    Assert-RunnerTrue -Condition (-not (Test-Path -LiteralPath (Join-Path $runnerRoot 'hooks\smartpipe-post-job-cleanup.ps1'))) -Message 'Uninstaller must remove the legacy hook copy.'
    $labelsAfterUninstall = @((Get-Content -LiteralPath $labelState -Raw | ConvertFrom-Json))
    Assert-RunnerTrue -Condition ('smartpipe-cleanup-v1' -notin $labelsAfterUninstall) -Message 'Uninstaller must remove only the owned cleanup label.'
    Assert-RunnerTrue -Condition ('existing-label' -in $labelsAfterUninstall) -Message 'Uninstaller must preserve unrelated runner labels.'

    $fakeGh = Join-Path $fixture 'fake-gh.ps1'
    $fakeCount = Join-Path $fixture 'fake-gh.count'
    @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)
$joined = $Arguments -join ' '
if ($joined -like '*run list*') {
    @(@{ databaseId = 99 }) | ConvertTo-Json -Compress
    exit 0
}
if ($joined -like '*run view*') {
    "build error $([string]::new('x', 1400))"
    exit 0
}
$count = if (Test-Path -LiteralPath $env:SMARTPIPE_FAKE_GH_COUNT) { [int](Get-Content -LiteralPath $env:SMARTPIPE_FAKE_GH_COUNT) } else { 0 }
Set-Content -LiteralPath $env:SMARTPIPE_FAKE_GH_COUNT -Value ($count + 1)
@{ state = 'OPEN'; mergeStateStatus = 'DIRTY'; headRefOid = '0123456789abcdef0123456789abcdef01234567'; statusCheckRollup = @(@{ name = 'build'; status = 'COMPLETED'; conclusion = 'FAILURE' }) } | ConvertTo-Json -Compress
'@ | Set-Content -LiteralPath $fakeGh
    $env:SMARTPIPE_FAKE_GH_COUNT = $fakeCount
    $monitor = Invoke-RunnerScript -ScriptPath $monitorScript -Arguments @(
        '-PullRequest', '42',
        '-Repository', 'MrFr3di/SmartPipe-Core',
        '-GhPath', $fakeGh,
        '-PollSeconds', '1',
        '-MaxPolls', '2'
    )
    Remove-Item Env:\SMARTPIPE_FAKE_GH_COUNT -ErrorAction SilentlyContinue
    Assert-RunnerEqual -Actual $monitor.ExitCode -Expected 0 -Message "PR monitor fixture must succeed. $($monitor.Output)"
    Assert-RunnerEqual -Actual @($monitor.Output -split '\r?\n' | Where-Object { $_ -match '^PR #42 transition:' }).Count -Expected 1 -Message 'PR monitor must emit only state transitions.'
    $diagnosticLines = @($monitor.Output -split '\r?\n' | Where-Object { $_ -match '^PR diagnostic: first causal slice:' })
    Assert-RunnerEqual -Actual $diagnosticLines.Count -Expected 1 -Message 'PR monitor must emit one first-causal slice per failed head.'
    Assert-RunnerTrue -Condition ($diagnosticLines[0].Length -le 1070) -Message 'PR monitor causal output must remain bounded.'

    Write-Output 'Runner contract tests passed (cleanup containment, lifecycle idempotence, and transition-only monitoring).'
}
finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:\SMARTPIPE_FAKE_GH_COUNT -ErrorAction SilentlyContinue
    Remove-Item Env:\SMARTPIPE_QUEUED_FLAG -ErrorAction SilentlyContinue
    Remove-Item Env:\SMARTPIPE_IN_PROGRESS_FLAG -ErrorAction SilentlyContinue
    Remove-Item Env:\SMARTPIPE_OFFLINE_FLAG -ErrorAction SilentlyContinue
}
