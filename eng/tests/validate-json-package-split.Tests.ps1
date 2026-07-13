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

function New-MockDotNet([string] $logFile) {
    $mockDir = Join-Path $root 'mock-dotnet'
    if (-not (Test-Path -LiteralPath $mockDir)) {
        New-Item -ItemType Directory -Path $mockDir | Out-Null
    }

    $script = @"
param([Parameter(ValueFromRemainingArguments = `$true)][string[]]`$Arguments)

`$command = `$Arguments[0]
"`$(`$Arguments -join ' ')" | Add-Content -Path "$logFile" -Encoding utf8NoBOM

`$assetsPath = Join-Path `$PWD 'obj/project.assets.json'

if (`$command -eq 'restore') {
    New-Item -ItemType Directory -Path (Split-Path -Parent `$assetsPath) -Force | Out-Null
    `$localPackages = @{
        'SmartPipe.Core/2.1.2-rc.1' = @{ type = 'package'; path = 'smartpipe.core/2.1.2-rc.1' }
        'SmartPipe.Extensions.Json/2.1.2-rc.1' = @{ type = 'package'; path = 'smartpipe.extensions.json/2.1.2-rc.1' }
        'SmartPipe.Extensions/2.1.2-rc.1' = @{ type = 'package'; path = 'smartpipe.extensions/2.1.2-rc.1' }
    }
    if (`$env:SMARTPIPE_MOCK_OMIT_PACKAGES) {
        foreach (`$packageId in (`$env:SMARTPIPE_MOCK_OMIT_PACKAGES -split ',')) {
            `$localPackages.Remove(`$packageId.Trim() + '/2.1.2-rc.1')
        }
    }

    `$validationRoot = Split-Path -Parent `$PWD
    `$packagesFolder = Join-Path `$validationRoot 'packages'
    `$assets = @{
        version = 3
        packageFolders = @{ `$packagesFolder = @{} }
        libraries = `$localPackages
    }
    `$assets | ConvertTo-Json -Depth 10 | Set-Content -Path `$assetsPath -Encoding utf8NoBOM

    `$source = if (`$env:SMARTPIPE_MOCK_PACKAGE_ROOT) { `$env:SMARTPIPE_MOCK_PACKAGE_ROOT } else { 'C:\local' }
    foreach (`$packageKey in `$localPackages.Keys) {
        `$metadataDir = Join-Path `$packagesFolder `$localPackages[`$packageKey].path
        New-Item -ItemType Directory -Path `$metadataDir -Force | Out-Null
        `$metadata = @{ version = 2; contentHash = 'test'; source = `$source }
        `$metadata | ConvertTo-Json -Depth 3 | Set-Content -Path (Join-Path `$metadataDir '.nupkg.metadata') -Encoding utf8NoBOM
    }
}
elseif (`$command -eq 'build') {
    if (Test-Path -LiteralPath `$assetsPath) {
        `$assets = Get-Content -Raw -LiteralPath `$assetsPath | ConvertFrom-Json
        if (-not `$assets.libraries.'SmartPipe.Extensions/2.1.1') {
            `$assets.libraries | Add-Member -NotePropertyName 'SmartPipe.Extensions/2.1.1' -NotePropertyValue @{ type = 'package'; path = 'smartpipe-local/smartpipe.extensions/2.1.1' } -Force
            `$assets | ConvertTo-Json -Depth 10 | Set-Content -Path `$assetsPath -Encoding utf8NoBOM
        }
    }
}

exit 0
"@

    $mockPath = Join-Path $mockDir 'dotnet.ps1'
    Set-Content -LiteralPath $mockPath -Value $script -Encoding utf8NoBOM
    return $mockPath
}

function Invoke-Case {
    param(
        [string] $Name,
        [hashtable] $JsonDependencies,
        [hashtable] $ExtensionsDependencies = (Get-DefaultExtensionsDependencies),
        [bool] $ShouldPass,
        [string] $ExpectedError = '',
        [bool] $KeepTemporaryFiles = $false,
        [bool] $ExpectedValidationRoot = $false,
        [bool] $ManifestOnly = $true,
        [string] $DotNetCommand = '',
        [hashtable] $Environment = @{},
        [string] $ValidationRoot = ''
    )

    $caseRoot = Join-Path $root $Name
    if ([string]::IsNullOrWhiteSpace($ValidationRoot)) {
        $ValidationRoot = Join-Path $caseRoot 'validation-root'
    }
    New-Item -ItemType Directory -Path $caseRoot | Out-Null
    $version = '2.1.2-rc.1'
    New-TestPackage $caseRoot 'SmartPipe.Core' $version @{}
    New-TestPackage $caseRoot 'SmartPipe.Extensions.Json' $version $JsonDependencies
    New-TestPackage $caseRoot 'SmartPipe.Extensions' $version $ExtensionsDependencies

    $previousEnvironment = @{}
    foreach ($key in $Environment.Keys) {
        $previousEnvironment[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $Environment[$key])
    }

    try {
        $arguments = @{
            PackageDirectory = $caseRoot
            Version = $version
            ManifestOnly = $ManifestOnly
            ValidationRoot = $ValidationRoot
            KeepTemporaryFiles = $KeepTemporaryFiles
        }
        if (-not [string]::IsNullOrWhiteSpace($DotNetCommand)) {
            $arguments['DotNetCommand'] = $DotNetCommand
        }

        & $validator @arguments
        if (-not $ShouldPass) { throw "Case '$Name' unexpectedly passed." }
    }
    catch {
        if ($ShouldPass) { throw }
        if ($_.Exception.Message -notlike "*$ExpectedError*") {
            throw "Case '$Name' failed with unexpected error: $($_.Exception.Message)"
        }
    }
    finally {
        foreach ($key in $Environment.Keys) {
            [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key])
        }
    }

    if ((Test-Path -LiteralPath $ValidationRoot) -ne $ExpectedValidationRoot) {
        throw "Case '$Name' validation root existence did not match expected '$ExpectedValidationRoot'."
    }
}

try {
    Invoke-Case -Name 'cleanup-success' -JsonDependencies @{
        'SmartPipe.Core' = '2.1.2-rc.1'
        'Microsoft.Extensions.Logging.Abstractions' = '10.0.8'
        'System.Text.Json' = '10.0.0'
    } -ShouldPass $true -ExpectedValidationRoot $false
    Invoke-Case -Name 'keep-success-preserves' -JsonDependencies @{
        'SmartPipe.Core' = '2.1.2-rc.1'
        'Microsoft.Extensions.Logging.Abstractions' = '10.0.8'
    } -ShouldPass $true -KeepTemporaryFiles $true -ExpectedValidationRoot $true
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

    $consumerCaseRoot = Join-Path $root 'consumer-validation'
    New-Item -ItemType Directory -Path $consumerCaseRoot | Out-Null

    function Invoke-ConsumerCase([string] $Name, [bool] $ShouldPass, [string] $ExpectedError = '', [bool] $KeepTemporaryFiles = $false, [hashtable] $Environment = @{}) {
        $validationRoot = Join-Path $consumerCaseRoot $Name
        $logFile = Join-Path $consumerCaseRoot "$Name.log"
        $mockDotNet = New-MockDotNet -logFile $logFile
        $caseRoot = Join-Path $root $Name

        $environmentWithSource = @{
            SMARTPIPE_MOCK_PACKAGE_ROOT = $caseRoot
        }
        foreach ($key in $Environment.Keys) {
            $environmentWithSource[$key] = $Environment[$key]
        }

        Invoke-Case -Name $Name -JsonDependencies @{
            'SmartPipe.Core' = '2.1.2-rc.1'
            'Microsoft.Extensions.Logging.Abstractions' = '10.0.8'
        } -ExtensionsDependencies (Get-DefaultExtensionsDependencies) -ShouldPass $ShouldPass -ExpectedError $ExpectedError -ManifestOnly $false -DotNetCommand $mockDotNet -KeepTemporaryFiles $KeepTemporaryFiles -ExpectedValidationRoot $KeepTemporaryFiles -ValidationRoot $validationRoot -Environment $environmentWithSource

        return @{ ValidationRoot = $validationRoot; LogFile = $logFile }
    }

    $result = Invoke-ConsumerCase -Name 'ValidationScript_UsesExplicitRestore' -ShouldPass $true
    $log = Get-Content -Path $result.LogFile
    $restoreCount = ($log | Where-Object { $_ -like 'restore*' }).Count
    if ($restoreCount -lt 4) {
        throw "Expected at least 4 explicit restore invocations, found $restoreCount."
    }

    $result = Invoke-ConsumerCase -Name 'ValidationScript_RunUsesNoRestore' -ShouldPass $true
    $log = Get-Content -Path $result.LogFile
    $runWithRestore = $log | Where-Object { $_ -like 'run*' -and $_ -notlike '*--no-restore*' }
    if ($null -ne $runWithRestore) {
        throw "Found dotnet run without --no-restore: $runWithRestore"
    }

    $result = Invoke-ConsumerCase -Name 'ValidationScript_BuildUsesNoRestore' -ShouldPass $true
    $log = Get-Content -Path $result.LogFile
    $buildWithRestore = $log | Where-Object { $_ -like 'build*' -and $_ -notlike '*--no-restore*' }
    if ($null -ne $buildWithRestore) {
        throw "Found dotnet build without --no-restore: $buildWithRestore"
    }

    $result = Invoke-ConsumerCase -Name 'ValidationScript_MapsSmartPipePackagesToLocalSource' -ShouldPass $true -KeepTemporaryFiles $true
    $nugetConfigPath = Join-Path $result.ValidationRoot 'NuGet.Config'
    if (-not (Test-Path -LiteralPath $nugetConfigPath)) {
        throw 'NuGet.Config was not produced in validation root.'
    }
    $nugetConfig = Get-Content -Raw -LiteralPath $nugetConfigPath
    if ($nugetConfig -notmatch '<packageSource key="smartpipe-local">[\s\S]*<package pattern="SmartPipe\.\*" />') {
        throw 'NuGet.Config does not map SmartPipe.* packages to smartpipe-local source.'
    }

    Invoke-ConsumerCase -Name 'ValidationScript_FailsWhenRequiredLocalPackageIsMissing' -ShouldPass $false -ExpectedError 'Package SmartPipe.Core was not resolved' -Environment @{
        SMARTPIPE_MOCK_OMIT_PACKAGES = 'SmartPipe.Core'
    } | Out-Null

    $result = Invoke-ConsumerCase -Name 'ValidationScript_DoesNotFallBackToNugetOrgForSmartPipePackages' -ShouldPass $true -KeepTemporaryFiles $true
    $metadataPath = Join-Path $result.ValidationRoot 'packages/smartpipe.core/2.1.2-rc.1/.nupkg.metadata'
    if (-not (Test-Path -LiteralPath $metadataPath)) {
        throw '.nupkg.metadata was not produced for SmartPipe.Core.'
    }
    $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
    if ($metadata.source -like '*nuget.org*' -or $metadata.source -like 'https://api.nuget.org/*') {
        throw "SmartPipe.Core fell back to NuGet.org: $($metadata.source)"
    }

    $result = Invoke-ConsumerCase -Name 'ValidationScript_CleansTemporaryDirectory' -ShouldPass $true
    if (Test-Path -LiteralPath $result.ValidationRoot) {
        throw 'ValidationScript_CleansTemporaryDirectory: validation root was not cleaned.'
    }

    Write-Output 'validate-json-package-split script tests passed.'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
