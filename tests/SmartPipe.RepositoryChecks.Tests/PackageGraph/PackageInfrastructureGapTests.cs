using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Packaging;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.PackageGraph;

public sealed class PackageInfrastructureGapTests
{
    [Fact]
    public async Task Loader_DefaultContractRejectsCatalogWithoutAllExactNineteenIds()
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("src/SmartPipe.Core/SmartPipe.Core.csproj", "<Project />");
        fixture.Write("eng/package-graph.json", PackageGraphContractTests.MinimalCatalogForTests());
        var error = await Assert.ThrowsAsync<PackageGraphException>(() => new PackageGraphLoader().LoadAsync(fixture.Path, "eng/package-graph.json", TestContext.Current.CancellationToken));
        Assert.Equal("SPGRAPH016", error.Code);
    }

    [Theory]
    [InlineData("SmartPipe.Core", "SmartPipe.Extensions.Json")]
    [InlineData("SmartPipe.Extensions.Http", "SmartPipe.Extensions.Polly")]
    [InlineData("SmartPipe.Extensions.HealthChecks", "SmartPipe.Extensions.Hosting")]
    public void InvariantEdgesCannotBeLegalizedByPolicy(string packageId, string dependency)
    {
        Assert.True(PackageGraphLoader.IsInvariantForbidden(packageId, dependency));
    }

    [Fact]
    public void AssetsProjectReferencesMustMatchEvaluatedProjectReferences()
    {
        var violations = PackageGraphValidator.ValidateRestoredProjectReferences(
            "SmartPipe.Extensions.Json", ["SmartPipe.Core"], ["SmartPipe.Wrong"]);
        Assert.Contains(violations, x => x.Code == "SPGRAPH054" && x.Dependency == "SmartPipe.Core");
        Assert.Contains(violations, x => x.Code == "SPGRAPH055" && x.Dependency == "SmartPipe.Wrong");
    }

    [Theory]
    [InlineData("README.md", true)]
    [InlineData("icon.png", true)]
    [InlineData("lib/net10.0/SmartPipe.Testing.dll", true)]
    [InlineData("package/services/metadata/core-properties/a.psmdcp", true)]
    [InlineData("tools/install.ps1", false)]
    [InlineData("lib/net9.0/SmartPipe.Core.dll", false)]
    [InlineData("tests/Fixture.dll", false)]
    [InlineData("src/Foo.cs", false)]
    public void NupkgContentUsesExactAllowlist(string path, bool allowed)
    {
        Assert.Equal(allowed, PackageContentValidator.IsAllowedPackagePath("SmartPipe.Testing", path));
    }
}
