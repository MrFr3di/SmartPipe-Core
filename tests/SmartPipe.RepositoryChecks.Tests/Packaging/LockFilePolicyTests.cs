using SmartPipe.RepositoryChecks.Commands;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Packaging;

[Trait("Category", "PackageInfrastructure")]
public sealed class LockFilePolicyTests
{
    [Fact]
    public async Task RepositoryLockFiles_AreCompleteAndReconciled()
    {
        var result = await new VerifyLockFilesCommand().ExecuteAsync(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")),
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public async Task MissingLockFile_IsRejected()
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("Directory.Packages.props", "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally><CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled><CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled></PropertyGroup></Project>");
        fixture.Write("src/Fixture/Fixture.csproj", "<Project />");
        var result = await new VerifyLockFilesCommand().ExecuteAsync(fixture.Path, TestContext.Current.CancellationToken);
        Assert.Contains(result.Errors, error => error.Contains("SPLOCK001", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectLockVersion_DiffersFromCentralPackageVersion_IsRejected()
    {
        using var fixture = new RepositoryTestDirectory();
        WriteRepository(fixture, """
            "Fixture.Package": { "type": "Direct", "requested": "[1.0.0, )", "resolved": "0.9.0", "contentHash": "sha512-valid" }
            """);

        var result = await new VerifyLockFilesCommand().ExecuteAsync(fixture.Path, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, error => error.Contains("SPLOCK006", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StaleDirectLockDependency_IsRejected()
    {
        using var fixture = new RepositoryTestDirectory();
        WriteRepository(fixture, """
            "Fixture.Package": { "type": "Direct", "requested": "[1.0.0, )", "resolved": "1.0.0", "contentHash": "sha512-valid" },
            "Stale.Package": { "type": "Direct", "requested": "[1.0.0, )", "resolved": "1.0.0", "contentHash": "sha512-valid" }
            """);

        var result = await new VerifyLockFilesCommand().ExecuteAsync(fixture.Path, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, error => error.Contains("SPLOCK007", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AssetsSources_DifferFromRootNuGetConfig_IsRejected()
    {
        using var fixture = new RepositoryTestDirectory();
        WriteRepository(fixture, """
            "Fixture.Package": { "type": "Direct", "requested": "[1.0.0, )", "resolved": "1.0.0", "contentHash": "sha512-valid" }
            """, "https://drift.example/v3/index.json");

        var result = await new VerifyLockFilesCommand().ExecuteAsync(fixture.Path, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, error => error.Contains("SPLOCK008", StringComparison.Ordinal));
    }

    private static void WriteRepository(RepositoryTestDirectory fixture, string packages, string source = "https://api.nuget.org/v3/index.json")
    {
        fixture.Write("Directory.Packages.props", """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                <CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled>
                <CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Fixture.Package" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        fixture.Write("NuGet.Config", """
            <configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" /></packageSources></configuration>
            """);
        fixture.Write("src/Fixture/Fixture.csproj", """
            <Project><ItemGroup><PackageReference Include="Fixture.Package" /></ItemGroup></Project>
            """);
        fixture.Write("src/Fixture/packages.lock.json", $$"""
            { "version": 2, "dependencies": { "net10.0": { {{packages}} } } }
            """);
        fixture.Write("src/Fixture/obj/project.assets.json", $$"""
            { "project": { "restore": { "sources": { "{{source}}": {} } } } }
            """);
    }
}
