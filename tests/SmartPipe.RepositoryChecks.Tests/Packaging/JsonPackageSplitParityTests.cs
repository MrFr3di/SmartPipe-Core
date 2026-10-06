using SmartPipe.RepositoryChecks.Ownership;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Packaging;

namespace SmartPipe.RepositoryChecks.Tests.Packaging;

public sealed class JsonPackageSplitParityTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("missing-json-dependency", false)]
    [InlineData("implementation-duplicated-in-facade", false)]
    [InlineData("missing-forwarder", false)]
    [InlineData("wrong-package-content", false)]
    public void GeneralizedValidatorsMatchFrozenLegacyMutationCorpus(
        string mutation,
        bool legacyAccepted)
    {
        var graphAccepted = PackageGraphValidator.ValidatePackedDependencies(
            "SmartPipe.Extensions",
            ["SmartPipe.Extensions.Json"],
            mutation == "missing-json-dependency" ? [] : ["SmartPipe.Extensions.Json"]
        ).Count == 0;

        var ownershipAccepted = ValidateOwnership(mutation);
        var contentAccepted = PackageContentValidator.IsAllowedPackagePath(
            "SmartPipe.Extensions.Json",
            mutation == "wrong-package-content"
                ? "src/SmartPipe.Extensions/Transforms/JsonTransform.cs"
                : "lib/net10.0/SmartPipe.Extensions.Json.dll"
        );

        var generalizedAccepted = graphAccepted && ownershipAccepted && contentAccepted;
        Assert.Equal(legacyAccepted, generalizedAccepted);
    }

    private static bool ValidateOwnership(string mutation)
    {
        const string type = "SmartPipe.Extensions.Transforms.JsonTransform`2";
        var assignment = new OwnershipAssignment
        {
            TypePattern = type,
            BaselineAssembly = "SmartPipe.Extensions",
            CurrentImplementationAssembly = "SmartPipe.Extensions.Json",
            TargetImplementationAssembly = "SmartPipe.Extensions.Json",
            CompatibilityAssembly = "SmartPipe.Extensions",
            Strategy = OwnershipStrategy.TypeForward,
            MigrationEpic = "SP220-01",
            NamespacePreserved = true,
            Evidence = "Frozen validate-json-package-split parity corpus",
        };
        var baseline = Snapshot(
            implementations: new Dictionary<string, IReadOnlySet<string>>
            {
                [type] = new HashSet<string> { "SmartPipe.Extensions" },
            },
            forwarders: new Dictionary<string, IReadOnlySet<string>>()
        );
        var implementationPackages = mutation == "implementation-duplicated-in-facade"
            ? new HashSet<string> { "SmartPipe.Extensions.Json", "SmartPipe.Extensions" }
            : new HashSet<string> { "SmartPipe.Extensions.Json" };
        var forwarders = mutation == "missing-forwarder"
            ? new Dictionary<string, IReadOnlySet<string>>()
            : new Dictionary<string, IReadOnlySet<string>>
            {
                [type] = new HashSet<string> { "SmartPipe.Extensions" },
            };
        var current = Snapshot(
            new Dictionary<string, IReadOnlySet<string>> { [type] = implementationPackages },
            forwarders
        );
        var report = new OwnershipValidator().Validate(
            new OwnershipDocument { SchemaVersion = 1, Assignments = [assignment] },
            Graph(),
            baseline,
            current,
            PackageGraphMode.Current
        );
        return report.Violations.Count == 0;
    }

    private static TypeOwnershipSnapshot Snapshot(
        IReadOnlyDictionary<string, IReadOnlySet<string>> implementations,
        IReadOnlyDictionary<string, IReadOnlySet<string>> forwarders) =>
        new(implementations, forwarders);

    private static PackageGraphDocument Graph()
    {
        var policy = new DependencyPolicy
        {
            RequiredSmartPipePackages = [],
            AllowedSmartPipePackages = [],
            AllowedExternalPackages = [],
            ForbiddenPackagePatterns = [],
        };
        PackageNode Node(string id, PackageLifecycle lifecycle, int order) => new()
        {
            Id = id,
            ProjectPath = $"src/{id}/{id}.csproj",
            Lifecycle = lifecycle,
            ActivationEpic = "existing",
            ScaffoldKind = null,
            PublishOrder = order,
            BaselineVersion = "2.1.2",
            AotContract = PackageAotContract.Full,
            CurrentDependencies = policy,
            ReleaseDependencies = policy,
            TemporaryAllowances = [],
            ConsumerScenarios = [],
        };
        return new PackageGraphDocument
        {
            SchemaVersion = 1,
            ReleaseVersion = "2.2.0",
            Packages =
            [
                Node("SmartPipe.Core", PackageLifecycle.Active, 1),
                Node("SmartPipe.Extensions.Json", PackageLifecycle.Active, 5),
                Node("SmartPipe.Extensions", PackageLifecycle.CompatibilityFacade, 19),
            ],
        };
    }
}
