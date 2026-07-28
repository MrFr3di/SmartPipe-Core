using SmartPipe.RepositoryChecks.Release;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Release;

public sealed class ReleaseVersionValidatorTests
{
    [Theory]
    [InlineData("v2.2.0", "2.2.0")]
    [InlineData("v2.2.0-ci.1", "2.2.0-ci.1")]
    public void ParseTagAcceptsCanonicalSemVer(string tag, string expected) => Assert.Equal(expected, ReleaseVersionValidator.ParseTag(tag));

    [Theory]
    [InlineData(" v2.2.0")]
    [InlineData("v2.2.0 ")]
    [InlineData("2.2.0")]
    [InlineData("v02.2.0")]
    [InlineData("v2.2.0+build")]
    [InlineData("v2.2")]
    public void ParseTagRejectsNonCanonicalOrBuildMetadata(string tag)
    {
        var error = Assert.Throws<ReleaseVersionException>(() => ReleaseVersionValidator.ParseTag(tag));
        Assert.Equal("SPVER001", error.Code);
    }

    [Theory]
    [InlineData("tag", "SPVER002")]
    [InlineData("project", "SPVER005")]
    [InlineData("package-version", "SPVER009")]
    [InlineData("duplicate", "SPVER006")]
    [InlineData("missing", "SPVER007")]
    [InlineData("planned-current", "SPVER010")]
    [InlineData("planned-release", "SPVER003")]
    [InlineData("casing", "SPVER008")]
    [InlineData("facade-omitted", "SPVER007")]
    [InlineData("unknown", "SPVER011")]
    public async Task GraphAndArtifactMutationHasStableDiagnostic(string mutation, string code)
    {
        using var fixture = new RepositoryTestDirectory();
        foreach (var id in new[] { "SmartPipe.Core", "SmartPipe.Extensions", "SmartPipe.Planned" }) fixture.Write($"src/{id}/{id}.csproj", "<Project />");
        Directory.CreateDirectory(Path.Combine(fixture.Path, "packages"));
        var artifacts = mutation switch
        {
            "missing" => new[] { ("Facade.nupkg", "SmartPipe.Extensions", "2.2.0") },
            "facade-omitted" => new[] { ("Core.nupkg", "SmartPipe.Core", "2.2.0") },
            "duplicate" => new[] { ("Core-a.nupkg", "SmartPipe.Core", "2.2.0"), ("Core-b.nupkg", "SmartPipe.Core", "2.2.0"), ("Facade.nupkg", "SmartPipe.Extensions", "2.2.0") },
            "planned-current" => new[] { ("Core.nupkg", "SmartPipe.Core", "2.2.0"), ("Facade.nupkg", "SmartPipe.Extensions", "2.2.0"), ("Planned.nupkg", "SmartPipe.Planned", "2.2.0") },
            "casing" => new[] { ("Core.nupkg", "smartpipe.core", "2.2.0"), ("Facade.nupkg", "SmartPipe.Extensions", "2.2.0") },
            "package-version" => new[] { ("Core.nupkg", "SmartPipe.Core", "2.1.0"), ("Facade.nupkg", "SmartPipe.Extensions", "2.2.0") },
            "unknown" => new[] { ("Core.nupkg", "SmartPipe.Core", "2.2.0"), ("Facade.nupkg", "SmartPipe.Extensions", "2.2.0"), ("Unknown.nupkg", "SmartPipe.Unknown", "2.2.0") },
            _ => new[] { ("Core.nupkg", "SmartPipe.Core", "2.2.0"), ("Facade.nupkg", "SmartPipe.Extensions", "2.2.0") },
        };
        foreach (var artifact in artifacts) File.WriteAllBytes(Path.Combine(fixture.Path, "packages", artifact.Item1), []);
        var evaluator = new FakeProjects(mutation == "project" ? "2.1.0" : "2.2.0");
        var validator = new ReleaseVersionValidator(evaluator, new FakePackages(artifacts));
        var mode = mutation == "planned-release" ? PackageGraphMode.Release : PackageGraphMode.Current;
        var result = await validator.ValidateAsync(Graph(), mutation == "tag" ? "v2.1.0" : "v2.2.0", mode, fixture.Path, Path.Combine(fixture.Path, "packages"), TestContext.Current.CancellationToken);
        Assert.Contains(result.Violations, x => x.Code == code);
    }

    [Fact]
    public async Task PrereleaseTagUsesBaseProjectVersionAndExactPackageVersion()
    {
        using var fixture = new RepositoryTestDirectory();
        foreach (var id in new[] { "SmartPipe.Core", "SmartPipe.Extensions" }) { fixture.Write($"src/{id}/{id}.csproj", "<Project />"); }
        Directory.CreateDirectory(Path.Combine(fixture.Path, "packages"));
        var artifacts = new[] { ("Core.nupkg", "SmartPipe.Core", "2.2.0-ci.1"), ("Facade.nupkg", "SmartPipe.Extensions", "2.2.0-ci.1") };
        foreach (var item in artifacts) File.WriteAllBytes(Path.Combine(fixture.Path, "packages", item.Item1), []);
        var result = await new ReleaseVersionValidator(new FakeProjects("2.2.0", "2.2.0-ci.1"), new FakePackages(artifacts))
            .ValidateAsync(Graph(), "v2.2.0-ci.1", PackageGraphMode.Current, fixture.Path, Path.Combine(fixture.Path, "packages"), TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Violations));
    }

    private static PackageGraphDocument Graph()
    {
        var policy = new DependencyPolicy { RequiredSmartPipePackages = [], AllowedSmartPipePackages = [], AllowedExternalPackages = [], ForbiddenPackagePatterns = [] };
        PackageNode Node(string id, PackageLifecycle lifecycle, int order) => new() { Id = id, ProjectPath = $"src/{id}/{id}.csproj", Lifecycle = lifecycle, ActivationEpic = lifecycle == PackageLifecycle.Planned ? "SP-X" : "existing", ScaffoldKind = lifecycle == PackageLifecycle.Planned ? PackageScaffoldKind.CoreLeaf : null, PublishOrder = order, BaselineVersion = lifecycle == PackageLifecycle.Planned ? null : "2.1.2", AotContract = PackageAotContract.Full, CurrentDependencies = policy, ReleaseDependencies = policy, TemporaryAllowances = [], ConsumerScenarios = [] };
        return new() { SchemaVersion = 1, ReleaseVersion = "2.2.0", Packages = [Node("SmartPipe.Core", PackageLifecycle.Active, 1), Node("SmartPipe.Planned", PackageLifecycle.Planned, 2), Node("SmartPipe.Extensions", PackageLifecycle.CompatibilityFacade, 3)] };
    }

    private sealed class FakeProjects(string version, string? packageVersion = null) : IEvaluatedProjectReader
    {
        public Task<EvaluatedProject> ReadAsync(string path, CancellationToken ct) => Task.FromResult(new EvaluatedProject(path, Path.GetFileNameWithoutExtension(path), version, packageVersion ?? version, ["net10.0"], true, true, [], [], "2.1.2", true, "README.md", "README.md", "icon.png"));
    }
    private sealed class FakePackages(IEnumerable<(string File, string Id, string Version)> packages) : IPackedNuspecReader
    {
        private readonly Dictionary<string, PackedPackageModel> _items = packages.ToDictionary(x => x.File, x => new PackedPackageModel(x.Id, x.Version, [], [], []), StringComparer.OrdinalIgnoreCase);
        public Task<PackedPackageModel> ReadAsync(string path, CancellationToken ct) => Task.FromResult(_items[Path.GetFileName(path)]);
    }
}
