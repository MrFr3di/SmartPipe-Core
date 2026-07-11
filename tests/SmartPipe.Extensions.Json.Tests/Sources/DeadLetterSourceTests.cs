#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using SmartPipe.Core;
using SmartPipe.Extensions;
using SmartPipe.Extensions.Selectors;
using Xunit;

namespace SmartPipe.Extensions.Tests.Sources;

public class DeadLetterSourceTests
{
    [Fact]
    public async Task Auto_UsesDocumentLimitAcrossNdjsonRecords()
    {
        var first = JsonSerializer.Serialize(CreateDeadLetter("one", 1));
        var second = JsonSerializer.Serialize(CreateDeadLetter("two", 2));
        var content = $"{first}\n{second}\n";
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
            var source = new DeadLetterSource<string>(path, new JsonLinesDeadLetterSerializer<string>(),
                new DeadLetterSourceOptions { Format = JsonFileFormat.Auto, MaxDocumentSizeBytes = content.Length - 1 });
            await Assert.ThrowsAsync<JsonException>(() => ReadAllAsync(source));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Array_UsesDocumentLimitAndIgnoresRecordLimit()
    {
        var content = JsonSerializer.Serialize(new[] { CreateDeadLetter("one", 1) });
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
            var source = new DeadLetterSource<string>(path, new JsonLinesDeadLetterSerializer<string>(),
                new DeadLetterSourceOptions
                {
                    Format = JsonFileFormat.Array,
                    MaxRecordSizeBytes = 1,
                    MaxDocumentSizeBytes = System.Text.Encoding.UTF8.GetByteCount(content),
                });
            Assert.Single(await ReadAllAsync(source));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Ndjson_UsesRecordLimitAndIgnoresDocumentLimit()
    {
        var content = JsonSerializer.Serialize(CreateDeadLetter("one", 1));
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, content + "\n", TestContext.Current.CancellationToken);
            var source = new DeadLetterSource<string>(path, new JsonLinesDeadLetterSerializer<string>(),
                new DeadLetterSourceOptions
                {
                    Format = JsonFileFormat.Ndjson,
                    MaxRecordSizeBytes = System.Text.Encoding.UTF8.GetByteCount(content),
                    MaxDocumentSizeBytes = 1,
                });
            Assert.Single(await ReadAllAsync(source));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Constructor_RejectsBatchJsonLinesFormat()
    {
        Assert.Throws<ArgumentException>(() => new DeadLetterSource<string>(
            "deadletters.json",
            new JsonLinesDeadLetterSerializer<string>(),
            new DeadLetterSourceOptions { Format = JsonFileFormat.BatchJsonLines }));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenPathIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new DeadLetterSource<string>(null!));
    }

    [Fact]
    public async Task InitializeAsync_ThrowsFileNotFoundException_WhenFileMissing()
    {
        var source = new DeadLetterSource<string>("nonexistent.json");

        await Assert.ThrowsAsync<FileNotFoundException>(() => source.InitializeAsync().AsTask());
    }

    [Fact]
    public async Task InitializeAsync_Completes_WhenFileExists()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "[]");
            var source = new DeadLetterSource<string>(path);

