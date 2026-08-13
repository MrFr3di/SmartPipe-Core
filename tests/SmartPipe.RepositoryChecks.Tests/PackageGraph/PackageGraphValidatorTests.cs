using SmartPipe.RepositoryChecks.PackageGraph;

namespace SmartPipe.RepositoryChecks.Tests.PackageGraph;

public sealed class PackageGraphValidatorTests
{
    [Fact]
    public async Task HealthChecksProject_RejectsHostingReference()
    {
        var root = RepositoryRoot();
        var graph = await new PackageGraphLoader().LoadAsync(
            root,
            "eng/package-graph.json",
            TestContext.Current.CancellationToken);
        var node = Assert.Single(
            graph.Packages,
            package => package.Id == "SmartPipe.Extensions.HealthChecks");
        var project = new EvaluatedProject(
            FullPath(root, node.ProjectPath),
            node.Id,
            graph.ReleaseVersion,
            graph.ReleaseVersion,
            ["net10.0"],
            true,
            true,
            [
                new("Microsoft.Extensions.DependencyInjection.Abstractions", null, null, null),
                new("Microsoft.Extensions.Diagnostics.HealthChecks", null, null, null),
                new("Microsoft.Extensions.Options", null, null, null),
            ],
            [
                FullPath(root, "src/SmartPipe.Core/SmartPipe.Core.csproj"),
                FullPath(root, "src/SmartPipe.Extensions.DependencyInjection/SmartPipe.Extensions.DependencyInjection.csproj"),
                FullPath(root, "src/SmartPipe.Extensions.Hosting/SmartPipe.Extensions.Hosting.csproj"),
            ],
            null,
            true,
            "README.md",
            "README.md",
            "icon.png");

        var violations = new PackageGraphValidator().ValidateProject(
            graph,
            node,
            project,
            PackageGraphMode.Current);

        Assert.Contains(
            violations,
            violation => violation.Code == "SPGRAPH048"
                && violation.Dependency == "SmartPipe.Extensions.Hosting");
    }

    [Theory]
    [InlineData("facade", "SPGRAPH049")]
    [InlineData("unknown-external", "SPGRAPH046")]
    [InlineData("missing-required", "SPGRAPH044")]
    [InlineData("wrong-project-reference", "SPGRAPH043")]
    public void ProjectMutation_FailsWithExactDiagnostic(string mutation, string code)
    {
        var root = Path.Combine(Path.GetTempPath(), "sp220-graph-tests");
        var corePath = Path.Combine(root, "src", "SmartPipe.Core", "SmartPipe.Core.csproj");
        var jsonPath = Path.Combine(root, "src", "SmartPipe.Extensions.Json", "SmartPipe.Extensions.Json.csproj");
        var facadePath = Path.Combine(root, "src", "SmartPipe.Extensions", "SmartPipe.Extensions.csproj");
        var graph = Graph();
        var node = graph.Packages[1];
        var references = mutation switch { "facade" => new[] { corePath, facadePath }, "wrong-project-reference" => new[] { corePath, Path.Combine(root, "src", "Wrong.csproj") }, "missing-required" => Array.Empty<string>(), _ => new[] { corePath } };
        var packages = mutation == "unknown-external" ? new[] { new EvaluatedPackageReference("Unknown.External", null, null, null) } : Array.Empty<EvaluatedPackageReference>();
        var project = new EvaluatedProject(jsonPath, node.Id, "2.2.0", "2.2.0", ["net10.0"], true, true, packages, references, "2.1.2", true, "README.md", "README.md", "icon.png");
        var violations = new PackageGraphValidator().ValidateProject(graph, node, project, PackageGraphMode.Current);
        Assert.Contains(violations, x => x.Code == code);
    }

