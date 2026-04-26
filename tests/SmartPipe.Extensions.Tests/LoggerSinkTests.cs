using FluentAssertions;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;
using SmartPipe.Extensions.Sinks;

namespace SmartPipe.Extensions.Tests;

public class LoggerSinkTests
{
    [Fact]
    public async Task WriteAsync_SuccessResult_ShouldNotThrow()
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<LoggerSink<string>>();
        var sink = new LoggerSink<string>(logger);
        var result = ProcessingResult<string>.Success("data", 1UL);

        await sink.Invoking(s => s.WriteAsync(result))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task WriteAsync_FailureResult_ShouldNotThrow()
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<LoggerSink<string>>();
        var sink = new LoggerSink<string>(logger);
        var error = new SmartPipeError("Test error", ErrorType.Permanent);
        var result = ProcessingResult<string>.Failure(error, 1UL);

        await sink.Invoking(s => s.WriteAsync(result))
            .Should().NotThrowAsync();
    }
}
