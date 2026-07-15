using SmartPipe.RepositoryChecks.Baselines;
using SmartPipe.RepositoryChecks.Tests.Baselines;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed class BaselineManifestSerializerTests
{
    [Fact]
    public void Serialize_IsByteStableAcrossRepeatedCalls()
    {
        var manifest = BaselineFixtures.CreateManifest();

        var first = BaselineManifestSerializer.Serialize(manifest);
        var second = BaselineManifestSerializer.Serialize(manifest);

        Assert.Equal(first, second);
        Assert.DoesNotContain("\r", first, StringComparison.Ordinal);
        Assert.False(first.AsSpan().StartsWith("\uFEFF"));
        Assert.EndsWith("\n", first, StringComparison.Ordinal);
        Assert.False(first.EndsWith("\n\n", StringComparison.Ordinal));
        Assert.True(first.IndexOf("\"schemaVersion\"", StringComparison.Ordinal) < first.IndexOf("\"baselineName\"", StringComparison.Ordinal));
        Assert.True(first.IndexOf("\"baselineName\"", StringComparison.Ordinal) < first.IndexOf("\"targetRelease\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Serialize_SortsStableIdentityLists()
    {
        var json = BaselineManifestSerializer.Serialize(BaselineFixtures.CreateManifest());

        Assert.True(json.IndexOf("SmartPipe.Core", StringComparison.Ordinal) < json.IndexOf("SmartPipe.Extensions\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("SmartPipe.Extensions\"", StringComparison.Ordinal) < json.IndexOf("SmartPipe.Extensions.Json", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"ci\"", StringComparison.Ordinal) < json.IndexOf("\"release\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Deserialize_RoundTripsCanonicalManifest()
    {
        var manifest = BaselineFixtures.CreateManifest();

        var deserialized = BaselineManifestSerializer.Deserialize(BaselineManifestSerializer.Serialize(manifest));

        Assert.Equal(manifest.SchemaVersion, deserialized.SchemaVersion);
        Assert.Equal(manifest.BaselineName, deserialized.BaselineName);
        Assert.Equal(3, deserialized.Packages.Count);
    }

    [Fact]
    public void Deserialize_RejectsUnknownProperties()
    {
        var json = BaselineManifestSerializer.Serialize(BaselineFixtures.CreateManifest());
        json = json.Replace("{\n", "{\n  \"unexpected\": true,\n", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => BaselineManifestSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_RejectsUnknownSchemaVersion()
    {
        var json = BaselineManifestSerializer.Serialize(BaselineFixtures.CreateManifest());
        json = json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => BaselineManifestSerializer.Deserialize(json));
        Assert.Contains("schema version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Schema_DefinesRequiredFailClosedManifestContracts()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "baseline.schema.json")));
        var root = schema.RootElement;

        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains(root.GetProperty("required").EnumerateArray(), item => item.GetString() == "schemaVersion");
        Assert.Equal(1, root.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32());

        var definitions = root.GetProperty("$defs");
        var commitPattern = GetPattern(definitions, "repository", "commitSha");
        Assert.Equal("^[0-9a-f]{40}$", commitPattern);
        Assert.Matches(commitPattern, "8e79902d22de714f493582946f7c260462b0895e");
        Assert.DoesNotMatch(commitPattern, "8E79902D22DE714F493582946F7C260462B0895E");

        var hashPattern = GetPattern(definitions, "snapshot", "sha256");
        Assert.Equal("^[0-9a-f]{64}$", hashPattern);
        Assert.Matches(hashPattern, new string('a', 64));
        Assert.DoesNotMatch(hashPattern, new string('a', 63));

        var sourcePattern = GetPattern(definitions, "package", "source");
        Assert.Matches(sourcePattern, "https://api.nuget.org/v3/index.json");
        Assert.DoesNotMatch(sourcePattern, "http://api.nuget.org/v3/index.json");

        Assert.Equal(3, root.GetProperty("properties").GetProperty("packages").GetProperty("minItems").GetInt32());
        Assert.Equal(3, root.GetProperty("properties").GetProperty("packages").GetProperty("maxItems").GetInt32());

        var pathPattern = GetPattern(definitions, "snapshot", "path");
        Assert.Matches(pathPattern, "eng/baselines/public-api.json");
        Assert.DoesNotMatch(pathPattern, "/eng/baselines/public-api.json");
        Assert.DoesNotMatch(pathPattern, @"C:\eng\baselines\public-api.json");
        Assert.DoesNotMatch(pathPattern, "eng/../public-api.json");
        Assert.DoesNotMatch(pathPattern, @"eng\..\public-api.json");

        foreach (var objectDefinition in new[] { "repository", "workflow", "package", "snapshot" })
        {
            Assert.False(definitions.GetProperty(objectDefinition).GetProperty("additionalProperties").GetBoolean());
        }
    }

    private static string GetPattern(JsonElement definitions, string definition, string property) =>
        definitions.GetProperty(definition)
            .GetProperty("properties")
            .GetProperty(property)
            .GetProperty("pattern")
            .GetString()!;
}