    [Fact]
    public void CompatibilityFacade_ReleasePolicyRejectsUnmigratedLeafPackagesAndEmbeddedDependencies()
    {
        var root = Path.Combine(Path.GetTempPath(), "sp220-compatibility-facade-tests");
        var corePath = Path.Combine(root, "src", "SmartPipe.Core", "SmartPipe.Core.csproj");
        var jsonPath = Path.Combine(root, "src", "SmartPipe.Extensions.Json", "SmartPipe.Extensions.Json.csproj");
        var facadePath = Path.Combine(root, "src", "SmartPipe.Extensions", "SmartPipe.Extensions.csproj");
        var missingLeafPackages = new[]
        {
            "SmartPipe.Extensions.Channels",
            "SmartPipe.Extensions.Csv",
            "SmartPipe.Extensions.Dapper",
            "SmartPipe.Extensions.DataAnnotations",
            "SmartPipe.Extensions.DependencyInjection",
            "SmartPipe.Extensions.EntityFrameworkCore",
            "SmartPipe.Extensions.HealthChecks",
            "SmartPipe.Extensions.Hosting",
            "SmartPipe.Extensions.Http",
            "SmartPipe.Extensions.Http.Json",
            "SmartPipe.Extensions.Logging",
            "SmartPipe.Extensions.Mapster",
            "SmartPipe.Extensions.OpenTelemetry",
            "SmartPipe.Extensions.Polly",
            "SmartPipe.Extensions.Transforms",
        };
        var expiredAllowances = new[]
        {
            "CsvHelper",
            "Dapper",
            "Mapster",
            "Microsoft.EntityFrameworkCore",
            "Microsoft.Extensions.Diagnostics.HealthChecks",
            "Microsoft.Extensions.Hosting.Abstractions",
        };
        var disallowedEmbeddedDependencies = expiredAllowances;
        var currentDependencies = new DependencyPolicy
        {
            RequiredSmartPipePackages = ["SmartPipe.Core", "SmartPipe.Extensions.Json"],
            AllowedSmartPipePackages = [],
            AllowedExternalPackages = ["Microsoft.Extensions.Logging.Abstractions"],
            ForbiddenPackagePatterns = [],
        };
        var releaseDependencies = new DependencyPolicy
        {
            RequiredSmartPipePackages = ["SmartPipe.Core", .. missingLeafPackages, "SmartPipe.Extensions.Json"],
            AllowedSmartPipePackages = [],
            AllowedExternalPackages = ["Microsoft.Extensions.Http", "Microsoft.Extensions.Resilience", "Microsoft.Extensions.Logging.Abstractions"],
            ForbiddenPackagePatterns = [],
        };
        var allowances = new[]
        {
            Allowance("CsvHelper", true),
            Allowance("Dapper", true),
            Allowance("Mapster", true),
            Allowance("Microsoft.EntityFrameworkCore", true),
            Allowance("Microsoft.Extensions.Diagnostics.HealthChecks", true),
            Allowance("Microsoft.Extensions.Hosting.Abstractions", true),
            Allowance("Microsoft.Extensions.Http", false),
            Allowance("Microsoft.Extensions.Resilience", false),
        };
        var emptyDependencies = new DependencyPolicy { RequiredSmartPipePackages = [], AllowedSmartPipePackages = [], AllowedExternalPackages = [], ForbiddenPackagePatterns = [] };
        var graph = new PackageGraphDocument
        {
            SchemaVersion = 1,
            ReleaseVersion = "2.2.0",
            Packages =
            [
                Node("SmartPipe.Core", "src/SmartPipe.Core/SmartPipe.Core.csproj", PackageLifecycle.Active, PackageAotContract.Full, emptyDependencies, emptyDependencies, []),
                Node("SmartPipe.Extensions.Json", "src/SmartPipe.Extensions.Json/SmartPipe.Extensions.Json.csproj", PackageLifecycle.Active, PackageAotContract.Full, emptyDependencies, emptyDependencies, []),
                Node("SmartPipe.Extensions", "src/SmartPipe.Extensions/SmartPipe.Extensions.csproj", PackageLifecycle.CompatibilityFacade, PackageAotContract.NoBlanket, currentDependencies, releaseDependencies, allowances),
            ],
        };
        var embeddedDependencies = new[]
        {
            "CsvHelper",
            "Dapper",
            "Mapster",
            "Microsoft.EntityFrameworkCore",
            "Microsoft.Extensions.Diagnostics.HealthChecks",
            "Microsoft.Extensions.Hosting.Abstractions",
            "Microsoft.Extensions.Http",
            "Microsoft.Extensions.Logging.Abstractions",
            "Microsoft.Extensions.Resilience",
        };
        var project = new EvaluatedProject(
            facadePath,
            "SmartPipe.Extensions",
            "2.2.0",
            "2.2.0",
            ["net10.0"],
            true,
            true,
            embeddedDependencies.Select(id => new EvaluatedPackageReference(id, null, null, null)).ToArray(),
            [corePath, jsonPath],
            "2.1.2",
            true,
            "README.md",
            "README.md",
            "icon.png");

        var validator = new PackageGraphValidator();
        Assert.Empty(validator.ValidateProject(graph, graph.Packages[2], project, PackageGraphMode.Current));

        var releaseViolations = validator.ValidateProject(graph, graph.Packages[2], project, PackageGraphMode.Release);
        Assert.Equal(27, releaseViolations.Count);
        Assert.Equal(missingLeafPackages.OrderBy(id => id), releaseViolations.Where(x => x.Code == "SPGRAPH044").Select(x => x.Dependency).OrderBy(id => id));
        Assert.Equal(disallowedEmbeddedDependencies.OrderBy(id => id), releaseViolations.Where(x => x.Code == "SPGRAPH046").Select(x => x.Dependency).OrderBy(id => id));
        Assert.Equal(expiredAllowances.OrderBy(id => id), releaseViolations.Where(x => x.Code == "SPGRAPH047").Select(x => x.Dependency).OrderBy(id => id));

        static TemporaryDependencyAllowance Allowance(string dependency, bool expiresBeforeRelease) => new()
        {
            Dependency = dependency,
            Reason = "Characterization test allowance.",
            OwnerEpic = "SP220-01",
            ExpiresBeforeRelease = expiresBeforeRelease,
            Evidence = "Test fixture.",
        };

        static PackageNode Node(
            string id,
            string projectPath,
            PackageLifecycle lifecycle,
            PackageAotContract aotContract,
            DependencyPolicy currentDependencies,
            DependencyPolicy releaseDependencies,
            IReadOnlyList<TemporaryDependencyAllowance> temporaryAllowances) => new()
            {
                Id = id,
                ProjectPath = projectPath,
                Lifecycle = lifecycle,
                ActivationEpic = "SP220-01",
                ScaffoldKind = null,
                PublishOrder = 1,
                BaselineVersion = "2.1.2",
                AotContract = aotContract,
                CurrentDependencies = currentDependencies,
                ReleaseDependencies = releaseDependencies,
                TemporaryAllowances = temporaryAllowances,
                ConsumerScenarios = [],
            };
    }

