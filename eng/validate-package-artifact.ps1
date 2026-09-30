[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][string]$GraphPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath($ArtifactRoot)

# Manifest paths are repository-relative, even when the artifact is downloaded elsewhere.
# Reject all links (including parent-directory links) rather than trusting lexical containment.
function Resolve-ArtifactFile([string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or $RelativePath.Contains('\') -or
        $RelativePath.Contains(':') -or ($RelativePath.Split('/') | Where-Object { $_ -in @('', '.', '..') })) {
        throw "Invalid artifact path: $RelativePath"
    }
    $full = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $RelativePath))
    $prefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$full.StartsWith($prefix, [StringComparison]::Ordinal)) { throw "Artifact path escapes root: $RelativePath" }
    if (!(Test-Path -LiteralPath $full -PathType Leaf)) { throw "Artifact file missing: $RelativePath" }
    $item = Get-Item -LiteralPath $full -Force
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Artifact link is forbidden: $RelativePath" }
        $item = if ($item -is [IO.FileInfo]) { $item.Directory } else { $item.Parent }
    }
    return $full
}

function Assert-Archive([string]$Path, [string]$Id, [string]$Version) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $nuspecs = @($zip.Entries | Where-Object { $_.FullName -match '^[^/\\]+\.nuspec$' })
        if ($nuspecs.Count -ne 1) { throw "Archive must contain exactly one root nuspec: $Id" }
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $stream = $nuspecs[0].Open()
        try {
            $reader = [Xml.XmlReader]::Create($stream, $settings)
            try { $xml = [Xml.XmlDocument]::new(); $xml.XmlResolver = $null; $xml.Load($reader) } finally { $reader.Dispose() }
        } finally { $stream.Dispose() }
        $ids = $xml.SelectNodes('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="id"]')
        $versions = $xml.SelectNodes('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="version"]')
        if ($ids.Count -ne 1 -or $versions.Count -ne 1 -or $ids[0].InnerText -cne $Id -or $versions[0].InnerText -cne $Version) {
            throw "Archive nuspec identity/version mismatch: $Id"
        }
    } finally { $zip.Dispose() }
}

$manifest = Get-Content -LiteralPath (Resolve-ArtifactFile 'artifacts/packages/manifest.json') -Raw | ConvertFrom-Json
$graph = Get-Content -LiteralPath $GraphPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.mode -cne 'current') { throw 'Unsupported package manifest schema/mode.' }
if ($ExpectedVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$' -or $manifest.version -cne $ExpectedVersion) { throw 'Artifact version does not match the expected version.' }
$expected = @{}
foreach ($node in $graph.packages) {
    if ($node.lifecycle -eq 'planned') { continue }
    if ($expected.ContainsKey($node.id)) { throw "Duplicate graph ID: $($node.id)" }
    $expected[$node.id] = $node
}
$ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if (@($manifest.packages).Count -ne $expected.Count -or $expected.Count -eq 0) { throw 'Package inventory does not match the graph.' }
foreach ($package in $manifest.packages) {
    if (!$ids.Add($package.id)) { throw "Duplicate package ID: $($package.id)" }
    if (!$expected.ContainsKey($package.id) -or $package.id -cne $expected[$package.id].id -or $package.publishOrder -ne $expected[$package.id].publishOrder) { throw "Package inventory/order does not match graph: $($package.id)" }
    if ($package.version -cne $ExpectedVersion) { throw "Package version mismatch: $($package.id)" }
    foreach ($extension in @('nupkg', 'snupkg')) {
        $relative = $package."${extension}Path"
        $path = Resolve-ArtifactFile $relative
        if ($relative -cne "artifacts/packages/$($package.id).$ExpectedVersion.$extension" -or !$paths.Add($relative)) { throw "Unexpected or duplicate artifact path: $relative" }
        $expectedHash = $package."${extension}Sha256"
        if ($expectedHash -cnotmatch '^[a-f0-9]{64}$' -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedHash) { throw "Artifact hash mismatch: $relative" }
        Assert-Archive $path $package.id $ExpectedVersion
        Write-Output "$expectedHash  $relative"
    }
}
$files = @(Get-ChildItem -LiteralPath (Join-Path $root 'artifacts/packages') -File -Recurse -Force | Where-Object { $_.Extension -in @('.nupkg', '.snupkg') })
if ($files.Count -ne $paths.Count) { throw 'Feed archive inventory differs from manifest.' }
Write-Output "Validated $($ids.Count) immutable packages at version $ExpectedVersion."
