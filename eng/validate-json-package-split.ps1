param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string] $DependencyPackageDirectory,

    [switch] $ManifestOnly,

    [switch] $KeepTemporaryFiles,

    [string] $ValidationRoot,

    [string] $DotNetCommand = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$packageRoot = [System.IO.Path]::GetFullPath($PackageDirectory)
if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
    throw "Package directory does not exist: $packageRoot"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-PackagePath([string] $packageId, [string] $extension = 'nupkg') {
    $path = Join-Path $packageRoot "$packageId.$Version.$extension"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Expected package was not produced: $path"
    }

    return $path
}

function Read-Nuspec([string] $packagePath) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entry = $archive.Entries | Where-Object FullName -Like '*.nuspec' | Select-Object -First 1
        if ($null -eq $entry) {
            throw "Package has no nuspec: $packagePath"
        }

        $reader = [System.IO.StreamReader]::new($entry.Open())
        try {
            return [xml] $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-Dependencies([xml] $nuspec) {
    $manager = [System.Xml.XmlNamespaceManager]::new($nuspec.NameTable)
    $manager.AddNamespace('n', $nuspec.DocumentElement.NamespaceURI)
    return @($nuspec.SelectNodes('//n:dependencies/n:group/n:dependency', $manager))
}

function Assert-Dependency($dependencies, [string] $packageId) {
    $dependency = $dependencies | Where-Object id -EQ $packageId | Select-Object -First 1
    if ($null -eq $dependency) {
        throw "Missing package dependency: $packageId"
    }

    if ($dependency.version -ne $Version) {
        throw "Dependency $packageId has version '$($dependency.version)', expected lower bound '$Version'."
    }
}

function Invoke-DotNet([string] $workingDirectory, [string[]] $arguments) {
    Push-Location $workingDirectory
    try {
        & $DotNetCommand @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet $($arguments -join ' ') failed with exit code $LASTEXITCODE in $workingDirectory"
        }
    }
    finally {
        Pop-Location
    }
}

function Assert-LocalPackageResolution([string] $projectDirectory, [string[]] $packageIds) {
    $assetsPath = Join-Path $projectDirectory 'obj/project.assets.json'
    if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
        throw "project.assets.json was not produced for $projectDirectory"
    }

    $assets = Get-Content -Raw -LiteralPath $assetsPath | ConvertFrom-Json
    $packageFolder = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($packageFolder)) {
        throw "Global packages folder was not recorded in $projectDirectory"
    }

    foreach ($packageId in $packageIds) {
        $libraryKey = "$packageId/$Version"
        $library = $assets.libraries.$libraryKey
        if ($null -eq $library) {
            throw "Package $packageId was not resolved for $projectDirectory"
        }

        $metadataPath = Join-Path $packageFolder (Join-Path $library.path '.nupkg.metadata')
        if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
            throw "Package metadata was not found for $packageId in $projectDirectory"
        }

        $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
        $source = [System.IO.Path]::GetFullPath($metadata.source)
        $expectedSource = [System.IO.Path]::GetFullPath($packageRoot)
        if ($source -ne $expectedSource) {
            throw "Package $packageId was resolved from '$source' instead of the local source '$expectedSource' for $projectDirectory"
        }
    }
}

function Assert-ExactPackageResolution(
    [string] $projectDirectory,
    [string] $packageId,
    [string] $expectedVersion) {
    $assetsPath = Join-Path $projectDirectory 'obj/project.assets.json'
    if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
        throw "project.assets.json was not produced for $projectDirectory"
    }

    $assets = Get-Content -Raw -LiteralPath $assetsPath | ConvertFrom-Json
    $libraryKey = "$packageId/$expectedVersion"
    if ($null -eq $assets.libraries.$libraryKey) {
        throw "Package $packageId $expectedVersion was not resolved for $projectDirectory"
    }

    $unexpectedKey = "$packageId/$Version"
    if ($expectedVersion -ne $Version -and $null -ne $assets.libraries.$unexpectedKey) {
        throw "Package $packageId $Version replaced the required $expectedVersion reference in $projectDirectory"
    }
}

if ([string]::IsNullOrWhiteSpace($ValidationRoot)) {
    $validationParent = Split-Path -Parent $packageRoot
    $validationRoot = Join-Path $validationParent "package-split-validation-$([Guid]::NewGuid().ToString('N'))"
}
else {
    $validationRoot = [System.IO.Path]::GetFullPath($ValidationRoot)
}

