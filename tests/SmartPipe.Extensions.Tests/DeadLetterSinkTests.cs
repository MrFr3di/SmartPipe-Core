using FluentAssertions;
using SmartPipe.Core;
using SmartPipe.Extensions.Sinks;

namespace SmartPipe.Extensions.Tests;

public class DeadLetterSinkTests
{
    [Fact]
    public async Task WriteAsync_SuccessResult_ShouldNotStore()
    {
        var sink = new DeadLetterSink<string>("dead_test.json");
        var result = ProcessingResult<string>.Success("ok", 1UL);
        
        await sink.WriteAsync(result);
        await sink.DisposeAsync();
        
        File.Exists("dead_test.json").Should().BeTrue();
        var content = await File.ReadAllTextAsync("dead_test.json");
        content.Should().Be("[]"); // Empty — no errors stored
        File.Delete("dead_test.json");
    }

    [Fact]
    public async Task WriteAsync_FailureResult_ShouldStore()
    {
        var sink = new DeadLetterSink<string>("dead_test.json");
        var error = new SmartPipeError("test error", ErrorType.Permanent);
        var result = ProcessingResult<string>.Failure(error, 2UL);
        
        await sink.WriteAsync(result);
        await sink.DisposeAsync();
        
        File.Exists("dead_test.json").Should().BeTrue();
        var content = await File.ReadAllTextAsync("dead_test.json");
        content.Should().Contain("test error");
        File.Delete("dead_test.json");
    }
}
