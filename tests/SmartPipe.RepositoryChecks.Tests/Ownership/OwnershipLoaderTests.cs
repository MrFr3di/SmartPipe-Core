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

    [Fact]
    public async Task CanonicalManifestQuarantinesLegacyDiTypesInFacade()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var graph = await new PackageGraphLoader().LoadAsync(root, "eng/package-graph.json", TestContext.Current.CancellationToken);
        var ownership = await new OwnershipLoader().LoadAsync(root, "eng/package-ownership.json", graph, TestContext.Current.CancellationToken);
        var assignments = ownership.Assignments.ToDictionary(x => x.TypePattern, StringComparer.Ordinal);

        foreach (var (pattern, epic) in new[]
        {
            ("SmartPipe.Extensions.ISmartPipeDefinition*", "SP220-03"),
            ("SmartPipe.Extensions.ISmartPipeFactory*", "SP220-03"),
            ("SmartPipe.Extensions.SmartPipeDefinition*", "SP220-03"),
            ("SmartPipe.Extensions.SmartPipeFactory*", "SP220-03"),
            ("SmartPipe.Extensions.SmartPipeHosted*", "SP220-04"),
            ("SmartPipe.Extensions.ISmartPipeRunHealthMonitor*", "SP220-05"),
            ("SmartPipe.Extensions.SmartPipeHealth*", "SP220-05"),
            ("SmartPipe.Extensions.SmartPipeRunHealthMonitor*", "SP220-05"),
        })
        {
            var assignment = assignments[pattern];
            Assert.Equal("SmartPipe.Extensions", assignment.CurrentImplementationAssembly);
            Assert.Equal("SmartPipe.Extensions", assignment.TargetImplementationAssembly);
            Assert.Equal("SmartPipe.Extensions", assignment.CompatibilityAssembly);
            Assert.Equal(OwnershipStrategy.ObsoleteWrapper, assignment.Strategy);
            Assert.Equal(epic, assignment.MigrationEpic);
        }
    }
}
