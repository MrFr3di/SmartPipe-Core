param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script = Join-Path (Split-Path $PSScriptRoot -Parent) 'compare-nuget-package-payload.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ("smartpipe-nuget-payload-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null

function New-TestPackage([string]$Path, [hashtable]$Entries) {
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($pair in $Entries.GetEnumerator()) {
            $entry = $zip.CreateEntry([string]$pair.Key)
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write([string]$pair.Value) }
            finally { $writer.Dispose() }
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Assert-Comparison([string]$Expected, [string]$Published, [bool]$ShouldPass) {
    $passed = $false
    try {
        & $script -ExpectedPackage $Expected -PublishedPackage $Published | Out-Null
        $passed = $true
    }
    catch {
        if ($ShouldPass) { throw }
    }

    if ($passed -ne $ShouldPass) {
        throw "Payload comparison result differed from expectation. Expected pass=$ShouldPass."
    }
}

try {
    $expected = Join-Path $root 'expected.nupkg'
    New-TestPackage $expected @{
        'SmartPipe.Core.nuspec' = '<package><metadata><id>SmartPipe.Core</id><version>2.2.0</version></metadata></package>'
        'lib/net10.0/SmartPipe.Core.dll' = 'validated payload'
    }

    $signed = Join-Path $root 'published-signed.nupkg'
    New-TestPackage $signed @{
        'SmartPipe.Core.nuspec' = '<package><metadata><id>SmartPipe.Core</id><version>2.2.0</version></metadata></package>'
        'lib/net10.0/SmartPipe.Core.dll' = 'validated payload'
        '.signature.p7s' = 'repository signature'
    }
    Assert-Comparison $expected $signed $true

    $mutated = Join-Path $root 'published-mutated.nupkg'
    New-TestPackage $mutated @{
        'SmartPipe.Core.nuspec' = '<package><metadata><id>SmartPipe.Core</id><version>2.2.0</version></metadata></package>'
        'lib/net10.0/SmartPipe.Core.dll' = 'different payload'
        '.signature.p7s' = 'repository signature'
    }
    Assert-Comparison $expected $mutated $false

    $extra = Join-Path $root 'published-extra.nupkg'
    New-TestPackage $extra @{
        'SmartPipe.Core.nuspec' = '<package><metadata><id>SmartPipe.Core</id><version>2.2.0</version></metadata></package>'
        'lib/net10.0/SmartPipe.Core.dll' = 'validated payload'
        'unexpected.txt' = 'extra'
        '.signature.p7s' = 'repository signature'
    }
    Assert-Comparison $expected $extra $false

    Write-Output 'NuGet published payload comparison fixtures passed.'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
