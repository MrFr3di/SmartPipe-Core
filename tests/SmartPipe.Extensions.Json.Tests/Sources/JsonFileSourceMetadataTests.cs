#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartPipe.Extensions;
using SmartPipe.Extensions.Selectors;
using Xunit;

namespace SmartPipe.Extensions.Tests.Sources;

public sealed class JsonFileSourceMetadataTests
{
    [Fact]
    public async Task SourceGeneratedOptions_AreClonedWithoutMutatingCallerOptions()
    {
        var serializerOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var context = new MetadataJsonContext(serializerOptions);
        var originalMaxDepth = serializerOptions.MaxDepth;
        var originalIsReadOnly = serializerOptions.IsReadOnly;
        var path = await WriteTempAsync("[{\"id\":7,\"name\":\"value\"}]");
        try
        {
            var source = new JsonFileSource<MetadataItem>(path, context.MetadataItem,
                context.ListMetadataItem, new JsonFileSourceOptions { Format = JsonFileFormat.Array, MaxDepth = 3 });
            var values = new List<MetadataItem>();
            await foreach (var envelope in source.ReadEnvelopesAsync())
                values.Add(envelope.Payload);

            Assert.Equal(7, Assert.Single(values).Id);
            Assert.Equal(originalMaxDepth, serializerOptions.MaxDepth);
            Assert.Equal(originalIsReadOnly, serializerOptions.IsReadOnly);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TypeInfosFromDifferentContexts_FailFast()
    {
        var first = new MetadataJsonContext(new JsonSerializerOptions());
        var second = new MetadataJsonContext(new JsonSerializerOptions());

        var exception = Assert.Throws<ArgumentException>(() => new JsonFileSource<MetadataItem>(
            "input.json", first.MetadataItem, second.ListMetadataItem,
            new JsonFileSourceOptions { Format = JsonFileFormat.Array }));
        Assert.Contains("same serializer context", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Auto_CollectionValuedItem_RequiresExplicitFormat()
    {
        var path = await WriteTempAsync("[[1,2]]");
        try
        {
            var source = new JsonFileSource<List<int>>(path);
            var exception = await Assert.ThrowsAsync<JsonException>(async () =>
            {
                await foreach (var _ in source.ReadEnvelopesAsync()) { }
            });
            Assert.Contains("ambiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("explicitly", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    private static async Task<string> WriteTempAsync(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"smartpipe-json-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, contents);
        return path;
    }
}

public sealed record MetadataItem(int Id, string Name);

[JsonSerializable(typeof(MetadataItem))]
[JsonSerializable(typeof(List<MetadataItem>))]
internal sealed partial class MetadataJsonContext : JsonSerializerContext;
