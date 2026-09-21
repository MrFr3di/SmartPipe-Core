param(
    [ValidateSet('baseline', 'candidate', 'all')]
    [string]$Target = 'all',

    [string]$CandidateSha = '61ceef6bf69aef0a4f79b25384352d238979200f',

    [ValidateSet('Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-ExitCode {
    param([string]$Step)
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE."
    }
}

function Invoke-DotNet {
    param(
        [string[]]$Arguments,
        [string]$Step
    )
    & dotnet @Arguments
    Assert-ExitCode $Step
}

function Write-JsonFile {
    param(
        [string]$Path,
        [object]$Value
    )
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $Value | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $Path -Encoding utf8
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$validator = Join-Path $repoRoot 'eng/perf/validate-perf-lab.ps1'
$artifactsRoot = Join-Path $repoRoot 'artifacts/perf/targets'
$repositoryChecks = Join-Path $repoRoot 'eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj'
$baselineManifest = Join-Path $repoRoot 'eng/baselines/2.1.2/manifest.json'
$targetsManifest = Get-Content -LiteralPath (Join-Path $repoRoot 'perf/manifests/targets.json') -Raw | ConvertFrom-Json -Depth 32

& $validator -Mode smoke -CandidateSha $CandidateSha

if ($CandidateSha -cnotmatch '^[0-9a-f]{40}$') {
    throw 'CandidateSha must be exactly 40 lowercase hexadecimal characters.'
}
if ($CandidateSha -cne [string]$targetsManifest.candidate.gitSha) {
    throw "CandidateSha '$CandidateSha' does not match pinned candidate '$($targetsManifest.candidate.gitSha)'."
}

$harnessSha = (& git -C $repoRoot rev-parse HEAD).Trim()
Assert-ExitCode 'Resolve harness SHA'

function Ensure-RepositoryChecksBuilt {
    Invoke-DotNet -Arguments @(
        'restore', $repositoryChecks,
        '--locked-mode',
        '-p:DisableImplicitLibraryPacksFolder=true'
    ) -Step 'Restore RepositoryChecks'

    Invoke-DotNet -Arguments @(
        'build', $repositoryChecks,
        '--configuration', $Configuration,
        '--no-restore',
        '-warnaserror'
    ) -Step 'Build RepositoryChecks'
}

function Prepare-Baseline {
    Ensure-RepositoryChecksBuilt

    $targetRoot = Join-Path $artifactsRoot '2.1.2'
    $packagesDir = Join-Path $targetRoot 'packages'
    Remove-Item -LiteralPath $targetRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $packagesDir -Force | Out-Null

    Invoke-DotNet -Arguments @(
        'run', '--project', $repositoryChecks,
        '--configuration', $Configuration,
        '--no-build', '--',
        'provision-baseline',
        '--repo-root', $repoRoot,
        '--manifest', $baselineManifest,
        '--packages-dir', $packagesDir
    ) -Step 'Provision 2.1.2 baseline'

    Invoke-DotNet -Arguments @(
        'run', '--project', $repositoryChecks,
        '--configuration', $Configuration,
        '--no-build', '--',
        'verify-baseline',
        '--repo-root', $repoRoot,
        '--manifest', $baselineManifest,
        '--packages-dir', $packagesDir,
        '--offline',
        '--mode', 'integrity'
    ) -Step 'Verify 2.1.2 baseline'

    $hashes = @(
        Get-ChildItem -LiteralPath $packagesDir -File -Recurse |
            Sort-Object FullName |
            ForEach-Object {
                [ordered]@{
                    file = [IO.Path]::GetRelativePath($targetRoot, $_.FullName).Replace('\', '/')
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                    length = $_.Length
                }
            }
    )

    Write-JsonFile -Path (Join-Path $targetRoot 'target.json') -Value ([ordered]@{
        schemaVersion = 1
        targetId = [string]$targetsManifest.baseline.id
        version = [string]$targetsManifest.baseline.version
        productSha = [string]$targetsManifest.baseline.gitSha
        harnessSha = $harnessSha
        source = 'published-nuget-baseline'
        packageCount = $hashes.Count
        packages = $hashes
    })

    Write-Output "PERF_TARGET_READY target=baseline path=$targetRoot"
}

function Prepare-Candidate {
    & git -C $repoRoot cat-file -e "$CandidateSha^{commit}"
    Assert-ExitCode 'Resolve candidate commit'

    $shortSha = $CandidateSha.Substring(0, 12)
    $targetRoot = Join-Path $artifactsRoot "candidate-$shortSha"
    $packagesDir = Join-Path $targetRoot 'packages'
    $worktreeRoot = Join-Path $repoRoot "artifacts/perf/worktrees/$shortSha"

    if (Test-Path -LiteralPath $worktreeRoot) {
        & git -C $repoRoot worktree remove --force $worktreeRoot 2>$null
        Remove-Item -LiteralPath $worktreeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Remove-Item -LiteralPath $targetRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $packagesDir -Force | Out-Null
    New-Item -ItemType Directory -Path (Split-Path -Parent $worktreeRoot) -Force | Out-Null

    & git -C $repoRoot worktree add --detach $worktreeRoot $CandidateSha
    Assert-ExitCode 'Create candidate worktree'

    try {
        $actualSha = (& git -C $worktreeRoot rev-parse HEAD).Trim()
        Assert-ExitCode 'Resolve candidate worktree SHA'
        if ($actualSha -cne $CandidateSha) {
            throw "Candidate worktree SHA mismatch. Expected $CandidateSha, got $actualSha."
        }

        $candidateSolution = Join-Path $worktreeRoot 'SmartPipe.Core.slnx'
        $candidateChecks = Join-Path $worktreeRoot 'eng/SmartPipe.RepositoryChecks/SmartPipe.RepositoryChecks.csproj'
        $workPackagesDir = Join-Path $worktreeRoot 'artifacts/perf-packages'
        $candidateManifest = Join-Path $workPackagesDir 'manifest.json'
        $workMetadataReport = Join-Path $workPackagesDir 'metadata-report.json'
        $metadataReport = Join-Path $targetRoot 'metadata-report.json'

        Invoke-DotNet -Arguments @(
            'restore', $candidateSolution,
            '--locked-mode',
            '-p:DisableImplicitLibraryPacksFolder=true'
        ) -Step 'Restore candidate'

        Invoke-DotNet -Arguments @(
            'build', $candidateSolution,
            '--configuration', $Configuration,
            '--no-restore',
            '-warnaserror'
        ) -Step 'Build candidate'

        $productVersion = (& dotnet msbuild (Join-Path $worktreeRoot 'src/SmartPipe.Core/SmartPipe.Core.csproj') '-getProperty:Version' '-nologo').Trim()
        Assert-ExitCode 'Read candidate product version'
        if ([string]::IsNullOrWhiteSpace($productVersion)) {
            throw 'Candidate product version is empty.'
        }
        $packageVersion = "$productVersion-perflab.$shortSha"

        Invoke-DotNet -Arguments @(
            'run', '--project', $candidateChecks,
            '--configuration', $Configuration,
            '--no-build', '--',
            'pack-packages',
            '--repo-root', $worktreeRoot,
            '--mode', 'current',
            '--configuration', $Configuration,
            '--package-version', $packageVersion,
            '--output', $workPackagesDir,
            '--manifest', $candidateManifest
        ) -Step 'Pack candidate packages'

        Invoke-DotNet -Arguments @(
            'run', '--project', $candidateChecks,
            '--configuration', $Configuration,
            '--no-build', '--',
            'verify-package-graph',
            '--repo-root', $worktreeRoot,
            '--mode', 'current',
            '--packages', $workPackagesDir
        ) -Step 'Verify candidate package graph'

        Invoke-DotNet -Arguments @(
            'run', '--project', $candidateChecks,
            '--configuration', $Configuration,
            '--no-build', '--',
            'verify-package-metadata',
            '--repo-root', $worktreeRoot,
            '--package-directory', $workPackagesDir,
            '--mode', 'current',
            '--report', $workMetadataReport
        ) -Step 'Verify candidate package metadata'

        Get-ChildItem -LiteralPath $workPackagesDir -File |
            Where-Object { $_.Extension -in @('.nupkg', '.snupkg') } |
            Copy-Item -Destination $packagesDir
        Copy-Item -LiteralPath $candidateManifest -Destination (Join-Path $packagesDir 'manifest.json')
        Copy-Item -LiteralPath $workMetadataReport -Destination $metadataReport

        $hashes = @(
            Get-ChildItem -LiteralPath $packagesDir -File -Recurse |
                Where-Object { $_.Extension -in @('.nupkg', '.snupkg') } |
                Sort-Object FullName |
                ForEach-Object {
                    [ordered]@{
                        file = [IO.Path]::GetRelativePath($targetRoot, $_.FullName).Replace('\', '/')
                        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                        length = $_.Length
                    }
                }
        )
        if ($hashes.Count -eq 0) {
            throw 'Candidate package set is empty.'
        }

        Write-JsonFile -Path (Join-Path $targetRoot 'target.json') -Value ([ordered]@{
            schemaVersion = 1
            targetId = [string]$targetsManifest.candidate.id
            productVersion = $productVersion
            packageVersion = $packageVersion
            productSha = $CandidateSha
            harnessSha = $harnessSha
            source = 'exact-git-sha-local-pack'
            includedEpics = @($targetsManifest.candidate.includedEpics)
            excludedEpics = @($targetsManifest.candidate.excludedEpics)
            packageCount = $hashes.Count
            packages = $hashes
        })

        Write-Output "PERF_TARGET_READY target=candidate sha=$CandidateSha path=$targetRoot"
    }
    catch {
        $diagnosticsDir = Join-Path $targetRoot 'diagnostics'
        New-Item -ItemType Directory -Path $diagnosticsDir -Force | Out-Null

        $workPackagesDir = Join-Path $worktreeRoot 'artifacts/perf-packages'
        if (Test-Path -LiteralPath $workPackagesDir) {
            Copy-Item -LiteralPath $workPackagesDir -Destination (Join-Path $diagnosticsDir 'perf-packages') -Recurse -Force
        }

        $failure = [ordered]@{
            schemaVersion = 1
            candidateSha = $CandidateSha
            harnessSha = $harnessSha
            exceptionType = $_.Exception.GetType().FullName
            message = $_.Exception.Message
            timestampUtc = [DateTimeOffset]::UtcNow.ToString('O')
        }
        Write-JsonFile -Path (Join-Path $diagnosticsDir 'failure.json') -Value $failure

        throw
    }
    finally {
        & git -C $repoRoot worktree remove --force $worktreeRoot 2>$null
        & git -C $repoRoot worktree prune 2>$null
    }
}

switch ($Target) {
    'baseline' { Prepare-Baseline }
    'candidate' { Prepare-Candidate }
    'all' {
        Prepare-Baseline
        Prepare-Candidate
    }
}
