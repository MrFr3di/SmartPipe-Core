#nullable enable
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPipe.Extensions;
using SmartPipe.Extensions.Selectors;
using Xunit;

namespace SmartPipe.Extensions.Tests.Sources;

public sealed class JsonFileSourceLimitTests
{
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public async Task RecordLimit_ExactSizeSucceeds_AndPlusOneHasPathAndIndex(bool sourceGenerated, int limit)
    {
        var exactPath = await WriteTempAsync("123\n");
        var oversizedPath = await WriteTempAsync("1234\n");
        try
        {
            var options = new JsonFileSourceOptions { Format = JsonFileFormat.Ndjson, MaxRecordSizeBytes = limit };
            JsonFileSource<int> Create(string path) => sourceGenerated
                ? new JsonFileSource<int>(path, LimitJsonContext.Default.Int32,
                    LimitJsonContext.Default.ListInt32, options)
                : new JsonFileSource<int>(path, options);

            var values = new List<int>();
            await foreach (var envelope in Create(exactPath).ReadEnvelopesAsync())
                values.Add(envelope.Payload);
            Assert.Equal([123], values);

            var exception = await Assert.ThrowsAsync<JsonException>(() => ReadAllAsync(Create(oversizedPath)));
            Assert.Contains(oversizedPath, exception.Message, StringComparison.Ordinal);
            Assert.Contains("record 1", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("3-byte", exception.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(exactPath); File.Delete(oversizedPath); }
    }

    [Fact]
    public async Task PrettyPrintedRootArray_UsesWholeDocumentLimitAcrossLines()
    {
        var path = await WriteTempAsync("[\n  1,\n  2\n]");
        try
        {
            var source = new JsonFileSource<int>(path, new JsonFileSourceOptions
            {
                Format = JsonFileFormat.Array,
                MaxRecordSizeBytes = 2,
                MaxUnframedInputSizeBytes = 8,
            });
            var exception = await Assert.ThrowsAsync<JsonException>(() => ReadAllAsync(source));
            Assert.Contains(path, exception.Message, StringComparison.Ordinal);
            Assert.Contains("8-byte", exception.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MultilineNdjsonObject_IsParsedAsSeparatePhysicalRows()
    {
        var path = await WriteTempAsync("{\"Id\":1,\n\"Name\":{\"Value\":\"x\"}}\n");
        try
        {
            var source = new JsonFileSource<DepthItem>(path,
                new JsonFileSourceOptions { Format = JsonFileFormat.Ndjson });
            var exception = await Assert.ThrowsAsync<JsonException>(() => ReadAllAsync(source));
            Assert.Contains(path, exception.Message, StringComparison.Ordinal);
            Assert.Contains("record 1", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReflectionAndSourceGenerated_EnforceSameDepth(bool sourceGenerated)
    {
        var path = await WriteTempAsync("[{\"Id\":1,\"Name\":{\"Value\":\"x\"}}]");
        try
        {
            var options = new JsonFileSourceOptions { Format = JsonFileFormat.Array, MaxDepth = 2 };
            var source = sourceGenerated
                ? new JsonFileSource<DepthItem>(path, DepthJsonContext.Default.DepthItem,
                    DepthJsonContext.Default.ListDepthItem, options)
                : new JsonFileSource<DepthItem>(path, options);

            var exception = await Assert.ThrowsAsync<JsonException>(() => ReadAllAsync(source));
            Assert.Contains(path, exception.Message, StringComparison.Ordinal);
            Assert.Contains("document", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Array_SkipAndLog_IsRejected()
    {
        var options = new JsonFileSourceOptions
        {
            Format = JsonFileFormat.Array,
            InvalidRecordBehavior = InvalidJsonRecordBehavior.SkipAndLog,
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            new JsonFileSource<int>("input.json", options, NullLogger<JsonFileSource<int>>.Instance));
        Assert.Contains("independently framed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Auto_SkipAndLog_IsRejectedBeforeOpeningInput()
    {
        var options = new JsonFileSourceOptions
        {
            Format = JsonFileFormat.Auto,
            InvalidRecordBehavior = InvalidJsonRecordBehavior.SkipAndLog,
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            new JsonFileSource<int>("missing.json", options, NullLogger<JsonFileSource<int>>.Instance));
        Assert.Contains("explicit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ndjson", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BatchJsonLines", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BatchJsonLines_RequiresArrayPerPhysicalLine()
    {
        var path = await WriteTempAsync("[1,2]\n3\n[4]\n");
        try
        {
            var source = new JsonFileSource<int>(path,
                new JsonFileSourceOptions { Format = JsonFileFormat.BatchJsonLines });

            var exception = await Assert.ThrowsAsync<JsonException>(() => ReadAllAsync(source));
            Assert.Contains(path, exception.Message, StringComparison.Ordinal);
            Assert.Contains("record 2", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RootArray_AboveUnframedInputLimit_Throws()
    {
        var path = await WriteTempAsync("[123456]");
        try
        {
            var source = new JsonFileSource<int>(path, new JsonFileSourceOptions
            {
                Format = JsonFileFormat.Array,
                MaxUnframedInputSizeBytes = 4,
            });
            var exception = await Assert.ThrowsAsync<JsonException>(() => ReadAllAsync(source));
            Assert.Contains(path, exception.Message, StringComparison.Ordinal);
            Assert.Contains("configured 4-byte limit", exception.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LegacyTopLevelSequence_TotalAboveLimit_Throws()
    {
        var path = await WriteTempAsync("[1,2]");
        try
        {
            var source = new JsonFileSource<int>(path, new JsonFileSourceOptions
            {
                Format = JsonFileFormat.Auto,
                MaxUnframedInputSizeBytes = 4,
            });
            var exception = await Assert.ThrowsAsync<JsonException>(() => ReadAllAsync(source));
            Assert.Contains(path, exception.Message, StringComparison.Ordinal);
            Assert.Contains("configured 4-byte limit", exception.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Ndjson_TotalAboveInputLimit_ButEachRecordBelowRecordLimit_Succeeds()
    {
        var path = await WriteTempAsync("123\n456\n789\n");
        try
        {
            var source = new JsonFileSource<int>(path, new JsonFileSourceOptions
            {
                Format = JsonFileFormat.Ndjson,
                MaxRecordSizeBytes = 16,
                MaxUnframedInputSizeBytes = 4,
            });
            var values = new List<int>();
            await foreach (var envelope in source.ReadEnvelopesAsync())
                values.Add(envelope.Payload);
            Assert.Equal([123, 456, 789], values);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task BatchLines_TotalAboveInputLimit_ButEachLineBelowRecordLimit_Succeeds()
    {
        var path = await WriteTempAsync("[1]\n[2]\n[3]\n");
        try
        {
            var source = new JsonFileSource<int>(path, new JsonFileSourceOptions
            {
                Format = JsonFileFormat.BatchJsonLines,
                MaxRecordSizeBytes = 16,
                MaxUnframedInputSizeBytes = 4,
            });
            var values = new List<int>();
            await foreach (var envelope in source.ReadEnvelopesAsync())
                values.Add(envelope.Payload);
            Assert.Equal([1, 2, 3], values);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RootArray_ExactlyAtUnframedInputLimit_Succeeds()
    {
        var path = await WriteTempAsync("[1]");
        try
        {
            var source = new JsonFileSource<int>(path, new JsonFileSourceOptions
            {
                Format = JsonFileFormat.Array,
                MaxUnframedInputSizeBytes = 3,
            });
            var values = new List<int>();
            await foreach (var envelope in source.ReadEnvelopesAsync())
                values.Add(envelope.Payload);
            Assert.Equal([1], values);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EmptyStream_ReturnsNoItems()
    {
        var path = await WriteTempAsync(string.Empty);
        try
        {
            var source = new JsonFileSource<int>(path, new JsonFileSourceOptions
            {
                Format = JsonFileFormat.Auto,
                MaxUnframedInputSizeBytes = 4,
            });
            var values = new List<int>();
            await foreach (var envelope in source.ReadEnvelopesAsync())
                values.Add(envelope.Payload);
            Assert.Empty(values);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task BomPlusPayload_CountsBomTowardUnframedInputLimit()
    {
        await using var stream = new MemoryStream("\uFEFF[]"u8.ToArray());
        var source = new JsonFileSource<string>(
            "bom-array.json",
            stream,
            new JsonFileSourceOptions
            {
                Format = JsonFileFormat.Array,
                MaxRecordSizeBytes = 1,
                MaxUnframedInputSizeBytes = 4,
            });

        var exception = await Assert.ThrowsAsync<JsonException>(async () =>
        {
            await foreach (var _ in source.ReadEnvelopesAsync()) { }
        });

        Assert.Contains("configured 4-byte limit", exception.Message, StringComparison.Ordinal);
        Assert.Contains("bom-array.json", exception.Message, StringComparison.Ordinal);
    }

    private static async Task ReadAllAsync<T>(JsonFileSource<T> source)
    {
        await foreach (var _ in source.ReadEnvelopesAsync()) { }
    }

    private static async Task<string> WriteTempAsync(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"smartpipe-json-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, contents);
        return path;
    }
}

public sealed record DepthItem(int Id, DepthValue Name);
public sealed record DepthValue(string Value);

[System.Text.Json.Serialization.JsonSerializable(typeof(DepthItem))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<DepthItem>))]
internal sealed partial class DepthJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<int>))]
internal sealed partial class LimitJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