$validationSucceeded = $false
try {
New-Item -ItemType Directory -Path $validationRoot | Out-Null

$corePackage = Get-PackagePath 'SmartPipe.Core'
$jsonPackage = Get-PackagePath 'SmartPipe.Extensions.Json'
$extensionsPackage = Get-PackagePath 'SmartPipe.Extensions'
Get-PackagePath 'SmartPipe.Core' 'snupkg' | Out-Null
Get-PackagePath 'SmartPipe.Extensions.Json' 'snupkg' | Out-Null
Get-PackagePath 'SmartPipe.Extensions' 'snupkg' | Out-Null

$jsonDependencies = Get-Dependencies (Read-Nuspec $jsonPackage)
Assert-Dependency $jsonDependencies 'SmartPipe.Core'
if ($null -eq ($jsonDependencies | Where-Object id -EQ 'Microsoft.Extensions.Logging.Abstractions' | Select-Object -First 1)) {
    throw 'Missing package dependency: Microsoft.Extensions.Logging.Abstractions'
}
$forbiddenJsonDependencies = @(
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
foreach ($packageId in $forbiddenJsonDependencies) {
    if ($jsonDependencies.id -contains $packageId) {
        throw "SmartPipe.Extensions.Json must not depend on $packageId."
    }
}

$extensionsDependencies = Get-Dependencies (Read-Nuspec $extensionsPackage)
Assert-Dependency $extensionsDependencies 'SmartPipe.Core'
Assert-Dependency $extensionsDependencies 'SmartPipe.Extensions.Json'
$requiredExtensionsDependencies = @(
    'CsvHelper',
    'Dapper',
    'Mapster',
    'Microsoft.EntityFrameworkCore',
    'Microsoft.Extensions.Diagnostics.HealthChecks',
    'Microsoft.Extensions.Hosting.Abstractions',
    'Microsoft.Extensions.Http',
    'Microsoft.Extensions.Logging.Abstractions',
    'Microsoft.Extensions.Resilience'
)
foreach ($packageId in $requiredExtensionsDependencies) {
    if ($extensionsDependencies.id -notcontains $packageId) {
        throw "Missing package dependency: $packageId"
    }
}

if ($ManifestOnly) {
    $validationSucceeded = $true
    Write-Output "SmartPipe JSON package manifest validation passed for $Version."
    return
}

$validationPackages = [System.Security.SecurityElement]::Escape((Join-Path $validationRoot 'packages'))
$escapedPackageRoot = [System.Security.SecurityElement]::Escape($packageRoot)
$dependencySource = ''
$nugetSource = '    <add key="nuget" value="https://api.nuget.org/v3/index.json" />'
$fallbackMappingSource = "    <packageSource key=`"nuget`">`n      <package pattern=`"SmartPipe.Core`" />`n      <package pattern=`"SmartPipe.Extensions`" />`n      <package pattern=`"*`" />`n    </packageSource>"
if (-not [string]::IsNullOrWhiteSpace($DependencyPackageDirectory)) {
    $dependencyRoot = [System.IO.Path]::GetFullPath($DependencyPackageDirectory)
    if (-not (Test-Path -LiteralPath $dependencyRoot -PathType Container)) {
        throw "Dependency package directory does not exist: $dependencyRoot"
    }

    $escapedDependencyRoot = [System.Security.SecurityElement]::Escape($dependencyRoot)
    $dependencySource = "    <add key=`"offline-dependencies`" value=`"$escapedDependencyRoot`" />"
    $nugetSource = ''
    $fallbackMappingSource = "    <packageSource key=`"offline-dependencies`">`n      <package pattern=`"SmartPipe.Core`" />`n      <package pattern=`"SmartPipe.Extensions`" />`n      <package pattern=`"*`" />`n    </packageSource>"
}
$localNuGetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="$validationPackages" />
  </config>
  <packageSources>
    <clear />
    <add key="smartpipe-local" value="$escapedPackageRoot" />
$dependencySource
$nugetSource
  </packageSources>
  <packageSourceMapping>
    <packageSource key="smartpipe-local">
      <package pattern="SmartPipe.Core" />
      <package pattern="SmartPipe.Extensions" />
      <package pattern="SmartPipe.*" />
    </packageSource>
$fallbackMappingSource
  </packageSourceMapping>
</configuration>
"@
Set-Content -LiteralPath (Join-Path $validationRoot 'NuGet.Config') -Value $localNuGetConfig -Encoding utf8NoBOM
$nugetConfig = Join-Path $validationRoot 'NuGet.Config'

$directJson = Join-Path $validationRoot 'DirectJson'
New-Item -ItemType Directory -Path $directJson | Out-Null
Set-Content -LiteralPath (Join-Path $directJson 'DirectJson.csproj') -Encoding utf8NoBOM -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <NuGetAudit>false</NuGetAudit>
    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SmartPipe.Extensions.Json" Version="[$Version]" />
  </ItemGroup>
</Project>
"@
Set-Content -LiteralPath (Join-Path $directJson 'Program.cs') -Encoding utf8NoBOM -Value @'
using SmartPipe.Extensions.Selectors;
using SmartPipe.Extensions.Sinks;
using SmartPipe.Extensions.Transforms;
using SmartPipe.Extensions;

Type[] jsonTypes =
[
    typeof(JsonFileSource<>),
    typeof(DeadLetterSource<>),
    typeof(JsonFileSink<>),
    typeof(DeadLetterSink<>),
    typeof(DeadLetterWriteFailureMode),
    typeof(DeadLetterWriteException),
    typeof(JsonTransform<,>),
    typeof(JsonFileSourceOptions),
    typeof(JsonFileSinkOptions),
    typeof(DeadLetterSourceOptions),
    typeof(DeadLetterSinkOptions),
];
if (jsonTypes.Any(type => type.Assembly.GetName().Name != "SmartPipe.Extensions.Json"))
    throw new InvalidOperationException("A JSON integration type is not owned by SmartPipe.Extensions.Json.");
Console.WriteLine("Direct JSON package consumer passed.");
'@
Invoke-DotNet $directJson @('restore', '--configfile', $nugetConfig, '--disable-parallel')
Assert-LocalPackageResolution $directJson @('SmartPipe.Core', 'SmartPipe.Extensions.Json')
Invoke-DotNet $directJson @('run', '--configuration', 'Release', '--no-restore')

$extensionsOnly = Join-Path $validationRoot 'ExtensionsOnly'
New-Item -ItemType Directory -Path $extensionsOnly | Out-Null
$legacyNullMetadataProbe = @'
/// <summary>Verifies legacy constructor overload resolution.</summary>
public static class JsonFileSinkLegacyNullMetadataProbe
{
    /// <summary>Runs null/default source-compatibility probes.</summary>
    public static void Verify()
    {
        VerifyCall(() => new SmartPipe.Extensions.Sinks.JsonFileSink<string>("output.json", null!));
        VerifyCall(() => new SmartPipe.Extensions.Sinks.JsonFileSink<string>(
            "output.json",
            default(System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<string>>)!));
    }

    private static void VerifyCall(Func<SmartPipe.Extensions.Sinks.JsonFileSink<string>> createSink)
    {
        try
        {
            _ = createSink();
        }
        catch (ArgumentNullException exception) when (exception.ParamName == "batchTypeInfo")
        {
            return;
        }

        throw new InvalidOperationException("The legacy null-metadata call did not select the JsonTypeInfo<List<T>> constructor.");
    }
}
'@
Set-Content -LiteralPath (Join-Path $extensionsOnly 'ExtensionsOnly.csproj') -Encoding utf8NoBOM -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <NuGetAudit>false</NuGetAudit>
    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SmartPipe.Extensions" Version="[$Version]" />
  </ItemGroup>
</Project>
"@
$extensionsOnlyProgram = @'
using SmartPipe.Extensions.Selectors;
using SmartPipe.Extensions.Sinks;
using SmartPipe.Extensions.Transforms;

Type[] forwardedTypes =
[
    typeof(JsonFileSource<>),
    typeof(DeadLetterSource<>),
    typeof(JsonFileSink<>),
    typeof(DeadLetterSink<>),
    typeof(DeadLetterWriteFailureMode),
    typeof(DeadLetterWriteException),
    typeof(JsonTransform<,>),
];
if (forwardedTypes.Any(type => type.Assembly.GetName().Name != "SmartPipe.Extensions.Json"))
    throw new InvalidOperationException("SmartPipe.Extensions did not resolve a forwarded JSON type.");
JsonFileSinkLegacyNullMetadataProbe.Verify();
Console.WriteLine("Extensions-only forwarding consumer passed.");
'@
Set-Content -LiteralPath (Join-Path $extensionsOnly 'Program.cs') -Encoding utf8NoBOM -Value (
    $extensionsOnlyProgram + [Environment]::NewLine + $legacyNullMetadataProbe)
Invoke-DotNet $extensionsOnly @('restore', '--configfile', $nugetConfig, '--disable-parallel')
Assert-LocalPackageResolution $extensionsOnly @('SmartPipe.Core', 'SmartPipe.Extensions.Json', 'SmartPipe.Extensions')
Invoke-DotNet $extensionsOnly @('run', '--configuration', 'Release', '--no-restore')

$legacyLibrary = Join-Path $validationRoot 'LegacyLibrary'
New-Item -ItemType Directory -Path $legacyLibrary | Out-Null
Set-Content -LiteralPath (Join-Path $legacyLibrary 'LegacyLibrary.csproj') -Encoding utf8NoBOM -Value @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <NuGetAudit>false</NuGetAudit>
    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SmartPipe.Extensions" Version="2.1.1" />
  </ItemGroup>
</Project>
'@
$legacyProbeProgram = @'
using SmartPipe.Extensions.Selectors;
using SmartPipe.Extensions.Sinks;
using SmartPipe.Extensions.Transforms;

public static class LegacyProbe
{
    public static string[] ResolveJsonAssemblyNames() =>
    [
        typeof(JsonFileSource<>).Assembly.GetName().Name!,
        typeof(DeadLetterSource<>).Assembly.GetName().Name!,
        typeof(JsonFileSink<>).Assembly.GetName().Name!,
        typeof(DeadLetterSink<>).Assembly.GetName().Name!,
        typeof(DeadLetterWriteFailureMode).Assembly.GetName().Name!,
        typeof(DeadLetterWriteException).Assembly.GetName().Name!,
        typeof(JsonTransform<,>).Assembly.GetName().Name!,
    ];
}
'@
Set-Content -LiteralPath (Join-Path $legacyLibrary 'LegacyProbe.cs') -Encoding utf8NoBOM -Value (
    $legacyProbeProgram + [Environment]::NewLine + $legacyNullMetadataProbe)
Invoke-DotNet $legacyLibrary @('restore', '--configfile', $nugetConfig, '--disable-parallel')
Invoke-DotNet $legacyLibrary @('build', '--configuration', 'Release', '--no-restore')
Assert-ExactPackageResolution $legacyLibrary 'SmartPipe.Extensions' '2.1.1'
$legacyProject = Get-Content -Raw -LiteralPath (Join-Path $legacyLibrary 'LegacyLibrary.csproj')
if ($legacyProject -notmatch 'Include\s*=\s*"SmartPipe\.Extensions"\s*Version\s*=\s*"2\.1\.1"') {
    throw 'Legacy compatibility library project does not reference SmartPipe.Extensions 2.1.1.'
}

$legacyHost = Join-Path $validationRoot 'LegacyHost'
New-Item -ItemType Directory -Path $legacyHost | Out-Null
$legacyProjectPath = [System.Security.SecurityElement]::Escape((Join-Path $legacyLibrary 'LegacyLibrary.csproj'))
Set-Content -LiteralPath (Join-Path $legacyHost 'LegacyHost.csproj') -Encoding utf8NoBOM -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <NuGetAudit>false</NuGetAudit>
    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SmartPipe.Extensions" Version="[$Version]" />
    <ProjectReference Include="$legacyProjectPath" />
  </ItemGroup>
</Project>
"@
Set-Content -LiteralPath (Join-Path $legacyHost 'Program.cs') -Encoding utf8NoBOM -Value @'
var assemblyNames = LegacyProbe.ResolveJsonAssemblyNames();
if (assemblyNames.Any(name => name != "SmartPipe.Extensions.Json"))
    throw new InvalidOperationException("A consumer compiled against SmartPipe.Extensions 2.1.1 did not follow the 2.1.2 type forwarders.");
JsonFileSinkLegacyNullMetadataProbe.Verify();
Console.WriteLine("SmartPipe.Extensions 2.1.1 binary compatibility consumer passed.");
'@
Invoke-DotNet $legacyHost @('restore', '--configfile', $nugetConfig, '--disable-parallel')
Assert-LocalPackageResolution $legacyHost @('SmartPipe.Core', 'SmartPipe.Extensions.Json', 'SmartPipe.Extensions')
Invoke-DotNet $legacyHost @('run', '--configuration', 'Release', '--no-restore')

$validationSucceeded = $true
Write-Output "SmartPipe JSON package split validation passed for $Version."
}
finally {
    if ($KeepTemporaryFiles) {
        if (Test-Path -LiteralPath $validationRoot) {
            Write-Warning "Temporary files were kept at: $validationRoot"
        }
    }
    elseif (Test-Path -LiteralPath $validationRoot) {
        Remove-Item -LiteralPath $validationRoot -Recurse -Force
    }
}
