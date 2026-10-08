using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.PackageGraph;

public sealed class RepositoryVersionCatalogTests
{
    [Fact]
    public void Load_AcceptsOnlyTwoUnconditionalVersionProperties()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("eng/SmartPipe.Versions.props", ValidCatalog());

        var versions = RepositoryVersionCatalog.Load(repository.Path);

        Assert.Equal("2.2.1", versions.VersionPrefix);
        Assert.Equal("2.2.0", versions.PreviousStableVersion);
    }

    [Theory]
    [InlineData("conditional-group")]
    [InlineData("conditional-property")]
    [InlineData("extra-import")]
    [InlineData("extra-property")]
    [InlineData("duplicate-property")]
    [InlineData("same-version")]
    [InlineData("newer-previous")]
    [InlineData("noncanonical-version")]
    public void Load_RejectsCatalogThatCouldDisagreeWithMsBuild(string mutation)
    {
        using var repository = new RepositoryTestDirectory();
        var content = mutation switch
        {
            "conditional-group" => ValidCatalog().Replace("<PropertyGroup>", "<PropertyGroup Condition=\"'$(Configuration)' == 'Never'\">", StringComparison.Ordinal),
            "conditional-property" => ValidCatalog().Replace("<SmartPipeVersionPrefix>", "<SmartPipeVersionPrefix Condition=\"'$(Configuration)' == 'Never'\">", StringComparison.Ordinal),
            "extra-import" => ValidCatalog().Replace("</Project>", "<Import Project=\"other.props\" /></Project>", StringComparison.Ordinal),
            "extra-property" => ValidCatalog().Replace("</PropertyGroup>", "<Other>ignored</Other></PropertyGroup>", StringComparison.Ordinal),
            "duplicate-property" => ValidCatalog().Replace("</PropertyGroup>", "<SmartPipeVersionPrefix>2.2.1</SmartPipeVersionPrefix></PropertyGroup>", StringComparison.Ordinal),
            "same-version" => ValidCatalog().Replace("<SmartPipePreviousStableVersion>2.2.0</SmartPipePreviousStableVersion>", "<SmartPipePreviousStableVersion>2.2.1</SmartPipePreviousStableVersion>", StringComparison.Ordinal),
            "newer-previous" => ValidCatalog().Replace("<SmartPipePreviousStableVersion>2.2.0</SmartPipePreviousStableVersion>", "<SmartPipePreviousStableVersion>2.3.0</SmartPipePreviousStableVersion>", StringComparison.Ordinal),
            "noncanonical-version" => ValidCatalog().Replace("<SmartPipeVersionPrefix>2.2.1</SmartPipeVersionPrefix>", "<SmartPipeVersionPrefix>2.02.1</SmartPipeVersionPrefix>", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        repository.Write("eng/SmartPipe.Versions.props", content);

        var error = Assert.Throws<PackageGraphException>(() => RepositoryVersionCatalog.Load(repository.Path));

        Assert.Equal("SPGRAPH019", error.Code);
    }

    private static string ValidCatalog() => """
        <Project>
          <PropertyGroup>
            <SmartPipeVersionPrefix>2.2.1</SmartPipeVersionPrefix>
            <SmartPipePreviousStableVersion>2.2.0</SmartPipePreviousStableVersion>
          </PropertyGroup>
        </Project>
        """;
}
