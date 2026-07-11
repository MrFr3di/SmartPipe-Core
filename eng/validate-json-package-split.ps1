param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string] $DependencyPackageDirectory
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
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet $($arguments -join ' ') failed with exit code $LASTEXITCODE in $workingDirectory"
        }
    }
    finally {
        Pop-Location
    }
}

$corePackage = Get-PackagePath 'SmartPipe.Core'
$jsonPackage = Get-PackagePath 'SmartPipe.Extensions.Json'
$extensionsPackage = Get-PackagePath 'SmartPipe.Extensions'
Get-PackagePath 'SmartPipe.Core' 'snupkg' | Out-Null
Get-PackagePath 'SmartPipe.Extensions.Json' 'snupkg' | Out-Null
Get-PackagePath 'SmartPipe.Extensions' 'snupkg' | Out-Null

$jsonDependencies = Get-Dependencies (Read-Nuspec $jsonPackage)
Assert-Dependency $jsonDependencies 'SmartPipe.Core'
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
    'System.Text.Json',
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

$validationParent = Split-Path -Parent $packageRoot
$validationRoot = Join-Path $validationParent "package-split-validation-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $validationRoot | Out-Null
$validationPackages = [System.Security.SecurityElement]::Escape((Join-Path $validationRoot 'packages'))
$escapedPackageRoot = [System.Security.SecurityElement]::Escape($packageRoot)
$dependencySource = ''
$nugetSource = '    <add key="nuget" value="https://api.nuget.org/v3/index.json" />'
if (-not [string]::IsNullOrWhiteSpace($DependencyPackageDirectory)) {
    $dependencyRoot = [System.IO.Path]::GetFullPath($DependencyPackageDirectory)
    if (-not (Test-Path -LiteralPath $dependencyRoot -PathType Container)) {
        throw "Dependency package directory does not exist: $dependencyRoot"
    }

    $escapedDependencyRoot = [System.Security.SecurityElement]::Escape($dependencyRoot)
    $dependencySource = "    <add key=`"offline-dependencies`" value=`"$escapedDependencyRoot`" />"
    $nugetSource = ''
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
</configuration>
"@
Set-Content -LiteralPath (Join-Path $validationRoot 'NuGet.Config') -Value $localNuGetConfig -Encoding utf8NoBOM

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
Invoke-DotNet $directJson @('run', '--configuration', 'Release', '--configfile', (Join-Path $validationRoot 'NuGet.Config'))

$extensionsOnly = Join-Path $validationRoot 'ExtensionsOnly'
New-Item -ItemType Directory -Path $extensionsOnly | Out-Null
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
Set-Content -LiteralPath (Join-Path $extensionsOnly 'Program.cs') -Encoding utf8NoBOM -Value @'
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
Console.WriteLine("Extensions-only forwarding consumer passed.");
'@
Invoke-DotNet $extensionsOnly @('run', '--configuration', 'Release', '--configfile', (Join-Path $validationRoot 'NuGet.Config'))

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
Set-Content -LiteralPath (Join-Path $legacyLibrary 'LegacyProbe.cs') -Encoding utf8NoBOM -Value @'
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
        typeof(JsonTransform<,>).Assembly.GetName().Name!,
    ];
}
'@
Invoke-DotNet $legacyLibrary @('build', '--configuration', 'Release', '--configfile', (Join-Path $validationRoot 'NuGet.Config'))
$legacyAssets = Get-Content -Raw -LiteralPath (Join-Path $legacyLibrary 'obj/project.assets.json')
if ($legacyAssets -notmatch '"SmartPipe\.Extensions/2\.1\.1"') {
    throw 'Legacy compatibility library was not compiled against SmartPipe.Extensions 2.1.1.'
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
Console.WriteLine("SmartPipe.Extensions 2.1.1 binary compatibility consumer passed.");
'@
Invoke-DotNet $legacyHost @('run', '--configuration', 'Release', '--configfile', (Join-Path $validationRoot 'NuGet.Config'))

Write-Output "SmartPipe JSON package split validation passed for $Version."
