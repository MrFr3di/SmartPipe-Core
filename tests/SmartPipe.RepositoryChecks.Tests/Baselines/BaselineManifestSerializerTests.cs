using System.Text;
using SmartPipe.RepositoryChecks.Baselines;
using SmartPipe.RepositoryChecks.Tests.Baselines;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public void SerializeToUtf8Bytes_MatchesCanonicalStringWithoutBom()
    {
        var manifest = BaselineFixtures.CreateManifest();

        var bytes = BaselineManifestSerializer.SerializeToUtf8Bytes(manifest);

        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal(BaselineManifestSerializer.Serialize(manifest), new UTF8Encoding(false, true).GetString(bytes));
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.NotEqual((byte)'\n', bytes[^2]);
    }

    [Fact]
    public async Task WriteAsync_WritesCanonicalUtf8Bytes()
    {
        var manifest = BaselineFixtures.CreateManifest();
        var path = Path.Combine(Path.GetTempPath(), $"smartpipe-manifest-{Guid.NewGuid():N}.json");
        try
        {
            await BaselineManifestSerializer.WriteAsync(path, manifest, TestContext.Current.CancellationToken);

            Assert.Equal(BaselineManifestSerializer.SerializeToUtf8Bytes(manifest), await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
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
    public void Deserialize_RejectsSchemaVersionZero()
    {
        var json = BaselineManifestSerializer.Serialize(BaselineFixtures.CreateManifest());
        json = json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 0", StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => BaselineManifestSerializer.Deserialize(json));

        Assert.Contains("schema version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeThenSerialize_CanonicalizesJsonObjectPropertyOrder()
    {
        var canonical = BaselineManifestSerializer.Serialize(BaselineFixtures.CreateManifest());
        var root = JsonNode.Parse(canonical)!.AsObject();
        var reversed = new JsonObject(root.Reverse().Select(property =>
            KeyValuePair.Create(property.Key, property.Value?.DeepClone())));

        var actual = BaselineManifestSerializer.Serialize(
            BaselineManifestSerializer.Deserialize(reversed.ToJsonString()));

        Assert.Equal(canonical, actual);
    }

    [Fact]
    public void Serialize_RejectsDuplicatePackageIdentitiesIgnoringCase()
    {
        var manifest = BaselineFixtures.CreateManifest();
        var packages = manifest.Packages.ToArray();
        packages[1] = packages[1] with { Id = "smartpipe.extensions.json" };

        Assert.Throws<JsonException>(() => BaselineManifestSerializer.Serialize(manifest with { Packages = packages }));
    }

    [Fact]
    public void Deserialize_RejectsMissingAndNullRequiredObjects()
    {
        AssertInvalid(root => root.Remove("repository"));
        AssertInvalid(root => root["repository"] = null);
        AssertInvalid(root => root["publicApi"] = null);
        AssertInvalid(root => root["packages"] = null);
        AssertInvalid(root => root["repository"]!["requiredWorkflows"] = null);
        AssertInvalid(root => root["repository"]!["requiredWorkflows"]![0] = null);
        AssertInvalid(root => root["repository"]!.AsObject().Remove("captureCommitSha"));
        AssertInvalid(root => root["repository"]!["requiredWorkflows"]![0]!.AsObject().Remove("headSha"));
        AssertInvalid(root => root["packages"]![0] = null);
    }

    [Fact]
    public void Deserialize_RejectsInvalidRepositoryAndWorkflowEvidence()
    {
        AssertInvalid(root => root["baselineName"] = " ");
        AssertInvalid(root => root["targetRelease"] = "2.2");
        AssertInvalid(root => root["repository"]!["fullName"] = " ");
        AssertInvalid(root => root["repository"]!["defaultBranch"] = "");
        AssertInvalid(root => root["repository"]!["captureCommitSha"] = new string('A', 40));
        AssertInvalid(root => root["repository"]!["sdkVersion"] = " ");
        AssertInvalid(root => root["repository"]!["solutionPath"] = "../SmartPipe.Core.slnx");
        AssertInvalid(root => root["repository"]!["requiredWorkflows"]![0]!["name"] = " ");
        AssertInvalid(root => root["repository"]!["requiredWorkflows"]![0]!["runId"] = 0);
        AssertInvalid(root => root["repository"]!["requiredWorkflows"]![0]!["headSha"] = new string('A', 40));
        AssertInvalid(root => root["repository"]!["requiredWorkflows"]![0]!["headSha"] = new string('b', 40));
        AssertInvalid(root => root["repository"]!["requiredWorkflows"]![0]!["url"] = "http://github.com/run/1");
        AssertInvalid(root => root["repository"]!["requiredWorkflows"]![0]!["url"] = "/actions/runs/1");
        AssertInvalid(root => root["repository"]!["requiredWorkflows"]![0]!["conclusion"] = "failure");
        AssertInvalid(root => root["repository"]!["requiredWorkflows"]![0]!["name"] = "RELEASE");
    }

    [Fact]
    public void Deserialize_RejectsInvalidPackageContract()
    {
        AssertInvalid(root => root["packages"]!.AsArray().RemoveAt(2));
        AssertInvalid(root => root["packages"]![0]!["id"] = "smartpipe.core");
        AssertInvalid(root => root["packages"]![0]!["version"] = "2.1.1");
        AssertInvalid(root => root["packages"]![0]!["requireRepositorySignature"] = false);
        AssertInvalid(root => root["packages"]![0]!["fileName"] = "other.nupkg");
        AssertInvalid(root => root["packages"]![0]!["sha256"] = new string('A', 64));
        AssertInvalid(root => root["packages"]![0]!["source"] = "http://api.nuget.org/v3/index.json");
        AssertInvalid(root => root["packages"]![0]!["source"] = "api.nuget.org/v3/index.json");
        AssertInvalid(root => root["packages"]![1]!["id"] = "SmartPipe.Core");
        AssertInvalid(root => root["publicApi"]!["sha256"] = new string('A', 64));
    }

    [Theory]
    [InlineData("/eng/baselines/public-api.json")]
    [InlineData("C:/eng/baselines/public-api.json")]
    [InlineData("eng/../public-api.json")]
    [InlineData("eng\\baselines\\public-api.json")]
    [InlineData("eng//baselines/public-api.json")]
    public void Deserialize_RejectsUnsafeOrNonCanonicalSnapshotPaths(string path)
    {
        AssertInvalid(root => root["publicApi"]!["path"] = path);
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
        var repositoryRequired = definitions.GetProperty("repository").GetProperty("required");
        Assert.Contains(repositoryRequired.EnumerateArray(), item => item.GetString() == "captureCommitSha");
        var workflowRequired = definitions.GetProperty("workflow").GetProperty("required");
        Assert.Contains(workflowRequired.EnumerateArray(), item => item.GetString() == "headSha");
        var commitPattern = GetPattern(definitions, "repository", "captureCommitSha");
        Assert.Equal("^[0-9a-f]{40}$", commitPattern);
        Assert.Matches(commitPattern, "8e79902d22de714f493582946f7c260462b0895e");
        Assert.DoesNotMatch(commitPattern, "8E79902D22DE714F493582946F7C260462B0895E");

        var workflowHeadPattern = GetPattern(definitions, "workflow", "headSha");
        Assert.Equal(commitPattern, workflowHeadPattern);

        var hashPattern = GetPattern(definitions, "snapshot", "sha256");
        Assert.Equal("^[0-9a-f]{64}$", hashPattern);
        Assert.Matches(hashPattern, new string('a', 64));
        Assert.DoesNotMatch(hashPattern, new string('a', 63));

        var sourcePattern = GetPattern(definitions, "package", "source");
        Assert.Matches(sourcePattern, "https://api.nuget.org/v3/index.json");
        Assert.DoesNotMatch(sourcePattern, "http://api.nuget.org/v3/index.json");
        Assert.Equal("success", definitions.GetProperty("workflow").GetProperty("properties").GetProperty("conclusion").GetProperty("const").GetString());

        Assert.Equal(3, root.GetProperty("properties").GetProperty("packages").GetProperty("minItems").GetInt32());
        Assert.Equal(3, root.GetProperty("properties").GetProperty("packages").GetProperty("maxItems").GetInt32());
        var packageSet = root.GetProperty("properties").GetProperty("packages").GetProperty("allOf");
        var expectedPackages = new[] { "SmartPipe.Core", "SmartPipe.Extensions", "SmartPipe.Extensions.Json" };
        Assert.Equal(expectedPackages.Length, packageSet.GetArrayLength());
        Assert.Equal(
            expectedPackages,
            packageSet.EnumerateArray()
                .Select(item => item.GetProperty("contains").GetProperty("properties").GetProperty("id").GetProperty("const").GetString())
                .ToArray());
        Assert.All(packageSet.EnumerateArray(), item =>
        {
            var properties = item.GetProperty("contains").GetProperty("properties");
            Assert.Equal("2.1.2", properties.GetProperty("version").GetProperty("const").GetString());
            Assert.True(properties.GetProperty("requireRepositorySignature").GetProperty("const").GetBoolean());
            Assert.Equal(1, item.GetProperty("minContains").GetInt32());
            Assert.Equal(1, item.GetProperty("maxContains").GetInt32());
        });

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

    private static void AssertInvalid(Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(BaselineManifestSerializer.Serialize(BaselineFixtures.CreateManifest()))!.AsObject();
        mutate(root);

        Assert.Throws<JsonException>(() => BaselineManifestSerializer.Deserialize(root.ToJsonString()));
    }
}
