using SmartPipe.RepositoryChecks.Ownership;
using SmartPipe.RepositoryChecks.PackageGraph;

namespace SmartPipe.RepositoryChecks.Tests.Ownership;

public sealed class OwnershipValidatorTests
{
    [Theory]
    [InlineData("unknown", "SPOWN014")]
    [InlineData("core-to-extensions", "SPOWN015")]
    [InlineData("http-to-http", "SPOWN016")]
    [InlineData("epic", "SPOWN017")]
    public void ManifestSemanticMutationFails(string mutation, string code)
    {
        var pattern = mutation == "core-to-extensions" ? "SmartPipe.Core.Foo" : mutation == "http-to-http" ? "SmartPipe.Extensions.Selectors.HttpSelector*" : "SmartPipe.Extensions.Foo";
        var target = mutation switch { "unknown" => "SmartPipe.Unknown", "core-to-extensions" => "SmartPipe.Extensions.Json", "http-to-http" => "SmartPipe.Extensions.Http", _ => "SmartPipe.Extensions.Json" };
        var assignment = Assignment(pattern, target) with { MigrationEpic = mutation == "epic" ? "WRONG" : "SP220-08" };
        var error = Assert.Throws<OwnershipException>(() => OwnershipLoader.Validate(new() { SchemaVersion = 1, Assignments = [assignment] }, Graph(plannedJson: mutation == "epic")));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void StrategyEvidenceDistinguishesForwarderAndWrapper()
    {
        const string type = "SmartPipe.Extensions.Foo";
        var graph = Graph(false);
        var baseline = Snapshot((type, "SmartPipe.Extensions"), forward: false);
        var implementationOnly = Snapshot((type, "SmartPipe.Extensions.Json"), forward: false);
        var forwardAssignment = Assignment(type, "SmartPipe.Extensions.Json") with { Strategy = OwnershipStrategy.TypeForward, CompatibilityAssembly = "SmartPipe.Extensions" };
        var missingForwarder = new OwnershipValidator().Validate(new() { SchemaVersion = 1, Assignments = [forwardAssignment] }, graph, baseline, implementationOnly, PackageGraphMode.Current);
        Assert.Contains(missingForwarder.Violations, x => x.Code == "SPOWN022");

        var wrapperWithForwarder = Snapshot((type, "SmartPipe.Extensions"), forward: true);
        var wrapper = Assignment(type, "SmartPipe.Extensions") with { Strategy = OwnershipStrategy.ObsoleteWrapper, CompatibilityAssembly = "SmartPipe.Extensions" };
        var invalidWrapper = new OwnershipValidator().Validate(new() { SchemaVersion = 1, Assignments = [wrapper] }, graph, baseline, wrapperWithForwarder, PackageGraphMode.Current);
        Assert.Contains(invalidWrapper.Violations, x => x.Code == "SPOWN023");
    }

    private static OwnershipAssignment Assignment(string pattern, string target) => new() { TypePattern = pattern, BaselineAssembly = pattern.StartsWith("SmartPipe.Core", StringComparison.Ordinal) ? "SmartPipe.Core" : "SmartPipe.Extensions", CurrentImplementationAssembly = "SmartPipe.Extensions", TargetImplementationAssembly = target, CompatibilityAssembly = null, Strategy = OwnershipStrategy.Stay, MigrationEpic = "SP220-08", NamespacePreserved = true, Evidence = "test" };
    private static PackageGraphDocument Graph(bool plannedJson)
    {
        var policy = new DependencyPolicy { RequiredSmartPipePackages = [], AllowedSmartPipePackages = [], AllowedExternalPackages = [], ForbiddenPackagePatterns = [] };
        PackageNode Node(string id, PackageLifecycle lifecycle, string epic, int order) => new() { Id = id, ProjectPath = $"src/{id}/{id}.csproj", Lifecycle = lifecycle, ActivationEpic = epic, ScaffoldKind = lifecycle == PackageLifecycle.Planned ? PackageScaffoldKind.CoreLeaf : null, PublishOrder = order, BaselineVersion = lifecycle == PackageLifecycle.Planned ? null : "2.1.2", AotContract = PackageAotContract.Full, CurrentDependencies = policy, ReleaseDependencies = policy, TemporaryAllowances = [], ConsumerScenarios = [] };
        return new() { SchemaVersion = 1, ReleaseVersion = "2.2.0", Packages = [Node("SmartPipe.Core", PackageLifecycle.Active, "existing", 1), Node("SmartPipe.Extensions.Json", plannedJson ? PackageLifecycle.Planned : PackageLifecycle.Active, plannedJson ? "SP220-08" : "existing", 2), Node("SmartPipe.Extensions.Http", PackageLifecycle.Planned, "SP220-13", 3), Node("SmartPipe.Extensions", PackageLifecycle.CompatibilityFacade, "existing", 4)] };
    }
    private static TypeOwnershipSnapshot Snapshot((string Type, string Package) item, bool forward) => forward
        ? new(new Dictionary<string, IReadOnlySet<string>> { [item.Type] = new HashSet<string> { item.Package } }, new Dictionary<string, IReadOnlySet<string>> { [item.Type] = new HashSet<string> { item.Package } })
        : new(new Dictionary<string, IReadOnlySet<string>> { [item.Type] = new HashSet<string> { item.Package } }, new Dictionary<string, IReadOnlySet<string>>());
}
