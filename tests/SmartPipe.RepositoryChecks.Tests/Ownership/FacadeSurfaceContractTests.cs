using SmartPipe.RepositoryChecks.Ownership;
using SmartPipe.RepositoryChecks.PackageGraph;

namespace SmartPipe.RepositoryChecks.Tests.Ownership;

public sealed class FacadeSurfaceContractTests
{
    [Theory]
    [InlineData(false, "SPOWN027")]
    [InlineData(true, "SPOWN028")]
    public void NewUniqueFacadeIdentityIsRejected(bool forwarded, string code)
    {
        const string retained = "SmartPipe.Extensions.Retained";
        const string extra = "SmartPipe.Extensions.NewFeature";
        var baseline = Snapshot([retained], []);
        var current = forwarded ? Snapshot([retained], [extra]) : Snapshot([retained, extra], []);

        var result = new OwnershipValidator().Validate(Document(retained), Graph(), baseline, current, PackageGraphMode.Release);

        Assert.Contains(result.Violations, violation => violation.Code == code && violation.Type == extra);
    }

    [Fact]
    public void NewCanonicalLeafIdentityIsAllowed()
    {
        const string retained = "SmartPipe.Extensions.Retained";
        var baseline = Snapshot([retained], []);
        var current = Snapshot([retained], []);
        var implementations = current.Implementations.ToDictionary(pair => pair.Key, pair => pair.Value);
        implementations["SmartPipe.Extensions.Json.NewFeature"] = new HashSet<string> { "SmartPipe.Extensions.Json" };

        var result = new OwnershipValidator().Validate(Document(retained), Graph(), baseline,
            new(implementations, current.Forwarders), PackageGraphMode.Release);

        Assert.True(result.Success);
    }

    internal static OwnershipDocument Document(string type, string target = "SmartPipe.Extensions") => new()
    {
        SchemaVersion = 1,
        Assignments = [new()
        {
            TypePattern = type, BaselineAssembly = "SmartPipe.Extensions",
            CurrentImplementationAssembly = "SmartPipe.Extensions", TargetImplementationAssembly = target,
            CompatibilityAssembly = "SmartPipe.Extensions", NamespacePreserved = true,
            Strategy = target == "SmartPipe.Extensions" ? OwnershipStrategy.ObsoleteWrapper : OwnershipStrategy.TypeForward,
            MigrationEpic = "SP220-17", Evidence = "fixture",
        }],
    };

    internal static PackageGraphDocument Graph()
    {
        var policy = new DependencyPolicy { RequiredSmartPipePackages = [], AllowedSmartPipePackages = [], AllowedExternalPackages = [], ForbiddenPackagePatterns = [] };
        PackageNode Node(string id, int order) => new()
        {
            Id = id, ProjectPath = $"src/{id}/{id}.csproj", Lifecycle = id == "SmartPipe.Extensions" ? PackageLifecycle.CompatibilityFacade : PackageLifecycle.Active,
            ActivationEpic = "existing", ScaffoldKind = null, PublishOrder = order, AotContract = PackageAotContract.Full,
            CurrentDependencies = policy, ReleaseDependencies = policy, TemporaryAllowances = [], ConsumerScenarios = [],
        };
        return new() { SchemaVersion = 1, ReleaseVersion = "2.2.0", Packages = [Node("SmartPipe.Extensions.Json", 1), Node("SmartPipe.Extensions", 2)] };
    }

    internal static TypeOwnershipSnapshot Snapshot(string[] implementations, string[] forwarders) => new(
        implementations.ToDictionary(type => type, _ => (IReadOnlySet<string>)new HashSet<string> { "SmartPipe.Extensions" }),
        forwarders.ToDictionary(type => type, _ => (IReadOnlySet<string>)new HashSet<string> { "SmartPipe.Extensions" }));
}
