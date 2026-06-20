using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using SmartPipe.Extensions.Selectors;
using SmartPipe.Testing.Fixtures;

namespace SmartPipe.Extensions.Tests.Fixtures;

public class JsonGoldenFixtureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "smartpipe-json-golden-" + Guid.NewGuid().ToString("N"));

    public JsonGoldenFixtureTests()
    {
        GeneratedFixtureData.WriteAllTo(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temp files created by the test.
        }
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task JsonFixture_RootArray_StreamsItems()
    {
        var rows = await ReadAllAsync<JsonGoldenRecord>("json/root-array.json");

        rows.Select(r => r.Name).Should().Equal("alpha", "beta");
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task JsonFixture_TopLevelValues_StreamsItems()
    {
        var rows = await ReadAllAsync<JsonGoldenRecord>("json/top-level-values.ndjson");

        rows.Select(r => r.Name).Should().Equal("alpha", "beta");
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task JsonFixture_Ndjson_StreamsItems_IfSupported()
    {
        var rows = await ReadAllAsync<JsonGoldenRecord>("json/items.ndjson");

        rows.Should().HaveCount(2);
        rows[0].Amount.Should().Be(1);
        rows[1].Amount.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task JsonFixture_NullAndMissingProperties_AreHandled()
    {
        var nullRows = await ReadAllAsync<JsonNullableRecord>("json/nulls.json");
        var missingRows = await ReadAllAsync<JsonNullableRecord>("json/missing-fields.json");

        nullRows.Should().HaveCount(2);
        nullRows[0].Amount.Should().BeNull();
        nullRows[1].Name.Should().BeNull();
        missingRows.Should().HaveCount(2);
        missingRows[0].Amount.Should().BeNull();
        missingRows[1].Name.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task JsonFixture_NumericValues_AreCultureInvariant()
    {
        var path = Path.Combine(_root, "json", "numeric-invariant.json");
        await File.WriteAllTextAsync(path, """[{"Name":"alpha","Amount":1.25}]""");
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");

            var rows = await ReadAllAsync<JsonGoldenRecord>("json/numeric-invariant.json");

            rows.Should().ContainSingle().Which.Amount.Should().Be(1.25m);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task JsonFixture_MalformedJson_UsesFailurePolicy()
    {
        var act = async () => await ReadAllAsync<JsonGoldenRecord>("json/malformed.json");

        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task JsonFixture_EmptyArray_CompletesSuccessfully()
    {
        var rows = await ReadAllAsync<JsonGoldenRecord>("json/empty-array.json");

        rows.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task JsonFixture_EmptyFile_UsesConfiguredPolicy()
    {
        var rows = await ReadAllAsync<JsonGoldenRecord>("json/empty-file.json");

        rows.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task JsonFixture_SourceGeneratedJsonTypeInfo_Works()
    {
        var rows = await ReadAllWithTypeInfoAsync("json/root-array.json");

        rows.Select(r => r.Name).Should().Equal("alpha", "beta");
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task JsonFixture_AotSafeOverload_IsUsedWhereRequired()
    {
        var rows = await ReadAllWithTypeInfoAsync("json/items.ndjson");

        rows.Should().HaveCount(2);
        rows[0].Name.Should().Be("alpha");
        rows[1].Name.Should().Be("beta");
    }

    private async Task<List<T>> ReadAllAsync<T>(string relativePath)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var source = new JsonFileSource<T>(path);
        var rows = new List<T>();

        await foreach (var envelope in source.ReadEnvelopesAsync(TestContext.Current.CancellationToken))
            rows.Add(envelope.Payload);

        return rows;
    }

    private async Task<List<JsonGoldenRecord>> ReadAllWithTypeInfoAsync(string relativePath)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var source = new JsonFileSource<JsonGoldenRecord>(
            path,
            JsonGoldenFixtureJsonContext.Default.ListJsonGoldenRecord,
            JsonGoldenFixtureJsonContext.Default.JsonGoldenRecord);
        var rows = new List<JsonGoldenRecord>();

        await foreach (var envelope in source.ReadEnvelopesAsync(TestContext.Current.CancellationToken))
            rows.Add(envelope.Payload);

        return rows;
    }

    public sealed record JsonGoldenRecord(string Name, decimal Amount);

    public sealed record JsonNullableRecord(string? Name, decimal? Amount);
}

[JsonSerializable(typeof(JsonGoldenFixtureTests.JsonGoldenRecord))]
[JsonSerializable(typeof(List<JsonGoldenFixtureTests.JsonGoldenRecord>))]
internal sealed partial class JsonGoldenFixtureJsonContext : JsonSerializerContext;
