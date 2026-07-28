using SmartPipe.RepositoryChecks.Ownership;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Ownership;

public sealed class OwnershipLoaderTests
{
    [Fact]
    public async Task LoaderRejectsUnknownPropertyAndCrLf()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var graph = await new PackageGraphLoader().LoadAsync(root, "eng/package-graph.json", TestContext.Current.CancellationToken);
        var canonical = await File.ReadAllTextAsync(Path.Combine(root, "eng/package-ownership.json"), TestContext.Current.CancellationToken);
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("ownership.json", canonical.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1,\n  \"unknown\": true,"));
        var unknown = await Assert.ThrowsAsync<OwnershipException>(() => new OwnershipLoader().LoadAsync(fixture.Path, "ownership.json", graph, TestContext.Current.CancellationToken));
        Assert.Equal("SPOWN010", unknown.Code);
        fixture.Write("ownership.json", canonical.Replace("\n", "\r\n", StringComparison.Ordinal));
        var crlf = await Assert.ThrowsAsync<OwnershipException>(() => new OwnershipLoader().LoadAsync(fixture.Path, "ownership.json", graph, TestContext.Current.CancellationToken));
        Assert.Equal("SPOWN010", crlf.Code);
    }

    [Fact]
    public async Task CanonicalManifestHasNoBroadCatchAll()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var graph = await new PackageGraphLoader().LoadAsync(root, "eng/package-graph.json", TestContext.Current.CancellationToken);
        var ownership = await new OwnershipLoader().LoadAsync(root, "eng/package-ownership.json", graph, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(ownership.Assignments, x => x.TypePattern is "SmartPipe.Extensions.*" or "SmartPipe.*");
    }
}
