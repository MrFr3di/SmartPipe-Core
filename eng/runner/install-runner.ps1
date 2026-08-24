[CmdletBinding()]
param(
    [string] $RunnerRoot = 'C:\SmartPipe-Runner',
    [string] $Repository = 'MrFr3di/SmartPipe-Core',
    [string] $RunnerName = '',
    [string] $GhPath = 'gh',
    [string] $ListenerFixturePath = '',
    [int] $ListenerTimeoutSeconds = 60,
    [switch] $SkipRemoteCheck,
    [switch] $SkipListenerReady,
    [switch] $AllowTestRoot,
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runner-safety.ps1')

try {
    Assert-SmartPipeRepository -Repository $Repository
    $runner = Get-SmartPipeFullPath -Path $RunnerRoot
    if (-not $AllowTestRoot -and -not (Test-SmartPipeSamePath -Left $runner -Right $script:SmartPipeRunnerDefaultRoot)) {
        throw "The production runner root must be $script:SmartPipeRunnerDefaultRoot."
    }

    if (-not (Test-Path -LiteralPath $runner -PathType Container)) {
        throw "Dedicated runner root is missing: $runner"
    }
    Assert-SmartPipeNoReparsePath -Path $runner -Boundary $runner
    $resolvedRunnerName = Resolve-SmartPipeRunnerName -Root $runner -RequestedName $RunnerName
    if ($SkipRemoteCheck) {
        throw 'Remote idle checks cannot be skipped because runner label registration is required. Recovery: no runner files or labels were changed.'
    }

    Assert-SmartPipeActionsRunsIdle -Repository $Repository -GhPath $GhPath
    $remoteRunner = Assert-SmartPipeRemoteRunnerIdle -Repository $Repository -RunnerName $resolvedRunnerName -GhPath $GhPath

    if ($Uninstall) {
        $environmentPath = Join-Path $runner '.env'
        Remove-SmartPipeEnvironment -EnvironmentPath $environmentPath
        $hookDirectory = Join-Path $runner 'hooks'
        foreach ($name in @('smartpipe-job-start-cleanup.ps1', 'smartpipe-post-job-cleanup.ps1', 'runner-safety.ps1')) {
            $path = Join-Path $hookDirectory $name
            if (Test-Path -LiteralPath $path) {
                Assert-SmartPipeNoReparsePath -Path $path -Boundary $runner
                Remove-Item -LiteralPath $path -Force -ErrorAction Stop
            }
        }
        Remove-SmartPipeRunnerLabel -Repository $Repository -Runner $remoteRunner -GhPath $GhPath
        if (-not $SkipListenerReady) {
            Restart-SmartPipeRunner -Root $runner -Repository $Repository -RunnerName $resolvedRunnerName -GhPath $GhPath -FixturePath $ListenerFixturePath -TimeoutSeconds $ListenerTimeoutSeconds
        }
        Write-Output "Removed SmartPipe-owned hook, environment entry, and label from $runner and restored one listener."
        exit 0
    }

    $hookSource = Get-SmartPipeFullPath -Path (Join-Path $PSScriptRoot 'job-start-cleanup.ps1')
    $safetySource = Get-SmartPipeFullPath -Path (Join-Path $PSScriptRoot 'runner-safety.ps1')
    if (-not (Test-Path -LiteralPath $hookSource -PathType Leaf) -or
        -not (Test-Path -LiteralPath $safetySource -PathType Leaf)) {
        throw 'Runner hook sources are missing.'
    }

    $hookDirectory = Join-Path $runner 'hooks'
    if (-not (Test-Path -LiteralPath $hookDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $hookDirectory -Force | Out-Null
    }
    Assert-SmartPipeNoReparsePath -Path $hookDirectory -Boundary $runner

    $legacyHookDestination = Join-Path $hookDirectory 'smartpipe-post-job-cleanup.ps1'
    if (Test-Path -LiteralPath $legacyHookDestination) {
        Assert-SmartPipeNoReparsePath -Path $legacyHookDestination -Boundary $runner
        Remove-Item -LiteralPath $legacyHookDestination -Force -ErrorAction Stop
    }

    $hookDestination = Join-Path $hookDirectory 'smartpipe-job-start-cleanup.ps1'
    $safetyDestination = Join-Path $hookDirectory 'runner-safety.ps1'
    Copy-Item -LiteralPath $hookSource -Destination $hookDestination -Force
    Copy-Item -LiteralPath $safetySource -Destination $safetyDestination -Force

    $environmentPath = Join-Path $runner '.env'
    $dotnetInstallDirectory = Join-Path $runner '_work\_tool\dotnet'
    Write-SmartPipeEnvironment -EnvironmentPath $environmentPath -HookPath $hookDestination -DotNetInstallDirectory $dotnetInstallDirectory
    Add-SmartPipeRunnerLabel -Repository $Repository -Runner $remoteRunner -GhPath $GhPath

    if (-not $SkipListenerReady) {
        Restart-SmartPipeRunner -Root $runner -Repository $Repository -RunnerName $resolvedRunnerName -GhPath $GhPath -FixturePath $ListenerFixturePath -TimeoutSeconds $ListenerTimeoutSeconds
    }
    Write-Output "Installed SmartPipe hook and label under $runner with one online idle listener."
}
catch {
    $errorText = [string]$_.Exception.Message
    Write-Error -Message "$errorText Recovery: confirm the runner and repository are idle, then inspect or rerun eng\runner\uninstall-runner.ps1; existing runner labels are never intentionally removed."
    exit 1
}
