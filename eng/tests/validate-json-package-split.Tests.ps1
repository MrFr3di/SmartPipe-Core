param()

$ErrorActionPreference = 'Stop'
$validator = Join-Path $PSScriptRoot '..\validate-json-package-split.ps1'
$root = Join-Path ([System.IO.Path]::GetTempPath()) "smartpipe-package-script-tests-$([Guid]::NewGuid().ToString('N'))"

function New-TestPackage([string] $directory, [string] $id, [string] $version, [hashtable] $dependencies) {
    $staging = Join-Path $directory "staging-$id"
    New-Item -ItemType Directory -Path $staging | Out-Null
    $dependencyXml = ($dependencies.GetEnumerator() | Sort-Object Key | ForEach-Object {
        "<dependency id=`"$($_.Key)`" version=`"$($_.Value)`" />"
    }) -join "`n"
    Set-Content -LiteralPath (Join-Path $staging "$id.nuspec") -Encoding utf8NoBOM -Value @"
<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata><id>$id</id><version>$version</version><authors>tests</authors><description>tests</description>
    <dependencies><group targetFramework="net10.0">$dependencyXml</group></dependencies>
  </metadata>
</package>
"@
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath (Join-Path $directory "$id.$version.zip")
    Move-Item -LiteralPath (Join-Path $directory "$id.$version.zip") -Destination (Join-Path $directory "$id.$version.nupkg")
    Set-Content -LiteralPath (Join-Path $directory "$id.$version.snupkg") -Value 'test'
}

function Get-DefaultExtensionsDependencies {
    return @{
        'SmartPipe.Core' = '2.1.2-rc.1'
        'SmartPipe.Extensions.Json' = '2.1.2-rc.1'
        'CsvHelper' = '33.1.0'
        'Dapper' = '2.1.79'
        'Mapster' = '10.0.10'
        'Microsoft.EntityFrameworkCore' = '10.0.8'
        'Microsoft.Extensions.Diagnostics.HealthChecks' = '10.0.8'
        'Microsoft.Extensions.Hosting.Abstractions' = '10.0.8'
        'Microsoft.Extensions.Http' = '10.0.8'
        'Microsoft.Extensions.Logging.Abstractions' = '10.0.8'
        'Microsoft.Extensions.Resilience' = '10.6.0'
    }
}

function Copy-Dependencies([hashtable] $dependencies) {
    $copy = @{}
    foreach ($entry in $dependencies.GetEnumerator()) { $copy[$entry.Key] = $entry.Value }
    return $copy
}

function Invoke-Case {
    param(
        [string] $Name,
        [hashtable] $JsonDependencies,
        [hashtable] $ExtensionsDependencies = (Get-DefaultExtensionsDependencies),
        [bool] $ShouldPass,
        [string] $ExpectedError = '',
        [bool] $KeepTemporaryFiles = $false,
        [bool] $ExpectedValidationRoot = $false
    )

    $caseRoot = Join-Path $root $Name
    $validationRoot = Join-Path $caseRoot 'validation-root'
    New-Item -ItemType Directory -Path $caseRoot | Out-Null
    $version = '2.1.2-rc.1'
    New-TestPackage $caseRoot 'SmartPipe.Core' $version @{}
    New-TestPackage $caseRoot 'SmartPipe.Extensions.Json' $version $JsonDependencies
    New-TestPackage $caseRoot 'SmartPipe.Extensions' $version $ExtensionsDependencies

    try {
        & $validator -PackageDirectory $caseRoot -Version $version -ManifestOnly -ValidationRoot $validationRoot -KeepTemporaryFiles:$KeepTemporaryFiles
        if (-not $ShouldPass) { throw "Case '$Name' unexpectedly passed." }
    }
    catch {
        if ($ShouldPass) { throw }
        if ($_.Exception.Message -notlike "*$ExpectedError*") {
            throw "Case '$Name' failed with unexpected error: $($_.Exception.Message)"
        }
    }

    if ((Test-Path -LiteralPath $validationRoot) -ne $ExpectedValidationRoot) {
        throw "Case '$Name' validation root existence did not match expected '$ExpectedValidationRoot'."
    }
}

