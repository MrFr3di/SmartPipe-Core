using SmartPipe.RepositoryChecks.Consumers;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Consumers;

[Trait("Category", "PackageInfrastructure")]
[Trait("Category", "Mutation")]
public sealed class ConsumerScenarioSchemaTests
{
    [Fact]
    public async Task CurrentManifest_HasExactlyTwentyEightStrictScenarios()
    {
        var root = RepositoryRoot();
        var graph = await new PackageGraphLoader().LoadAsync(root, "eng/package-graph.json", TestContext.Current.CancellationToken);
        var document = await new ConsumerScenarioLoader().LoadAsync(root, "eng/consumer-scenarios.json", graph, TestContext.Current.CancellationToken);
        Assert.Equal(28, document.Scenarios.Count);
        Assert.Equal(
            [
                "core-direct",
                "json-direct",
                "extensions-meta",
                "legacy-binary-2.1.2",
                "core-trim",
                "core-nativeaot",
                "json-nativeaot",
                "dependency-injection-direct",
                "dependency-injection-keyed",
                "dependency-injection-from-keyed-services",
                "dependency-injection-facade-source",
                "dependency-injection-facade-binary-2.1.2",
                "dependency-injection-trim",
                "dependency-injection-nativeaot",
                "hosting-direct",
                "hosting-facade-source",
                "hosting-facade-binary-2.1.2",
                "hosting-trim",
                "hosting-nativeaot",
                "health-checks-direct",
                "health-checks-aspnet",
                "health-checks-trim",
                "health-checks-nativeaot",
                "opentelemetry-direct",
                "opentelemetry-otlp",
                "opentelemetry-facade",
                "opentelemetry-trim",
                "opentelemetry-nativeaot",
            ],
            document.Scenarios.Select(x => x.Id));
        Assert.All(
            document.Scenarios.Where(scenario => scenario.Id.StartsWith("hosting-", StringComparison.Ordinal)),
            scenario => Assert.Equal("hosting", scenario.Category));
        Assert.All(
            document.Scenarios.Where(scenario => scenario.Id.StartsWith("health-checks-", StringComparison.Ordinal)),
            scenario => Assert.Equal("health-checks", scenario.Category));
        Assert.All(
            document.Scenarios.Where(scenario => scenario.Id.StartsWith("opentelemetry-", StringComparison.Ordinal)),
            scenario => Assert.Equal("opentelemetry", scenario.Category));
    }

