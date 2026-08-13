using SmartPipe.RepositoryChecks.Commands;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Scaffolding;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Scaffolding;

public sealed class ScaffoldPackageCommandTests
{
    [Fact]
    public async Task DryRun_AllThirteenPlannedIds_WritesNothing()
    {
        var root = RepositoryRoot(); var graph = await new PackageGraphLoader().LoadAsync(root, "eng/package-graph.json", TestContext.Current.CancellationToken);
        var command = new ScaffoldPackageCommand();
        foreach (var node in graph.Packages.Where(x => x.Lifecycle == PackageLifecycle.Planned))
        {
            var report = await command.ExecuteAsync(new(root, node.Id, true, null), TestContext.Current.CancellationToken);
            Assert.True(report.Success); Assert.Equal(node.Id, report.PackageId); Assert.All(report.Files, path => Assert.False(File.Exists(Path.Combine(root, path))));
        }
        Assert.Equal(13, graph.Packages.Count(x => x.Lifecycle == PackageLifecycle.Planned));
    }

    [Fact]
    public async Task Write_CollisionAndThirdWriteFailureLeaveNoTargets()
    {
        using var fixture = new RepositoryTestDirectory();
        var root = RepositoryRoot(); var graph = await new PackageGraphLoader().LoadAsync(root, "eng/package-graph.json", TestContext.Current.CancellationToken);
        var node = graph.Packages.Single(x => x.Id == "SmartPipe.Extensions.Csv");
        var plan = new PackageTemplateRenderer(root).Render(graph, node);
        fixture.Write(plan.Files[0].RelativePath, "collision");
        var collision = await Assert.ThrowsAsync<ScaffoldException>(() => new AtomicFileWriter().WriteAsync(fixture.Path, plan.Files, TestContext.Current.CancellationToken));
        Assert.Equal("SPSCAF004", collision.Code);

        using var rollback = new RepositoryTestDirectory();
        var failure = await Assert.ThrowsAsync<IOException>(() => new AtomicFileWriter(writeFailureAt: 3).WriteAsync(rollback.Path, plan.Files, TestContext.Current.CancellationToken));
        Assert.NotNull(failure);
        Assert.All(plan.Files, file => Assert.False(File.Exists(Path.Combine(rollback.Path, file.RelativePath))));
    }

    [Fact]
    public async Task Write_PathTraversalIsRejectedBeforeAnyWrite()
    {
        using var fixture = new RepositoryTestDirectory();
        var error = await Assert.ThrowsAsync<ScaffoldException>(() => new AtomicFileWriter().WriteAsync(
            fixture.Path, [new("safe/file.txt", "safe"), new("../escape.txt", "escape")], TestContext.Current.CancellationToken));
        Assert.Equal("SPSCAF003", error.Code);
        Assert.False(File.Exists(Path.Combine(fixture.Path, "safe", "file.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "..", "escape.txt")));
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
}
