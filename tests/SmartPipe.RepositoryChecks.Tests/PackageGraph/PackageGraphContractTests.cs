using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Tests.Repository;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace SmartPipe.RepositoryChecks.Tests.PackageGraph;

[Trait("Category", "PackageInfrastructure")]
[Trait("Category", "Mutation")]
public sealed class PackageGraphContractTests
{
    [Fact]
    public async Task RepositoryGraph_HealthChecksPackageActivationIsComplete()
    {
        var root = RepositoryRoot();
        var graph = await new PackageGraphLoader().LoadAsync(
            root,
            "eng/package-graph.json",
            TestContext.Current.CancellationToken);
        var package = Assert.Single(
            graph.Packages,
            node => node.Id == "SmartPipe.Extensions.HealthChecks");
        const string testProject = "tests/SmartPipe.Extensions.HealthChecks.Tests/SmartPipe.Extensions.HealthChecks.Tests.csproj";

        Assert.Equal(PackageLifecycle.Active, package.Lifecycle);
        Assert.True(File.Exists(Path.Combine(root, package.ProjectPath)), package.ProjectPath);
        Assert.True(File.Exists(Path.Combine(root, testProject)), testProject);

        var solutionProjects = XDocument.Load(Path.Combine(root, "SmartPipe.Core.slnx"))
            .Descendants("Project")
            .Select(element => (string?)element.Attribute("Path"))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(package.ProjectPath, solutionProjects);
        Assert.Contains(testProject, solutionProjects);
    }

