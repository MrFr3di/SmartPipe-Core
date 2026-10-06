using SmartPipe.RepositoryChecks.Ownership;

namespace SmartPipe.RepositoryChecks.Tests.Ownership;

public sealed class OwnershipResolverTests
{
    [Fact]
    public void Resolve_ExactBeatsWildcardAndEqualPrefixesAreAmbiguous()
    {
        var exact = Assignment("SmartPipe.Extensions.JsonFileFormat");
        var wildcard = Assignment("SmartPipe.Extensions.*");
        Assert.Same(exact, OwnershipResolver.Resolve("SmartPipe.Extensions.JsonFileFormat", [wildcard, exact]));
        var error = Assert.Throws<OwnershipException>(() => OwnershipResolver.Resolve("SmartPipe.Extensions.Foo", [wildcard, wildcard with { Evidence = "other" }]));
        Assert.Equal("SPOWN002", error.Code);
    }

    [Fact]
    public void Resolve_GapFailsClosed()
    {
        var error = Assert.Throws<OwnershipException>(() => OwnershipResolver.Resolve("SmartPipe.Missing", [Assignment("SmartPipe.Core.*")]));
        Assert.Equal("SPOWN001", error.Code);
    }

    private static OwnershipAssignment Assignment(string pattern) => new()
    {
        TypePattern = pattern,
        BaselineAssembly = "SmartPipe.Extensions",
        CurrentImplementationAssembly = "SmartPipe.Extensions",
        TargetImplementationAssembly = "SmartPipe.Extensions",
        CompatibilityAssembly = null,
        Strategy = OwnershipStrategy.Stay,
        MigrationEpic = "existing",
        NamespacePreserved = true,
        Evidence = "test",
    };
}
