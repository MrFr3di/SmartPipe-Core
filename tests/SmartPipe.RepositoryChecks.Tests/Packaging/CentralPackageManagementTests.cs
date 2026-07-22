using SmartPipe.RepositoryChecks.Packaging;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Packaging;

public sealed class CentralPackageManagementTests
{
    [Fact]
    public async Task Verify_RepositoryPackageVersions_AreDeclaredExactlyOnce()
    {
        var result = await new CentralPackageVersionReader().VerifyAsync(
            RepositoryRoot(), CentralPackageValidationMode.Current, TestContext.Current.CancellationToken);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Verify_ProjectPackageReferenceContainsVersion_Fails()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Directory.Packages.props", """
            <Project><PropertyGroup>
              <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              <CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled>
              <CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled>
            </PropertyGroup><ItemGroup><PackageVersion Include="Example" Version="1.0.0" /></ItemGroup></Project>
            """);
        repository.Write("src/Project.csproj", "<Project><ItemGroup><PackageReference Include=\"Example\" Version=\"1.0.0\" /></ItemGroup></Project>");

        var result = await new CentralPackageVersionReader().VerifyAsync(
            repository.Path, CentralPackageValidationMode.Current, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, violation => violation.Code == "SPCPM004");
    }

    [Fact]
    public async Task Verify_NestedDirectoryPackagesProps_Fails()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Directory.Packages.props", "<Project />");
        repository.Write("src/Nested/Directory.Packages.props", "<Project />");

        var result = await new CentralPackageVersionReader().VerifyAsync(
            repository.Path, CentralPackageValidationMode.Current, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, violation => violation.Code == "SPCPM002");
    }

    [Fact]
    public async Task Verify_DuplicateCentralIds_FailsCaseInsensitively()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Directory.Packages.props", "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally><CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled><CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled></PropertyGroup><ItemGroup><PackageVersion Include=\"Example\" Version=\"1.0.0\" /><PackageVersion Include=\"example\" Version=\"1.0.0\" /></ItemGroup></Project>");

        var result = await new CentralPackageVersionReader().VerifyAsync(
            repository.Path, CentralPackageValidationMode.Current, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, violation => violation.Code == "SPCPM003");
    }

    [Fact]
    public async Task Verify_MissingCentralVersion_Fails()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Directory.Packages.props", "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally><CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled><CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled></PropertyGroup><ItemGroup><PackageVersion Include=\"Other\" Version=\"1.0.0\" /></ItemGroup></Project>");
        repository.Write("src/Project.csproj", "<Project><ItemGroup><PackageReference Include=\"Example\" /></ItemGroup></Project>");

        var result = await new CentralPackageVersionReader().VerifyAsync(
            repository.Path, CentralPackageValidationMode.Current, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, violation => violation.Code == "SPCPM005");
    }

    [Fact]
    public async Task Verify_ReleaseMode_TreatsUnusedCentralVersionAsError()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Directory.Packages.props", "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally><CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled><CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled></PropertyGroup><ItemGroup><PackageVersion Include=\"Unused\" Version=\"1.0.0\" /></ItemGroup></Project>");

        var result = await new CentralPackageVersionReader().VerifyAsync(
            repository.Path, CentralPackageValidationMode.Release, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, violation => violation.Code == "SPCPM006");
    }

    [Fact]
    public async Task Verify_CurrentMode_ReportsUnusedCentralVersionAsWarning()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Directory.Packages.props", CentralProps("<PackageVersion Include=\"Unused\" Version=\"1.0.0\" />"));

        var result = await new CentralPackageVersionReader().VerifyAsync(
            repository.Path, CentralPackageValidationMode.Current, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.Errors, violation => violation.Code == "SPCPM006");
        Assert.Contains(result.Warnings, violation => violation.Code == "SPCPM006");
    }

    [Fact]
    public async Task Verify_TransitivePinningEnabled_Fails()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Directory.Packages.props", CentralProps("", transitivePinning: "true"));

        var result = await new CentralPackageVersionReader().VerifyAsync(
            repository.Path, CentralPackageValidationMode.Current, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, violation => violation.Code == "SPCPM007");
    }

    [Fact]
    public async Task Verify_VersionOverrideEnabled_Fails()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Directory.Packages.props", CentralProps("", versionOverride: "true"));

        var result = await new CentralPackageVersionReader().VerifyAsync(
            repository.Path, CentralPackageValidationMode.Current, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, violation => violation.Code == "SPCPM008");
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1..0")]
    [InlineData("1.0.0-")]
    [InlineData("[1.0.0]")]
    public async Task Verify_NonExactCentralVersion_Fails(string version)
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Directory.Packages.props", CentralProps($"<PackageVersion Include=\"Example\" Version=\"{version}\" />"));

        var result = await new CentralPackageVersionReader().VerifyAsync(
            repository.Path, CentralPackageValidationMode.Current, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, violation => violation.Code == "SPCPM009");
    }

    [Fact]
    public async Task Verify_MissingRootProps_Fails()
    {
        using var repository = new RepositoryTestDirectory();

        var result = await new CentralPackageVersionReader().VerifyAsync(
            repository.Path, CentralPackageValidationMode.Current, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, violation => violation.Code == "SPCPM001");
    }

    private static string CentralProps(string packageVersion, string transitivePinning = "false", string versionOverride = "false") => $"""
        <Project><PropertyGroup>
          <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
          <CentralPackageTransitivePinningEnabled>{transitivePinning}</CentralPackageTransitivePinningEnabled>
          <CentralPackageVersionOverrideEnabled>{versionOverride}</CentralPackageVersionOverrideEnabled>
        </PropertyGroup><ItemGroup>{packageVersion}</ItemGroup></Project>
        """;

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
}
