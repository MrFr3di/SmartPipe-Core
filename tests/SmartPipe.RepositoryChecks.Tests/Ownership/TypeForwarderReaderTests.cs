using SmartPipe.RepositoryChecks.Ownership;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Ownership;

public sealed class TypeForwarderReaderTests
{
    [Fact]
    public async Task BaselineReaderPreservesImplementationsForwardersAndDuplicateImplementations()
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("package-assets.json", """
            [
              {"packageId":"SmartPipe.Extensions.Json","assemblies":[{"name":"SmartPipe.Extensions.Json","version":"2.1.2.0","assetPath":"lib/net10.0/a.dll","targetFramework":"net10.0","exportedTypes":["SmartPipe.Extensions.JsonType","SmartPipe.Extensions.Duplicate"],"typeForwarders":[]}]},
              {"packageId":"SmartPipe.Extensions","assemblies":[{"name":"SmartPipe.Extensions","version":"2.1.2.0","assetPath":"lib/net10.0/b.dll","targetFramework":"net10.0","exportedTypes":["SmartPipe.Extensions.Duplicate"],"typeForwarders":["SmartPipe.Extensions.JsonType"]}]}
            ]
            """);
        var result = await new TypeForwarderReader().ReadBaselineAsync(Path.Combine(fixture.Path, "package-assets.json"), TestContext.Current.CancellationToken);
        Assert.Contains("SmartPipe.Extensions.Json", result.Implementations["SmartPipe.Extensions.JsonType"]);
        Assert.Contains("SmartPipe.Extensions", result.Forwarders["SmartPipe.Extensions.JsonType"]);
        Assert.Equal(2, result.Implementations["SmartPipe.Extensions.Duplicate"].Count);
    }
}
