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
    public async Task WriteAsync_ThrowsDirectoryNotFoundException_ForInvalidPath()
    {
        var sink = new JsonFileSink<object>("Z:\\nonexistent\\file.json");
        var result = ProcessingResult<object>.Success(new(), 1);
        
        // DisposeAsync tries to write to path
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => sink.DisposeAsync());
    }

    private class TestItem
    {
        public string? Value { get; set; }
    }
}
