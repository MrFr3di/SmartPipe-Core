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
        var envelope = ProcessingEnvelope<string>.Create("data");

        await sink.Invoking(s => s.WriteAsync(envelope).AsTask())
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task WriteAsync_NullPayload_ShouldNotThrow()
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<LoggerSink<string?>>();
        var sink = new LoggerSink<string?>(logger);
        var envelope = ProcessingEnvelope<string?>.Create(null);

        await sink.Invoking(s => s.WriteAsync(envelope).AsTask())
            .Should().NotThrowAsync();
    }
}