            await source.InitializeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExplicitSerializer_ReadsEnvelopesAndPreservesReplayContext()
    {
        var path = Path.GetTempFileName();
        try
        {
            var serializer = new JsonLinesDeadLetterSerializer<string>();
            await using (var output = File.Create(path))
            {
                await serializer.WriteAsync(CreateDeadLetter("one", 11UL), output);
                await serializer.WriteAsync(CreateDeadLetter("two", 12UL), output);
            }

            var source = new DeadLetterSource<string>(
                path,
                serializer,
                new DeadLetterSourceOptions());
            var items = await ReadAllAsync(source);

            Assert.Equal(["one", "two"], items.Select(static item => item.Payload));
            Assert.Equal([11UL, 12UL], items.Select(static item => item.TraceId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadEnvelopesAsync_ReturnsEmpty_WhenNoDeadLetters()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "[]");
            var source = new DeadLetterSource<string>(path);

            var items = await ReadAllAsync(source);

            Assert.Empty(items);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadEnvelopesAsync_ReplaysDeadLetterPayloadsAndContext()
    {
        var path = Path.GetTempFileName();
        try
        {
            var deadLetters = new[]
            {
                CreateDeadLetter("item1", 101UL, "customer", "gold"),
                CreateDeadLetter("item2", 102UL, "customer", "silver"),
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(deadLetters));

            var source = new DeadLetterSource<string>(path);
            var items = await ReadAllAsync(source);

            Assert.Equal(["item1", "item2"], items.Select(x => x.Payload));
            Assert.Equal("pipe", items[0].PipelineId);
            Assert.Equal("run", items[0].RunId);
            Assert.Equal(101UL, items[0].TraceId);
            Assert.Equal("gold", items[0].Metadata.GetString("customer"));
            Assert.Equal(new DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero), items[0].CreatedAtUtc);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadEnvelopesAsync_ReadsNdjsonDeadLetterRecords()
    {
        var path = Path.GetTempFileName();
        try
        {
            var first = JsonSerializer.Serialize(CreateDeadLetter("item1", 201UL));
            var second = JsonSerializer.Serialize(CreateDeadLetter("item2", 202UL));
            await File.WriteAllTextAsync(path, $"{first}{Environment.NewLine}{second}");

            var source = new DeadLetterSource<string>(path);
            var result = await ReadAllAsync(source);

            Assert.Equal(["item1", "item2"], result.Select(x => x.Payload));
            Assert.Equal([201UL, 202UL], result.Select(x => x.TraceId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadEnvelopesAsync_WithSourceGeneratedTypeInfo_ReturnsPayload()
    {
        var path = Path.GetTempFileName();
        try
        {
            var typeInfo = DeadLetterSourceTestJsonContext.Default.DeadLetterEnvelopeAotDeadLetterSourceItem;
            var serializer = new JsonLinesDeadLetterSerializer<AotDeadLetterSourceItem>(typeInfo);
            await using (var output = File.Create(path))
                await serializer.WriteAsync(
                    CreateDeadLetter(new AotDeadLetterSourceItem(5, "five"), 5UL),
                    output);

            var source = new DeadLetterSource<AotDeadLetterSourceItem>(
                path,
                typeInfo,
                new DeadLetterSourceOptions());
            var result = await ReadAllAsync(source);

            Assert.Single(result);
            Assert.Equal(new AotDeadLetterSourceItem(5, "five"), result[0].Payload);
            Assert.Equal(5UL, result[0].TraceId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadEnvelopesAsync_ThrowsForRecordsWithoutPayloadByDefault()
    {
        var path = Path.GetTempFileName();
        try
        {
            var items = new object[]
            {
                new { PipelineId = "pipe", RunId = "run", TraceId = 1UL },
                new { OriginalPayload = (string?)null, PipelineId = "pipe", RunId = "run", TraceId = 2UL },
                CreateDeadLetter("valid", 3UL),
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(items));

            var source = new DeadLetterSource<string>(path);
            await Assert.ThrowsAnyAsync<JsonException>(() => ReadAllAsync(source));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadEnvelopesAsync_HandlesSingleObjectJson()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(CreateDeadLetter("single", 301UL)));
            var source = new DeadLetterSource<string>(path);

            var items = await ReadAllAsync(source);

            Assert.Single(items);
            Assert.Equal("single", items[0].Payload);
            Assert.Equal(301UL, items[0].TraceId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadEnvelopesAsync_ShouldHandleComplexType()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(new[] { CreateDeadLetter(new TestComplexType { Id = 1, Name = "Test" }, 401UL) }));

            var source = new DeadLetterSource<TestComplexType>(path);
            var result = await ReadAllAsync(source);

            Assert.Single(result);
            Assert.Equal(1, result[0].Payload.Id);
            Assert.Equal("Test", result[0].Payload.Name);
            Assert.Equal(401UL, result[0].TraceId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadEnvelopesAsync_ThrowsJsonException_ForInvalidJson()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "invalid json");
            var source = new DeadLetterSource<string>(path);

            await Assert.ThrowsAnyAsync<JsonException>(async () =>
            {
                await foreach (var _ in source.ReadEnvelopesAsync()) { }
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("\"just a string\"")]
    [InlineData("12345")]
    public async Task ReadEnvelopesAsync_ThrowsJsonException_ForUnexpectedJsonRoot(string json)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, json);
            var source = new DeadLetterSource<string>(path);

            await Assert.ThrowsAnyAsync<JsonException>(async () =>
            {
                await foreach (var _ in source.ReadEnvelopesAsync()) { }
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadEnvelopesAsync_EmptyFile_ReturnsNoRecords()
    {
        var path = Path.GetTempFileName();
        try
        {
            var source = new DeadLetterSource<string>(path);
            var records = await ReadAllAsync(source);
            Assert.Empty(records);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadEnvelopesAsync_ObservesCancellation()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[] { CreateDeadLetter("test", 1UL) }));
            var source = new DeadLetterSource<string>(path);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in source.ReadEnvelopesAsync(cts.Token)) { }
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DisposeAsync_CompletesWithoutError()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "[]");
            var source = new DeadLetterSource<string>(path);

            await source.DisposeAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<List<ProcessingEnvelope<T>>> ReadAllAsync<T>(DeadLetterSource<T> source)
    {
        var items = new List<ProcessingEnvelope<T>>();
        await foreach (var item in source.ReadEnvelopesAsync())
            items.Add(item);
        return items;
    }

    private static DeadLetterEnvelope<T> CreateDeadLetter<T>(
        T payload,
        ulong traceId,
        string? metadataKey = null,
        string? metadataValue = null) =>
        new()
        {
            SchemaVersion = 1,
            PipelineId = "pipe",
            RunId = "run",
            TraceId = traceId,
            StageId = "stage",
            StageName = "Stage",
            OriginalPayload = payload,
            Metadata = metadataKey is null
                ? MetadataBag.Empty
                : MetadataBag.Empty.Set(metadataKey, metadataValue ?? string.Empty),
            Error = new SmartPipeError("failed", ErrorType.Permanent),
            Attempt = 1,
            FailedAtUtc = new DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero),
        };

    private sealed class TestComplexType
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }
}

public sealed record AotDeadLetterSourceItem(int Id, string Name);

[JsonSerializable(typeof(AotDeadLetterSourceItem))]
[JsonSerializable(typeof(DeadLetterEnvelope<AotDeadLetterSourceItem>))]
internal sealed partial class DeadLetterSourceTestJsonContext : JsonSerializerContext;
