using SmartPipe.RepositoryChecks.Commands;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Packaging;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Packaging;

public sealed class PackPackagesCommandTests
{
    [Fact]
    public async Task Current_PacksGraphNodesTopologicallyWithArgumentListAndImmutableManifest()
    {
        using var fixture = CreateRepository();
        var runner = new FakePackRunner();
        var manifest = await new PackPackagesCommand(runner).ExecuteAsync(new(
            fixture.Path, PackageGraphMode.Current, "Release", "2.2.0",
            Path.Combine(fixture.Path, "artifacts/packages"), Path.Combine(fixture.Path, "artifacts/packages/manifest.json")), TestContext.Current.CancellationToken);
        Assert.Equal(["SmartPipe.Core", "SmartPipe.Extensions.DependencyInjection", "SmartPipe.Extensions.Hosting", "SmartPipe.Extensions.Json", "SmartPipe.Extensions"], manifest.Packages.Select(x => x.Id));
        Assert.Equal([1, 14, 16, 5, 19], manifest.Packages.Select(x => x.PublishOrder));
        Assert.All(manifest.Packages, item => { Assert.Equal(64, item.NupkgSha256.Length); Assert.Equal(64, item.SnupkgSha256.Length); Assert.DoesNotContain('\\', item.NupkgPath); });
        Assert.Equal(5, runner.Requests.Count);
        Assert.All(runner.Requests, request =>
        {
            Assert.Equal("dotnet", request.FileName); Assert.Equal(fixture.Path, request.WorkingDirectory);
            Assert.Contains("--no-build", request.Arguments); Assert.Contains("--no-restore", request.Arguments);
            Assert.DoesNotContain(request.Arguments, x => x.Contains("&&", StringComparison.Ordinal) || x.Contains(";", StringComparison.Ordinal));
        });
        var bytes = await File.ReadAllBytesAsync(Path.Combine(fixture.Path, "artifacts/packages/manifest.json"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain((byte)'\r', bytes);
        await Assert.ThrowsAsync<PackagePackException>(() => new PackPackagesCommand(runner).ExecuteAsync(new(
            fixture.Path, PackageGraphMode.Current, "Release", "2.2.0", Path.Combine(fixture.Path, "artifacts/packages"), Path.Combine(fixture.Path, "artifacts/packages/manifest.json")), TestContext.Current.CancellationToken));
    }

    private static RepositoryTestDirectory CreateRepository()
    {
        var fixture = new RepositoryTestDirectory();
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        fixture.Write("eng/package-graph.json", File.ReadAllText(Path.Combine(root, "eng/package-graph.json")));
        fixture.Write("src/SmartPipe.Core/SmartPipe.Core.csproj", "<Project />");
        fixture.Write("src/SmartPipe.Extensions.Json/SmartPipe.Extensions.Json.csproj", "<Project />");
        fixture.Write("src/SmartPipe.Extensions.DependencyInjection/SmartPipe.Extensions.DependencyInjection.csproj", "<Project />");
        fixture.Write("src/SmartPipe.Extensions.Hosting/SmartPipe.Extensions.Hosting.csproj", "<Project />");
        fixture.Write("src/SmartPipe.Extensions/SmartPipe.Extensions.csproj", "<Project />");
        return fixture;
    }

    private sealed class FakePackRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var project = request.Arguments[1]; var id = Path.GetFileNameWithoutExtension(project);
            var version = request.Arguments.Single(x => x.StartsWith("-p:PackageVersion=", StringComparison.Ordinal)).Split('=')[1];
            var output = request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "--output") + 1];
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, $"{id}.{version}.nupkg"), id + " package");
            File.WriteAllText(Path.Combine(output, $"{id}.{version}.snupkg"), id + " symbols");
            return Task.FromResult(new ProcessResult(0, "packed", ""));
        }
    }
}
