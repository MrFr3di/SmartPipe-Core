using SmartPipe.RepositoryChecks.Commands;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Scaffolding;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Scaffolding;

public sealed class ScaffoldPackageCommandTests
{
    [Fact]
    public async Task DryRun_RejectsTheNowActiveTestingPackage()
    {
        var root = RepositoryRoot(); var graph = await new PackageGraphLoader().LoadAsync(root, "eng/package-graph.json", TestContext.Current.CancellationToken);
        var command = new ScaffoldPackageCommand();
        Assert.DoesNotContain(graph.Packages, node => node.Lifecycle == PackageLifecycle.Planned);
        var error = await Assert.ThrowsAsync<ScaffoldException>(() => command.ExecuteAsync(
            new(root, "SmartPipe.Testing", true, null), TestContext.Current.CancellationToken));
        Assert.Equal("SPSCAF002", error.Code);
    }

    [Fact]
    public async Task Write_CollisionAndThirdWriteFailureLeaveNoTargets()
    {
        using var fixture = new RepositoryTestDirectory();
        var root = RepositoryRoot(); var graph = await new PackageGraphLoader().LoadAsync(root, "eng/package-graph.json", TestContext.Current.CancellationToken);
        var node = graph.Packages.Single(x => x.Id == "SmartPipe.Testing") with
        {
            Lifecycle = PackageLifecycle.Planned,
            ScaffoldKind = PackageScaffoldKind.Testing,
        };
        graph = graph with { Packages = graph.Packages.Select(item => item.Id == node.Id ? node : item).ToArray() };
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
