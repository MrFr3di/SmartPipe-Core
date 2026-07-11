#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SmartPipe.Core;
using SmartPipe.Extensions.Sinks;
using Xunit;

namespace SmartPipe.Extensions.Tests.Sinks;

[Trait("Category", "CorrectnessRegression")]
[Trait("Category", "ConcurrencyRegression")]
public class JsonFileSinkTests
{
    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenPathIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new JsonFileSink<object>(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ThrowsArgumentException_WhenPathIsEmptyOrWhitespace(string path)
    {
        Assert.Throws<ArgumentException>(() => new JsonFileSink<object>(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ThrowsArgumentOutOfRangeException_WhenFlushIntervalIsInvalid(
        int flushInterval)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new JsonFileSink<object>("items.json", flushInterval));
    }

    [Fact]
    public async Task WriteAsync_CreatesFileWithCorrectContent_SingleItem()
    {
        var path = Path.GetTempFileName();
        try
        {
            var sink = new JsonFileSink<TestItem>(path);

            var result = ProcessingEnvelope<TestItem>.Create(new TestItem { Value = "test" });
            await sink.WriteAsync(result);
            await sink.DisposeAsync();

            var content = await File.ReadAllTextAsync(path);
            Assert.Contains("test", content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteAsync_AddsToExistingFile_AppendMode()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "[{\"Value\":\"existing\"}]\n");
            var sink = new JsonFileSink<TestItem>(path, flushInterval: 1);
            await sink.InitializeAsync();

            await sink.WriteAsync(ProcessingEnvelope<TestItem>.Create(new TestItem { Value = "appended" }));
            await sink.DisposeAsync();

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(2, lines.Length);
            Assert.Equal("[{\"Value\":\"existing\"}]", lines[0]);

            using var document = JsonDocument.Parse(lines[1]);
            var item = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("appended", item.GetProperty("Value").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DisposeAsync_WritesNewlineDelimitedJsonBatchArrays()
    {
        var path = Path.GetTempFileName();
        try
        {
            var sink = new JsonFileSink<TestItem>(path, flushInterval: 2);

            await sink.WriteAsync(ProcessingEnvelope<TestItem>.Create(new TestItem { Value = "first" }));
            await sink.WriteAsync(ProcessingEnvelope<TestItem>.Create(new TestItem { Value = "second" }));
            await sink.WriteAsync(ProcessingEnvelope<TestItem>.Create(new TestItem { Value = "third" }));
            await sink.DisposeAsync();

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(2, lines.Length);

            using var firstBatch = JsonDocument.Parse(lines[0]);
            Assert.Equal(JsonValueKind.Array, firstBatch.RootElement.ValueKind);
            Assert.Equal(2, firstBatch.RootElement.GetArrayLength());
            Assert.Equal("first", firstBatch.RootElement[0].GetProperty("Value").GetString());
            Assert.Equal("second", firstBatch.RootElement[1].GetProperty("Value").GetString());

            using var secondBatch = JsonDocument.Parse(lines[1]);
            Assert.Equal(JsonValueKind.Array, secondBatch.RootElement.ValueKind);
            var item = Assert.Single(secondBatch.RootElement.EnumerateArray());
            Assert.Equal("third", item.GetProperty("Value").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteAsync_SeekableStreamFailure_TruncatesCheckpointAndKeepsBatchBuffered()
    {
        await using var stream = new FailFirstWriteAfterPartialStream();
        var sink = new JsonFileSink<TestItem>("dummy.json", stream, flushInterval: 1);

        await Assert.ThrowsAsync<IOException>(() =>
            sink.WriteAsync(ProcessingEnvelope<TestItem>.Create(new TestItem { Value = "retry" })).AsTask());
        Assert.Equal(0, stream.Length);

        await sink.DisposeAsync();

        var content = Encoding.UTF8.GetString(stream.ToArray());
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(lines);
        using var document = JsonDocument.Parse(line);
        var item = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("retry", item.GetProperty("Value").GetString());
    }

    [Fact]
    public async Task WriteAsync_ConcurrentFlushes_UseSingleGate()
    {
        await using var stream = new ConcurrentWriteDetectingStream();
        var sink = new JsonFileSink<TestItem>("dummy.json", stream, flushInterval: 1);
        var tasks = Enumerable.Range(0, 20)
            .Select(i => sink.WriteAsync(
                ProcessingEnvelope<TestItem>.Create(new TestItem { Value = i.ToString() })).AsTask())
            .ToArray();

        await Task.WhenAll(tasks);
        await sink.DisposeAsync();

        Assert.Equal(1, stream.MaxConcurrentWrites);

        var content = Encoding.UTF8.GetString(stream.ToArray());
        var itemCount = 0;
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            itemCount += document.RootElement.GetArrayLength();
        }

        Assert.Equal(20, itemCount);
    }

    [Fact]
    public async Task WriteAsync_ThrowsIOException_ForInvalidPath()
    {
        var sink = new JsonFileSink<object>("/nonexistent/path/file.json");
        var result = ProcessingEnvelope<object>.Create(new());

        // WriteAsync only buffers (flushInterval=1000 default), no I/O yet
        await sink.WriteAsync(result);

        // DisposeAsync should fail when trying to flush to invalid path
        var ex = await Record.ExceptionAsync(() => sink.DisposeAsync().AsTask());

        Assert.NotNull(ex);
        Assert.True(ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException,
            $"Expected IOException-derived exception, got {ex?.GetType().Name ?? "null"}");
    }

    [Fact]
    public async Task WriteAsync_WithSourceGeneratedTypeInfo_WritesJson()
    {
        var path = Path.GetTempFileName();
        try
        {
            var sink = new JsonFileSink<AotJsonSinkItem>(
                path,
                JsonFileSinkTestJsonContext.Default.ListAotJsonSinkItem,
                flushInterval: 1);

            await sink.WriteAsync(
                ProcessingEnvelope<AotJsonSinkItem>.Create(new AotJsonSinkItem(7, "seven")));
            await sink.DisposeAsync();

            var content = await File.ReadAllTextAsync(path);
            Assert.Contains("\"Id\":7", content);
            Assert.Contains("\"Name\":\"seven\"", content);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        await using var stream = new MemoryStream();
        var sink = new JsonFileSink<TestItem>("dummy.json", stream);

        await sink.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            sink.WriteAsync(ProcessingEnvelope<TestItem>.Create(new TestItem { Value = "late" })).AsTask());
    }

    [Fact]
    public async Task InitializeAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        await using var stream = new MemoryStream();
        var sink = new JsonFileSink<TestItem>("dummy.json", stream);

        await sink.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => sink.InitializeAsync().AsTask());
    }

    private class TestItem
    {
        public string? Value { get; set; }
    }

    private sealed class FailFirstWriteAfterPartialStream : MemoryStream
    {
        private bool _failed;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_failed)
            {
                _failed = true;
                base.Write(buffer.Span[..Math.Min(3, buffer.Length)]);
                throw new IOException("partial write failed");
            }

            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class ConcurrentWriteDetectingStream : MemoryStream
    {
        private readonly object _sync = new();
        private int _activeWrites;

        public int MaxConcurrentWrites { get; private set; }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var activeWrites = Interlocked.Increment(ref _activeWrites);
            lock (_sync)
            {
                MaxConcurrentWrites = Math.Max(MaxConcurrentWrites, activeWrites);
            }

            try
            {
                await Task.Yield();
                await base.WriteAsync(buffer, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeWrites);
            }
        }
    }
}

public sealed record AotJsonSinkItem(int Id, string Name);

[JsonSerializable(typeof(AotJsonSinkItem))]
[JsonSerializable(typeof(List<AotJsonSinkItem>))]
internal sealed partial class JsonFileSinkTestJsonContext : JsonSerializerContext;