    [Theory]
    [InlineData("duplicate", "SPCONS003")]
    [InlineData("traversal", "SPCONS005")]
    [InlineData("absolute", "SPCONS005")]
    [InlineData("unknown-package", "SPCONS006")]
    [InlineData("zero-timeout", "SPCONS007")]
    [InlineData("large-timeout", "SPCONS007")]
    [InlineData("shell-command", "SPCONS001")]
    [InlineData("unknown-mode", "SPCONS001")]
    [InlineData("baseline-missing", "SPCONS008")]
    [InlineData("required-present", "SPCONS003")]
    [InlineData("required-missing", "SPCONS001")]
    public async Task Loader_RejectsSecurityAndSemanticMutations(string mutation, string code)
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("tests/Consumers/Scenarios/fixture/Consumer.csproj", "<Project />");
        var json = ValidJson();
        json = mutation switch
        {
            "duplicate" => json.Replace("  ]", ",\n" + Scenario("fixture") + "\n  ]", StringComparison.Ordinal),
            "traversal" => json.Replace("tests/Consumers/Scenarios/fixture/Consumer.csproj", "../Consumer.csproj", StringComparison.Ordinal),
            "absolute" => json.Replace("tests/Consumers/Scenarios/fixture/Consumer.csproj", "C:/outside/Consumer.csproj", StringComparison.Ordinal),
            "unknown-package" => json.Replace("SmartPipe.Core", "SmartPipe.Unknown", StringComparison.Ordinal),
            "zero-timeout" => json.Replace("00:01:00", "00:00:00", StringComparison.Ordinal),
            "large-timeout" => json.Replace("00:01:00", "00:31:00", StringComparison.Ordinal),
            "shell-command" => json.Replace("\"set\": \"current\"", "\"set\": \"current\", \"command\": \"cmd /c evil\"", StringComparison.Ordinal),
            "unknown-mode" => json.Replace("build-and-run", "shell", StringComparison.Ordinal),
            "baseline-missing" => json.Replace("build-and-run", "binary-compatibility", StringComparison.Ordinal),
            "required-present" => json.Replace("\"requiredAtRelease\": []", "\"requiredAtRelease\": [\"fixture\"]", StringComparison.Ordinal),
            "required-missing" => json.Replace("  \"requiredAtRelease\": [],\n", string.Empty, StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        fixture.Write("eng/consumer-scenarios.json", json);
        var graphRoot = RepositoryRoot();
        var graph = await new PackageGraphLoader().LoadAsync(graphRoot, "eng/package-graph.json", TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<ConsumerScenarioException>(() => new ConsumerScenarioLoader().LoadAsync(fixture.Path, "eng/consumer-scenarios.json", FixtureGraph(graph), TestContext.Current.CancellationToken));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task Loader_AcceptsThirtyMinutePolicyBoundary()
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("tests/Consumers/Scenarios/fixture/Consumer.csproj", "<Project />");
        fixture.Write("eng/consumer-scenarios.json", ValidJson().Replace("00:01:00", "00:30:00", StringComparison.Ordinal));
        var root = RepositoryRoot();
        var graph = await new PackageGraphLoader().LoadAsync(root, "eng/package-graph.json", TestContext.Current.CancellationToken);
        var result = await new ConsumerScenarioLoader().LoadAsync(fixture.Path, "eng/consumer-scenarios.json", FixtureGraph(graph), TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromMinutes(30), result.Scenarios[0].Timeout);
    }

    [Fact]
    public async Task Loader_RejectsCurrentScenarioAbsentFromPackageGraph()
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("tests/Consumers/Scenarios/fixture/Consumer.csproj", "<Project />");
        fixture.Write("eng/consumer-scenarios.json", ValidJson());
        var root = RepositoryRoot();
        var graph = await new PackageGraphLoader().LoadAsync(root, "eng/package-graph.json", TestContext.Current.CancellationToken);
        var mutated = FixtureGraph(graph) with
        {
            Packages = graph.Packages.Select(package => package.Id == "SmartPipe.Core"
                ? package with { ConsumerScenarios = [] }
                : package).ToArray(),
        };

        var error = await Assert.ThrowsAsync<ConsumerScenarioException>(() => new ConsumerScenarioLoader()
            .LoadAsync(fixture.Path, "eng/consumer-scenarios.json", mutated, TestContext.Current.CancellationToken));

        Assert.Equal("SPCONS009", error.Code);
    }

    private static PackageGraphDocument FixtureGraph(PackageGraphDocument graph) => graph with
    {
        Packages = graph.Packages.Select(package => package with
        {
            ConsumerScenarios = package.Id == "SmartPipe.Core" ? ["fixture"] : [],
        }).ToArray(),
    };

    private static string ValidJson() => "{\n  \"schemaVersion\": 1,\n  \"requiredAtRelease\": [],\n  \"scenarios\": [\n" + Scenario("fixture") + "\n  ]\n}\n";
    private static string Scenario(string id) => $$"""
        {
          "id": "{{id}}",
          "set": "current",
          "mode": "build-and-run",
          "templatePath": "tests/Consumers/Scenarios/fixture/Consumer.csproj",
          "packageIds": ["SmartPipe.Core"],
          "expectedSmartPipeDependencies": ["SmartPipe.Core"],
          "forbiddenDependencies": [],
          "baselineVersion": null,
          "timeout": "00:01:00",
          "runSecondLockedRestore": true
        }
        """;
    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
}