try {
    Invoke-Case -Name 'cleanup-success' -JsonDependencies @{
        'SmartPipe.Core' = '2.1.2-rc.1'
        'Microsoft.Extensions.Logging.Abstractions' = '10.0.8'
        'System.Text.Json' = '10.0.0'
    } -ShouldPass $true -ExpectedValidationRoot $false
    Invoke-Case -Name 'keep-success-cleans' -JsonDependencies @{
        'SmartPipe.Core' = '2.1.2-rc.1'
        'Microsoft.Extensions.Logging.Abstractions' = '10.0.8'
    } -ShouldPass $true -KeepTemporaryFiles $true -ExpectedValidationRoot $false
    Invoke-Case -Name 'cleanup-failure' -JsonDependencies @{} -ShouldPass $false -ExpectedError 'Missing package dependency: SmartPipe.Core' -ExpectedValidationRoot $false
    Invoke-Case -Name 'keep-failure-preserves' -JsonDependencies @{} -ShouldPass $false -ExpectedError 'Missing package dependency: SmartPipe.Core' -KeepTemporaryFiles $true -ExpectedValidationRoot $true
    Invoke-Case -Name 'missing-json-core' -JsonDependencies @{'Microsoft.Extensions.Logging.Abstractions' = '10.0.8'} -ShouldPass $false -ExpectedError 'Missing package dependency: SmartPipe.Core'
    Invoke-Case -Name 'missing-logging' -JsonDependencies @{'SmartPipe.Core' = '2.1.2-rc.1'} -ShouldPass $false -ExpectedError 'Missing package dependency: Microsoft.Extensions.Logging.Abstractions'
    Invoke-Case -Name 'wrong-json-core-version' -JsonDependencies @{
        'SmartPipe.Core' = '2.1.2'
        'Microsoft.Extensions.Logging.Abstractions' = '10.0.8'
    } -ShouldPass $false -ExpectedError "expected lower bound '2.1.2-rc.1'"

    foreach ($dependency in @('SmartPipe.Core', 'SmartPipe.Extensions.Json')) {
        $missing = Get-DefaultExtensionsDependencies
        $missing.Remove($dependency)
        Invoke-Case -Name "extensions-missing-$dependency" -JsonDependencies @{
            'SmartPipe.Core' = '2.1.2-rc.1'; 'Microsoft.Extensions.Logging.Abstractions' = '10.0.8'
        } -ExtensionsDependencies $missing -ShouldPass $false -ExpectedError "Missing package dependency: $dependency"

        $wrong = Get-DefaultExtensionsDependencies
        $wrong[$dependency] = '2.1.2'
        Invoke-Case -Name "extensions-wrong-$dependency" -JsonDependencies @{
            'SmartPipe.Core' = '2.1.2-rc.1'; 'Microsoft.Extensions.Logging.Abstractions' = '10.0.8'
        } -ExtensionsDependencies $wrong -ShouldPass $false -ExpectedError "expected lower bound '2.1.2-rc.1'"
    }

    $integrationDependencies = (Get-DefaultExtensionsDependencies).Keys | Where-Object { $_ -notlike 'SmartPipe.*' }
    foreach ($dependency in $integrationDependencies) {
        $missing = Get-DefaultExtensionsDependencies
        $missing.Remove($dependency)
        Invoke-Case -Name "extensions-missing-$dependency" -JsonDependencies @{
            'SmartPipe.Core' = '2.1.2-rc.1'; 'Microsoft.Extensions.Logging.Abstractions' = '10.0.8'
        } -ExtensionsDependencies $missing -ShouldPass $false -ExpectedError "Missing package dependency: $dependency"
    }
    $forbiddenDependencies = @(
        'SmartPipe.Extensions',
        'CsvHelper',
        'Dapper',
        'Mapster',
        'Microsoft.EntityFrameworkCore',
        'Microsoft.Extensions.Diagnostics.HealthChecks',
        'Microsoft.Extensions.Hosting.Abstractions',
        'Microsoft.Extensions.Http',
        'Microsoft.Extensions.Resilience',
        'Newtonsoft.Json'
    )
    foreach ($forbiddenDependency in $forbiddenDependencies) {
        Invoke-Case -Name "forbidden-$($forbiddenDependency.Replace('.', '-'))" -JsonDependencies @{
            'SmartPipe.Core' = '2.1.2-rc.1'
            'Microsoft.Extensions.Logging.Abstractions' = '10.0.8'
            $forbiddenDependency = '1.0.0'
        } -ShouldPass $false -ExpectedError "must not depend on $forbiddenDependency"
    }
    Write-Output 'validate-json-package-split script tests passed.'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
