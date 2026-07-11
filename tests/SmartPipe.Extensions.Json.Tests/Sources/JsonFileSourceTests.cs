#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Moq;
using SmartPipe.Core;
using SmartPipe.Extensions;
using SmartPipe.Extensions.Selectors;
using Xunit;

namespace SmartPipe.Extensions.Tests.Sources;

public class JsonFileSourceTests
{
    [Fact]
    public async Task ReadAsync_AutoDetectsArray_WhenBomArrivesInPartialReads()
    {
        await using var stream = new OneByteReadMemoryStream(
            "\uFEFF[{\"Value\":\"first\"}]"u8.ToArray());
        var source = new JsonFileSource<TestItem>(
            "partial-bom.json",
            stream,
            new JsonFileSourceOptions());

        var items = new List<TestItem>();
        await foreach (var envelope in source.ReadEnvelopesAsync())
            items.Add(envelope.Payload);

        Assert.Equal("first", Assert.Single(items).Value);
    }

    [Fact]
    public async Task ReadAsync_AutoPreservesMultipleTopLevelValuesWithoutLineBreaks()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "\"first\" \"second\"");
            var source = new JsonFileSource<string>(path);
            var values = new List<string>();

            await foreach (var envelope in source.ReadEnvelopesAsync())
                values.Add(envelope.Payload);

            Assert.Equal(["first", "second"], values);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenPathIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new JsonFileSource<string>(null!));
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenPathIsEmpty()
    {
        Assert.Throws<ArgumentException>(() => new JsonFileSource<string>(""));
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenPathIsWhitespace()
    {
        Assert.Throws<ArgumentException>(() => new JsonFileSource<string>("   "));
    }

    [Fact]
    public async Task ReadAsync_ReturnsExpectedJson_ForValidFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            // Используем правильный JSON формат - массив строк
            await File.WriteAllTextAsync(path, "[\"test\"]");

            var source = new JsonFileSource<string>(path);
            var items = new List<ProcessingEnvelope<string>>();

            await foreach (var item in source.ReadEnvelopesAsync())
            {
                items.Add(item);
            }

            Assert.Single(items);
            Assert.Equal("test", items[0].Payload);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ThrowsFileNotFoundException_ForMissingFile()
    {
        var source = new JsonFileSource<string>("missing.json");
        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
        {
            await foreach (var item in source.ReadEnvelopesAsync()) { }
        });
    }

    [Fact]
    public async Task ReadAsync_ThrowsJsonException_ForMalformedJson()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "not json");

            var source = new JsonFileSource<string>(path);
            await Assert.ThrowsAsync<JsonException>(async () =>
            {
                await foreach (var item in source.ReadEnvelopesAsync()) { }
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ReturnsEmptyEnumerable_ForEmptyFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "");

            var source = new JsonFileSource<string>(path);
            var items = new List<ProcessingEnvelope<string>>();

            await foreach (var item in source.ReadEnvelopesAsync())
            {
                items.Add(item);
            }

            Assert.Empty(items);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_HandlesNdjsonFormat()
    {
        var path = Path.GetTempFileName();
        try
        {
            // NDJSON format: each line is a separate JSON object
            var ndjson = "\"item1\"\n\"item2\"\n\"item3\"\n";
            await File.WriteAllTextAsync(path, ndjson);

            var source = new JsonFileSource<string>(path);
            var items = new List<ProcessingEnvelope<string>>();

            await foreach (var item in source.ReadEnvelopesAsync())
            {
                items.Add(item);
            }

            Assert.Equal(3, items.Count);
            Assert.Equal("item1", items[0].Payload);
            Assert.Equal("item2", items[1].Payload);
            Assert.Equal("item3", items[2].Payload);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_HandlesNdjsonWithObjects()
    {
        var path = Path.GetTempFileName();
        try
        {
            // NDJSON with objects
            var ndjson = "{\"Value\":\"item1\"}\n{\"Value\":\"item2\"}\n";
            await File.WriteAllTextAsync(path, ndjson);

            var source = new JsonFileSource<TestItem>(path);
            var items = new List<ProcessingEnvelope<TestItem>>();

            await foreach (var item in source.ReadEnvelopesAsync())
            {
                items.Add(item);
            }

            Assert.Equal(2, items.Count);
            Assert.Equal("item1", items[0].Payload?.Value);
            Assert.Equal("item2", items[1].Payload?.Value);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ReturnsEmpty_ForNdjsonWithEmptyLines()
    {
        var path = Path.GetTempFileName();
        try
        {
            // NDJSON with empty lines
            var ndjson = "\"item1\"\n\n\"item2\"\n";
            await File.WriteAllTextAsync(path, ndjson);

            var source = new JsonFileSource<string>(path);
            var items = new List<ProcessingEnvelope<string>>();

            await foreach (var item in source.ReadEnvelopesAsync())
            {
                items.Add(item);
            }

            Assert.Equal(2, items.Count);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_HandlesJsonArrayWithObjects()
    {
        var path = Path.GetTempFileName();
        try
        {
            var json = "[{\"Value\":\"item1\"},{\"Value\":\"item2\"}]";
            await File.WriteAllTextAsync(path, json);

            var source = new JsonFileSource<TestItem>(path);
            var items = new List<ProcessingEnvelope<TestItem>>();

            await foreach (var item in source.ReadEnvelopesAsync())
            {
                items.Add(item);
            }

            Assert.Equal(2, items.Count);
            Assert.Equal("item1", items[0].Payload?.Value);
            Assert.Equal("item2", items[1].Payload?.Value);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_HandlesWhitespaceOnlyFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "   \n  \n");

            var source = new JsonFileSource<string>(path);
            var items = new List<ProcessingEnvelope<string>>();

            await foreach (var item in source.ReadEnvelopesAsync())
            {
                items.Add(item);
            }

            // NDJSON format will try to read lines, whitespace lines are skipped
            Assert.Empty(items);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_HandlesSingleObjectJson_AsNdjson()
    {
        var path = Path.GetTempFileName();
        try
        {
            // Single object - will be treated as NDJSON (first char is not '[')
            var json = "{\"Value\":\"test\"}";
            await File.WriteAllTextAsync(path, json);

            var source = new JsonFileSource<TestItem>(path);
            var items = new List<ProcessingEnvelope<TestItem>>();

            await foreach (var item in source.ReadEnvelopesAsync())
            {
                items.Add(item);
            }

            Assert.Single(items);
            Assert.Equal("test", items[0].Payload?.Value);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ThrowsForNullItems_InNdjsonByDefault()
    {
        var path = Path.GetTempFileName();
        try
        {
            // NDJSON with invalid JSON line that deserializes to null
            var ndjson = "\"valid\"\nnull\n\"also valid\"\n";
            await File.WriteAllTextAsync(path, ndjson);

            var source = new JsonFileSource<string?>(path);
            await Assert.ThrowsAsync<JsonException>(async () =>
            {
                await foreach (var item in source.ReadEnvelopesAsync()) { }
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ThrowsForNullItems_InJsonArrayByDefault()
    {
        var path = Path.GetTempFileName();
        try
        {
            // JSON array with null value
            var json = "[\"valid\",null,\"also valid\"]";
            await File.WriteAllTextAsync(path, json);

            var source = new JsonFileSource<string?>(path);
            await Assert.ThrowsAsync<JsonException>(async () =>
            {
                await foreach (var item in source.ReadEnvelopesAsync()) { }
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_SkipAndLog_SkipsNullAtSafeRecordBoundary()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "\"valid\"\nnull\n\"also valid\"\n");
            var logger = new Mock<ILogger<JsonFileSource<string?>>>();
            var source = new JsonFileSource<string?>(
                path,
                new JsonFileSourceOptions
                {
                    Format = JsonFileFormat.Ndjson,
                    InvalidRecordBehavior = InvalidJsonRecordBehavior.SkipAndLog,
                },
                logger.Object);
            var values = new List<string?>();

            await foreach (var item in source.ReadEnvelopesAsync())
                values.Add(item.Payload);

            Assert.Equal(["valid", "also valid"], values);
            Assert.NotEmpty(logger.Invocations);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(JsonFileFormat.Ndjson, "\"first\"\nnot-json\n\"third\"\n")]
    [InlineData(JsonFileFormat.BatchJsonLines, "[\"first\"]\nnot-json\n[\"third\"]\n")]
    public async Task ReadAsync_SkipAndLog_ContinuesAfterMalformedFramedRecord(
        JsonFileFormat format,
        string content)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, content);
            var logger = new Mock<ILogger<JsonFileSource<string>>>();
            var source = new JsonFileSource<string>(
                path,
                new JsonFileSourceOptions
                {
                    Format = format,
                    InvalidRecordBehavior = InvalidJsonRecordBehavior.SkipAndLog,
                },
                logger.Object);
            var values = new List<string>();

            await foreach (var item in source.ReadEnvelopesAsync())
                values.Add(item.Payload);

            Assert.Equal(["first", "third"], values);
            Assert.NotEmpty(logger.Invocations);
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
        await File.WriteAllTextAsync(path, "[\"test\"]");

        var source = new JsonFileSource<string>(path);
        await source.DisposeAsync();

        File.Delete(path);
    }

    [Fact]
    public async Task ReadAsync_ThrowsCancellation_WhenTokenCancelled()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "[\"test\"]");

            var source = new JsonFileSource<string>(path);
            var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var item in source.ReadEnvelopesAsync(cts.Token))
                {
                }
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_HandlesEmptyJsonArray()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "[]");

            var source = new JsonFileSource<string>(path);
            var items = new List<ProcessingEnvelope<string>>();

            await foreach (var item in source.ReadEnvelopesAsync())
            {
                items.Add(item);
            }

            Assert.Empty(items);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ShouldTreatLeadingWhitespaceBeforeArray_AsJsonArray()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, " \r\n\t[\"first\",\"second\"]");

            var source = new JsonFileSource<string>(path);
            var items = new List<ProcessingEnvelope<string>>();

            await foreach (var item in source.ReadEnvelopesAsync())
                items.Add(item);

            Assert.Equal(2, items.Count);
            Assert.Equal("first", items[0].Payload);
            Assert.Equal("second", items[1].Payload);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_WithSourceGeneratedTypeInfo_ReadsJsonArray()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, """[{"Id":1,"Name":"one"},{"Id":2,"Name":"two"}]""");

            var source = new JsonFileSource<AotJsonFileItem>(
                path,
                JsonFileSourceTestJsonContext.Default.ListAotJsonFileItem,
                JsonFileSourceTestJsonContext.Default.AotJsonFileItem);
            var items = new List<ProcessingEnvelope<AotJsonFileItem>>();

            await foreach (var item in source.ReadEnvelopesAsync())
                items.Add(item);

            Assert.Equal(2, items.Count);
            Assert.Equal(new AotJsonFileItem(1, "one"), items[0].Payload);
            Assert.Equal(new AotJsonFileItem(2, "two"), items[1].Payload);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_WithSourceGeneratedTypeInfo_ReadsNdjson()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {"Id":3,"Name":"three"}
                {"Id":4,"Name":"four"}
                """);

            var source = new JsonFileSource<AotJsonFileItem>(
                path,
                JsonFileSourceTestJsonContext.Default.ListAotJsonFileItem,
                JsonFileSourceTestJsonContext.Default.AotJsonFileItem);
            var items = new List<ProcessingEnvelope<AotJsonFileItem>>();

            await foreach (var item in source.ReadEnvelopesAsync())
                items.Add(item);

            Assert.Equal(2, items.Count);
            Assert.Equal(new AotJsonFileItem(3, "three"), items[0].Payload);
            Assert.Equal(new AotJsonFileItem(4, "four"), items[1].Payload);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_NdjsonRecordExceedingByteLimit_ThrowsJsonException()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "\"record larger than limit\"\n");
            var source = new JsonFileSource<string>(
                path,
                new JsonFileSourceOptions
                {
                    Format = JsonFileFormat.Ndjson,
                    MaxRecordSizeBytes = 8,
                });

            await Assert.ThrowsAsync<JsonException>(async () =>
            {
                await foreach (var item in source.ReadEnvelopesAsync()) { }
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ArrayYieldsFirstItemBeforeEndOfStreamArrives()
    {
        var stream = new GatedReadStream();
        stream.Append("[{\"Value\":\"first\"},"u8.ToArray());
        var source = new JsonFileSource<TestItem>(
            "gated.json",
            stream,
            new JsonFileSourceOptions { Format = JsonFileFormat.Array });
        await using var enumerator = source.ReadEnvelopesAsync().GetAsyncEnumerator();

        var firstMove = enumerator.MoveNextAsync().AsTask();
        Assert.Same(firstMove, await Task.WhenAny(firstMove, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.True(await firstMove);
        Assert.Equal("first", enumerator.Current.Payload.Value);

        stream.Append("{\"Value\":\"second\"}]"u8.ToArray());
        stream.Complete();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("second", enumerator.Current.Payload.Value);
        Assert.False(await enumerator.MoveNextAsync());
    }

    private class TestItem
    {
        public string? Value { get; set; }
    }

    private sealed class GatedReadStream : Stream
    {
        private readonly Channel<byte[]> _segments = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;

        public void Append(byte[] bytes) => Assert.True(_segments.Writer.TryWrite(bytes));
        public void Complete() => _segments.Writer.TryComplete();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_current == null || _offset == _current.Length)
            {
                if (!await _segments.Reader.WaitToReadAsync(cancellationToken))
                    return 0;
                if (_segments.Reader.TryRead(out _current))
                    _offset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class OneByteReadMemoryStream(byte[] buffer) : MemoryStream(buffer)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(destination[..Math.Min(1, destination.Length)], cancellationToken);
    }
}

public sealed record AotJsonFileItem(int Id, string Name);

[JsonSerializable(typeof(AotJsonFileItem))]
[JsonSerializable(typeof(List<AotJsonFileItem>))]
internal sealed partial class JsonFileSourceTestJsonContext : JsonSerializerContext;
