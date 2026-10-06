using System.Text.Json;
using SmartPipe.RepositoryChecks.PackageGraph;

namespace SmartPipe.RepositoryChecks.Tests.PackageGraph;

[Trait("Category", "PackageInfrastructure")]
public sealed class Sp22007ActivationContractTests
{
    private static readonly string[] PackageIds =
    [
        "SmartPipe.Extensions.Channels",
        "SmartPipe.Extensions.Transforms",
        "SmartPipe.Extensions.Logging",
        "SmartPipe.Extensions.DataAnnotations",
    ];

    [Fact]
    public async Task LeafPackagesAreActiveWithExactDependencyEdges()
    {
        var root = RepositoryRoot();
        var graph = await new PackageGraphLoader().LoadAsync(
            root,
            "eng/package-graph.json",
            TestContext.Current.CancellationToken);

        foreach (var id in PackageIds)
        {
            var package = Assert.Single(graph.Packages, item => item.Id == id);
            Assert.Equal(PackageLifecycle.Active, package.Lifecycle);
            Assert.Null(package.ScaffoldKind);
            Assert.True(File.Exists(Path.Combine(root, package.ProjectPath)));
        }

        Assert.Equal(["SmartPipe.Core"], Assert.Single(graph.Packages, item => item.Id == PackageIds[0]).CurrentDependencies.RequiredSmartPipePackages);
        Assert.Equal(["SmartPipe.Core"], Assert.Single(graph.Packages, item => item.Id == PackageIds[1]).CurrentDependencies.RequiredSmartPipePackages);
        Assert.Equal(["SmartPipe.Core"], Assert.Single(graph.Packages, item => item.Id == PackageIds[2]).CurrentDependencies.RequiredSmartPipePackages);
        Assert.Equal(["SmartPipe.Core", "SmartPipe.Extensions.Transforms"], Assert.Single(graph.Packages, item => item.Id == PackageIds[3]).CurrentDependencies.RequiredSmartPipePackages);
        Assert.Equal(["Microsoft.Extensions.Logging.Abstractions"], Assert.Single(graph.Packages, item => item.Id == PackageIds[2]).CurrentDependencies.AllowedExternalPackages);
    }

    [Fact]
    public void OwnershipManifestUsesTypeForwardingForEveryMovedCluster()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "eng/package-ownership.json")));
        var assignments = document.RootElement.GetProperty("assignments").EnumerateArray()
            .ToDictionary(item => item.GetProperty("typePattern").GetString()!, StringComparer.Ordinal);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SmartPipe.Extensions.ChannelMerge*"] = "SmartPipe.Extensions.Channels",
            ["SmartPipe.Extensions.Transforms.CompositeTransform*"] = "SmartPipe.Extensions.Transforms",
            ["SmartPipe.Extensions.Transforms.ConditionalTransform*"] = "SmartPipe.Extensions.Transforms",
            ["SmartPipe.Extensions.Transforms.FilterTransform*"] = "SmartPipe.Extensions.Transforms",
            ["SmartPipe.Extensions.Transforms.ValidationTransform*"] = "SmartPipe.Extensions.DataAnnotations",
            ["SmartPipe.Extensions.Transforms.FilterValidationExtensions*"] = "SmartPipe.Extensions.DataAnnotations",
            ["SmartPipe.Extensions.Sinks.LoggerSink*"] = "SmartPipe.Extensions.Logging",
        };

        foreach (var (pattern, target) in expected)
        {
            var assignment = Assert.Contains(pattern, assignments);
            Assert.Equal(target, assignment.GetProperty("targetImplementationAssembly").GetString());
            Assert.Equal("type-forward", assignment.GetProperty("strategy").GetString());
            Assert.Equal("SP220-07", assignment.GetProperty("migrationEpic").GetString());
        }
    }

    [Fact]
    public void ConsumerManifestActivatesTheExactDirectLeafScenarios()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "eng/consumer-scenarios.json")));
        var scenarios = document.RootElement.GetProperty("scenarios").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var expectedClosures = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["channels-direct"] = ["SmartPipe.Core", "SmartPipe.Extensions.Channels"],
            ["transforms-direct"] = ["SmartPipe.Core", "SmartPipe.Extensions.Transforms"],
            ["logging-direct"] = ["SmartPipe.Core", "SmartPipe.Extensions.Logging"],
            ["data-annotations-direct"] = ["SmartPipe.Core", "SmartPipe.Extensions.Transforms", "SmartPipe.Extensions.DataAnnotations"],
            ["data-annotations-runtime"] = ["SmartPipe.Core", "SmartPipe.Extensions.Transforms", "SmartPipe.Extensions.DataAnnotations"],
        };
        var expectedDirectPackages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channels-direct"] = "SmartPipe.Extensions.Channels",
            ["transforms-direct"] = "SmartPipe.Extensions.Transforms",
            ["logging-direct"] = "SmartPipe.Extensions.Logging",
            ["data-annotations-direct"] = "SmartPipe.Extensions.DataAnnotations",
            ["data-annotations-runtime"] = "SmartPipe.Extensions.DataAnnotations",
        };

        foreach (var (id, dependencies) in expectedClosures)
        {
            var scenario = Assert.Contains(id, scenarios);
            Assert.Equal("current", scenario.GetProperty("set").GetString());
            Assert.Equal([expectedDirectPackages[id]], scenario.GetProperty("packageIds").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(dependencies, scenario.GetProperty("expectedSmartPipeDependencies").EnumerateArray().Select(item => item.GetString()));
            Assert.Contains("SmartPipe.Extensions", scenario.GetProperty("forbiddenDependencies").EnumerateArray().Select(item => item.GetString()));
            Assert.True(scenario.GetProperty("runSecondLockedRestore").GetBoolean());
            Assert.True(File.Exists(Path.Combine(RepositoryRoot(), scenario.GetProperty("templatePath").GetString()!)));
        }
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
}
