[CmdletBinding()]
param(
    [string] $RunnerRoot = 'C:\SmartPipe-Runner',
    [string] $Repository = 'MrFr3di/SmartPipe-Core',
    [string] $RunnerName = '',
    [string] $GhPath = 'gh',
    [string] $ListenerFixturePath = '',
    [int] $ListenerTimeoutSeconds = 60,
    [switch] $SkipListenerReady,
    [switch] $AllowTestRoot
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
        Write-Output "Runner root is already absent: $runner"
        exit 0
    }
    Assert-SmartPipeNoReparsePath -Path $runner -Boundary $runner
    $resolvedRunnerName = Resolve-SmartPipeRunnerName -Root $runner -RequestedName $RunnerName
    Assert-SmartPipeActionsRunsIdle -Repository $Repository -GhPath $GhPath
    $remoteRunner = Assert-SmartPipeRemoteRunnerIdle -Repository $Repository -RunnerName $resolvedRunnerName -GhPath $GhPath

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
}
catch {
    $errorText = [string]$_.Exception.Message
    Write-Error -Message "$errorText Recovery: confirm the runner and repository are idle, then inspect or rerun eng\runner\uninstall-runner.ps1; unrelated runner labels are never removed."
    exit 1
}
