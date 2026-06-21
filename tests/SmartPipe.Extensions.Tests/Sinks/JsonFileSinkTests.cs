#nullable enable
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SmartPipe.Core;
using SmartPipe.Extensions.Sinks;
using Xunit;

namespace SmartPipe.Extensions.Tests.Sinks;

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
        var sink = new JsonFileSink<TestItem>(path);

        var result = ProcessingEnvelope<TestItem>.Create(new TestItem { Value = "test" });
        await sink.WriteAsync(result);
        await sink.DisposeAsync();

        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("test", content);

        File.Delete(path);
    }

    [Fact]
    public async Task WriteAsync_AddsToExistingFile_AppendMode()
    {
        var path = Path.GetTempFileName();
        var sink = new JsonFileSink<TestItem>(path);

        await sink.WriteAsync(ProcessingEnvelope<TestItem>.Create(new TestItem { Value = "first" }));
        await sink.WriteAsync(ProcessingEnvelope<TestItem>.Create(new TestItem { Value = "second" }));
        await sink.DisposeAsync();

        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("first", content);
        Assert.Contains("second", content);

        File.Delete(path);
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

    private class TestItem
    {
        public string? Value { get; set; }
    }
}

public sealed record AotJsonSinkItem(int Id, string Name);

[JsonSerializable(typeof(AotJsonSinkItem))]
[JsonSerializable(typeof(List<AotJsonSinkItem>))]
internal sealed partial class JsonFileSinkTestJsonContext : JsonSerializerContext;