    [Fact]
    public void PackedMutation_DetectsPromotedAndMissingDependencies()
    {
        var violations = PackageGraphValidator.ValidatePackedDependencies("SmartPipe.Core", ["Required"], ["Promoted"]);
        Assert.Contains(violations, x => x.Code == "SPGRAPH062" && x.Dependency == "Required");
        Assert.Contains(violations, x => x.Code == "SPGRAPH063" && x.Dependency == "Promoted");
    }

    private static PackageGraphDocument Graph()
    {
        var empty = new DependencyPolicy { RequiredSmartPipePackages = [], AllowedSmartPipePackages = [], AllowedExternalPackages = [], ForbiddenPackagePatterns = [] };
        PackageNode Node(string id, string path, int order, DependencyPolicy policy) => new() { Id = id, ProjectPath = path, Lifecycle = PackageLifecycle.Active, ActivationEpic = "existing", ScaffoldKind = null, PublishOrder = order, BaselineVersion = "2.1.2", AotContract = PackageAotContract.Full, CurrentDependencies = policy, ReleaseDependencies = policy, TemporaryAllowances = [], ConsumerScenarios = [] };
        return new()
        {
            SchemaVersion = 1,
            ReleaseVersion = "2.2.0",
            Packages =
        [
            Node("SmartPipe.Core", "src/SmartPipe.Core/SmartPipe.Core.csproj", 1, empty),
            Node("SmartPipe.Extensions.Json", "src/SmartPipe.Extensions.Json/SmartPipe.Extensions.Json.csproj", 2, new() { RequiredSmartPipePackages = ["SmartPipe.Core"], AllowedSmartPipePackages = [], AllowedExternalPackages = [], ForbiddenPackagePatterns = ["SmartPipe.Extensions"] }),
            Node("SmartPipe.Extensions", "src/SmartPipe.Extensions/SmartPipe.Extensions.csproj", 3, empty),
        ]
        };
    }

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static string FullPath(string root, string relativePath) =>
        Path.GetFullPath(relativePath.Replace('/', Path.DirectorySeparatorChar), root);
}
