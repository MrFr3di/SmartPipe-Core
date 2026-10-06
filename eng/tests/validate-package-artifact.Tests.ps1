$ErrorActionPreference = 'Stop'
$validator = Join-Path $PSScriptRoot '../validate-package-artifact.ps1'
if (!(Test-Path $validator)) { throw 'Package artifact validator must exist before packages can be consumed.' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('artifact-fixture-' + [guid]::NewGuid())
function Write-Fixture {
    New-Item -ItemType Directory -Path "$root/artifacts/packages" -Force | Out-Null
    $packages = @()
    foreach ($id in @('Fixture.Core', 'Fixture.Leaf')) {
        $entry = @{id=$id; version='2.2.0'; publishOrder=($packages.Count + 1)}
        foreach ($extension in @('nupkg', 'snupkg')) {
            $relative = "artifacts/packages/$id.2.2.0.$extension"
            $zip = [IO.Compression.ZipFile]::Open("$root/$relative", 'Create')
            try {
                $stream = [IO.StreamWriter]::new($zip.CreateEntry("$id.nuspec").Open())
                try { $stream.Write("<package><metadata><id>$id</id><version>2.2.0</version><repository type='git' commit='1111111111111111111111111111111111111111'/></metadata></package>") } finally { $stream.Dispose() }
            } finally { $zip.Dispose() }
            $entry["${extension}Path"] = $relative
            $entry["${extension}Sha256"] = (Get-FileHash "$root/$relative").Hash.ToLowerInvariant()
        }
        $packages += $entry
    }
    @{schemaVersion=1; mode='current'; version='2.2.0'; packages=$packages} | ConvertTo-Json -Depth 10 | Set-Content "$root/artifacts/packages/manifest.json"
    @{schemaVersion=1; packages=@(@{id='Fixture.Core'; lifecycle='active'; publishOrder=1}, @{id='Fixture.Leaf'; lifecycle='active'; publishOrder=2}, @{id='Fixture.Planned'; lifecycle='planned'; publishOrder=3})} | ConvertTo-Json -Depth 10 | Set-Content "$root/graph.json"
}
function Invoke-Validator([string]$Mode = 'current', [string]$Commit = '') { & $validator -ArtifactRoot $root -ExpectedVersion '2.2.0' -GraphPath "$root/graph.json" -ExpectedMode $Mode -ExpectedCommit $Commit }
function Test-Rejected([string]$Name, [scriptblock]$Mutate, [string]$Expected, [string]$Mode = 'current', [string]$Commit = '') {
    Remove-Item $root -Recurse -Force
    Write-Fixture
    & $Mutate
    $failure = $null
    try { Invoke-Validator -Mode $Mode -Commit $Commit } catch { $failure = $_.Exception.Message }
    if (!$failure -or $failure -notmatch $Expected) { throw "$Name failed: expected '$Expected', got '$failure'." }
    Write-Output "PASS: $Name"
}
function Edit-Manifest([scriptblock]$Edit) {
    $manifest = Get-Content "$root/artifacts/packages/manifest.json" -Raw | ConvertFrom-Json
    & $Edit $manifest
    $manifest | ConvertTo-Json -Depth 10 | Set-Content "$root/artifacts/packages/manifest.json"
}
try {
    Write-Fixture
    Invoke-Validator
    Write-Output 'PASS: valid artifact and planned inventory exclusion'
    Invoke-Validator -Commit '1111111111111111111111111111111111111111'
    Test-Rejected 'wrong source commit with valid archive hashes' {} 'commit' -Commit '2222222222222222222222222222222222222222'
    Test-Rejected 'release artifact offered to current consumer' { Edit-Manifest { param($m) $m.mode='release' } } 'mode'
    Test-Rejected 'current artifact offered to release consumer' {} 'mode' -Mode release
    Test-Rejected 'unknown artifact mode' { Edit-Manifest { param($m) $m.mode='preview' } } 'mode'
    Test-Rejected 'planned package in release graph' { Edit-Manifest { param($m) $m.mode='release' } } 'planned' -Mode release
    Test-Rejected 'missing archive' { Remove-Item "$root/artifacts/packages/Fixture.Core.2.2.0.nupkg" } 'missing'
    Test-Rejected 'tampered archive' { Add-Content "$root/artifacts/packages/Fixture.Core.2.2.0.nupkg" 'tampered' } 'hash'
    Test-Rejected 'wrong version' { Edit-Manifest { param($m) $m.version='2.1.2' } } 'version'
    Test-Rejected 'path escape' { Edit-Manifest { param($m) $m.packages[0].nupkgPath='../escape.nupkg' } } 'path'
    Test-Rejected 'absolute path' { Edit-Manifest { param($m) $m.packages[0].nupkgPath='/tmp/escape.nupkg' } } 'path'
    Test-Rejected 'duplicate ID' { Edit-Manifest { param($m) $m.packages[1].id=$m.packages[0].id.ToLowerInvariant() } } 'duplicate'
    Test-Rejected 'missing graph package' { Edit-Manifest { param($m) $m.packages=@($m.packages[0]) } } 'inventory'
    Test-Rejected 'extra feed package' { Copy-Item "$root/artifacts/packages/Fixture.Core.2.2.0.nupkg" "$root/artifacts/packages/extra.nupkg" } 'inventory'
    Test-Rejected 'incorrect archive version with updated hash' {
        $path="$root/artifacts/packages/Fixture.Core.2.2.0.nupkg"
        $zip=[IO.Compression.ZipFile]::Open($path, 'Update')
        try { $zip.GetEntry('Fixture.Core.nuspec').Delete(); $writer=[IO.StreamWriter]::new($zip.CreateEntry('Fixture.Core.nuspec').Open()); try { $writer.Write('<package><metadata><id>Fixture.Core</id><version>9.9.9</version></metadata></package>') } finally { $writer.Dispose() } } finally { $zip.Dispose() }
        Edit-Manifest { param($m) $m.packages[0].nupkgSha256=(Get-FileHash $path).Hash.ToLowerInvariant() }
    } 'nuspec'
    if (!$IsWindows) {
        Test-Rejected 'symlink archive' {
            $path="$root/artifacts/packages/Fixture.Core.2.2.0.nupkg"
            Move-Item $path "$root/outside.nupkg"
            New-Item -ItemType SymbolicLink -Path $path -Target "$root/outside.nupkg" | Out-Null
        } 'link'
    }
    Remove-Item $root -Recurse -Force
    Write-Fixture
    Edit-Manifest { param($m) $m.mode='release' }
    $graph = Get-Content "$root/graph.json" -Raw | ConvertFrom-Json
    $graph.packages = @($graph.packages | Where-Object lifecycle -ne 'planned')
    $graph | ConvertTo-Json -Depth 10 | Set-Content "$root/graph.json"
    $releaseFailure = $null
    try { & $validator -ArtifactRoot $root -ExpectedVersion '2.2.0' -GraphPath "$root/graph.json" -ExpectedMode release -ExpectedCommit '1111111111111111111111111111111111111111' }
    catch { $releaseFailure = $_.Exception.Message }
    if ($releaseFailure) { throw "Valid release artifact must accept explicit release mode: $releaseFailure" }
    Write-Output 'PASS: explicit release artifact mode'
    $missingCommitFailure = $null
    try { & $validator -ArtifactRoot $root -ExpectedVersion '2.2.0' -GraphPath "$root/graph.json" -ExpectedMode release }
    catch { $missingCommitFailure = $_.Exception.Message }
    if (!$missingCommitFailure -or $missingCommitFailure -notmatch 'commit') { throw 'Release mode must reject missing expected source commit.' }
    Write-Output 'PASS: release mode requires expected source commit'
    Write-Output 'Package artifact fixtures passed.'
} finally { if (Test-Path $root) { Remove-Item $root -Recurse -Force } }
