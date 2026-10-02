[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExpectedPackage,
    [Parameter(Mandatory)][string]$PublishedPackage
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-PackagePayload([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (!(Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "NuGet package is missing: $Path"
    }

    $entries = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $signatureCount = 0
    $zip = [IO.Compression.ZipFile]::OpenRead($full)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName -ceq '.signature.p7s') {
                $signatureCount++
                if ($signatureCount -gt 1) {
                    throw "NuGet package contains multiple signature entries."
                }
                continue
            }
            if ($entry.FullName.EndsWith('/', [StringComparison]::Ordinal)) {
                continue
            }

            if ([string]::IsNullOrWhiteSpace($entry.FullName) -or $entries.ContainsKey($entry.FullName)) {
                throw "NuGet package contains an invalid or duplicate entry: $($entry.FullName)"
            }

            $stream = $entry.Open()
            try {
                $sha = [Security.Cryptography.SHA256]::Create()
                try {
                    $hash = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant()
                }
                finally {
                    $sha.Dispose()
                }
            }
            finally {
                $stream.Dispose()
            }

            $entries.Add($entry.FullName, [pscustomobject]@{
                Length = [long]$entry.Length
                Sha256 = $hash
            })
        }
    }
    finally {
        $zip.Dispose()
    }

    return ,$entries
}

$expected = Get-PackagePayload $ExpectedPackage
$published = Get-PackagePayload $PublishedPackage

if ($expected.Count -ne $published.Count) {
    throw "Published NuGet package payload entry count differs from the validated producer package."
}

foreach ($name in $expected.Keys) {
    $actual = $null
    if (!$published.TryGetValue($name, [ref]$actual)) {
        throw "Published NuGet package is missing payload entry: $name"
    }

    $wanted = $expected[$name]
    if ($wanted.Length -ne $actual.Length -or $wanted.Sha256 -cne $actual.Sha256) {
        throw "Published NuGet package payload differs for entry: $name"
    }
}

Write-Output "Published NuGet package payload matches the validated producer package (repository signature ignored)."
