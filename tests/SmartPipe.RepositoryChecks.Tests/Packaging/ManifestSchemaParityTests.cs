using System.Text.Json;

namespace SmartPipe.RepositoryChecks.Tests.Packaging;

[Trait("Category", "PackageInfrastructure")]
public sealed class ManifestSchemaParityTests
{
    [Fact]
    public void ConsumerManifest_RequiredPropertiesMatchExecutableModelInputs()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eng/consumer-scenarios.json")));
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eng/consumer-scenarios.schema.json")));
        var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.All(new[] { "schemaVersion", "requiredAtRelease", "scenarios" }, property => Assert.Contains(property, required));
        var scenarioRequired = schema.RootElement.GetProperty("$defs").GetProperty("scenario").GetProperty("required").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
        var actual = manifest.RootElement.GetProperty("scenarios")[0].EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(scenarioRequired, actual);
    }

    [Fact]
    public void PackageGraphSchema_RequiresEveryCanonicalGraphProperty()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eng/package-graph.json")));
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eng/package-graph.schema.json")));
        var required = schema.RootElement.GetProperty("$defs").GetProperty("package").GetProperty("required").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
        var actual = manifest.RootElement.GetProperty("packages")[0].EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(required, actual);
    }
}
