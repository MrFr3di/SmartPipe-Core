[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [int] $PullRequest,
    [string] $Repository = 'MrFr3di/SmartPipe-Core',
    [string] $GhPath = 'gh',
    [int] $PollSeconds = 60,
    [int] $MaxPolls = 0,
    [switch] $Once
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runner-safety.ps1')

function Get-SmartPipeCheckSummary {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Checks
    )

    $parts = [Collections.Generic.List[string]]::new()
    foreach ($check in @($Checks)) {
        if ($null -eq $check) {
            continue
        }
        $properties = @($check.PSObject.Properties.Name)
        $name = if ('name' -in $properties -and $null -ne $check.name) { [string]$check.name } elseif ('context' -in $properties -and $null -ne $check.context) { [string]$check.context } else { 'check' }
        $state = if ('conclusion' -in $properties -and [string]$check.conclusion) { [string]$check.conclusion } elseif ('status' -in $properties -and $null -ne $check.status) { [string]$check.status } else { 'pending' }
        $parts.Add("$name=$state")
    }

    $summary = $parts -join ','
    if ($summary.Length -gt 512) {
        return $summary.Substring(0, 512) + '...'
    }

    return $summary
}

function Write-SmartPipeFirstFailure {
    param(
        [Parameter(Mandatory = $true)] [string] $Head,
        [Parameter(Mandatory = $true)] [string] $TemporaryRoot
    )

    $global:LASTEXITCODE = 0
    $runJson = & $GhPath run list --repo $Repository --commit $Head --status failure --limit 1 --json databaseId 2>&1
    if ($global:LASTEXITCODE -ne 0) {
        Write-Output 'PR diagnostic: unable to list the failed workflow run.'
        return
    }

    $runs = @(($runJson -join [Environment]::NewLine) | ConvertFrom-Json)
    if ($runs.Count -eq 0) {
        Write-Output 'PR diagnostic: no failed workflow run is available yet.'
        return
    }

    $runId = [string]$runs[0].databaseId
    if ($runId -notmatch '^[0-9]+$') {
        Write-Output 'PR diagnostic: failed workflow run id is invalid.'
        return
    }

    $global:LASTEXITCODE = 0
    $failedLog = @(& $GhPath run view $runId --repo $Repository --log-failed 2>&1 | ForEach-Object { [string]$_ })
    $logExitCode = $global:LASTEXITCODE
    $logPath = Join-Path $TemporaryRoot "failed-$Head-$runId.log"
    [IO.File]::WriteAllLines($logPath, $failedLog)
    if ($logExitCode -ne 0) {
        Write-Output 'PR diagnostic: failed-step log retrieval was incomplete.'
        return
    }

    $index = -1
    for ($line = 0; $line -lt $failedLog.Count; $line++) {
        if ($failedLog[$line] -match '(?i)(error|exception|failed|NU[0-9]{4}|SP[A-Z]+[0-9]{3})') {
            $index = $line
            break
        }
    }
    if ($index -lt 0) { $index = 0 }
    $last = [Math]::Min($failedLog.Count - 1, $index + 4)
    $slice = if ($failedLog.Count -eq 0) { 'no failed-step output' } else { ($failedLog[$index..$last] -join ' | ').Trim() }
    if ($slice.Length -gt 1024) { $slice = $slice.Substring(0, 1024) + '...' }
    Write-Output "PR diagnostic: first causal slice: $slice"
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "smartpipe-pr-monitor-$PID-$([Guid]::NewGuid().ToString('N'))"
try {
    Assert-SmartPipeRepository -Repository $Repository
    if ($PullRequest -lt 1) {
        throw 'PullRequest must be positive.'
    }
    if ($PollSeconds -lt 1) {
        throw 'PollSeconds must be positive.'
    }
    if ($MaxPolls -lt 0) {
        throw 'MaxPolls cannot be negative.'
    }

    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    $previous = $null
    $diagnosedHead = ''
    $poll = 0
    while ($true) {
        $LASTEXITCODE = 0
        $json = & $GhPath pr view $PullRequest --repo $Repository --json state,mergeStateStatus,headRefOid,statusCheckRollup 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "gh pr view failed: $($json -join ' ')"
        }

        $view = ($json -join [Environment]::NewLine) | ConvertFrom-Json
        $state = [string]$view.state
        $mergeState = [string]$view.mergeStateStatus
        $head = [string]$view.headRefOid
        $checks = Get-SmartPipeCheckSummary -Checks $view.statusCheckRollup
        $signature = "$state|$mergeState|$head|$checks"
        if ($signature -ne $previous) {
            Write-Output "PR #$PullRequest transition: state=$state merge=$mergeState head=$head checks=$checks"
            $previous = $signature
        }
        if ($head -ne $diagnosedHead -and $checks -match '(?i)=(FAILURE|CANCELLED|TIMED_OUT|ACTION_REQUIRED|STARTUP_FAILURE)') {
            Write-SmartPipeFirstFailure -Head $head -TemporaryRoot $temporaryRoot
            $diagnosedHead = $head
        }

        $poll++
        if ($state -in @('MERGED', 'CLOSED') -or $Once -or ($MaxPolls -gt 0 -and $poll -ge $MaxPolls)) {
            break
        }

        Start-Sleep -Seconds $PollSeconds
    }
}
catch {
    $errorText = [string]$_.Exception.Message
    Write-Error -Message $errorText
    exit 1
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
