using SmartPipe.RepositoryChecks.Packaging;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Packaging;

public sealed class OfficialPackageProjectTests
{
    private static readonly IReadOnlySet<string> FixturePackageIds = new HashSet<string>(["SmartPipe.Core", "SmartPipe.Extensions.Json", "SmartPipe.Extensions"], StringComparer.OrdinalIgnoreCase);
    [Fact]
    public async Task Verify_CurrentPackageProjects_HaveMarkerAndPackageSpecificMetadata()
    {
        var result = await new OfficialPackageProjectVerifier().VerifyAsync(
            RepositoryRoot(), TestContext.Current.CancellationToken);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Verify_MarkedTestProject_Fails()
    {
        using var repository = FixtureRepository();
        repository.Write("tests/Fixture.Tests.csproj", """
            <Project><PropertyGroup><SmartPipePackage>true</SmartPipePackage><PackageId>SmartPipe.Fixture</PackageId><Description>fixture</Description><PackageTags>fixture</PackageTags><SmartPipePackageReadmeSource>README.md</SmartPipePackageReadmeSource></PropertyGroup></Project>
            """);

        var result = await new OfficialPackageProjectVerifier(FixturePackageIds).VerifyAsync(
            repository.Path, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, violation => violation.Code == "SPPKG006");
    }

    [Fact]
    public async Task Verify_MissingBaselineForExistingPackage_Fails()
    {
        using var repository = FixtureRepository();
        repository.Write("src/SmartPipe.Core/SmartPipe.Core.csproj", ProjectXml("SmartPipe.Core", baseline: null));

        var result = await new OfficialPackageProjectVerifier(FixturePackageIds).VerifyAsync(
            repository.Path, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, violation => violation.Code == "SPPKG004");
    }

    [Fact]
    public async Task Verify_SharedMetadataOverride_Fails()
    {
        using var repository = FixtureRepository();
        repository.Write("src/SmartPipe.Core/SmartPipe.Core.csproj", ProjectXml("SmartPipe.Core", extra: "<Authors>Other</Authors>"));

        var result = await new OfficialPackageProjectVerifier(FixturePackageIds).VerifyAsync(
            repository.Path, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, violation => violation.Code == "SPPKG007");
    }

    [Fact]
    public async Task Verify_ReadmeOutsideRepository_Fails()
    {
        using var repository = FixtureRepository();
        repository.Write("src/SmartPipe.Core/SmartPipe.Core.csproj", ProjectXml("SmartPipe.Core", readme: "$(MSBuildProjectDirectory)/../../outside/README.md"));

        var result = await new OfficialPackageProjectVerifier(FixturePackageIds).VerifyAsync(
            repository.Path, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, violation => violation.Code == "SPPKG005");
    }

    private static RepositoryTestDirectory FixtureRepository()
    {
        var repository = new RepositoryTestDirectory();
        repository.Write("Directory.Build.props", "<Project />");
        repository.Write("eng/SmartPipe.Package.props", "<Project />");
        repository.Write("src/SmartPipe.Extensions.Json/SmartPipe.Extensions.Json.csproj", ProjectXml("SmartPipe.Extensions.Json", readme: "README.md", baseline: "2.1.2"));
        repository.Write("src/SmartPipe.Extensions/SmartPipe.Extensions.csproj", ProjectXml("SmartPipe.Extensions", readme: "README.md", baseline: "2.1.2"));
        repository.Write("README.md", "# Fixture");
        return repository;
    }

    private static string ProjectXml(string packageId, string? baseline = "2.1.2", string? readme = "README.md", string extra = "") => $"""
        <Project><PropertyGroup><SmartPipePackage>true</SmartPipePackage><PackageId>{packageId}</PackageId><Description>fixture</Description><PackageTags>fixture</PackageTags><SmartPipePackageReadmeSource>{readme}</SmartPipePackageReadmeSource>{(baseline is null ? "" : $"<PackageValidationBaselineVersion>{baseline}</PackageValidationBaselineVersion>")}{extra}</PropertyGroup><Import Project="$(SmartPipeRepositoryRoot)eng/SmartPipe.Package.props" /></Project>
        """;

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
}
