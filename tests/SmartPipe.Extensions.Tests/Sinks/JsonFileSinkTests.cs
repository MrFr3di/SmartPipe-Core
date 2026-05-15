#nullable enable
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

    [Fact]
    public async Task WriteAsync_CreatesFileWithCorrectContent_SingleItem()
    {
        var path = Path.GetTempFileName();
        var sink = new JsonFileSink<TestItem>(path);
        
        var result = ProcessingResult<TestItem>.Success(new TestItem { Value = "test" }, 1);
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
        
        await sink.WriteAsync(ProcessingResult<TestItem>.Success(new TestItem { Value = "first" }, 1));
        await sink.WriteAsync(ProcessingResult<TestItem>.Success(new TestItem { Value = "second" }, 2));
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
        var result = ProcessingResult<object>.Success(new(), 1);
        
        // WriteAsync only buffers (flushInterval=1000 default), no I/O yet
        await sink.WriteAsync(result);
        
        // DisposeAsync should fail when trying to flush to invalid path
        var ex = await Record.ExceptionAsync(() => sink.DisposeAsync());
        
        Assert.NotNull(ex);
        Assert.True(ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException,
            $"Expected IOException-derived exception, got {ex?.GetType().Name ?? "null"}");
    }

    private class TestItem
    {
        public string? Value { get; set; }
    }
}
