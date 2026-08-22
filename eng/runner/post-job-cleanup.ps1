[CmdletBinding()]
param(
    [string] $RunnerRoot = 'C:\SmartPipe-Runner',
    [string] $WorkspaceRoot = $env:GITHUB_WORKSPACE,
    [string] $TempRoot = $env:RUNNER_TEMP,
    [string] $Repository = $env:GITHUB_REPOSITORY,
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
        throw "Dedicated runner root is missing: $runner"
    }

    Assert-SmartPipeNoReparsePath -Path $runner -Boundary $runner

    if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
        throw 'GITHUB_WORKSPACE is required.'
    }

    $workspace = Get-SmartPipeFullPath -Path $WorkspaceRoot
    if (-not (Test-SmartPipeContainedPath -Path $workspace -Boundary $runner)) {
        throw "Workspace is outside the dedicated runner root: $workspace"
    }

    if (Test-Path -LiteralPath $workspace -PathType Container) {
        Assert-SmartPipeWorkspaceRepository -Workspace $workspace
        [void](Remove-SmartPipeCleanupTarget -Path $workspace -Boundary $runner -AllowBoundary)
    }
    else {
        Assert-SmartPipeNoReparsePath -Path $workspace -Boundary $runner
    }

    if (-not [string]::IsNullOrWhiteSpace($TempRoot)) {
        $temp = Get-SmartPipeFullPath -Path $TempRoot
        if (-not (Test-SmartPipeContainedPath -Path $temp -Boundary $runner)) {
            throw "Runner temp is outside the dedicated runner root: $temp"
        }

        if (Test-Path -LiteralPath $temp -PathType Container) {
            Assert-SmartPipeNoReparsePath -Path $temp -Boundary $runner
            foreach ($name in @('SmartPipe.Core', 'SmartPipe-Core', 'CodeQL', 'codeql')) {
                $target = Join-Path $temp $name
                [void](Remove-SmartPipeCleanupTarget -Path $target -Boundary $temp)
            }
        }
    }

    Write-Output 'SmartPipe post-job cleanup completed.'
}
catch {
    $errorText = [string]$_.Exception.Message
    Write-Error -Message $errorText
    exit 1
}
