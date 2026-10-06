using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.PackageGraph;

public sealed class AssetsGraphReaderTests
{
    [Fact]
    public async Task Read_DistinguishesPackageProjectTransitiveAndPrunedDependencies()
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("obj/project.assets.json", """
            {"version":4,"targets":{"net10.0":{"Direct/1.0.0":{"dependencies":{"Transitive":"1.0.0"}},"Transitive/1.0.0":{},"Project/1.0.0":{}}},"libraries":{"Direct/1.0.0":{"type":"package"},"Transitive/1.0.0":{"type":"package"},"Project/1.0.0":{"type":"project"}},"projectFileDependencyGroups":{"net10.0":["Direct >= 1.0.0","Project >= 1.0.0","Pruned >= 1.0.0"]},"project":{"frameworks":{"net10.0":{"dependencies":{"Direct":{"target":"Package","version":"[1.0.0, )"},"Project":{"target":"Project"},"Pruned":{"target":"Package","version":"[1.0.0, )"}},"frameworkReferences":{"Microsoft.NETCore.App":{"privateAssets":"all"}}}}}}
            """);
        var result = await new AssetsGraphReader().ReadAsync(Path.Combine(fixture.Path, "obj/project.assets.json"), TestContext.Current.CancellationToken);
        var framework = Assert.Single(result.Frameworks);
        Assert.Equal(["Direct", "Pruned"], framework.DirectPackages);
        Assert.Equal(["Project"], framework.DirectProjects);
        Assert.Equal(["Transitive"], framework.TransitivePackages);
        Assert.Equal(["Pruned"], framework.PrunedPackages);
        Assert.Equal(["Microsoft.NETCore.App"], framework.FrameworkReferences);
    }
}