    [Fact]
    public async Task Loader_RejectsUnknownProperty()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("eng/package-graph.json", MinimalJson().Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\n  \"unknown\": true,"));

        var exception = await Assert.ThrowsAsync<PackageGraphException>(() =>
            new PackageGraphLoader(false).LoadAsync(repository.Path, "eng/package-graph.json", TestContext.Current.CancellationToken));

        Assert.Equal("SPGRAPH001", exception.Code);
    }

    [Fact]
    public async Task Loader_RejectsDuplicateProperty()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("eng/package-graph.json", MinimalJson().Replace(
            "\"schemaVersion\": 1,", "\"schemaVersion\": 1,\n  \"schemaVersion\": 1,"));
        var exception = await Assert.ThrowsAsync<PackageGraphException>(() =>
            new PackageGraphLoader(false).LoadAsync(repository.Path, "eng/package-graph.json", TestContext.Current.CancellationToken));
        Assert.Equal("SPGRAPH001", exception.Code);
    }

    [Theory]
    [InlineData("duplicate-id", "SPGRAPH004")]
    [InlineData("duplicate-order", "SPGRAPH005")]
    [InlineData("invalid-enum", "SPGRAPH001")]
    [InlineData("active-missing", "SPGRAPH008")]
    [InlineData("planned-missing-epic", "SPGRAPH009")]
    [InlineData("planned-baseline", "SPGRAPH010")]
    [InlineData("planned-missing-scaffold-kind", "SPGRAPH017")]
    [InlineData("active-scaffold-kind", "SPGRAPH017")]
    [InlineData("absolute-path", "SPGRAPH006")]
    [InlineData("self-dependency", "SPGRAPH011")]
    [InlineData("allowance-missing-evidence", "SPGRAPH012")]
    [InlineData("unordered", "SPGRAPH005")]
    public async Task Loader_RejectsSemanticMutationWithStableCode(string mutation, string expectedCode)
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("src/SmartPipe.Core/SmartPipe.Core.csproj", "<Project />");
        repository.Write("src/SmartPipe.Extensions.Json/SmartPipe.Extensions.Json.csproj", "<Project />");
        repository.Write("src/SmartPipe.Extensions.Channels/SmartPipe.Extensions.Channels.csproj", "<Project />");
        repository.Write("eng/package-graph.json", Mutate(MinimalJson(), mutation));
        var exception = await Assert.ThrowsAsync<PackageGraphException>(() =>
            new PackageGraphLoader(false).LoadAsync(repository.Path, "eng/package-graph.json", TestContext.Current.CancellationToken));
        Assert.True(expectedCode == exception.Code, exception.InnerException?.ToString() ?? exception.Message);
    }

    [Fact]
    public async Task Loader_RejectsNonCanonicalPackageOrder()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("src/SmartPipe.Core/SmartPipe.Core.csproj", "<Project />");
        repository.Write("eng/package-graph.json", MinimalJson().Replace("\"publishOrder\": 1", "\"publishOrder\": 2"));

        var exception = await Assert.ThrowsAsync<PackageGraphException>(() =>
            new PackageGraphLoader(false).LoadAsync(repository.Path, "eng/package-graph.json", TestContext.Current.CancellationToken));

        Assert.Equal("SPGRAPH002", exception.Code);
    }

    [Fact]
    public void Sorter_IsDeterministicAndReportsExactCyclePath()
    {
        var order = TopologicalPackageSorter.Sort(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["B"] = ["A"],
            ["C"] = ["A"],
            ["A"] = [],
        });
        Assert.Equal(["A", "B", "C"], order);

        var exception = Assert.Throws<PackageGraphException>(() =>
            TopologicalPackageSorter.Sort(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["A"] = ["B"],
                ["B"] = ["C"],
                ["C"] = ["A"],
            }));
        Assert.Equal("SPGRAPH020", exception.Code);
        Assert.Contains("A -> B -> C -> A", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loader_RejectsCyclePresentOnlyInCurrentPolicy()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("src/SmartPipe.Extensions.Json/SmartPipe.Extensions.Json.csproj", "<Project />");
        repository.Write("src/SmartPipe.Extensions.Channels/SmartPipe.Extensions.Channels.csproj", "<Project />");
        var empty = new DependencyPolicy { RequiredSmartPipePackages = [], AllowedSmartPipePackages = [], AllowedExternalPackages = [], ForbiddenPackagePatterns = [] };
        var first = new PackageNode
        {
            Id = "SmartPipe.Extensions.Json",
            ProjectPath = "src/SmartPipe.Extensions.Json/SmartPipe.Extensions.Json.csproj",
            Lifecycle = PackageLifecycle.Active,
            ActivationEpic = "existing",
            ScaffoldKind = null,
            PublishOrder = 1,
            BaselineVersion = "2.1.2",
            AotContract = PackageAotContract.Full,
            CurrentDependencies = empty with { RequiredSmartPipePackages = ["SmartPipe.Extensions.Channels"] },
            ReleaseDependencies = empty,
            TemporaryAllowances = [],
            ConsumerScenarios = [],
        };
        var second = new PackageNode
        {
            Id = "SmartPipe.Extensions.Channels",
            ProjectPath = "src/SmartPipe.Extensions.Channels/SmartPipe.Extensions.Channels.csproj",
            Lifecycle = PackageLifecycle.Active,
            ActivationEpic = "existing",
            ScaffoldKind = null,
            PublishOrder = 2,
            BaselineVersion = "2.1.2",
            AotContract = PackageAotContract.Full,
            CurrentDependencies = empty with { RequiredSmartPipePackages = ["SmartPipe.Extensions.Json"] },
            ReleaseDependencies = empty,
            TemporaryAllowances = [],
            ConsumerScenarios = [],
        };
        var graph = new PackageGraphDocument { SchemaVersion = 1, ReleaseVersion = "2.2.0", Packages = [first, second] };
        repository.Write("eng/package-graph.json", CanonicalJson.Serialize(graph, RepositoryChecksJsonContext.Default.PackageGraphDocument));

        var exception = await Assert.ThrowsAsync<PackageGraphException>(() => new PackageGraphLoader(false).LoadAsync(
            repository.Path, "eng/package-graph.json", TestContext.Current.CancellationToken));
        Assert.Equal("SPGRAPH020", exception.Code);
    }

    internal static string MinimalCatalogForTests() => MinimalJson();

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static string MinimalJson() => """
        {
          "schemaVersion": 1,
          "releaseVersion": "2.2.0",
          "packages": [
            {
              "id": "SmartPipe.Core",
              "projectPath": "src/SmartPipe.Core/SmartPipe.Core.csproj",
              "lifecycle": "active",
              "activationEpic": "existing",
              "scaffoldKind": null,
              "publishOrder": 1,
              "baselineVersion": "2.1.2",
              "aotContract": "full",
              "currentDependencies": { "requiredSmartPipePackages": [], "allowedSmartPipePackages": [], "allowedExternalPackages": [], "forbiddenPackagePatterns": [] },
              "releaseDependencies": { "requiredSmartPipePackages": [], "allowedSmartPipePackages": [], "allowedExternalPackages": [], "forbiddenPackagePatterns": [] },
              "temporaryAllowances": [],
              "consumerScenarios": []
            }
          ]
        }
        """;

    private static string Mutate(string json, string mutation)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var packages = root["packages"]!.AsArray();
        var first = packages[0]!.AsObject();
        switch (mutation)
        {
            case "duplicate-id": AddSecond(2, duplicateId: true); break;
            case "duplicate-order": AddSecond(1); break;
            case "invalid-enum": first["lifecycle"] = "retired"; break;
            case "active-missing": first["projectPath"] = "src/Missing/Missing.csproj"; break;
            case "planned-missing-epic": first["lifecycle"] = "planned"; first["activationEpic"] = null; first["scaffoldKind"] = "core-leaf"; first["baselineVersion"] = null; break;
            case "planned-baseline": first["lifecycle"] = "planned"; first["activationEpic"] = "SP220-X"; first["scaffoldKind"] = "core-leaf"; break;
            case "planned-missing-scaffold-kind": first["lifecycle"] = "planned"; first["activationEpic"] = "SP220-X"; first["baselineVersion"] = null; break;
            case "active-scaffold-kind": first["scaffoldKind"] = "core-leaf"; break;
            case "absolute-path": first["projectPath"] = Path.GetFullPath("outside.csproj"); break;
            case "self-dependency": first["currentDependencies"]!["requiredSmartPipePackages"] = new JsonArray("SmartPipe.Core"); break;
            case "allowance-missing-evidence":
                first["temporaryAllowances"] = new JsonArray(new JsonObject
                { ["dependency"] = "X", ["reason"] = "r", ["ownerEpic"] = "", ["expiresBeforeRelease"] = true, ["evidence"] = "" }); break;
            case "unordered": first["publishOrder"] = 3; AddSecond(2); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            .Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

        void AddSecond(int order, bool duplicateId = false)
        {
            var second = first.DeepClone().AsObject();
            if (!duplicateId) second["id"] = "SmartPipe.Extensions.Json";
            second["projectPath"] = "src/SmartPipe.Extensions.Json/SmartPipe.Extensions.Json.csproj";
            second["publishOrder"] = order;
            packages.Add(second);
        }
    }
}
