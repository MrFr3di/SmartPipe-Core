using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Packaging;

[Trait("Category", "PackageInfrastructure")]
[Collection(ExternalProcessCollection.Name)]
public sealed class PackageMetadataTargetTests
{
    [Fact]
    public void CommonPackageProps_DefineRequiredMetadataDefaults()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../eng/SmartPipe.Package.props"));
        var content = File.ReadAllText(path);

        Assert.Contains("<Authors>SmartPipe</Authors>", content, StringComparison.Ordinal);
        Assert.Contains("<PackageLicenseExpression>MIT</PackageLicenseExpression>", content, StringComparison.Ordinal);
        Assert.Contains("<PackageReadmeFile>README.md</PackageReadmeFile>", content, StringComparison.Ordinal);
        Assert.Contains("<SymbolPackageFormat>snupkg</SymbolPackageFormat>", content, StringComparison.Ordinal);
        Assert.Contains("<SmartPipePackageBaselinePolicy Condition=", content, StringComparison.Ordinal);
        Assert.Contains(">previous-stable</SmartPipePackageBaselinePolicy>", content, StringComparison.Ordinal);
        Assert.Contains("$(SmartPipePreviousStableVersion)</PackageValidationBaselineVersion>", content, StringComparison.Ordinal);
        Assert.Contains("$(SmartPipeVersionPrefix) servicing release", content, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageTargets_DefineMetadataAndContentGuards()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../eng/SmartPipe.Package.targets"));
        var content = File.ReadAllText(path);

        Assert.Contains("SmartPipePackageValidate", content, StringComparison.Ordinal);
        Assert.Contains("SmartPipePackageReadmeSource", content, StringComparison.Ordinal);
        Assert.Contains("icon.png", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageTargets_RejectVersionMismatchEvenWhenCiIsEnabled()
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("README.md", "# fixture");
        fixture.Write("assets/nuget/icon.png", "icon");
        fixture.Write("fixture.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <SmartPipePackage>true</SmartPipePackage>
                <PackageId>SmartPipe.Fixture</PackageId>
                <Description>A package fixture for target validation.</Description>
                <PackageTags>fixture</PackageTags>
                <SmartPipePackageReadmeSource>README.md</SmartPipePackageReadmeSource>
                <PackageVersion>2.1.0</PackageVersion>
                <SmartPipeVersionPrefix>2.2.0</SmartPipeVersionPrefix>
                <SmartPipeRepositoryRoot>{RepositoryRoot().Replace("\\", "/")}/</SmartPipeRepositoryRoot>
              </PropertyGroup>
              <Import Project="{Path.Combine(RepositoryRoot(), "eng", "SmartPipe.Package.targets").Replace("\\", "/")}" />
            </Project>
            """);

        var result = await new ProcessRunner().RunAsync(
            new("dotnet", ["msbuild", Path.Combine(fixture.Path, "fixture.csproj"), "-nologo", "-t:SmartPipePackageValidate", "-p:CI=true"], TimeSpan.FromMinutes(1), fixture.Path),
            TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not match repository version", result.StandardError + result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PackageTargets_AcceptPrereleaseWithMatchingVersionPrefix()
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("README.md", "# fixture");
        fixture.Write("fixture.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <SmartPipePackage>true</SmartPipePackage>
                <PackageId>SmartPipe.Fixture</PackageId>
                <Description>A package fixture for target validation.</Description>
                <PackageTags>fixture</PackageTags>
                <SmartPipePackageReadmeSource>README.md</SmartPipePackageReadmeSource>
                <PackageVersion>2.2.0-rc.1</PackageVersion>
                <SmartPipeVersionPrefix>2.2.0</SmartPipeVersionPrefix>
                <SmartPipeRepositoryRoot>${RepositoryRoot().Replace("\\", "/")}/</SmartPipeRepositoryRoot>
              </PropertyGroup>
              <Import Project="${Path.Combine(RepositoryRoot(), "eng", "SmartPipe.Package.targets").Replace("\\", "/")}" />
            </Project>
            """);

        var result = await new ProcessRunner().RunAsync(
            new("dotnet", ["msbuild", Path.Combine(fixture.Path, "fixture.csproj"), "-nologo", "-t:SmartPipePackageValidate"], TimeSpan.FromMinutes(1), fixture.Path),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
}
